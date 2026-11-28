#pragma once

#include "BroadcastSession.h"

#include "api/scoped_refptr.h"
#include "api/environment/environment.h"
#include "common_audio/resampler/include/push_resampler.h"
#include "modules/audio_device/include/audio_device.h"
#include "modules/audio_device/include/audio_device_defines.h"

#include <cstdint>
#include <deque>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

struct AVCodecContext;
struct AVCodecParameters;
struct AVFrame;
struct AVPacket;

namespace broadcast
{
    // The AAC of unified parts as 48 kHz interleaved stereo. One decoder for the whole stream: AAC frames do not line
    // up with part boundaries, and a fresh decoder would start every second with its priming silence.
    class UnifiedAudioDecoder
    {
    public:
        UnifiedAudioDecoder();
        ~UnifiedAudioDecoder();

        UnifiedAudioDecoder(UnifiedAudioDecoder const&) = delete;
        UnifiedAudioDecoder& operator=(UnifiedAudioDecoder const&) = delete;

        // Appends the part's audio to `output`.
        void Decode(uint8_t const* data, size_t size, std::vector<int16_t>& output);

    private:
        bool Prepare(AVCodecParameters const* parameters);
        void Release();
        void Append(AVFrame const* frame, std::vector<int16_t>& output);

        AVCodecContext* m_context = nullptr;
        AVCodecParameters* m_parameters = nullptr;
        AVPacket* m_packet = nullptr;
        AVFrame* m_frame = nullptr;

        int m_sampleRate = 0;
        webrtc::PushResampler<int16_t> m_resampler;
        // Source-rate stereo not yet a whole 10 ms block.
        std::vector<int16_t> m_residual;
        std::vector<int16_t> m_block;
    };

    // Plays the stream's audio on its own device and is the stream's clock: the position moves only while it plays.
    // It plays from a continuous buffer, stops when less than 0.2 s is left, and starts again once a second is there.
    class StreamPlayer : public webrtc::AudioTransport
    {
    public:
        struct Report
        {
            int64_t positionMs = 0;
            int64_t hostUs = 0;
            bool playing = false;
        };

        StreamPlayer(std::string const& outputDeviceId, std::function<int64_t()> nowUs);
        ~StreamPlayer() override;

        void SetOutputDevice(std::string const& id);
        // Replaces the buffer: `samples` start at `positionMs`.
        void Reset(int64_t positionMs, std::vector<int16_t>&& samples);
        void Append(std::vector<int16_t> const& samples);
        Report Current();

        int32_t RecordedDataIsAvailable(void const* audioSamples, size_t nSamples, size_t nBytesPerSample, size_t nChannels, uint32_t samplesPerSec, uint32_t totalDelayMS, int32_t clockDrift, uint32_t currentMicLevel, bool keyPressed, uint32_t& newMicLevel) override;
        int32_t NeedMorePlayData(size_t nSamples, size_t nBytesPerSample, size_t nChannels, uint32_t samplesPerSec, void* audioSamples, size_t& nSamplesOut, int64_t* elapsed_time_ms, int64_t* ntp_time_ms) override;
        void PullRenderData(int bits_per_sample, int sample_rate, size_t number_of_channels, size_t number_of_frames, void* audio_data, int64_t* elapsed_time_ms, int64_t* ntp_time_ms) override;

    private:
        void StartPlayout();
        void StopPlayout();

        std::function<int64_t()> m_nowUs;
        webrtc::Environment m_env;
        webrtc::scoped_refptr<webrtc::AudioDeviceModule> m_device;

        std::mutex m_lock;
        std::deque<int16_t> m_buffer;
        bool m_started = false;
        bool m_playing = false;
        int64_t m_baseMs = 0;
        int64_t m_playedFrames = 0;
        int64_t m_lastRenderUs = 0;

        // Device thread.
        int16_t m_block[480 * 2] = {};
        std::vector<int16_t> m_converted;
        webrtc::PushResampler<int16_t> m_resampler;
    };

    // An RTMP stream or RTMP live story: no engine, unified parts on their own player.
    class StreamSession : public Session, public std::enable_shared_from_this<StreamSession>
    {
    public:
        StreamSession(std::shared_ptr<TaskQueue> queue, std::shared_ptr<VideoHub> video, SessionHost host, std::string outputDeviceId);
        ~StreamSession() override;

        void Start() override;
        void Stop() override;
        void Rejoined() override;
        void SetRequestedVideoChannels(std::vector<VideoChannelRequest> channels) override;
        void SetOutputDevice(std::string const& id);

    private:
        void Deliver(DeliveredSegment const& segment);
        void SetPosition(int64_t positionMs);
        void Flush();
        void Poll();

        std::shared_ptr<TaskQueue> m_queue;
        std::shared_ptr<VideoHub> m_video;
        SessionHost m_host;
        std::string m_outputDeviceId;

        std::unique_ptr<Controller> m_controller;
        std::unique_ptr<StreamPlayer> m_player;
        UnifiedAudioDecoder m_decoder;

        struct PendingPart
        {
            std::vector<int16_t> samples;
            Bytes video;
        };

        // Parts the player and the video hub have not taken yet, by timestamp.
        std::map<int64_t, PendingPart> m_pending;
        bool m_flushPosted = false;
        bool m_positioned = false;

        bool m_videoPlaying = false;
        int64_t m_announcedTs = INT64_MIN;
    };
}
