#include "pch.h"
#include "VoipGroupManager.h"
#if __has_include("VoipGroupManager.g.cpp")
#include "VoipGroupManager.g.cpp"
#endif

#include "VoipVideoCapture.h"
#include "VoipScreenCapture.h"
#include "VoipVideoOutputSink.h"
#include "GroupNetworkStateChangedEventArgs.h"
#include "BroadcastPartRequestedEventArgs.h"
#include "BroadcastTimeRequestedEventArgs.h"
#include "MediaChannelDescriptionsRequestedEventArgs.h"

#include "Broadcast/BroadcastStream.h"

#include "StaticThreads.h"

#include "api/audio/create_audio_device_module.h"
#include "api/environment/environment_factory.h"
#include "modules/audio_device/include/audio_device_data_observer.h"

#include "../Telegram.Native/AppModelPaths.h"

#include <algorithm>
#include <cstdint>
#include <random>

namespace winrt::Telegram::Native::Calls::implementation
{
    namespace
    {
        constexpr int64_t kStatusTickUs = 500000;

        // The device thread's 10 ms playout block: the broadcast replaces the call's playout while it plays.
        class BroadcastPlayoutObserver : public webrtc::AudioDeviceDataObserver
        {
        public:
            explicit BroadcastPlayoutObserver(std::shared_ptr<broadcast_audio::Core> core)
                : m_core(std::move(core))
            {
            }

            void OnCaptureData(const void* audio_samples, const size_t num_samples, const size_t bytes_per_sample, const size_t num_channels, const uint32_t samples_per_sec) override
            {
            }

            void OnRenderData(const void* audio_samples, const size_t num_samples, const size_t bytes_per_sample, const size_t num_channels, const uint32_t samples_per_sec) override
            {
                if (bytes_per_sample != num_channels * sizeof(int16_t) || samples_per_sec % 100 != 0 || num_samples != samples_per_sec / 100)
                {
                    return;
                }

                // The observer hands playout over as const, but the buffer is the device's own and is played after this returns.
                m_core->render(static_cast<int16_t*>(const_cast<void*>(audio_samples)), num_samples, num_channels, samples_per_sec);
            }

        private:
            const std::shared_ptr<broadcast_audio::Core> m_core;
        };
    }

    VoipGroupManager::VoipGroupManager(VoipGroupDescriptor descriptor)
    {
        m_isScreencast = VoipVideoContentType::Screencast == descriptor.VideoContentType();
        m_isStream = descriptor.IsStream();
        m_isNoiseSuppressionEnabled = descriptor.IsNoiseSuppressionEnabled();

        if (!m_isScreencast)
        {
            CreateBroadcast(descriptor);
        }

        // An RTMP stream has nobody to send to and nothing to receive but the stream: no engine at all.
        if (m_isStream)
        {
            return;
        }

        // Sharing a screen runs a second manager alongside the main one, and both would
        // otherwise interleave into the same file.
        auto logPath = AppModel::LocalPath()
            + (m_isScreencast
                ? hstring(L"\\tgcalls_screencast.txt")
                : hstring(L"\\tgcalls_group.txt"));

        tgcalls::GroupConfig config = tgcalls::GroupConfig{
            true,
            logPath.data()
        };

        tgcalls::GroupInstanceDescriptor impl = tgcalls::GroupInstanceDescriptor
        {
            .threads = tgcalls::StaticThreads::getThreads(),
            .config = config,
            .networkStateUpdated = [weakThis{ get_weak() }](tgcalls::GroupNetworkState state) {
                if (auto strongThis = weakThis.get())
                {
                    return strongThis->OnNetworkStateUpdated(state);
                }
            },
            .audioLevelsUpdated = [weakThis{ get_weak() }](tgcalls::GroupLevelsUpdate const& levels) {
                if (auto strongThis = weakThis.get())
                {
                    return strongThis->OnAudioLevelsUpdated(levels);
                }
            },
            .initialInputDeviceId = winrt::to_string(descriptor.AudioInputId()),
            .initialOutputDeviceId = winrt::to_string(descriptor.AudioOutputId()),
            .videoContentType = (tgcalls::VideoContentType)descriptor.VideoContentType(),
            .initialEnableNoiseSuppression = m_isNoiseSuppressionEnabled,
        };

        if (descriptor.IsConference())
        {
            impl.isConference = true;
            impl.e2eEncryptDecrypt = [weakThis{ get_weak() }](std::vector<uint8_t> const& message, int64_t userId, bool encrypt, int32_t channelId) {
                if (auto strongThis = weakThis.get())
                {
                    return strongThis->OnE2EEncryptDecrypt(message, userId, encrypt, channelId);
                }

                return std::vector<uint8_t>();
            };
            impl.requestMediaChannelDescriptions = [weakThis{ get_weak() }](std::vector<uint32_t> const& ssrcs, std::function<void(std::vector<tgcalls::MediaChannelDescription>&&)> done) {
                if (auto strongThis = weakThis.get())
                {
                    return strongThis->OnRequestMediaChannelDescriptions(ssrcs, done);
                }

                return std::shared_ptr<tgcalls::RequestMediaChannelDescriptionTask>();
            };
        }

        impl.videoCapture = GetVideoCaptureImpl(descriptor.VideoCapture());

        // tgcalls builds no platform audio device of its own: without this the call has none. A
        // screencast is left without it, because only then does tgcalls give it its fake device.
        if (!m_isScreencast)
        {
            // The broadcast is rendered over the device's playout here rather than through the
            // engine's onPlayoutFrame, which GroupInstanceReferenceImpl does not call.
            impl.createAudioDeviceModule = [core = m_core](webrtc::TaskQueueFactory*) {
                auto module = webrtc::CreateAudioDeviceModule(webrtc::CreateEnvironment(), webrtc::AudioDeviceModule::kPlatformDefaultAudio);
                if (core && module)
                {
                    return webrtc::CreateAudioDeviceWithDataObserver(module, std::make_unique<BroadcastPlayoutObserver>(core));
                }

                return module;
            };
        }

        if (descriptor.IsReferenceImpl())
        {
            m_impl = std::make_unique<tgcalls::GroupInstanceReferenceImpl>(std::move(impl));
        }
        else
        {
            m_impl = std::make_unique<tgcalls::GroupInstanceCustomImpl>(std::move(impl));
        }

        if (m_isScreencast)
        {
            auto audioProcessId = descriptor.AudioProcessId();
            if (audioProcessId == 0)
            {
                return;
            }

            m_loopback = winrt::make_self<VoipLoopbackCapture>([weakThis{ get_weak() }](std::vector<uint8_t>&& samples) {
                if (auto strongThis = weakThis.get())
                {
                    strongThis->AddExternalAudioSamples(std::move(samples));
                }
                });

            // -1 means everything this process is not rendering, i.e. the whole system
            // minus ourselves; anything else is that process and its children.
            auto result = audioProcessId == -1
                ? m_loopback->Start(GetCurrentProcessId(), false)
                : m_loopback->Start(static_cast<uint32_t>(audioProcessId), true);

            if (FAILED(result))
            {
                // Process loopback is unavailable on older builds; share the screen
                // without its audio rather than failing the whole capture.
                m_loopback = nullptr;
                return;
            }

            IsMuted(false);
        }
    }

    VoipGroupManager::~VoipGroupManager()
    {
        Stop();
    }

    void VoipGroupManager::CreateBroadcast(VoipGroupDescriptor const& descriptor)
    {
        m_outputDeviceId = winrt::to_string(descriptor.AudioOutputId());
        m_queue = std::make_shared<broadcast::TaskQueue>();

        if (!m_isStream)
        {
            m_core = std::make_shared<broadcast_audio::Core>();
        }

        // Frames are timed on the clock the session plays on: the core's, or the same steady clock for a stream.
        std::function<int64_t()> nowUs = broadcast_audio::defaultClockMicroseconds;
        m_video = std::make_shared<broadcast::VideoHub>(nowUs);

        broadcast::HandoffHost host;
        host.setEngineRtc = [this](bool rtc)
        {
            if (m_impl)
            {
                m_impl->setConnectionMode(rtc ? tgcalls::GroupConnectionMode::GroupConnectionModeRtc : tgcalls::GroupConnectionMode::GroupConnectionModeNone);
            }
        };

        host.silence = [core = m_core]
        {
            if (core)
            {
                core->setActive(false);
            }
        };

        host.startSession = [weakThis{ get_weak() }, queue = m_queue]
        {
            queue->Post([weakThis]
            {
                if (auto strongThis = weakThis.get())
                {
                    strongThis->StartSession();
                }
            });
        };

        host.stopSession = [weakThis{ get_weak() }, queue = m_queue]
        {
            queue->Post([weakThis]
            {
                if (auto strongThis = weakThis.get())
                {
                    strongThis->StopSession();
                }
            });
        };

        host.rejoined = [weakThis{ get_weak() }, queue = m_queue]
        {
            queue->Post([weakThis]
            {
                if (auto strongThis = weakThis.get())
                {
                    if (strongThis->m_session)
                    {
                        strongThis->m_session->Rejoined();
                    }
                }
            });
        };

        host.rejoinRequested = [weakThis{ get_weak() }]
        {
            if (auto strongThis = weakThis.get())
            {
                strongThis->m_rejoinRequested(*strongThis, nullptr);
            }
        };

        host.networkStateUpdated = [weakThis{ get_weak() }](bool connected, bool transitioning)
        {
            if (auto strongThis = weakThis.get())
            {
                strongThis->OnBroadcastNetworkStateUpdated(connected, transitioning);
            }
        };

        m_handoff = std::make_unique<broadcast::Handoff>(std::move(host), !m_isStream);

        m_queue->PostDelayed(kStatusTickUs, [weakThis{ get_weak() }]
        {
            if (auto strongThis = weakThis.get())
            {
                strongThis->Tick();
            }
        });
    }

    void VoipGroupManager::Tick()
    {
        if (m_handoff)
        {
            m_handoff->Tick();
        }

        m_queue->PostDelayed(kStatusTickUs, [weakThis{ get_weak() }]
        {
            if (auto strongThis = weakThis.get())
            {
                strongThis->Tick();
            }
        });
    }

    void VoipGroupManager::StartSession()
    {
        if (m_session)
        {
            return;
        }

        if (m_isStream)
        {
            m_session = std::make_shared<broadcast::StreamSession>(m_queue, m_video, MakeSessionHost(), m_outputDeviceId);
        }
        else
        {
            m_session = std::make_shared<broadcast::ListenerSession>(m_queue, m_core, m_video, MakeSessionHost());
        }

        m_session->SetRequestedVideoChannels(m_videoChannels);
        m_session->Start();
    }

    void VoipGroupManager::StopSession()
    {
        if (m_session)
        {
            m_session->Stop();
            m_session = nullptr;
        }

        std::lock_guard lock(m_levelsLock);
        m_broadcastLevels.clear();
    }

    broadcast::SessionHost VoipGroupManager::MakeSessionHost()
    {
        broadcast::SessionHost host;

        host.requestTime = [weakThis{ get_weak() }](std::function<void(int64_t)> answer)
        {
            if (auto strongThis = weakThis.get())
            {
                strongThis->OnBroadcastTimeRequested(std::move(answer));
            }
        };

        host.requestPart = [weakThis{ get_weak() }](broadcast::PartRequest const& request, std::function<void(broadcast::PartAnswer&&)> answer)
        {
            if (auto strongThis = weakThis.get())
            {
                strongThis->OnBroadcastPartRequested(request, std::move(answer));
            }
        };

        host.requestRejoin = [weakThis{ get_weak() }]
        {
            if (auto strongThis = weakThis.get())
            {
                if (strongThis->m_handoff)
                {
                    strongThis->m_handoff->RequestRejoin();
                }
            }
        };

        host.levels = [weakThis{ get_weak() }](std::vector<broadcast_audio::Level>&& levels)
        {
            if (auto strongThis = weakThis.get())
            {
                strongThis->OnBroadcastLevels(std::move(levels));
            }
        };

        return host;
    }

    void VoipGroupManager::Stop()
    {
        if (m_loopback)
        {
            m_loopback->Stop();
            m_loopback = nullptr;
        }

        // The broadcast goes first: the engine's playout must not reach a core whose session is going away, and the
        // handoff must not call into an engine being stopped.
        if (m_handoff)
        {
            m_handoff->Close();
        }

        if (m_core)
        {
            m_core->setActive(false);
        }

        // The session ends on the queue's thread: a stream's audio device must be stopped on the thread that made it.
        if (m_queue)
        {
            m_queue->Close([this]
            {
                StopSession();
            });
        }

        if (m_video)
        {
            m_video->Close();
        }

        if (m_impl)
        {
            m_impl->stop([]() {});
            m_impl.reset();
        }
    }

    void VoipGroupManager::SetConnectionMode(VoipGroupConnectionMode connectionMode, bool keepBroadcastIfWasEnabled)
    {
        if (m_handoff)
        {
            m_handoff->SetConnectionMode(static_cast<broadcast::ConnectionMode>(connectionMode), keepBroadcastIfWasEnabled);
        }
        else if (m_impl)
        {
            m_impl->setConnectionMode(connectionMode == VoipGroupConnectionMode::Rtc
                ? tgcalls::GroupConnectionMode::GroupConnectionModeRtc
                : tgcalls::GroupConnectionMode::GroupConnectionModeNone);
        }
    }

    void VoipGroupManager::EmitJoinPayload(EmitJsonPayloadDelegate completion)
    {
        if (m_impl)
        {
            m_impl->emitJoinPayload([completion](auto const& payload) {
                completion(payload.audioSsrc, winrt::to_hstring(payload.json));
                });
        }
        else if (m_isStream && m_queue)
        {
            // A stream joins without media: the server wants a payload, any ssrc, and nothing else in it.
            m_queue->Post([completion]
            {
                std::random_device device;
                std::uniform_int_distribution<int32_t> distribution(1, INT32_MAX - 2);
                int32_t ssrc = distribution(device);

                auto json = "{\"fingerprints\":[],\"ufrag\":\"\",\"pwd\":\"\",\"ssrc\":" + std::to_string(ssrc) + ",\"ssrc-groups\":[]}";
                completion(ssrc, winrt::to_hstring(json));
            });
        }

        // Nothing to join with once the instance is gone. Completing with an empty
        // payload only got the caller as far as a join the server would reject, and it
        // did it on this thread rather than the one the success path answers on.
    }

    void VoipGroupManager::SetJoinResponsePayload(hstring payload)
    {
        if (m_impl)
        {
            m_impl->setJoinResponsePayload(winrt::to_string(payload));
        }
    }

    void VoipGroupManager::RemoveSsrcs(IVector<int32_t> ssrcs)
    {
        if (m_impl)
        {
            m_impl->removeSsrcs(std::vector<uint32_t>(ssrcs.begin(), ssrcs.end()));
        }
    }

    void VoipGroupManager::AddIncomingVideoOutput(hstring endpointId, winrt::Telegram::Native::Calls::VoipVideoOutputSink sink)
    {
        if (sink == nullptr)
        {
            return;
        }

        auto implementation = winrt::get_self<VoipVideoOutputSink>(sink);
        auto endpoint = winrt::to_string(endpointId);

        if (m_impl)
        {
            m_impl->addIncomingVideoOutput(endpoint, implementation->Sink());
        }

        // The same tile shows the call's video and the broadcast's, whichever is playing.
        if (m_video)
        {
            m_video->AddSink(endpoint, implementation->Sink());
        }
    }



    bool VoipGroupManager::IsMuted()
    {
        return m_isMuted;
    }

    void VoipGroupManager::IsMuted(bool value)
    {
        if (m_impl)
        {
            m_impl->setIsMuted(m_isMuted = value);
        }
    }

    bool VoipGroupManager::IsNoiseSuppressionEnabled()
    {
        return m_isNoiseSuppressionEnabled;
    }

    void VoipGroupManager::IsNoiseSuppressionEnabled(bool value)
    {
        if (m_impl)
        {
            m_impl->setIsNoiseSuppressionEnabled(m_isNoiseSuppressionEnabled = value);
        }
    }

    void VoipGroupManager::SetAudioOutputDevice(hstring id)
    {
        auto device = winrt::to_string(id);

        if (m_impl)
        {
            m_impl->setAudioOutputDevice(device);
        }

        if (m_isStream && m_queue)
        {
            m_queue->Post([weakThis{ get_weak() }, device]
            {
                if (auto strongThis = weakThis.get())
                {
                    strongThis->m_outputDeviceId = device;

                    if (auto stream = std::dynamic_pointer_cast<broadcast::StreamSession>(strongThis->m_session))
                    {
                        stream->SetOutputDevice(device);
                    }
                }
            });
        }
    }

    void VoipGroupManager::SetAudioInputDevice(hstring id)
    {
        if (m_impl)
        {
            m_impl->setAudioInputDevice(winrt::to_string(id));
        }
    }

    void VoipGroupManager::SetVideoCapture(Telegram::Native::Calls::VoipCaptureBase videoCapture)
    {
        if (m_impl)
        {
            m_impl->setVideoCapture(GetVideoCaptureImpl(videoCapture));
        }
    }

    void VoipGroupManager::AddExternalAudioSamples(std::vector<uint8_t>&& samples)
    {
        if (m_impl)
        {
            m_impl->addExternalAudioSamples(std::move(samples));
        }
    }

    void VoipGroupManager::SetVolume(int32_t ssrc, double volume)
    {
        if (ssrc == 0)
        {
            return;
        }

        if (m_impl)
        {
            m_impl->setVolume(ssrc, volume);
        }

        // The core's control calls belong to the queue.
        if (m_core && m_queue)
        {
            m_queue->Post([core = m_core, ssrc, volume]
            {
                core->setVolume(static_cast<uint32_t>(ssrc), static_cast<float>(volume));
            });
        }
    }

    void VoipGroupManager::SetRequestedVideoChannels(IVector<VoipVideoChannelInfo> descriptions)
    {
        auto impl = std::vector<tgcalls::VideoChannelDescription>();
        auto requests = std::vector<broadcast::VideoChannelRequest>();
        impl.reserve(descriptions.Size());
        requests.reserve(descriptions.Size());

        for (const VoipVideoChannelInfo& x : descriptions)
        {
            tgcalls::VideoChannelDescription item;
            item.audioSsrc = x.AudioSource();
            item.endpointId = winrt::to_string(x.EndpointId());
            item.userId = x.ParticipantId();
            item.minQuality = (tgcalls::VideoChannelDescription::Quality)x.MinQuality();
            item.maxQuality = (tgcalls::VideoChannelDescription::Quality)x.MaxQuality();

            for (const VoipVideoSourceGroup& group : x.SourceGroups())
            {
                tgcalls::MediaSsrcGroup groupImpl;
                groupImpl.semantics = winrt::to_string(group.Semantics());
                groupImpl.ssrcs = vector_to_unmanaged<uint32_t, int32_t>(group.SourceIds());

                item.ssrcGroups.push_back(std::move(groupImpl));
            }

            requests.push_back(broadcast::VideoChannelRequest{
                .endpointId = item.endpointId,
                .minQuality = static_cast<broadcast::VideoQuality>(x.MinQuality()),
                .maxQuality = static_cast<broadcast::VideoQuality>(x.MaxQuality())
                });

            impl.push_back(std::move(item));
        }

        if (m_impl)
        {
            m_impl->setRequestedVideoChannels(std::move(impl));
        }

        if (m_queue && !m_isStream)
        {
            m_queue->Post([weakThis{ get_weak() }, requests = std::move(requests)]
            {
                if (auto strongThis = weakThis.get())
                {
                    strongThis->m_videoChannels = requests;

                    if (strongThis->m_session)
                    {
                        strongThis->m_session->SetRequestedVideoChannels(requests);
                    }
                }
            });
        }
    }



    void VoipGroupManager::OnNetworkStateUpdated(tgcalls::GroupNetworkState state)
    {
        if (m_handoff)
        {
            // Called here, on the engine's thread: a kept broadcast must be silent before RTC audio plays.
            m_handoff->RtcStateChanged(state.isConnected);
        }
        else
        {
            auto args = winrt::make_self<GroupNetworkStateChangedEventArgs>(state.isConnected, false);
            m_networkStateUpdated(*this, *args);
        }
    }

    void VoipGroupManager::OnBroadcastNetworkStateUpdated(bool connected, bool transitioning)
    {
        auto args = winrt::make_self<GroupNetworkStateChangedEventArgs>(connected, transitioning);
        m_networkStateUpdated(*this, *args);
    }

    void VoipGroupManager::OnBroadcastLevels(std::vector<broadcast_audio::Level>&& levels)
    {
        std::lock_guard lock(m_levelsLock);

        // The levels of the engine's report window: the maximum, and voice if any had it.
        for (auto const& level : levels)
        {
            auto existing = std::find_if(m_broadcastLevels.begin(), m_broadcastLevels.end(), [&](broadcast_audio::Level const& other)
            {
                return other.ssrc == level.ssrc;
            });

            if (existing == m_broadcastLevels.end())
            {
                m_broadcastLevels.push_back(level);
            }
            else
            {
                existing->level = std::max(existing->level, level.level);
                existing->voice = existing->voice || level.voice;
            }
        }
    }

    void VoipGroupManager::OnAudioLevelsUpdated(tgcalls::GroupLevelsUpdate const& levels)
    {
        std::vector<broadcast_audio::Level> broadcastLevels;

        {
            std::lock_guard lock(m_levelsLock);
            broadcastLevels.swap(m_broadcastLevels);
        }

        // Filled first and handed over whole: appending to the IVector would be a COM
        // call per participant, and this arrives about ten times a second for the whole
        // length of the call.
        auto participants = std::vector<winrt::Telegram::Native::Calls::VoipGroupParticipant>();
        participants.reserve(levels.updates.size() + broadcastLevels.size());

        for (const tgcalls::GroupLevelUpdate& x : levels.updates)
        {
            participants.push_back(winrt::Telegram::Native::Calls::VoipGroupParticipant{
                .AudioSource = static_cast<int32_t>(x.ssrc),
                    .Level = x.value.level,
                    .IsSpeaking = x.value.voice,
                    .IsMuted = x.value.isMuted
                });
        }

        // The broadcast's speakers join the engine's report, which goes out every 100 ms in every mode.
        for (auto const& level : broadcastLevels)
        {
            bool reported = std::any_of(levels.updates.begin(), levels.updates.end(), [&](tgcalls::GroupLevelUpdate const& x)
            {
                return x.ssrc == level.ssrc;
            });

            if (!reported)
            {
                participants.push_back(winrt::Telegram::Native::Calls::VoipGroupParticipant{
                    .AudioSource = static_cast<int32_t>(level.ssrc),
                        .Level = level.level,
                        .IsSpeaking = level.voice,
                        .IsMuted = false
                    });
            }
        }

        m_audioLevelsUpdated(*this, winrt::single_threaded_vector(std::move(participants)));
    }

    void VoipGroupManager::OnBroadcastTimeRequested(std::function<void(int64_t)> answer)
    {
        auto args = winrt::make_self<BroadcastTimeRequestedEventArgs>([answer](int64_t time) { answer(time); });
        m_broadcastTimeRequested(*this, *args);
    }

    void VoipGroupManager::OnBroadcastPartRequested(broadcast::PartRequest const& request, std::function<void(broadcast::PartAnswer&&)> answer)
    {
        auto deferral = [answer](BroadcastPartStatus status, int64_t response, array_view<uint8_t const> data)
        {
            broadcast::PartAnswer part;
            part.responseTimestampMs = response;

            switch (status)
            {
            case BroadcastPartStatus::Success:
                part.status = data.size() ? broadcast::AnswerStatus::Data : broadcast::AnswerStatus::NotReady;
                break;
            case BroadcastPartStatus::ResyncNeeded:
                part.status = broadcast::AnswerStatus::ResyncNeeded;
                break;
            case BroadcastPartStatus::RejoinNeeded:
                part.status = broadcast::AnswerStatus::RejoinNeeded;
                break;
            default:
                part.status = broadcast::AnswerStatus::NotReady;
                break;
            }

            if (part.status == broadcast::AnswerStatus::Data)
            {
                part.data = std::make_shared<std::vector<uint8_t> const>(data.begin(), data.end());
            }

            answer(std::move(part));
        };

        // Parts are a second each: scale 0.
        if (request.kind == broadcast::PartKind::Audio)
        {
            auto args = winrt::make_self<AudioBroadcastPartRequestedEventArgs>(0, request.timestampMs, deferral);
            m_audioBroadcastPartRequested(*this, *args);
        }
        else
        {
            auto args = winrt::make_self<VideoBroadcastPartRequestedEventArgs>(0, request.timestampMs, request.channelId, static_cast<VoipVideoChannelQuality>(request.quality), deferral);
            m_videoBroadcastPartRequested(*this, *args);
        }
    }

    std::shared_ptr<tgcalls::RequestMediaChannelDescriptionTask> VoipGroupManager::OnRequestMediaChannelDescriptions(const std::vector<uint32_t>& ssrcs, std::function<void(std::vector<tgcalls::MediaChannelDescription>&&)> done)
    {

        auto audioSourceIds = winrt::single_threaded_vector<uint32_t>(std::vector<uint32_t>(ssrcs));

        auto task = std::make_shared<RequestMediaChannelDescriptionTaskImpl>(std::move(done));
        auto args = winrt::make_self<MediaChannelDescriptionsRequestedEventArgs>(audioSourceIds,
            [task](IVector<VoipMediaChannelDescription> participants) { task->done(participants); });

        m_mediaChannelDescriptionsRequested(*this, *args);
        return task;
    }

    std::vector<uint8_t> VoipGroupManager::OnE2EEncryptDecrypt(std::vector<uint8_t> const& message, int64_t userId, bool encrypt, int32_t unencryptedPrefixSize)
    {
        // Taken by copy rather than held: this runs per frame on a media thread, and the
        // managed delegates block on a TDLib round trip. The lock covers only the read.
        EncryptGroupCallDataDelegate encryptData{ nullptr };
        DecryptGroupCallDataDelegate decryptData{ nullptr };

        {
            std::lock_guard const guard(m_encryptLock);
            encryptData = m_encryptData;
            decryptData = m_decryptData;
        }

        // An empty result is how tgcalls is told the frame failed and must be dropped.
        // Returning the input untouched would put plaintext on the wire when encrypting,
        // and hand ciphertext to the decoder as if it were plaintext when decrypting.
        if (encrypt ? encryptData == nullptr : decryptData == nullptr)
        {
            return {};
        }

        // A view over the caller's buffer, so nothing is copied on this side: the
        // projection marshals it straight into the managed array.
        auto view = array_view<uint8_t const>(message.data(), message.data() + message.size());

        auto data = encrypt
            ? encryptData(m_isScreencast ? VoipDataChannel::ScreenSharing : VoipDataChannel::Main, view, unencryptedPrefixSize)
            : decryptData(userId, view);

        return std::vector<uint8_t>(data.begin(), data.end());
    }



    void VoipGroupManager::SetEncryptDecrypt(EncryptGroupCallDataDelegate encryptData, DecryptGroupCallDataDelegate decryptData)
    {
        std::lock_guard const guard(m_encryptLock);
        m_encryptData = encryptData;
        m_decryptData = decryptData;
    }


    winrt::event_token VoipGroupManager::NetworkStateUpdated(Windows::Foundation::TypedEventHandler<
        winrt::Telegram::Native::Calls::VoipGroupManager,
        winrt::Telegram::Native::Calls::GroupNetworkStateChangedEventArgs> const& value)
    {
        return m_networkStateUpdated.add(value);
    }

    void VoipGroupManager::NetworkStateUpdated(winrt::event_token const& token)
    {
        m_networkStateUpdated.remove(token);
    }



    winrt::event_token VoipGroupManager::AudioLevelsUpdated(Windows::Foundation::TypedEventHandler<
        winrt::Telegram::Native::Calls::VoipGroupManager,
        IVector<winrt::Telegram::Native::Calls::VoipGroupParticipant>> const& value)
    {
        return m_audioLevelsUpdated.add(value);
    }

    void VoipGroupManager::AudioLevelsUpdated(winrt::event_token const& token)
    {
        m_audioLevelsUpdated.remove(token);
    }



    winrt::event_token VoipGroupManager::AudioBroadcastPartRequested(Windows::Foundation::TypedEventHandler<
        winrt::Telegram::Native::Calls::VoipGroupManager,
        winrt::Telegram::Native::Calls::AudioBroadcastPartRequestedEventArgs> const& value)
    {
        return m_audioBroadcastPartRequested.add(value);
    }

    void VoipGroupManager::AudioBroadcastPartRequested(winrt::event_token const& token)
    {
        m_audioBroadcastPartRequested.remove(token);
    }



    winrt::event_token VoipGroupManager::VideoBroadcastPartRequested(Windows::Foundation::TypedEventHandler<
        winrt::Telegram::Native::Calls::VoipGroupManager,
        winrt::Telegram::Native::Calls::VideoBroadcastPartRequestedEventArgs> const& value)
    {
        return m_videoBroadcastPartRequested.add(value);
    }

    void VoipGroupManager::VideoBroadcastPartRequested(winrt::event_token const& token)
    {
        m_videoBroadcastPartRequested.remove(token);
    }



    winrt::event_token VoipGroupManager::BroadcastTimeRequested(Windows::Foundation::TypedEventHandler<
        winrt::Telegram::Native::Calls::VoipGroupManager,
        winrt::Telegram::Native::Calls::BroadcastTimeRequestedEventArgs> const& value)
    {
        return m_broadcastTimeRequested.add(value);
    }

    void VoipGroupManager::BroadcastTimeRequested(winrt::event_token const& token)
    {
        m_broadcastTimeRequested.remove(token);
    }



    winrt::event_token VoipGroupManager::RejoinRequested(Windows::Foundation::TypedEventHandler<
        winrt::Telegram::Native::Calls::VoipGroupManager,
        Windows::Foundation::IInspectable> const& value)
    {
        return m_rejoinRequested.add(value);
    }

    void VoipGroupManager::RejoinRequested(winrt::event_token const& token)
    {
        m_rejoinRequested.remove(token);
    }



    winrt::event_token VoipGroupManager::MediaChannelDescriptionsRequested(Windows::Foundation::TypedEventHandler<
        winrt::Telegram::Native::Calls::VoipGroupManager,
        winrt::Telegram::Native::Calls::MediaChannelDescriptionsRequestedEventArgs> const& value)
    {
        return m_mediaChannelDescriptionsRequested.add(value);
    }

    void VoipGroupManager::MediaChannelDescriptionsRequested(winrt::event_token const& token)
    {
        m_mediaChannelDescriptionsRequested.remove(token);
    }
}
