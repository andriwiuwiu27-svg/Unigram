#include "BroadcastVideo.h"

#include <algorithm>

namespace broadcast
{
    namespace
    {
        // A display refresh, so a 60 fps part shows every frame.
        constexpr int64_t kTickUs = 16000;
    }

    VideoHub::VideoHub(std::function<int64_t()> nowUs)
        : m_nowUs(std::move(nowUs))
    {
    }

    VideoHub::~VideoHub()
    {
        Close();
    }

    void VideoHub::AddSink(std::string const& endpointId, VideoSink sink)
    {
        std::shared_ptr<VideoEndpoint> endpoint;

        {
            std::lock_guard lock(m_lock);

            if (m_closed)
            {
                return;
            }

            auto& sinks = m_sinks[endpointId];
            sinks.erase(std::remove_if(sinks.begin(), sinks.end(), [](VideoSink const& existing)
            {
                return existing.expired();
            }), sinks.end());
            sinks.push_back(sink);

            auto found = m_endpoints.find(endpointId);
            if (found != m_endpoints.end())
            {
                endpoint = found->second;
            }
        }

        if (endpoint)
        {
            endpoint->AddSink(std::move(sink));
        }
    }

    void VideoHub::Deliver(std::string const& endpointId, int64_t timestampMs, Bytes data)
    {
        std::shared_ptr<VideoEndpoint> endpoint;

        {
            std::lock_guard lock(m_lock);

            if (m_closed)
            {
                return;
            }

            // An endpoint gets its thread with its first part: in an RTC call no tile ever needs one.
            auto& slot = m_endpoints[endpointId];
            if (!slot)
            {
                slot = std::make_shared<VideoEndpoint>(m_nowUs, m_sinks[endpointId]);
            }

            endpoint = slot;
        }

        endpoint->Deliver(timestampMs, std::move(data));
    }

    void VideoHub::Play(int64_t timestampMs, int64_t hostUs, int32_t offsetMs)
    {
        std::lock_guard lock(m_lock);

        for (auto const& [id, endpoint] : m_endpoints)
        {
            endpoint->Play(timestampMs, hostUs, offsetMs);
        }
    }

    void VideoHub::Pause()
    {
        std::lock_guard lock(m_lock);

        for (auto const& [id, endpoint] : m_endpoints)
        {
            endpoint->Pause();
        }
    }

    void VideoHub::Reset()
    {
        std::map<std::string, std::shared_ptr<VideoEndpoint>> endpoints;

        {
            std::lock_guard lock(m_lock);
            endpoints.swap(m_endpoints);
        }

        for (auto const& [id, endpoint] : endpoints)
        {
            endpoint->Close();
        }
    }

    void VideoHub::Close()
    {
        {
            std::lock_guard lock(m_lock);
            m_closed = true;
            m_sinks.clear();
        }

        Reset();
    }

    VideoEndpoint::VideoEndpoint(std::function<int64_t()> nowUs, std::vector<VideoSink> sinks)
        : m_nowUs(std::move(nowUs))
        , m_sinks(std::move(sinks))
        , m_decoder(std::make_shared<VideoDecoder>())
    {
    }

    void VideoEndpoint::AddSink(VideoSink sink)
    {
        m_queue.Post([weak = weak_from_this(), sink = std::move(sink)]
        {
            if (auto strong = weak.lock())
            {
                strong->m_sinks.push_back(sink);
            }
        });
    }

    void VideoEndpoint::Deliver(int64_t timestampMs, Bytes data)
    {
        m_queue.Post([weak = weak_from_this(), timestampMs, data = std::move(data)]
        {
            if (auto strong = weak.lock())
            {
                auto& entry = strong->m_parts[timestampMs];
                entry.data = data;
                entry.part.reset();
                entry.opened = false;

                if (strong->m_shownTimestampMs == timestampMs)
                {
                    strong->m_shownIndex = -1;
                }

                strong->Wake();
            }
        });
    }

    void VideoEndpoint::Play(int64_t timestampMs, int64_t hostUs, int32_t offsetMs)
    {
        m_queue.Post([weak = weak_from_this(), timestampMs, hostUs, offsetMs]
        {
            if (auto strong = weak.lock())
            {
                strong->m_plays.push_back({ timestampMs, hostUs, offsetMs });
                strong->Wake();
            }
        });
    }

    void VideoEndpoint::Pause()
    {
        m_queue.Post([weak = weak_from_this()]
        {
            if (auto strong = weak.lock())
            {
                strong->m_plays.clear();
            }
        });
    }

    void VideoEndpoint::Close()
    {
        m_queue.Close();
    }

    void VideoEndpoint::Wake()
    {
        if (m_ticking)
        {
            return;
        }

        m_ticking = true;
        m_queue.Post([weak = weak_from_this()]
        {
            if (auto strong = weak.lock())
            {
                strong->Tick();
            }
        });
    }

    void VideoEndpoint::Tick()
    {
        int64_t now = m_nowUs();

        while (m_plays.size() > 1 && m_plays[1].hostUs <= now)
        {
            m_plays.pop_front();
        }

        if (m_plays.empty() || m_plays.front().hostUs > now)
        {
            // Nothing playing yet: parts wait for their play, and the tile keeps its frame.
            m_ticking = m_parts.size() && m_plays.size();
        }
        else
        {
            auto const& play = m_plays.front();

            m_parts.erase(m_parts.begin(), m_parts.lower_bound(play.timestampMs));

            auto entry = m_parts.find(play.timestampMs);
            if (entry != m_parts.end())
            {
                if (!entry->second.opened)
                {
                    entry->second.opened = true;

                    if (entry->second.data)
                    {
                        entry->second.part = VideoPart::Open(m_decoder, entry->second.data->data(), entry->second.data->size());
                    }

                    entry->second.data.reset();
                }

                if (entry->second.part)
                {
                    double seconds = (play.offsetMs * 1000.0 + (now - play.hostUs)) / 1000000.0;

                    if (auto frame = entry->second.part->FrameAt(seconds))
                    {
                        if (play.timestampMs != m_shownTimestampMs || frame->index != m_shownIndex)
                        {
                            m_shownTimestampMs = play.timestampMs;
                            m_shownIndex = frame->index;
                            Show(*frame);
                        }
                    }
                }
            }

            m_ticking = m_parts.size() || m_plays.size() > 1;
        }

        if (m_ticking)
        {
            m_queue.PostDelayed(kTickUs, [weak = weak_from_this()]
            {
                if (auto strong = weak.lock())
                {
                    strong->Tick();
                }
            });
        }
    }

    void VideoEndpoint::Show(VideoPartFrame const& frame)
    {
        auto videoFrame = webrtc::VideoFrame::Builder()
            .set_video_frame_buffer(frame.buffer)
            .set_rotation(frame.rotation)
            .set_timestamp_us(m_nowUs())
            .build();

        for (auto it = m_sinks.begin(); it != m_sinks.end();)
        {
            if (auto sink = it->lock())
            {
                sink->OnFrame(videoFrame);
                ++it;
            }
            else
            {
                it = m_sinks.erase(it);
            }
        }
    }
}
