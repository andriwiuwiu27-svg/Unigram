#pragma once

#include <cstdint>
#include <deque>
#include <functional>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

// The playback controller for group-call broadcast: what to fetch and when, when each segment plays, and at which
// quality. Its rules are hls.js 1.7.3's wherever hls.js has one (live position, EWMA bandwidth, level choice,
// abandon), so anything summarised here can be checked against hls.js's source.
//
// Every method and every host callback runs on one thread, the controller's, and no host callback may answer
// synchronously. It never parses parts: the client hands it each listener audio part's duration and channel map
// with the answer.
namespace broadcast
{
    enum class StreamFormat
    {
        // A large group call or an RTC live story: an Ogg Opus audio part a second, plus a video part per watched
        // participant.
        Listener,
        // An RTMP stream or RTMP live story: an mp4 slice (video and AAC) a second.
        Unified
    };

    enum class VideoQuality : int32_t
    {
        Thumbnail = 0,
        Medium = 1,
        Full = 2
    };

    struct VideoChannelRequest
    {
        std::string endpointId;
        VideoQuality minQuality = VideoQuality::Thumbnail;
        VideoQuality maxQuality = VideoQuality::Full;
    };

    enum class PartKind
    {
        Audio,
        Video,
        Unified
    };

    struct PartRequest
    {
        uint64_t id = 0;
        PartKind kind = PartKind::Audio;
        int64_t timestampMs = 0;
        // 0 for listener audio, the audio part's map for listener video, 1 for unified parts.
        int32_t channelId = 0;
        VideoQuality quality = VideoQuality::Full;
        std::string endpointId;
    };

    enum class AnswerStatus
    {
        Data,
        NotReady,
        ResyncNeeded,
        RejoinNeeded
    };

    using Bytes = std::shared_ptr<std::vector<uint8_t> const>;

    struct PartAnswer
    {
        AnswerStatus status = AnswerStatus::NotReady;
        // The server's time when it answered, in milliseconds on every path.
        int64_t responseTimestampMs = 0;
        Bytes data;
        // Listener audio only, read from the Ogg metadata without decoding.
        int32_t durationMs = 0;
        std::map<std::string, int32_t> videoChannels;
    };

    struct LoadedPart
    {
        PartRequest request;
        Bytes data;
    };

    struct DeliveredSegment
    {
        int64_t timestampMs = 0;
        LoadedPart primary;
        std::vector<LoadedPart> video;
    };

    // hls.js's EwmaBandWidthEstimator (abrEwmaFastLive 3, abrEwmaSlowLive 9, abrEwmaDefaultEstimate 500 kbit/s),
    // shared by every controller of the process: a new call starts from what the last one learned.
    class BandwidthEstimator
    {
    public:
        static std::shared_ptr<BandwidthEstimator> Shared();

        explicit BandwidthEstimator(double defaultEstimateBps = 500000);

        void Sample(double durationMs, double bytes);
        void SampleTtfb(double ttfbMs);
        bool CanEstimate();
        double Estimate();
        double TtfbMs();
        // hls.js's resetEstimator: every sample is forgotten and `defaultEstimateBps` becomes the default.
        void Reset(double defaultEstimateBps);

    private:
        class Ewma
        {
        public:
            explicit Ewma(double halfLife);

            void Sample(double weight, double value);
            double TotalWeight() const;
            double Estimate() const;

        private:
            double m_alpha;
            double m_estimate = 0;
            double m_totalWeight = 0;
        };

        bool CanEstimateLocked() const;

        std::mutex m_lock;
        double m_defaultEstimate;
        Ewma m_fast;
        Ewma m_slow;
        Ewma m_ttfb;
    };

    struct ControllerHost
    {
        // The monotonic host clock in microseconds: the audio core's clock.
        std::function<int64_t()> nowUs;
        // Runs `task` on the controller's thread after `delayUs`.
        std::function<void(int64_t delayUs, std::function<void()> task)> schedule;
        // Listener: the server's time in ms. Unified: last_timestamp_ms + 1000, or the server time if that failed.
        // 0 when there is nothing to play yet. Answered through Controller::LiveEdge.
        std::function<void()> requestLiveEdge;
        // Answered through Controller::Answer with the request's id.
        std::function<void(PartRequest const&)> loadPart;
        std::function<void(std::vector<uint64_t> const&)> cancelParts;
        // All of a segment's parts are loaded: decode them (listener) or append them to the player (unified).
        std::function<void(DeliveredSegment const&)> deliver;
        // A video part of a delivered segment was fetched again at a higher quality.
        std::function<void(int64_t timestampMs, LoadedPart const&)> replaceVideo;
        // Listener: the playhead enters the segment at `hostTimeUs`, `offsetMs` into it.
        std::function<void(int64_t timestampMs, int64_t hostTimeUs, int32_t offsetMs)> play;
        // Unified: start or jump the player. Parts before the position are dropped.
        std::function<void(int64_t positionMs)> setUnifiedPosition;
        // The call must rejoin, then call Controller::Rejoined.
        std::function<void()> requestRejoin;
    };

    class Controller
    {
    public:
        Controller(StreamFormat format, ControllerHost host, std::shared_ptr<BandwidthEstimator> estimator);
        ~Controller();

        Controller(Controller const&) = delete;
        Controller& operator=(Controller const&) = delete;

        void Start();
        void Stop();

        void LiveEdge(int64_t edgeMs);
        void Answer(uint64_t requestId, PartAnswer&& answer);
        void SetRequestedVideoChannels(std::vector<VideoChannelRequest> channels);
        void Rejoined();
        // Unified: the player's report.
        void UpdatePlayback(int64_t positionMs, bool isPlaying);

    private:
        struct Part
        {
            enum class State
            {
                Loading,
                Retrying,
                Loaded
            };

            PartRequest request;
            State state = State::Loading;
            Bytes data;
            size_t bytes = 0;
            int64_t issuedUs = 0;
            // The abandon rule watches a part from when the segment before it finished loading.
            int64_t watchFromUs = 0;
            bool issuedOnIdleLink = false;
            // Fetched again for a segment already delivered.
            bool refetch = false;
            uint64_t retryGeneration = 0;
        };

        struct Segment
        {
            int64_t timestampMs = 0;
            Part primary;
            std::vector<Part> video;
            // Listener: video was issued with the newest known channel map, before this segment's audio landed.
            bool issuedWithMap = false;
            bool audioLanded = false;
            int32_t durationMs = 1000;
            std::map<std::string, int32_t> channelMap;
            bool sampled = false;
            bool delivered = false;
            bool hasVideo = false;
        };

        struct PlayRecord
        {
            int64_t timestampMs = 0;
            int64_t hostUs = 0;
            int32_t offsetMs = 0;
        };

        enum class Playback
        {
            Buffering,
            Playing,
            Stalled
        };

        struct ChannelChoice
        {
            std::string endpointId;
            VideoQuality floor;
            VideoQuality ceiling;
            VideoQuality level;
        };

        void ResetState();
        int64_t Now() const;
        void Schedule(int64_t delayUs, std::function<void()> task);
        int64_t EdgeNowMs(int64_t now) const;
        int64_t LiveSyncPosition(int64_t edgeMs) const;

        void RequestEdge();
        void Anchor(int64_t edgeMs);
        void Reanchor(int64_t edgeMs);

        void Pump();
        void SchedulePump(int64_t delayMs);
        void IssueSegment(int64_t timestampMs);
        Part MakeVideoPart(int64_t timestampMs, int32_t channelId, std::string const& endpointId);
        void IssuePart(Segment& segment, Part& part);
        void CancelPart(Part& part);
        void CancelSegment(Segment& segment);
        void CancelIncomplete();
        void DropUndelivered();

        Segment* FindSegment(int64_t timestampMs);
        Part* FindPart(Segment& segment, uint64_t requestId);
        bool AllLoaded(Segment const& segment) const;
        bool IsEarliestIncomplete(Segment const& segment) const;
        size_t IncompleteCount() const;
        int64_t BufferedFrom(int64_t timestampMs) const;
        int64_t BufferAheadMs(int64_t now) const;
        int64_t PlayheadTs(int64_t now) const;
        int64_t LastSegmentEnd() const;

        void OnData(Segment& segment, Part& part, PartAnswer&& answer);
        void CorrectVideo(Segment& segment);
        void OnSegmentLoaded(Segment& segment);
        void SampleSegment(Segment const& segment, int64_t now);
        void Deliver(Segment& segment);
        void Resync(int64_t responseTimestampMs);
        void RejoinNeeded();
        void Continue();

        // Listener.
        void TryDeliver();
        void AdvanceListener();
        PlayRecord const& CurrentPlay(int64_t now) const;
        int64_t PlayEndUs(PlayRecord const& play) const;
        void SendPlay(int64_t timestampMs, int64_t hostUs, int32_t offsetMs);
        void ScheduleStallCheck(int64_t atUs);
        void Stall();

        // Unified.
        void FeedPlayer();
        int64_t PositionMs(int64_t now) const;

        void Prune(int64_t now);

        void RecomputeLevels(int64_t now);
        void ChooseLevels(std::vector<ChannelChoice>& channels, int64_t now) const;
        void RefetchRaised(std::string const& endpointId, int64_t playingTs);
        VideoQuality LevelFor(std::string const& endpointId) const;
        VideoQuality FloorFor(Part const& part) const;
        VideoChannelRequest const* RequestedChannel(std::string const& endpointId) const;
        double LevelBitrate(PartKind kind, VideoQuality quality) const;
        void LearnBitrate(PartKind kind, VideoQuality quality, size_t bytes);

        void ScheduleAbandonCheck();
        void AbandonCheck();

        StreamFormat m_format;
        ControllerHost m_host;
        std::shared_ptr<BandwidthEstimator> m_estimator;
        // Replaced on Stop, so every task scheduled before it is dropped.
        std::shared_ptr<bool> m_alive;

        bool m_started = false;
        bool m_anchored = false;
        bool m_waitingEdge = false;
        bool m_waitingRejoin = false;
        int64_t m_rejoinStartUs = 0;

        int64_t m_edgeMs = 0;
        int64_t m_edgeHostUs = 0;
        int64_t m_targetLatencyMs = 0;
        int64_t m_startPositionMs = 0;
        int64_t m_nextTimestampMs = 0;

        std::map<int64_t, Segment> m_segments;
        // Request id to segment timestamp, for every part in flight.
        std::map<uint64_t, int64_t> m_requests;
        uint64_t m_nextRequestId = 1;

        std::vector<VideoChannelRequest> m_videoChannels;
        std::map<std::string, int32_t> m_newestChannelMap;
        int64_t m_newestChannelMapTimestampMs = INT64_MIN;
        std::map<std::string, VideoQuality> m_currentLevels;
        std::map<int32_t, std::deque<double>> m_bitrates;

        bool m_pumpScheduled = false;
        int64_t m_pumpAtUs = 0;
        uint64_t m_pumpGeneration = 0;
        uint64_t m_stallGeneration = 0;
        bool m_abandonScheduled = false;
        bool m_firstPlayDone = false;
        int64_t m_lastSegmentLoadEndUs = 0;

        // Listener.
        Playback m_playback = Playback::Buffering;
        int64_t m_stallTimestampMs = 0;
        std::deque<PlayRecord> m_plays;

        // Unified.
        bool m_playerStarted = false;
        bool m_playerReportedPlaying = false;
        bool m_playerPlaying = false;
        bool m_holding = false;
        bool m_jumpPending = false;
        int64_t m_jumpPositionMs = 0;
        int64_t m_givenEndMs = 0;
        int64_t m_playerPositionMs = 0;
        int64_t m_playerPositionHostUs = 0;
    };
}
