#include "BroadcastController.h"

#include <algorithm>
#include <cmath>
#include <limits>

namespace broadcast
{
    namespace
    {
        constexpr int64_t kPartMs = 1000;
        constexpr int64_t kTargetLatencyMs = 3000;
        constexpr int64_t kStallLatencyIncreaseMs = 1000;
        constexpr int64_t kWindowMs = 30000;
        constexpr int64_t kListenerStartBufferMs = 1000;
        constexpr int64_t kUnifiedStartBufferMs = 2000;
        constexpr int64_t kResumeBufferMs = 2000;
        constexpr size_t kMaxLoadingSegments = 2;
        constexpr int64_t kMaxAheadMs = 30000;
        constexpr int64_t kRetryUs = 100000;
        constexpr int64_t kEdgeRetryUs = 1000000;
        constexpr int64_t kAbandonCheckUs = 100000;
        constexpr int64_t kAbandonWatchUs = 500000;
        constexpr double kBwFactor = 0.95;
        constexpr double kBwUpFactor = 0.7;
        constexpr size_t kBitrateSamples = 10;
        constexpr double kAudioBitrate = 100000;
        constexpr int64_t kNotWatched = std::numeric_limits<int64_t>::max();

        constexpr double kEwmaFast = 3;
        constexpr double kEwmaSlow = 9;
        constexpr double kMinWeight = 0.001;
        constexpr double kMinDelayMs = 50;
        constexpr double kDefaultTtfbMs = 100;

        int64_t AlignDown(int64_t ms)
        {
            int64_t remainder = ms % kPartMs;
            return remainder < 0 ? ms - remainder - kPartMs : ms - remainder;
        }

        double SeedBitrate(VideoQuality quality)
        {
            switch (quality)
            {
            case VideoQuality::Full:
                return 3300000;
            case VideoQuality::Medium:
                return 1000000;
            default:
                return 300000;
            }
        }

        int32_t BitrateKey(PartKind kind, VideoQuality quality)
        {
            return static_cast<int32_t>(kind) * 4 + static_cast<int32_t>(quality);
        }
    }

    BandwidthEstimator::Ewma::Ewma(double halfLife)
        : m_alpha(halfLife > 0 ? std::exp(std::log(0.5) / halfLife) : 0)
    {
    }

    void BandwidthEstimator::Ewma::Sample(double weight, double value)
    {
        double adjustedAlpha = std::pow(m_alpha, weight);
        m_estimate = value * (1 - adjustedAlpha) + adjustedAlpha * m_estimate;
        m_totalWeight += weight;
    }

    double BandwidthEstimator::Ewma::TotalWeight() const
    {
        return m_totalWeight;
    }

    double BandwidthEstimator::Ewma::Estimate() const
    {
        if (m_alpha > 0)
        {
            double zeroFactor = 1 - std::pow(m_alpha, m_totalWeight);
            if (zeroFactor != 0)
            {
                return m_estimate / zeroFactor;
            }
        }

        return m_estimate;
    }

    std::shared_ptr<BandwidthEstimator> BandwidthEstimator::Shared()
    {
        static auto shared = std::make_shared<BandwidthEstimator>();
        return shared;
    }

    BandwidthEstimator::BandwidthEstimator(double defaultEstimateBps)
        : m_defaultEstimate(defaultEstimateBps)
        , m_fast(kEwmaFast)
        , m_slow(kEwmaSlow)
        , m_ttfb(kEwmaSlow)
    {
    }

    void BandwidthEstimator::Sample(double durationMs, double bytes)
    {
        std::lock_guard lock(m_lock);
        durationMs = std::max(durationMs, kMinDelayMs);
        double seconds = durationMs / 1000;
        double bitsPerSecond = 8 * bytes / seconds;
        m_fast.Sample(seconds, bitsPerSecond);
        m_slow.Sample(seconds, bitsPerSecond);
    }

    void BandwidthEstimator::SampleTtfb(double ttfbMs)
    {
        std::lock_guard lock(m_lock);
        double seconds = ttfbMs / 1000;
        double weight = std::sqrt(2) * std::exp(-seconds * seconds / 2);
        m_ttfb.Sample(weight, std::max(ttfbMs, 5.0));
    }

    bool BandwidthEstimator::CanEstimateLocked() const
    {
        return m_fast.TotalWeight() >= kMinWeight;
    }

    bool BandwidthEstimator::CanEstimate()
    {
        std::lock_guard lock(m_lock);
        return CanEstimateLocked();
    }

    double BandwidthEstimator::Estimate()
    {
        std::lock_guard lock(m_lock);
        return CanEstimateLocked() ? std::min(m_fast.Estimate(), m_slow.Estimate()) : m_defaultEstimate;
    }

    double BandwidthEstimator::TtfbMs()
    {
        std::lock_guard lock(m_lock);
        return m_ttfb.TotalWeight() >= kMinWeight ? m_ttfb.Estimate() : kDefaultTtfbMs;
    }

    void BandwidthEstimator::Reset(double defaultEstimateBps)
    {
        std::lock_guard lock(m_lock);
        m_defaultEstimate = defaultEstimateBps;
        m_fast = Ewma(kEwmaFast);
        m_slow = Ewma(kEwmaSlow);
        m_ttfb = Ewma(kEwmaSlow);
    }

    Controller::Controller(StreamFormat format, ControllerHost host, std::shared_ptr<BandwidthEstimator> estimator)
        : m_format(format)
        , m_host(std::move(host))
        , m_estimator(std::move(estimator))
        , m_alive(std::make_shared<bool>(true))
        , m_targetLatencyMs(kTargetLatencyMs)
    {
    }

    Controller::~Controller()
    {
        Stop();
    }

    void Controller::Start()
    {
        if (m_started)
        {
            return;
        }

        m_started = true;
        RequestEdge();
    }

    void Controller::Stop()
    {
        if (!m_started)
        {
            return;
        }

        m_started = false;

        std::vector<uint64_t> ids;
        ids.reserve(m_requests.size());

        for (auto const& [id, timestamp] : m_requests)
        {
            ids.push_back(id);
        }

        if (ids.size())
        {
            m_host.cancelParts(ids);
        }

        m_alive = std::make_shared<bool>(true);
        ResetState();
    }

    void Controller::ResetState()
    {
        m_anchored = false;
        m_waitingEdge = false;
        m_waitingRejoin = false;
        m_rejoinStartUs = 0;
        m_edgeMs = 0;
        m_edgeHostUs = 0;
        m_targetLatencyMs = kTargetLatencyMs;
        m_startPositionMs = 0;
        m_nextTimestampMs = 0;
        m_segments.clear();
        m_requests.clear();
        m_newestChannelMap.clear();
        m_newestChannelMapTimestampMs = INT64_MIN;
        m_currentLevels.clear();
        m_pumpScheduled = false;
        m_pumpAtUs = 0;
        m_abandonScheduled = false;
        m_firstPlayDone = false;
        m_lastSegmentLoadEndUs = 0;
        m_playback = Playback::Buffering;
        m_stallTimestampMs = 0;
        m_plays.clear();
        m_playerStarted = false;
        m_playerReportedPlaying = false;
        m_playerPlaying = false;
        m_holding = false;
        m_jumpPending = false;
        m_jumpPositionMs = 0;
        m_givenEndMs = 0;
        m_playerPositionMs = 0;
        m_playerPositionHostUs = 0;
    }

    int64_t Controller::Now() const
    {
        return m_host.nowUs();
    }

    void Controller::Schedule(int64_t delayUs, std::function<void()> task)
    {
        std::weak_ptr<bool> alive = m_alive;
        m_host.schedule(std::max<int64_t>(delayUs, 0), [alive, task = std::move(task)]
        {
            if (alive.lock())
            {
                task();
            }
        });
    }

    int64_t Controller::EdgeNowMs(int64_t now) const
    {
        return m_edgeMs + (now - m_edgeHostUs) / 1000;
    }

    int64_t Controller::LiveSyncPosition(int64_t edgeMs) const
    {
        return std::min(std::max(edgeMs - kWindowMs, edgeMs - m_targetLatencyMs), edgeMs - kPartMs);
    }

    void Controller::RequestEdge()
    {
        if (!m_started || m_waitingEdge)
        {
            return;
        }

        m_waitingEdge = true;
        m_host.requestLiveEdge();
    }

    void Controller::LiveEdge(int64_t edgeMs)
    {
        if (!m_started || !m_waitingEdge)
        {
            return;
        }

        if (edgeMs <= 0 || LiveSyncPosition(edgeMs) <= 0)
        {
            // Still waiting, so nothing is fetched against the edge being replaced.
            Schedule(kEdgeRetryUs, [this]
            {
                if (m_started && m_waitingEdge)
                {
                    m_host.requestLiveEdge();
                }
            });
            return;
        }

        m_waitingEdge = false;
        m_edgeMs = edgeMs;
        m_edgeHostUs = Now();

        if (m_anchored)
        {
            Reanchor(edgeMs);
        }
        else
        {
            Anchor(edgeMs);
        }
    }

    void Controller::Anchor(int64_t edgeMs)
    {
        m_anchored = true;
        m_startPositionMs = LiveSyncPosition(edgeMs);
        m_nextTimestampMs = AlignDown(m_startPositionMs);
        Pump();
    }

    void Controller::Reanchor(int64_t edgeMs)
    {
        int64_t position = LiveSyncPosition(edgeMs);
        int64_t anchorTs = AlignDown(position);

        if (m_format == StreamFormat::Listener)
        {
            if (m_firstPlayDone)
            {
                m_nextTimestampMs = std::max(anchorTs, LastSegmentEnd());
            }
            else
            {
                DropUndelivered();
                m_playback = Playback::Buffering;
                m_startPositionMs = position;
                m_nextTimestampMs = anchorTs;
            }
        }
        else if (!m_playerStarted)
        {
            DropUndelivered();
            m_jumpPending = false;
            m_startPositionMs = position;
            m_nextTimestampMs = anchorTs;
        }
        else if (edgeMs < m_givenEndMs)
        {
            // The edge is behind what the player was given, which loading never goes past: the stream restarted and
            // its timestamps started over. Nothing held belongs to the new timeline, and it plays on only until the
            // new position has its start buffer.
            for (auto& [timestamp, segment] : m_segments)
            {
                CancelSegment(segment);
            }

            m_segments.clear();
            m_jumpPending = true;
            m_holding = false;
            m_jumpPositionMs = position;
            m_nextTimestampMs = anchorTs;
        }
        else if (position <= m_givenEndMs)
        {
            // The player already holds the anchor: it never seeks back, so loading goes on after what it has.
            if (m_jumpPending)
            {
                DropUndelivered();
                m_jumpPending = false;
            }

            m_nextTimestampMs = std::max(m_givenEndMs, LastSegmentEnd());
        }
        else
        {
            // Past the buffer: the old parts play on until the new position has its start buffer.
            DropUndelivered();
            m_jumpPending = true;
            m_holding = false;
            m_jumpPositionMs = position;
            m_nextTimestampMs = anchorTs;
        }

        Continue();
    }

    void Controller::Continue()
    {
        if (m_format == StreamFormat::Listener)
        {
            TryDeliver();
        }
        else
        {
            FeedPlayer();
        }

        Pump();
    }

    void Controller::Pump()
    {
        if (!m_started || !m_anchored || m_waitingEdge || m_waitingRejoin)
        {
            return;
        }

        int64_t now = Now();
        Prune(now);

        int64_t edge = EdgeNowMs(now);
        int64_t limit = PlayheadTs(now) + kMaxAheadMs;

        while (IncompleteCount() < kMaxLoadingSegments)
        {
            while (m_segments.count(m_nextTimestampMs))
            {
                m_nextTimestampMs += kPartMs;
            }

            int64_t timestamp = m_nextTimestampMs;
            if (timestamp + kPartMs > edge)
            {
                SchedulePump(timestamp + kPartMs - edge);
                break;
            }
            else if (timestamp >= limit)
            {
                SchedulePump(kPartMs);
                break;
            }

            IssueSegment(timestamp);
            m_nextTimestampMs = timestamp + kPartMs;
        }
    }

    void Controller::SchedulePump(int64_t delayMs)
    {
        int64_t at = Now() + delayMs * 1000;
        if (m_pumpScheduled && m_pumpAtUs <= at)
        {
            return;
        }

        m_pumpScheduled = true;
        m_pumpAtUs = at;

        uint64_t generation = ++m_pumpGeneration;
        Schedule(delayMs * 1000, [this, generation]
        {
            if (generation == m_pumpGeneration)
            {
                m_pumpScheduled = false;
                Pump();
            }
        });
    }

    void Controller::IssueSegment(int64_t timestampMs)
    {
        RecomputeLevels(Now());

        Segment segment;
        segment.timestampMs = timestampMs;
        segment.primary.request.timestampMs = timestampMs;

        if (m_format == StreamFormat::Listener)
        {
            segment.primary.request.kind = PartKind::Audio;
            segment.primary.request.channelId = 0;

            if (m_newestChannelMap.size())
            {
                // The map rarely changes, so video loads beside the audio and is corrected if its map differs.
                segment.issuedWithMap = true;

                for (auto const& channel : m_videoChannels)
                {
                    auto mapped = m_newestChannelMap.find(channel.endpointId);
                    if (mapped != m_newestChannelMap.end())
                    {
                        segment.video.push_back(MakeVideoPart(timestampMs, mapped->second, channel.endpointId));
                    }
                }
            }
        }
        else
        {
            segment.primary.request.kind = PartKind::Unified;
            segment.primary.request.channelId = 1;
            segment.primary.request.quality = LevelFor(std::string());
        }

        auto& stored = m_segments.emplace(timestampMs, std::move(segment)).first->second;
        IssuePart(stored, stored.primary);

        for (auto& part : stored.video)
        {
            IssuePart(stored, part);
        }
    }

    Controller::Part Controller::MakeVideoPart(int64_t timestampMs, int32_t channelId, std::string const& endpointId)
    {
        Part part;
        part.request.kind = PartKind::Video;
        part.request.timestampMs = timestampMs;
        part.request.channelId = channelId;
        part.request.quality = LevelFor(endpointId);
        part.request.endpointId = endpointId;
        return part;
    }

    void Controller::IssuePart(Segment& segment, Part& part)
    {
        int64_t now = Now();

        part.request.id = m_nextRequestId++;
        part.state = Part::State::Loading;
        part.data.reset();
        part.bytes = 0;
        part.issuedUs = now;
        part.issuedOnIdleLink = m_requests.empty();
        part.watchFromUs = part.refetch || IsEarliestIncomplete(segment) ? now : kNotWatched;

        m_requests.emplace(part.request.id, segment.timestampMs);
        m_host.loadPart(part.request);

        ScheduleAbandonCheck();
    }

    void Controller::CancelPart(Part& part)
    {
        if (part.state == Part::State::Loading)
        {
            m_requests.erase(part.request.id);
            m_host.cancelParts({ part.request.id });
        }

        part.retryGeneration++;
    }

    void Controller::CancelSegment(Segment& segment)
    {
        CancelPart(segment.primary);

        for (auto& part : segment.video)
        {
            CancelPart(part);
        }
    }

    void Controller::CancelIncomplete()
    {
        for (auto it = m_segments.begin(); it != m_segments.end();)
        {
            auto& segment = it->second;
            if (segment.delivered)
            {
                auto& video = segment.video;
                for (auto part = video.begin(); part != video.end();)
                {
                    if (part->refetch)
                    {
                        CancelPart(*part);
                        part = video.erase(part);
                    }
                    else
                    {
                        ++part;
                    }
                }

                ++it;
            }
            else if (AllLoaded(segment))
            {
                ++it;
            }
            else
            {
                CancelSegment(segment);
                it = m_segments.erase(it);
            }
        }
    }

    void Controller::DropUndelivered()
    {
        for (auto it = m_segments.begin(); it != m_segments.end();)
        {
            if (it->second.delivered)
            {
                ++it;
            }
            else
            {
                CancelSegment(it->second);
                it = m_segments.erase(it);
            }
        }
    }

    Controller::Segment* Controller::FindSegment(int64_t timestampMs)
    {
        auto it = m_segments.find(timestampMs);
        return it == m_segments.end() ? nullptr : &it->second;
    }

    Controller::Part* Controller::FindPart(Segment& segment, uint64_t requestId)
    {
        if (segment.primary.request.id == requestId)
        {
            return &segment.primary;
        }

        for (auto& part : segment.video)
        {
            if (part.request.id == requestId)
            {
                return &part;
            }
        }

        return nullptr;
    }

    bool Controller::AllLoaded(Segment const& segment) const
    {
        if (segment.primary.state != Part::State::Loaded)
        {
            return false;
        }

        for (auto const& part : segment.video)
        {
            if (!part.refetch && part.state != Part::State::Loaded)
            {
                return false;
            }
        }

        return true;
    }

    bool Controller::IsEarliestIncomplete(Segment const& segment) const
    {
        for (auto const& [timestamp, other] : m_segments)
        {
            if (!other.delivered && !AllLoaded(other))
            {
                return timestamp == segment.timestampMs;
            }
        }

        return true;
    }

    size_t Controller::IncompleteCount() const
    {
        size_t count = 0;

        for (auto const& [timestamp, segment] : m_segments)
        {
            if (!segment.delivered && !AllLoaded(segment))
            {
                count++;
            }
        }

        return count;
    }

    int64_t Controller::BufferedFrom(int64_t timestampMs) const
    {
        int64_t buffered = 0;

        for (auto it = m_segments.lower_bound(timestampMs); it != m_segments.end() && AllLoaded(it->second); ++it)
        {
            buffered += kPartMs;
        }

        return buffered;
    }

    int64_t Controller::BufferAheadMs(int64_t now) const
    {
        int64_t position;

        if (m_format == StreamFormat::Unified)
        {
            position = m_playerStarted ? PositionMs(now) : m_startPositionMs;
        }
        else if (m_playback == Playback::Buffering)
        {
            position = m_startPositionMs;
        }
        else if (m_playback == Playback::Stalled || m_plays.empty())
        {
            return BufferedFrom(m_stallTimestampMs);
        }
        else
        {
            auto const& play = CurrentPlay(now);
            int64_t elapsed = std::clamp<int64_t>(play.offsetMs + (now - play.hostUs) / 1000, 0, kPartMs);
            return std::max<int64_t>(0, BufferedFrom(play.timestampMs) - elapsed);
        }

        int64_t first = AlignDown(position);
        return std::max<int64_t>(0, BufferedFrom(first) - (position - first));
    }

    int64_t Controller::PlayheadTs(int64_t now) const
    {
        if (m_format == StreamFormat::Unified)
        {
            return AlignDown(m_playerStarted ? PositionMs(now) : m_startPositionMs);
        }
        else if (m_playback == Playback::Buffering)
        {
            return AlignDown(m_startPositionMs);
        }
        else if (m_playback == Playback::Stalled || m_plays.empty())
        {
            return m_stallTimestampMs;
        }

        return CurrentPlay(now).timestampMs;
    }

    int64_t Controller::LastSegmentEnd() const
    {
        int64_t from = m_format == StreamFormat::Unified && m_jumpPending ? AlignDown(m_jumpPositionMs) : INT64_MIN;

        auto last = m_segments.rbegin();
        if (last != m_segments.rend() && last->first >= from)
        {
            return last->first + kPartMs;
        }
        else if (from != INT64_MIN)
        {
            return from;
        }
        else if (m_format == StreamFormat::Unified)
        {
            return m_playerStarted ? m_givenEndMs : AlignDown(m_startPositionMs);
        }
        else if (m_playback == Playback::Stalled)
        {
            return m_stallTimestampMs;
        }
        else if (m_plays.size())
        {
            return m_plays.back().timestampMs + kPartMs;
        }

        return AlignDown(m_startPositionMs);
    }

    void Controller::Answer(uint64_t requestId, PartAnswer&& answer)
    {
        auto request = m_requests.find(requestId);
        if (request == m_requests.end())
        {
            return;
        }

        int64_t timestamp = request->second;
        m_requests.erase(request);

        Segment* segment = FindSegment(timestamp);
        if (segment == nullptr)
        {
            return;
        }

        Part* part = FindPart(*segment, requestId);
        if (part == nullptr || part->state != Part::State::Loading)
        {
            return;
        }

        int64_t now = Now();

        // An answer without data costs about one round trip, so alone on the link it measures time to first byte.
        if (answer.status != AnswerStatus::Data && m_requests.empty())
        {
            m_estimator->SampleTtfb((now - part->issuedUs) / 1000.0);
        }

        switch (answer.status)
        {
        case AnswerStatus::Data:
            OnData(*segment, *part, std::move(answer));
            break;
        case AnswerStatus::NotReady:
        {
            part->state = Part::State::Retrying;

            uint64_t generation = ++part->retryGeneration;
            Schedule(kRetryUs, [this, timestamp, requestId, generation]
            {
                if (Segment* retrying = FindSegment(timestamp))
                {
                    Part* retry = FindPart(*retrying, requestId);
                    if (retry && retry->state == Part::State::Retrying && retry->retryGeneration == generation)
                    {
                        IssuePart(*retrying, *retry);
                    }
                }
            });
            break;
        }
        case AnswerStatus::ResyncNeeded:
            Resync(answer.responseTimestampMs);
            break;
        case AnswerStatus::RejoinNeeded:
            RejoinNeeded();
            break;
        }
    }

    void Controller::OnData(Segment& segment, Part& part, PartAnswer&& answer)
    {
        int64_t now = Now();

        // Listener edges are the server's time, which every answer carries. A unified edge is the stream's last
        // part, which an answer does not tell.
        if (m_format == StreamFormat::Listener && answer.responseTimestampMs > 0)
        {
            m_edgeMs = answer.responseTimestampMs;
            m_edgeHostUs = now;
        }

        part.state = Part::State::Loaded;
        part.data = std::move(answer.data);
        part.bytes = part.data ? part.data->size() : 0;

        if (part.request.kind != PartKind::Audio)
        {
            LearnBitrate(part.request.kind, part.request.quality, part.bytes);
        }

        if (part.refetch)
        {
            LoadedPart loaded{ part.request, part.data };

            // The refetched part becomes the segment's record of the quality it holds.
            auto& video = segment.video;
            for (auto it = video.begin(); it != video.end();)
            {
                if (!it->refetch && it->request.endpointId == loaded.request.endpointId)
                {
                    it = video.erase(it);
                }
                else
                {
                    if (it->request.id == loaded.request.id)
                    {
                        it->refetch = false;
                        it->data.reset();
                    }

                    ++it;
                }
            }

            m_host.replaceVideo(segment.timestampMs, loaded);
            return;
        }

        if (m_format == StreamFormat::Listener && &part == &segment.primary)
        {
            segment.audioLanded = true;
            segment.durationMs = answer.durationMs > 0 ? answer.durationMs : static_cast<int32_t>(kPartMs);
            segment.channelMap = std::move(answer.videoChannels);

            if (segment.timestampMs >= m_newestChannelMapTimestampMs)
            {
                m_newestChannelMap = segment.channelMap;
                m_newestChannelMapTimestampMs = segment.timestampMs;
            }

            CorrectVideo(segment);
        }

        if (AllLoaded(segment))
        {
            OnSegmentLoaded(segment);
        }
    }

    void Controller::CorrectVideo(Segment& segment)
    {
        // A video channel id is only valid for the part whose audio map gave it.
        std::vector<std::string> pending;
        auto& video = segment.video;

        for (auto it = video.begin(); it != video.end();)
        {
            if (it->refetch)
            {
                ++it;
                continue;
            }

            auto mapped = segment.channelMap.find(it->request.endpointId);
            if (mapped == segment.channelMap.end() || RequestedChannel(it->request.endpointId) == nullptr)
            {
                CancelPart(*it);
                it = video.erase(it);
                continue;
            }
            else if (it->request.channelId != mapped->second)
            {
                CancelPart(*it);
                it->request.channelId = mapped->second;
                pending.push_back(it->request.endpointId);
            }

            ++it;
        }

        for (auto const& channel : m_videoChannels)
        {
            auto mapped = segment.channelMap.find(channel.endpointId);
            if (mapped == segment.channelMap.end())
            {
                continue;
            }

            bool present = std::any_of(video.begin(), video.end(), [&](Part const& part)
            {
                return !part.refetch && part.request.endpointId == channel.endpointId;
            });

            if (!present)
            {
                video.push_back(MakeVideoPart(segment.timestampMs, mapped->second, channel.endpointId));
                pending.push_back(channel.endpointId);
            }
        }

        for (auto& part : video)
        {
            if (!part.refetch && std::find(pending.begin(), pending.end(), part.request.endpointId) != pending.end())
            {
                IssuePart(segment, part);
            }
        }
    }

    void Controller::OnSegmentLoaded(Segment& segment)
    {
        int64_t now = Now();

        if (!segment.sampled)
        {
            segment.sampled = true;
            SampleSegment(segment, now);
        }

        m_lastSegmentLoadEndUs = now;

        for (auto& [timestamp, other] : m_segments)
        {
            if (other.delivered || AllLoaded(other))
            {
                continue;
            }

            if (other.primary.watchFromUs == kNotWatched)
            {
                other.primary.watchFromUs = now;
            }

            for (auto& part : other.video)
            {
                if (part.watchFromUs == kNotWatched)
                {
                    part.watchFromUs = now;
                }
            }

            break;
        }

        Continue();
    }

    void Controller::SampleSegment(Segment const& segment, int64_t now)
    {
        double bytes = 0;
        int64_t start = INT64_MAX;
        bool idle = false;

        auto add = [&](Part const& part)
        {
            if (part.refetch)
            {
                return;
            }

            bytes += part.bytes;

            if (part.issuedUs < start)
            {
                start = part.issuedUs;
                idle = part.issuedOnIdleLink;
            }
        };

        add(segment.primary);

        for (auto const& part : segment.video)
        {
            add(part);
        }

        if (bytes <= 0 || start == INT64_MAX)
        {
            return;
        }

        // Two segments load at once, so a segment's busy time starts when the one before it finished.
        double durationMs;
        if (m_lastSegmentLoadEndUs > start)
        {
            durationMs = (now - m_lastSegmentLoadEndUs) / 1000.0;
        }
        else
        {
            durationMs = (now - start) / 1000.0;

            if (idle)
            {
                durationMs -= m_estimator->TtfbMs();
            }
        }

        m_estimator->Sample(durationMs, bytes);
    }

    void Controller::Deliver(Segment& segment)
    {
        DeliveredSegment delivered;
        delivered.timestampMs = segment.timestampMs;
        delivered.primary = { segment.primary.request, segment.primary.data };

        segment.hasVideo = false;

        for (auto& part : segment.video)
        {
            if (!part.refetch && part.data && part.data->size())
            {
                delivered.video.push_back({ part.request, part.data });
                segment.hasVideo = true;
            }
        }

        segment.delivered = true;
        segment.primary.data.reset();

        for (auto& part : segment.video)
        {
            if (!part.refetch)
            {
                part.data.reset();
            }
        }

        m_givenEndMs = segment.timestampMs + kPartMs;
        m_host.deliver(delivered);
    }

    void Controller::Resync(int64_t responseTimestampMs)
    {
        if (m_waitingRejoin || m_waitingEdge)
        {
            return;
        }

        CancelIncomplete();

        if (m_format == StreamFormat::Unified)
        {
            RequestEdge();
            return;
        }

        int64_t now = Now();
        int64_t edge = responseTimestampMs > 0 ? responseTimestampMs : EdgeNowMs(now);

        if (LiveSyncPosition(edge) <= 0)
        {
            RequestEdge();
            return;
        }

        m_edgeMs = edge;
        m_edgeHostUs = now;
        Reanchor(edge);
    }

    void Controller::RejoinNeeded()
    {
        if (m_waitingRejoin)
        {
            return;
        }

        CancelIncomplete();

        m_waitingRejoin = true;
        m_rejoinStartUs = Now();

        // What is loaded plays on through the rejoin.
        Continue();
        m_host.requestRejoin();
    }

    void Controller::Rejoined()
    {
        if (!m_started || !m_waitingRejoin)
        {
            return;
        }

        m_waitingRejoin = false;

        if (Now() - m_rejoinStartUs > kWindowMs * 1000)
        {
            // The parts after the last ready one have left the server's window.
            RequestEdge();
            return;
        }

        m_nextTimestampMs = LastSegmentEnd();
        Pump();
    }

    void Controller::TryDeliver()
    {
        bool delivered = false;

        for (auto& [timestamp, segment] : m_segments)
        {
            if (segment.delivered)
            {
                continue;
            }
            else if (!AllLoaded(segment))
            {
                break;
            }

            Deliver(segment);
            delivered = true;
        }

        if (delivered)
        {
            AdvanceListener();
        }
    }

    void Controller::AdvanceListener()
    {
        int64_t now = Now();
        bool changed = false;

        if (m_playback == Playback::Buffering)
        {
            int64_t first = AlignDown(m_startPositionMs);
            int32_t offset = static_cast<int32_t>(m_startPositionMs - first);

            auto segment = m_segments.find(first);
            if (segment == m_segments.end() || !segment->second.delivered)
            {
                return;
            }
            else if (BufferedFrom(first) - offset < kListenerStartBufferMs)
            {
                return;
            }

            m_playback = Playback::Playing;
            m_firstPlayDone = true;
            SendPlay(first, now, offset);
            changed = true;
        }
        else if (m_playback == Playback::Stalled)
        {
            auto segment = m_segments.lower_bound(m_stallTimestampMs);
            if (segment == m_segments.end() || !segment->second.delivered)
            {
                return;
            }
            else if (BufferedFrom(segment->first) < kResumeBufferMs)
            {
                return;
            }

            m_playback = Playback::Playing;
            SendPlay(segment->first, now, 0);
            changed = true;
        }

        while (m_plays.size())
        {
            PlayRecord last = m_plays.back();

            auto current = m_segments.find(last.timestampMs);
            if (current == m_segments.end())
            {
                break;
            }

            auto next = std::next(current);
            if (next == m_segments.end() || !next->second.delivered)
            {
                break;
            }

            // A segment with video lasts its full second. Without video it ends when its audio has played and the
            // next one is ready, a second at the latest.
            int64_t length = current->second.hasVideo ? kPartMs : current->second.durationMs;
            int64_t earliest = last.hostUs + (length - last.offsetMs) * 1000;
            int64_t latest = PlayEndUs(last);
            int64_t start = std::max(earliest, now);

            if (start > latest)
            {
                Stall();
                AdvanceListener();
                return;
            }

            SendPlay(next->first, start, 0);
            changed = true;
        }

        if (changed)
        {
            ScheduleStallCheck(PlayEndUs(m_plays.back()));
        }
    }

    Controller::PlayRecord const& Controller::CurrentPlay(int64_t now) const
    {
        for (auto it = m_plays.rbegin(); it != m_plays.rend(); ++it)
        {
            if (it->hostUs <= now)
            {
                return *it;
            }
        }

        return m_plays.front();
    }

    int64_t Controller::PlayEndUs(PlayRecord const& play) const
    {
        int64_t length = kPartMs;

        auto segment = m_segments.find(play.timestampMs);
        if (segment != m_segments.end())
        {
            length = std::max<int64_t>(length, segment->second.durationMs);
        }

        return play.hostUs + (length - play.offsetMs) * 1000;
    }

    void Controller::SendPlay(int64_t timestampMs, int64_t hostUs, int32_t offsetMs)
    {
        m_plays.push_back({ timestampMs, hostUs, offsetMs });
        m_host.play(timestampMs, hostUs, offsetMs);
    }

    void Controller::ScheduleStallCheck(int64_t atUs)
    {
        uint64_t generation = ++m_stallGeneration;
        Schedule(atUs - Now(), [this, generation]
        {
            if (generation != m_stallGeneration || m_playback != Playback::Playing)
            {
                return;
            }

            AdvanceListener();

            if (generation == m_stallGeneration && m_playback == Playback::Playing)
            {
                Stall();
            }
        });
    }

    void Controller::Stall()
    {
        int64_t awaited = m_nextTimestampMs;

        if (m_plays.size())
        {
            int64_t last = m_plays.back().timestampMs;
            auto next = m_segments.upper_bound(last);
            awaited = next == m_segments.end() ? last + kPartMs : next->first;
        }

        m_playback = Playback::Stalled;
        m_stallTimestampMs = awaited;
        m_plays.clear();
        m_stallGeneration++;

        // No catch-up: the latency a stall adds stays, and later anchors keep it.
        m_targetLatencyMs = kTargetLatencyMs + kStallLatencyIncreaseMs;

        Pump();
    }

    void Controller::FeedPlayer()
    {
        int64_t now = Now();

        if (!m_playerStarted || m_jumpPending)
        {
            if (!m_anchored)
            {
                return;
            }

            int64_t position = m_jumpPending ? m_jumpPositionMs : m_startPositionMs;
            int64_t first = AlignDown(position);

            if (BufferedFrom(first) - (position - first) < kUnifiedStartBufferMs)
            {
                return;
            }

            for (auto it = m_segments.lower_bound(first); it != m_segments.end() && AllLoaded(it->second); ++it)
            {
                if (!it->second.delivered)
                {
                    Deliver(it->second);
                }
            }

            m_playerStarted = true;
            m_jumpPending = false;
            m_holding = false;
            m_firstPlayDone = true;
            m_playerReportedPlaying = false;
            m_playerPlaying = false;
            m_playerPositionMs = position;
            m_playerPositionHostUs = now;
            m_host.setUnifiedPosition(position);
            return;
        }

        if (m_holding)
        {
            if (BufferAheadMs(now) < kResumeBufferMs)
            {
                return;
            }

            m_holding = false;
        }

        for (auto it = m_segments.lower_bound(m_givenEndMs); it != m_segments.end() && AllLoaded(it->second); ++it)
        {
            if (!it->second.delivered)
            {
                Deliver(it->second);
            }
        }
    }

    int64_t Controller::PositionMs(int64_t now) const
    {
        return m_playerPositionMs + (m_playerPlaying ? (now - m_playerPositionHostUs) / 1000 : 0);
    }

    void Controller::UpdatePlayback(int64_t positionMs, bool isPlaying)
    {
        if (!m_started || m_format != StreamFormat::Unified || !m_playerStarted)
        {
            return;
        }

        m_playerPositionMs = positionMs;
        m_playerPositionHostUs = Now();
        m_playerPlaying = isPlaying;

        if (isPlaying)
        {
            m_playerReportedPlaying = true;
        }
        else if (m_playerReportedPlaying && !m_holding)
        {
            // Asked to play and not playing: a stall. Parts are held back until it can resume with a buffer.
            m_holding = true;
            m_targetLatencyMs = kTargetLatencyMs + kStallLatencyIncreaseMs;
        }

        FeedPlayer();
        Pump();
    }

    void Controller::Prune(int64_t now)
    {
        int64_t before;

        if (m_format == StreamFormat::Unified)
        {
            if (!m_playerStarted)
            {
                return;
            }

            before = AlignDown(PositionMs(now));
        }
        else
        {
            if (m_playback == Playback::Buffering)
            {
                return;
            }

            while (m_plays.size() > 1 && m_plays[1].hostUs <= now)
            {
                m_plays.pop_front();
            }

            before = PlayheadTs(now);
        }

        for (auto it = m_segments.begin(); it != m_segments.end() && it->first < before;)
        {
            if (m_format == StreamFormat::Unified && !it->second.delivered)
            {
                ++it;
                continue;
            }

            CancelSegment(it->second);
            it = m_segments.erase(it);
        }
    }

    void Controller::SetRequestedVideoChannels(std::vector<VideoChannelRequest> channels)
    {
        if (m_format != StreamFormat::Listener)
        {
            return;
        }

        auto previous = std::move(m_videoChannels);
        m_videoChannels = std::move(channels);

        if (!m_started || !m_anchored)
        {
            return;
        }

        int64_t now = Now();
        RecomputeLevels(now);

        for (auto& [timestamp, segment] : m_segments)
        {
            auto& video = segment.video;
            for (auto it = video.begin(); it != video.end();)
            {
                if (RequestedChannel(it->request.endpointId) || (segment.delivered && !it->refetch))
                {
                    ++it;
                }
                else
                {
                    CancelPart(*it);
                    it = video.erase(it);
                }
            }
        }

        // A lowered ceiling keeps what is buffered; a raised one fetches the buffer again.
        int64_t playing = PlayheadTs(now);

        for (auto const& channel : m_videoChannels)
        {
            auto old = std::find_if(previous.begin(), previous.end(), [&](VideoChannelRequest const& request)
            {
                return request.endpointId == channel.endpointId;
            });

            if (old == previous.end() || channel.maxQuality > old->maxQuality)
            {
                RefetchRaised(channel.endpointId, playing);
            }
        }

        Continue();
    }

    void Controller::RefetchRaised(std::string const& endpointId, int64_t playingTs)
    {
        VideoQuality level = LevelFor(endpointId);

        auto sameEndpoint = [&](Part const& part)
        {
            return !part.refetch && part.request.endpointId == endpointId;
        };

        for (auto& [timestamp, segment] : m_segments)
        {
            auto& video = segment.video;

            if (!segment.delivered)
            {
                // Without a map the part is added by CorrectVideo when the audio lands.
                if (!segment.audioLanded && !segment.issuedWithMap)
                {
                    continue;
                }

                auto const& map = segment.audioLanded ? segment.channelMap : m_newestChannelMap;
                auto mapped = map.find(endpointId);
                if (mapped == map.end())
                {
                    continue;
                }

                auto existing = std::find_if(video.begin(), video.end(), sameEndpoint);
                if (existing == video.end())
                {
                    video.push_back(MakeVideoPart(timestamp, mapped->second, endpointId));
                    IssuePart(segment, video.back());
                }
                else if (existing->request.quality < level)
                {
                    CancelPart(*existing);
                    existing->request.quality = level;
                    IssuePart(segment, *existing);
                }

                continue;
            }
            else if (timestamp <= playingTs)
            {
                continue;
            }

            auto mapped = segment.channelMap.find(endpointId);
            if (mapped == segment.channelMap.end())
            {
                continue;
            }

            auto held = std::find_if(video.begin(), video.end(), sameEndpoint);
            if (held != video.end() && held->request.quality >= level)
            {
                continue;
            }

            for (auto it = video.begin(); it != video.end();)
            {
                if (it->refetch && it->request.endpointId == endpointId)
                {
                    CancelPart(*it);
                    it = video.erase(it);
                }
                else
                {
                    ++it;
                }
            }

            Part part = MakeVideoPart(timestamp, mapped->second, endpointId);
            part.refetch = true;
            video.push_back(std::move(part));
            IssuePart(segment, video.back());
        }
    }

    void Controller::RecomputeLevels(int64_t now)
    {
        std::vector<ChannelChoice> channels;

        if (m_format == StreamFormat::Listener)
        {
            channels.reserve(m_videoChannels.size());

            for (auto const& request : m_videoChannels)
            {
                VideoQuality floor = std::min(request.minQuality, request.maxQuality);
                channels.push_back({ request.endpointId, floor, request.maxQuality, request.maxQuality });
            }
        }
        else
        {
            // Streams have no thumbnail level.
            channels.push_back({ std::string(), VideoQuality::Medium, VideoQuality::Full, VideoQuality::Full });
        }

        ChooseLevels(channels, now);

        m_currentLevels.clear();

        for (auto const& channel : channels)
        {
            m_currentLevels[channel.endpointId] = channel.level;
        }
    }

    void Controller::ChooseLevels(std::vector<ChannelChoice>& channels, int64_t now) const
    {
        if (channels.empty())
        {
            return;
        }
        else if (!m_estimator->CanEstimate())
        {
            for (auto& channel : channels)
            {
                channel.level = channel.ceiling;
            }

            return;
        }

        PartKind kind = m_format == StreamFormat::Listener ? PartKind::Video : PartKind::Unified;
        double bandwidth = m_estimator->Estimate();
        double ttfb = m_estimator->TtfbMs() / 1000;
        double buffer = BufferAheadMs(now) / 1000.0;
        double audio = m_format == StreamFormat::Listener ? kAudioBitrate : 0;

        auto fits = [&](double budget)
        {
            double total = audio;
            bool up = false;

            for (auto const& channel : channels)
            {
                total += LevelBitrate(kind, channel.level);

                auto current = m_currentLevels.find(channel.endpointId);
                if (current != m_currentLevels.end() && channel.level > current->second)
                {
                    up = true;
                }
            }

            double adjusted = bandwidth * (up ? kBwUpFactor : kBwFactor);
            return adjusted > total && total / adjusted + ttfb <= budget;
        };

        // The first pass must fit in the buffer, the second may starve it for a second.
        for (double budget : { buffer, buffer + 1.0 })
        {
            for (auto& channel : channels)
            {
                channel.level = channel.ceiling;
            }

            while (true)
            {
                if (fits(budget))
                {
                    return;
                }

                // The smallest tiles give way first, the focused one last.
                ChannelChoice* step = nullptr;

                for (auto& channel : channels)
                {
                    if (channel.level <= channel.floor)
                    {
                        continue;
                    }
                    else if (step == nullptr || channel.ceiling < step->ceiling || (channel.ceiling == step->ceiling && channel.level >= step->level))
                    {
                        step = &channel;
                    }
                }

                if (step == nullptr)
                {
                    break;
                }

                step->level = static_cast<VideoQuality>(static_cast<int32_t>(step->level) - 1);
            }
        }

        double floorsCost = 0;
        double currentCost = 0;

        for (auto& channel : channels)
        {
            auto current = m_currentLevels.find(channel.endpointId);
            channel.level = current == m_currentLevels.end() ? channel.floor : std::clamp(current->second, channel.floor, channel.ceiling);

            floorsCost += LevelBitrate(kind, channel.floor);
            currentCost += LevelBitrate(kind, channel.level);
        }

        if (floorsCost < currentCost)
        {
            for (auto& channel : channels)
            {
                channel.level = channel.floor;
            }
        }
    }

    VideoQuality Controller::LevelFor(std::string const& endpointId) const
    {
        auto current = m_currentLevels.find(endpointId);
        if (current != m_currentLevels.end())
        {
            return current->second;
        }
        else if (auto request = RequestedChannel(endpointId))
        {
            return request->maxQuality;
        }

        return VideoQuality::Full;
    }

    VideoQuality Controller::FloorFor(Part const& part) const
    {
        if (part.request.kind == PartKind::Unified)
        {
            return VideoQuality::Medium;
        }
        else if (auto request = RequestedChannel(part.request.endpointId))
        {
            return std::min(request->minQuality, request->maxQuality);
        }

        return VideoQuality::Thumbnail;
    }

    VideoChannelRequest const* Controller::RequestedChannel(std::string const& endpointId) const
    {
        for (auto const& channel : m_videoChannels)
        {
            if (channel.endpointId == endpointId)
            {
                return &channel;
            }
        }

        return nullptr;
    }

    double Controller::LevelBitrate(PartKind kind, VideoQuality quality) const
    {
        if (kind == PartKind::Audio)
        {
            return kAudioBitrate;
        }

        auto learned = m_bitrates.find(BitrateKey(kind, quality));
        if (learned != m_bitrates.end() && learned->second.size())
        {
            double sum = 0;

            for (double bitrate : learned->second)
            {
                sum += bitrate;
            }

            return sum / learned->second.size();
        }

        return SeedBitrate(quality);
    }

    void Controller::LearnBitrate(PartKind kind, VideoQuality quality, size_t bytes)
    {
        if (bytes == 0)
        {
            return;
        }

        // A part is a second, so its bits are its bitrate.
        auto& samples = m_bitrates[BitrateKey(kind, quality)];
        samples.push_back(bytes * 8.0);

        if (samples.size() > kBitrateSamples)
        {
            samples.pop_front();
        }
    }

    void Controller::ScheduleAbandonCheck()
    {
        if (m_abandonScheduled)
        {
            return;
        }

        m_abandonScheduled = true;
        Schedule(kAbandonCheckUs, [this]
        {
            m_abandonScheduled = false;

            if (m_started)
            {
                AbandonCheck();

                if (m_requests.size())
                {
                    ScheduleAbandonCheck();
                }
            }
        });
    }

    void Controller::AbandonCheck()
    {
        // hls.js's _abandonRulesCheck with time alone: the client sees no bytes until a part is complete.
        if (!m_firstPlayDone || !m_estimator->CanEstimate())
        {
            return;
        }

        int64_t now = Now();
        double buffer = BufferAheadMs(now) / 1000.0;

        auto check = [&](Segment& segment, Part& part)
        {
            if (part.refetch || part.state != Part::State::Loading || part.request.kind == PartKind::Audio)
            {
                return;
            }
            else if (part.watchFromUs == kNotWatched || now - part.watchFromUs < kAbandonWatchUs)
            {
                return;
            }

            PartKind kind = part.request.kind;
            double bandwidth = m_estimator->Estimate();
            double ttfb = m_estimator->TtfbMs() / 1000;
            double loadedDelay = ttfb + LevelBitrate(kind, part.request.quality) / bandwidth;

            if (loadedDelay <= buffer)
            {
                return;
            }

            int32_t floor = static_cast<int32_t>(FloorFor(part));
            int32_t next = -1;
            double nextDelay = std::numeric_limits<double>::infinity();

            for (int32_t level = static_cast<int32_t>(part.request.quality) - 1; level >= floor; level--)
            {
                next = level;
                nextDelay = ttfb + LevelBitrate(kind, static_cast<VideoQuality>(level)) / bandwidth;

                if (nextDelay < std::min(buffer, 1.0 + ttfb))
                {
                    break;
                }
            }

            if (next < 0 || nextDelay >= loadedDelay || nextDelay > 10.0)
            {
                return;
            }

            auto nextLevel = static_cast<VideoQuality>(next);
            double nextBitrate = LevelBitrate(kind, nextLevel);

            if (bandwidth * kBwUpFactor > nextBitrate)
            {
                m_estimator->Reset(nextBitrate);
            }

            auto current = m_currentLevels.find(part.request.endpointId);
            if (current == m_currentLevels.end())
            {
                m_currentLevels.emplace(part.request.endpointId, nextLevel);
            }
            else
            {
                current->second = std::min(current->second, nextLevel);
            }

            CancelPart(part);
            part.request.quality = nextLevel;
            IssuePart(segment, part);
        };

        for (auto& [timestamp, segment] : m_segments)
        {
            if (segment.delivered)
            {
                continue;
            }

            check(segment, segment.primary);

            for (auto& part : segment.video)
            {
                check(segment, part);
            }
        }
    }
}
