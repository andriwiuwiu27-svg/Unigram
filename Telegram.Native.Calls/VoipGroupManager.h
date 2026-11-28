#pragma once

#include "VoipGroupManager.g.h"
#include "group/GroupInstanceCustomImpl.h"
#include "group/GroupInstanceReferenceImpl.h"

#include "rtc_base/synchronization/mutex.h"

#include "VoipLoopbackCapture.h"

#include "Broadcast/BroadcastHandoff.h"
#include "Broadcast/BroadcastQueue.h"
#include "Broadcast/BroadcastSession.h"
#include "Broadcast/BroadcastVideo.h"

#include <mutex>

using namespace winrt::Windows::Foundation::Collections;

namespace winrt::Telegram::Native::Calls::implementation
{
    struct VoipGroupManager : VoipGroupManagerT<VoipGroupManager>
    {
        VoipGroupManager(VoipGroupDescriptor descriptor);

        // Stop is what the managed side calls, but nothing guarantees it gets there on
        // every path, and destroying the instance without stopping it first leaves the
        // loopback capture running.
        ~VoipGroupManager();

        void Stop();

        void SetConnectionMode(VoipGroupConnectionMode connectionMode, bool keepBroadcastIfWasEnabled);

        void EmitJoinPayload(EmitJsonPayloadDelegate completion);
        void SetJoinResponsePayload(hstring payload);
        void RemoveSsrcs(IVector<int32_t> ssrcs);

        void AddIncomingVideoOutput(hstring endpointId, winrt::Telegram::Native::Calls::VoipVideoOutputSink sink);

        bool IsMuted();
        void IsMuted(bool value);

        bool IsNoiseSuppressionEnabled();
        void IsNoiseSuppressionEnabled(bool value);

        void SetAudioOutputDevice(hstring id);
        void SetAudioInputDevice(hstring id);
        void SetVideoCapture(Telegram::Native::Calls::VoipCaptureBase videoCapture);

        void AddExternalAudioSamples(std::vector<uint8_t>&& samples);

        void SetVolume(int32_t ssrc, double volume);
        void SetRequestedVideoChannels(IVector<VoipVideoChannelInfo> descriptions);

        void SetEncryptDecrypt(EncryptGroupCallDataDelegate encryptData, DecryptGroupCallDataDelegate decryptData);

        winrt::event_token NetworkStateUpdated(Windows::Foundation::TypedEventHandler<
            winrt::Telegram::Native::Calls::VoipGroupManager,
            winrt::Telegram::Native::Calls::GroupNetworkStateChangedEventArgs> const& value);
        void NetworkStateUpdated(winrt::event_token const& token);

        winrt::event_token AudioLevelsUpdated(Windows::Foundation::TypedEventHandler<
            winrt::Telegram::Native::Calls::VoipGroupManager,
            IVector<winrt::Telegram::Native::Calls::VoipGroupParticipant>> const& value);
        void AudioLevelsUpdated(winrt::event_token const& token);

        winrt::event_token AudioBroadcastPartRequested(Windows::Foundation::TypedEventHandler<
            winrt::Telegram::Native::Calls::VoipGroupManager,
            winrt::Telegram::Native::Calls::AudioBroadcastPartRequestedEventArgs> const& value);
        void AudioBroadcastPartRequested(winrt::event_token const& token);

        winrt::event_token VideoBroadcastPartRequested(Windows::Foundation::TypedEventHandler<
            winrt::Telegram::Native::Calls::VoipGroupManager,
            winrt::Telegram::Native::Calls::VideoBroadcastPartRequestedEventArgs> const& value);
        void VideoBroadcastPartRequested(winrt::event_token const& token);

        winrt::event_token BroadcastTimeRequested(Windows::Foundation::TypedEventHandler<
            winrt::Telegram::Native::Calls::VoipGroupManager,
            winrt::Telegram::Native::Calls::BroadcastTimeRequestedEventArgs> const& value);
        void BroadcastTimeRequested(winrt::event_token const& token);

        winrt::event_token RejoinRequested(Windows::Foundation::TypedEventHandler<
            winrt::Telegram::Native::Calls::VoipGroupManager,
            Windows::Foundation::IInspectable> const& value);
        void RejoinRequested(winrt::event_token const& token);

        winrt::event_token MediaChannelDescriptionsRequested(Windows::Foundation::TypedEventHandler<
            winrt::Telegram::Native::Calls::VoipGroupManager,
            winrt::Telegram::Native::Calls::MediaChannelDescriptionsRequestedEventArgs> const& value);
        void MediaChannelDescriptionsRequested(winrt::event_token const& token);

    private:
        std::unique_ptr<tgcalls::GroupInstanceInterface> m_impl = nullptr;

        // Only the encrypt/decrypt delegates need guarding: they are written from the UI
        // thread and read per frame from a media thread. The events guard themselves.
        std::mutex m_encryptLock;

        // Ref-counted: the MF work item it queues holds a reference of its own, so it
        // must outlive us if a capture is still draining. Created only for screencast.
        com_ptr<VoipLoopbackCapture> m_loopback;

        bool m_isMuted = true;
        bool m_isNoiseSuppressionEnabled = true;

        bool m_isScreencast = false;
        bool m_isStream = false;

        // Broadcast, for every manager but a screencast's. The queue runs the session; the core plays listener
        // audio through the engine's playout; the handoff decides when a broadcast plays.
        std::shared_ptr<broadcast::TaskQueue> m_queue;
        std::shared_ptr<broadcast_audio::Core> m_core;
        std::shared_ptr<broadcast::VideoHub> m_video;
        std::unique_ptr<broadcast::Handoff> m_handoff;

        // Queue thread.
        std::shared_ptr<broadcast::Session> m_session;
        std::vector<broadcast::VideoChannelRequest> m_videoChannels;
        std::string m_outputDeviceId;

        // What the broadcast played since the engine last reported levels.
        std::mutex m_levelsLock;
        std::vector<broadcast_audio::Level> m_broadcastLevels;

        void CreateBroadcast(VoipGroupDescriptor const& descriptor);
        void StartSession();
        void StopSession();
        void Tick();
        broadcast::SessionHost MakeSessionHost();
        void OnBroadcastLevels(std::vector<broadcast_audio::Level>&& levels);
        void OnBroadcastTimeRequested(std::function<void(int64_t)> answer);
        void OnBroadcastPartRequested(broadcast::PartRequest const& request, std::function<void(broadcast::PartAnswer&&)> answer);
        void OnBroadcastNetworkStateUpdated(bool connected, bool transitioning);

        void OnNetworkStateUpdated(tgcalls::GroupNetworkState state);
        void OnAudioLevelsUpdated(tgcalls::GroupLevelsUpdate const& levels);
        std::shared_ptr<tgcalls::RequestMediaChannelDescriptionTask> OnRequestMediaChannelDescriptions(const std::vector<uint32_t>& ssrcs, std::function<void(std::vector<tgcalls::MediaChannelDescription>&&)> done);
        std::vector<uint8_t> OnE2EEncryptDecrypt(std::vector<uint8_t> const& message, int64_t userId, bool encrypt, int32_t channelId);

        EncryptGroupCallDataDelegate m_encryptData;
        DecryptGroupCallDataDelegate m_decryptData;

        winrt::event<Windows::Foundation::TypedEventHandler<
            winrt::Telegram::Native::Calls::VoipGroupManager,
            winrt::Telegram::Native::Calls::GroupNetworkStateChangedEventArgs>> m_networkStateUpdated;
        winrt::event<Windows::Foundation::TypedEventHandler<
            winrt::Telegram::Native::Calls::VoipGroupManager,
            IVector<winrt::Telegram::Native::Calls::VoipGroupParticipant>>> m_audioLevelsUpdated;
        winrt::event<Windows::Foundation::TypedEventHandler<
            winrt::Telegram::Native::Calls::VoipGroupManager,
            winrt::Telegram::Native::Calls::AudioBroadcastPartRequestedEventArgs>> m_audioBroadcastPartRequested;
        winrt::event<Windows::Foundation::TypedEventHandler<
            winrt::Telegram::Native::Calls::VoipGroupManager,
            winrt::Telegram::Native::Calls::VideoBroadcastPartRequestedEventArgs>> m_videoBroadcastPartRequested;
        winrt::event<Windows::Foundation::TypedEventHandler<
            winrt::Telegram::Native::Calls::VoipGroupManager,
            winrt::Telegram::Native::Calls::BroadcastTimeRequestedEventArgs>> m_broadcastTimeRequested;
        winrt::event<Windows::Foundation::TypedEventHandler<
            winrt::Telegram::Native::Calls::VoipGroupManager,
            Windows::Foundation::IInspectable>> m_rejoinRequested;
        winrt::event<Windows::Foundation::TypedEventHandler<
            winrt::Telegram::Native::Calls::VoipGroupManager,
            winrt::Telegram::Native::Calls::MediaChannelDescriptionsRequestedEventArgs>> m_mediaChannelDescriptionsRequested;
    };

    class RequestMediaChannelDescriptionTaskImpl final : public tgcalls::RequestMediaChannelDescriptionTask
    {
    public:
        RequestMediaChannelDescriptionTaskImpl(std::function<void(std::vector<tgcalls::MediaChannelDescription>&&)> done)
            : _done(std::move(done))
        {

        }

        void done(IVector<VoipMediaChannelDescription> participants)
        {
            webrtc::MutexLock lock(&_mutex);

            if (_done)
            {
                auto result = std::vector<tgcalls::MediaChannelDescription>();

                // null is how the managed side says "nothing", so it does not have to allocate a
                // collection to describe an empty answer on a path that runs per media request.
                if (participants)
                {
                    result.reserve(participants.Size());

                    for (auto const& value : participants)
                    {
                        result.push_back(tgcalls::MediaChannelDescription{
                            .type = tgcalls::MediaChannelDescription::Type::Audio,
                            .audioSsrc = uint32_t(value.AudioSource),
                            .userId = value.UserId
                            });
                    }
                }

                _done(std::move(result));

                // tgcalls expects exactly one completion, and the deferral is handed out
                // to managed code that can raise it more than once.
                _done = nullptr;
            }
        }

        void cancel() override
        {
            webrtc::MutexLock lock(&_mutex);

            if (!_done)
            {
                return;
            }

            _done = nullptr;
        }

    private:
        std::function<void(std::vector<tgcalls::MediaChannelDescription>&&)> _done;
        webrtc::Mutex _mutex;

    };
}

namespace winrt::Telegram::Native::Calls::factory_implementation
{
    struct VoipGroupManager : VoipGroupManagerT<VoipGroupManager, implementation::VoipGroupManager>
    {
    };
}
