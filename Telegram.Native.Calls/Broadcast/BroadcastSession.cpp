#include "BroadcastSession.h"

#include "macos/Sources/BroadcastMedia/BroadcastListenerAudio.h"

namespace broadcast
{
    namespace
    {
        constexpr int64_t kLevelsPollUs = 100000;
    }

    ControllerHost MakeControllerHost(std::shared_ptr<TaskQueue> const& queue, std::function<int64_t()> nowUs, SessionHost const& sessionHost, WithController withController)
    {
        ControllerHost host;
        host.nowUs = std::move(nowUs);

        host.schedule = [queue](int64_t delayUs, std::function<void()> task)
        {
            queue->PostDelayed(delayUs, std::move(task));
        };

        host.requestLiveEdge = [queue, sessionHost, withController]
        {
            sessionHost.requestTime([queue, withController](int64_t edgeMs)
            {
                queue->Post([withController, edgeMs]
                {
                    withController([edgeMs](Controller& controller)
                    {
                        controller.LiveEdge(edgeMs);
                    });
                });
            });
        };

        host.loadPart = [queue, sessionHost, withController](PartRequest const& request)
        {
            uint64_t id = request.id;
            bool audio = request.kind == PartKind::Audio;

            sessionHost.requestPart(request, [queue, withController, id, audio](PartAnswer&& answer)
            {
                auto shared = std::make_shared<PartAnswer>(std::move(answer));
                queue->Post([withController, id, audio, shared]
                {
                    // The controller never parses parts: what it needs from listener audio is read here.
                    if (audio && shared->status == AnswerStatus::Data && shared->data)
                    {
                        if (auto info = broadcast_media::readListenerAudioInfo(shared->data->data(), shared->data->size()))
                        {
                            shared->durationMs = info->durationMs;
                            shared->videoChannels = std::move(info->videoChannels);
                        }
                    }

                    withController([id, shared](Controller& controller)
                    {
                        controller.Answer(id, std::move(*shared));
                    });
                });
            });
        };

        // The client cannot cancel a request: a late answer is dropped by the controller.
        host.cancelParts = [](std::vector<uint64_t> const&) {};
        host.requestRejoin = sessionHost.requestRejoin;
        return host;
    }

    ListenerSession::ListenerSession(std::shared_ptr<TaskQueue> queue, std::shared_ptr<broadcast_audio::Core> core, std::shared_ptr<VideoHub> video, SessionHost host)
        : m_queue(std::move(queue))
        , m_core(std::move(core))
        , m_video(std::move(video))
        , m_host(std::move(host))
    {
    }

    ListenerSession::~ListenerSession()
    {
        Stop();
    }

    void ListenerSession::Start()
    {
        if (m_controller)
        {
            return;
        }

        std::weak_ptr<ListenerSession> weak = weak_from_this();
        WithController withController = [weak](std::function<void(Controller&)> task)
        {
            if (auto strong = weak.lock())
            {
                if (strong->m_controller)
                {
                    task(*strong->m_controller);
                }
            }
        };

        auto core = m_core;
        auto host = MakeControllerHost(m_queue, [core] { return core->nowUs(); }, m_host, withController);

        host.deliver = [this](DeliveredSegment const& segment)
        {
            Deliver(segment);
        };

        host.replaceVideo = [this](int64_t timestampMs, LoadedPart const& part)
        {
            m_video->Deliver(part.request.endpointId, timestampMs, part.data);
        };

        host.play = [this](int64_t timestampMs, int64_t hostUs, int32_t offsetMs)
        {
            m_core->play(timestampMs, hostUs, offsetMs);
            m_video->Play(timestampMs, hostUs, offsetMs);
        };

        host.setUnifiedPosition = [](int64_t) {};

        // What a call that ended left in the core is not this broadcast's.
        m_core->reset();
        m_core->setActive(true);

        m_controller = std::make_unique<Controller>(StreamFormat::Listener, std::move(host), BandwidthEstimator::Shared());
        m_controller->SetRequestedVideoChannels(m_videoChannels);
        m_controller->Start();

        PollLevels();
    }

    void ListenerSession::Stop()
    {
        if (!m_controller)
        {
            return;
        }

        m_controller->Stop();
        m_controller.reset();

        m_core->setActive(false);
        m_core->reset();
        m_video->Reset();
    }

    void ListenerSession::Rejoined()
    {
        if (m_controller)
        {
            m_controller->Rejoined();
        }
    }

    void ListenerSession::SetRequestedVideoChannels(std::vector<VideoChannelRequest> channels)
    {
        m_videoChannels = channels;

        if (m_controller)
        {
            m_controller->SetRequestedVideoChannels(std::move(channels));
        }
    }

    void ListenerSession::Deliver(DeliveredSegment const& segment)
    {
        if (auto const& data = segment.primary.data)
        {
            if (auto audio = broadcast_media::decodeListenerAudio(data->data(), data->size()))
            {
                broadcast_media::enqueueListenerAudio(*m_core, segment.timestampMs, *audio);
            }
        }

        for (auto const& part : segment.video)
        {
            m_video->Deliver(part.request.endpointId, segment.timestampMs, part.data);
        }
    }

    void ListenerSession::PollLevels()
    {
        if (!m_controller)
        {
            return;
        }

        auto levels = m_core->takeLevels();
        if (levels.size())
        {
            m_host.levels(std::move(levels));
        }

        m_queue->PostDelayed(kLevelsPollUs, [weak = weak_from_this()]
        {
            if (auto strong = weak.lock())
            {
                strong->PollLevels();
            }
        });
    }
}
