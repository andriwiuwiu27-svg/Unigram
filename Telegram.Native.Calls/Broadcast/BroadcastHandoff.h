#pragma once

#include <functional>
#include <mutex>

namespace broadcast
{
    enum class ConnectionMode
    {
        None,
        Rtc,
        Broadcast
    };

    // Each callback runs outside the coordinator's lock, on the thread of the call that caused it.
    struct HandoffHost
    {
        // The engine's own mode: Rtc, or None for both None and Broadcast.
        std::function<void(bool rtc)> setEngineRtc;
        // Mutes the broadcast at once, on the calling thread, before the session is stopped.
        std::function<void()> silence;
        std::function<void()> startSession;
        std::function<void()> stopSession;
        // The rejoin the session asked for ended in broadcast again: it continues where it was.
        std::function<void()> rejoined;
        std::function<void()> rejoinRequested;
        std::function<void(bool connected, bool transitioning)> networkStateUpdated;
    };

    // Hands a call over between the broadcast and RTC, as the engine did before broadcast left it. Leaving broadcast
    // for another mode with `keepBroadcast` keeps it playing, reported connected and transitioning, until RTC's
    // connectivity first changes; without it the broadcast stops. A rejoin the session asked for keeps it playing
    // through the None between, and stops it only if the call comes back as RTC.
    class Handoff
    {
    public:
        Handoff(HandoffHost host, bool hasEngine);

        void SetConnectionMode(ConnectionMode mode, bool keepBroadcast);
        // The engine's network state.
        void RtcStateChanged(bool connected);
        // Every 500 ms from creation: a broadcast counts as connected from the first tick after it started.
        void Tick();
        void RequestRejoin();
        // Nothing is called back after this returns, except by a call already running.
        void Close();

    private:
        struct Actions
        {
            int engine = -1;
            bool silence = false;
            bool stop = false;
            bool start = false;
            bool rejoined = false;
            bool rejoinRequested = false;
            bool report = false;
            bool connected = false;
            bool transitioning = false;
        };

        void StopLocked(Actions& actions);
        void UpdateLocked(Actions& actions);
        void Run(Actions const& actions);

        HandoffHost m_host;
        bool const m_hasEngine;

        std::mutex m_lock;
        bool m_closed = false;
        ConnectionMode m_mode = ConnectionMode::None;
        bool m_engineRtc = false;
        bool m_session = false;
        bool m_kept = false;
        bool m_rejoinPending = false;
        bool m_broadcastConnected = false;
        bool m_rtcConnected = false;
        bool m_reported = false;
        bool m_connected = false;
        bool m_transitioning = false;
    };
}
