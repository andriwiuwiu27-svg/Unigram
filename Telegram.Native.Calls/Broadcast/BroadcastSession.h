#pragma once

#include "BroadcastController.h"
#include "BroadcastQueue.h"
#include "BroadcastVideo.h"

#include "macos/Sources/BroadcastAudio/BroadcastAudioCore.h"

#include <functional>
#include <memory>
#include <vector>

namespace broadcast
{
    // What a session needs from the call. The answers may be called from any thread, once each, and after the session
    // ended (they are dropped then).
    struct SessionHost
    {
        std::function<void(std::function<void(int64_t edgeMs)> answer)> requestTime;
        std::function<void(PartRequest const& request, std::function<void(PartAnswer&& answer)> answer)> requestPart;
        std::function<void()> requestRejoin;
        // Listener: the levels the broadcast played since the last report, every 100 ms.
        std::function<void(std::vector<broadcast_audio::Level>&& levels)> levels;
    };

    // One broadcast from start to stop. Every method runs on the session's queue.
    class Session
    {
    public:
        virtual ~Session() = default;

        virtual void Start() = 0;
        virtual void Stop() = 0;
        virtual void Rejoined() = 0;
        virtual void SetRequestedVideoChannels(std::vector<VideoChannelRequest> channels) = 0;
    };

    // A large group call or an RTC live story: listener audio through the call's audio core, video through the hub.
    class ListenerSession : public Session, public std::enable_shared_from_this<ListenerSession>
    {
    public:
        ListenerSession(std::shared_ptr<TaskQueue> queue, std::shared_ptr<broadcast_audio::Core> core, std::shared_ptr<VideoHub> video, SessionHost host);
        ~ListenerSession() override;

        void Start() override;
        void Stop() override;
        void Rejoined() override;
        void SetRequestedVideoChannels(std::vector<VideoChannelRequest> channels) override;

    private:
        void Deliver(DeliveredSegment const& segment);
        void PollLevels();

        std::shared_ptr<TaskQueue> m_queue;
        std::shared_ptr<broadcast_audio::Core> m_core;
        std::shared_ptr<VideoHub> m_video;
        SessionHost m_host;

        std::unique_ptr<Controller> m_controller;
        std::vector<VideoChannelRequest> m_videoChannels;
    };

    // Runs a task on the session's controller, if the session and its controller still exist.
    using WithController = std::function<void(std::function<void(Controller&)>)>;

    // The host callbacks every session's controller shares: the clock, the queue, and the requests to the call,
    // whose answers come back on the queue while the session lives.
    ControllerHost MakeControllerHost(std::shared_ptr<TaskQueue> const& queue, std::function<int64_t()> nowUs, SessionHost const& sessionHost, WithController withController);
}
