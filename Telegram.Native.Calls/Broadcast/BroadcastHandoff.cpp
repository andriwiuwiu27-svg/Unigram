#include "BroadcastHandoff.h"

namespace broadcast
{
    Handoff::Handoff(HandoffHost host, bool hasEngine)
        : m_host(std::move(host))
        , m_hasEngine(hasEngine)
    {
    }

    void Handoff::SetConnectionMode(ConnectionMode mode, bool keepBroadcast)
    {
        Actions actions;

        {
            std::lock_guard lock(m_lock);

            if (m_closed || (mode == m_mode && mode != ConnectionMode::None))
            {
                return;
            }

            ConnectionMode previous = m_mode;
            m_mode = mode;

            if (previous == ConnectionMode::Broadcast && m_session && !m_rejoinPending)
            {
                if (keepBroadcast)
                {
                    m_kept = true;
                }
                else
                {
                    StopLocked(actions);
                }
            }

            switch (mode)
            {
            case ConnectionMode::None:
                if (m_hasEngine)
                {
                    actions.engine = 0;
                    m_engineRtc = false;
                }
                break;
            case ConnectionMode::Rtc:
                if (m_hasEngine)
                {
                    actions.engine = 1;
                    m_engineRtc = true;
                }

                if (m_rejoinPending)
                {
                    m_rejoinPending = false;

                    if (m_session && !m_kept)
                    {
                        StopLocked(actions);
                    }
                }
                break;
            case ConnectionMode::Broadcast:
                m_broadcastConnected = false;

                // Every None makes the engine generate new ssrcs, which would not match the join payload just sent.
                if (m_hasEngine && m_engineRtc)
                {
                    actions.engine = 0;
                    m_engineRtc = false;
                }

                // A kept broadcast stays kept: it still ends when RTC's connectivity changes.
                if (m_session)
                {
                    if (m_rejoinPending)
                    {
                        m_rejoinPending = false;
                        actions.rejoined = true;
                    }
                }
                else
                {
                    m_session = true;
                    actions.start = true;
                }
                break;
            }

            UpdateLocked(actions);
        }

        Run(actions);
    }

    void Handoff::RtcStateChanged(bool connected)
    {
        Actions actions;

        {
            std::lock_guard lock(m_lock);

            if (m_closed || connected == m_rtcConnected)
            {
                return;
            }

            m_rtcConnected = connected;

            if (m_kept)
            {
                m_kept = false;

                // A kept broadcast that entered broadcast again is playing the call: only the hold ends.
                if (m_mode != ConnectionMode::Broadcast)
                {
                    StopLocked(actions);
                }
            }

            UpdateLocked(actions);
        }

        Run(actions);
    }

    void Handoff::Tick()
    {
        Actions actions;

        {
            std::lock_guard lock(m_lock);

            if (m_closed || m_broadcastConnected || !m_session)
            {
                return;
            }
            else if (m_mode != ConnectionMode::Broadcast && !m_kept)
            {
                return;
            }

            m_broadcastConnected = true;
            UpdateLocked(actions);
        }

        Run(actions);
    }

    void Handoff::RequestRejoin()
    {
        Actions actions;

        {
            std::lock_guard lock(m_lock);

            if (m_closed || !m_session || m_rejoinPending)
            {
                return;
            }

            m_rejoinPending = true;
            actions.rejoinRequested = true;
        }

        Run(actions);
    }

    void Handoff::Close()
    {
        std::lock_guard lock(m_lock);
        m_closed = true;
    }

    void Handoff::StopLocked(Actions& actions)
    {
        m_session = false;
        m_kept = false;
        m_rejoinPending = false;
        m_broadcastConnected = false;
        actions.silence = true;
        actions.stop = true;
        actions.start = false;
    }

    void Handoff::UpdateLocked(Actions& actions)
    {
        bool kept = m_kept && m_broadcastConnected;
        bool connected = false;
        bool transitioning = false;

        switch (m_mode)
        {
        case ConnectionMode::None:
            connected = kept;
            transitioning = kept;
            break;
        case ConnectionMode::Rtc:
            connected = m_rtcConnected || kept;
            transitioning = kept;
            break;
        case ConnectionMode::Broadcast:
            connected = m_broadcastConnected;
            break;
        }

        if (!m_reported || connected != m_connected || transitioning != m_transitioning)
        {
            m_reported = true;
            m_connected = connected;
            m_transitioning = transitioning;

            actions.report = true;
            actions.connected = connected;
            actions.transitioning = transitioning;
        }
    }

    void Handoff::Run(Actions const& actions)
    {
        if (actions.engine >= 0)
        {
            m_host.setEngineRtc(actions.engine == 1);
        }

        if (actions.silence)
        {
            m_host.silence();
        }

        if (actions.stop)
        {
            m_host.stopSession();
        }

        if (actions.start)
        {
            m_host.startSession();
        }

        if (actions.rejoined)
        {
            m_host.rejoined();
        }

        if (actions.rejoinRequested)
        {
            m_host.rejoinRequested();
        }

        if (actions.report)
        {
            m_host.networkStateUpdated(actions.connected, actions.transitioning);
        }
    }
}
