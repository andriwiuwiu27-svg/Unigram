#include "BroadcastStream.h"

#include "AudioDeviceHelper.h"

#include "macos/Sources/BroadcastAudio/BroadcastAudioClock.h"
#include "macos/Sources/BroadcastMedia/BroadcastMediaIO.h"
#include "macos/Sources/BroadcastMedia/BroadcastPartHeader.h"

#include "api/audio/audio_view.h"
#include "api/audio/create_audio_device_module.h"
#include "api/environment/environment_factory.h"

extern "C"
{
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
}

#include <algorithm>
#include <cstring>

namespace broadcast
{
    namespace
    {
        constexpr int kSampleRate = 48000;
        constexpr size_t kBlockFrames = 480;
        constexpr size_t kStopFrames = kSampleRate / 5;
        constexpr size_t kResumeFrames = kSampleRate;
        constexpr int64_t kPollUs = 50000;
        constexpr int64_t kPartMs = 1000;

        int16_t ToSample(float value)
        {
            return static_cast<int16_t>(std::clamp(value * 32767.0f, -32768.0f, 32767.0f));
        }

        bool SameAudioParameters(AVCodecParameters const& lhs, AVCodecParameters const& rhs)
        {
            if (lhs.codec_id != rhs.codec_id || lhs.extradata_size != rhs.extradata_size)
            {
                return false;
            }
            else if (lhs.extradata_size != 0 && std::memcmp(lhs.extradata, rhs.extradata, static_cast<size_t>(lhs.extradata_size)) != 0)
            {
                return false;
            }

            return lhs.sample_rate == rhs.sample_rate
                && lhs.ch_layout.nb_channels == rhs.ch_layout.nb_channels
                && lhs.format == rhs.format;
        }

        int64_t AlignDown(int64_t ms)
        {
            int64_t remainder = ms % kPartMs;
            return remainder < 0 ? ms - remainder - kPartMs : ms - remainder;
        }
    }

    UnifiedAudioDecoder::UnifiedAudioDecoder()
        : m_packet(av_packet_alloc())
        , m_frame(av_frame_alloc())
    {
    }

    UnifiedAudioDecoder::~UnifiedAudioDecoder()
    {
        Release();
        av_packet_free(&m_packet);
        av_frame_free(&m_frame);
    }

    void UnifiedAudioDecoder::Release()
    {
        if (m_context)
        {
            avcodec_free_context(&m_context);
        }

        if (m_parameters)
        {
            avcodec_parameters_free(&m_parameters);
        }
    }

    bool UnifiedAudioDecoder::Prepare(AVCodecParameters const* parameters)
    {
        if (m_context && m_parameters && SameAudioParameters(*m_parameters, *parameters))
        {
            return true;
        }

        Release();
        m_residual.clear();

        AVCodec const* codec = avcodec_find_decoder(parameters->codec_id);
        if (!codec)
        {
            return false;
        }

        m_context = avcodec_alloc_context3(codec);
        if (!m_context || avcodec_parameters_to_context(m_context, parameters) < 0 || avcodec_open2(m_context, codec, nullptr) < 0)
        {
            Release();
            return false;
        }

        m_parameters = avcodec_parameters_alloc();
        avcodec_parameters_copy(m_parameters, parameters);
        return true;
    }

    void UnifiedAudioDecoder::Decode(uint8_t const* data, size_t size, std::vector<int16_t>& output)
    {
        if (!m_packet || !m_frame)
        {
            return;
        }

        auto header = broadcast_media::parsePartHeader(data, size);
        if (!header)
        {
            return;
        }

        AVInputFormat const* inputFormat = av_find_input_format(header->container.c_str());
        if (!inputFormat)
        {
            return;
        }

        broadcast_media::MemoryIO io(data + header->sliceOffset, header->sliceSize);
        if (!io.context())
        {
            return;
        }

        AVFormatContext* format = avformat_alloc_context();
        if (!format)
        {
            return;
        }

        format->pb = io.context();

        // avformat_open_input frees the context when it fails.
        if (avformat_open_input(&format, "", inputFormat, nullptr) < 0)
        {
            return;
        }

        int streamIndex = -1;

        if (avformat_find_stream_info(format, nullptr) >= 0)
        {
            for (unsigned int i = 0; i < format->nb_streams; i++)
            {
                if (format->streams[i]->codecpar->codec_type == AVMEDIA_TYPE_AUDIO)
                {
                    streamIndex = static_cast<int>(i);
                    break;
                }
            }
        }

        if (streamIndex >= 0 && Prepare(format->streams[streamIndex]->codecpar))
        {
            m_context->pkt_timebase = format->streams[streamIndex]->time_base;

            while (av_read_frame(format, m_packet) >= 0)
            {
                if (m_packet->stream_index == streamIndex && avcodec_send_packet(m_context, m_packet) >= 0)
                {
                    while (avcodec_receive_frame(m_context, m_frame) == 0)
                    {
                        Append(m_frame, output);
                        av_frame_unref(m_frame);
                    }
                }

                av_packet_unref(m_packet);
            }
        }

        avformat_close_input(&format);
    }

    void UnifiedAudioDecoder::Append(AVFrame const* frame, std::vector<int16_t>& output)
    {
        int channels = frame->ch_layout.nb_channels;
        int rate = frame->sample_rate;

        if (channels <= 0 || rate <= 0 || rate % 100 != 0)
        {
            return;
        }
        else if (rate != m_sampleRate)
        {
            m_sampleRate = rate;
            m_residual.clear();
        }

        auto sample = [&](int index, int channel) -> int16_t
        {
            switch (frame->format)
            {
            case AV_SAMPLE_FMT_FLTP:
                return ToSample(reinterpret_cast<float const*>(frame->extended_data[channel])[index]);
            case AV_SAMPLE_FMT_FLT:
                return ToSample(reinterpret_cast<float const*>(frame->data[0])[index * channels + channel]);
            case AV_SAMPLE_FMT_S16P:
                return reinterpret_cast<int16_t const*>(frame->extended_data[channel])[index];
            case AV_SAMPLE_FMT_S16:
                return reinterpret_cast<int16_t const*>(frame->data[0])[index * channels + channel];
            default:
                return 0;
            }
        };

        switch (frame->format)
        {
        case AV_SAMPLE_FMT_FLTP:
        case AV_SAMPLE_FMT_FLT:
        case AV_SAMPLE_FMT_S16P:
        case AV_SAMPLE_FMT_S16:
            break;
        default:
            return;
        }

        for (int i = 0; i < frame->nb_samples; i++)
        {
            int16_t left = sample(i, 0);
            m_residual.push_back(left);
            m_residual.push_back(channels > 1 ? sample(i, 1) : left);
        }

        // The resampler takes whole 10 ms blocks.
        size_t chunk = static_cast<size_t>(rate / 100) * 2;
        size_t consumed = 0;

        if (rate == kSampleRate)
        {
            consumed = m_residual.size() - m_residual.size() % chunk;
            output.insert(output.end(), m_residual.begin(), m_residual.begin() + consumed);
        }
        else
        {
            m_block.resize(kBlockFrames * 2);

            while (m_residual.size() - consumed >= chunk)
            {
                m_resampler.Resample(
                    webrtc::InterleavedView<const int16_t>(m_residual.data() + consumed, chunk / 2, 2),
                    webrtc::InterleavedView<int16_t>(m_block.data(), kBlockFrames, 2));
                output.insert(output.end(), m_block.begin(), m_block.end());

                consumed += chunk;
            }
        }

        m_residual.erase(m_residual.begin(), m_residual.begin() + consumed);
    }

    StreamPlayer::StreamPlayer(std::string const& outputDeviceId, std::function<int64_t()> nowUs)
        : m_nowUs(std::move(nowUs))
        , m_env(webrtc::CreateEnvironment())
    {
        m_device = webrtc::CreateAudioDeviceModule(m_env, webrtc::AudioDeviceModule::kPlatformDefaultAudio);

        if (m_device && m_device->Init() == 0)
        {
            m_device->RegisterAudioCallback(this);
            tgcalls::SetAudioOutputDeviceById(m_device.get(), outputDeviceId);
        }
        else
        {
            m_device = nullptr;
        }
    }

    StreamPlayer::~StreamPlayer()
    {
        if (m_device)
        {
            m_device->StopPlayout();
            m_device->RegisterAudioCallback(nullptr);
            m_device->Terminate();
            m_device = nullptr;
        }
    }

    void StreamPlayer::SetOutputDevice(std::string const& id)
    {
        if (m_device)
        {
            tgcalls::SetAudioOutputDeviceById(m_device.get(), id);
        }
    }

    void StreamPlayer::Reset(int64_t positionMs, std::vector<int16_t>&& samples)
    {
        std::lock_guard lock(m_lock);
        m_buffer.assign(samples.begin(), samples.end());
        m_baseMs = positionMs;
        m_playedFrames = 0;
        m_lastRenderUs = m_nowUs();
        m_started = true;
        m_playing = m_buffer.size() / 2 >= kResumeFrames;
    }

    void StreamPlayer::Append(std::vector<int16_t> const& samples)
    {
        std::lock_guard lock(m_lock);
        m_buffer.insert(m_buffer.end(), samples.begin(), samples.end());
    }

    StreamPlayer::Report StreamPlayer::Current()
    {
        std::lock_guard lock(m_lock);

        Report report;
        report.hostUs = m_nowUs();
        report.playing = m_playing;
        report.positionMs = m_baseMs + m_playedFrames * 1000 / kSampleRate;

        if (m_playing)
        {
            // The device pulls a block at a time: the position between two pulls moves on the clock.
            report.positionMs += std::clamp<int64_t>((report.hostUs - m_lastRenderUs) / 1000, 0, 10);
        }

        return report;
    }

    int32_t StreamPlayer::RecordedDataIsAvailable(void const*, size_t, size_t, size_t, uint32_t, uint32_t, int32_t, uint32_t, bool, uint32_t& newMicLevel)
    {
        newMicLevel = 0;
        return 0;
    }

    int32_t StreamPlayer::NeedMorePlayData(size_t nSamples, size_t nBytesPerSample, size_t nChannels, uint32_t samplesPerSec, void* audioSamples, size_t& nSamplesOut, int64_t* elapsed_time_ms, int64_t* ntp_time_ms)
    {
        nSamplesOut = nSamples;
        *elapsed_time_ms = -1;
        *ntp_time_ms = -1;

        auto output = static_cast<int16_t*>(audioSamples);
        std::fill(output, output + nSamples * nChannels, int16_t(0));

        if (nChannels == 0 || nBytesPerSample != nChannels * sizeof(int16_t) || nSamples * 100 != samplesPerSec)
        {
            return 0;
        }

        bool have = false;

        {
            std::lock_guard lock(m_lock);

            size_t ahead = m_buffer.size() / 2;
            if (m_started && !m_playing && ahead >= kResumeFrames)
            {
                m_playing = true;
            }

            if (m_playing && ahead >= kBlockFrames)
            {
                std::copy_n(m_buffer.begin(), kBlockFrames * 2, m_block);
                m_buffer.erase(m_buffer.begin(), m_buffer.begin() + kBlockFrames * 2);
                m_playedFrames += kBlockFrames;
                m_lastRenderUs = m_nowUs();
                have = true;

                if (ahead - kBlockFrames < kStopFrames)
                {
                    m_playing = false;
                }
            }
            else
            {
                m_playing = false;
            }
        }

        if (!have)
        {
            return 0;
        }

        // 10 ms of 48 kHz stereo to the device's channels, then to its rate.
        size_t channels = nChannels;
        int16_t* target = output;

        if (samplesPerSec != kSampleRate)
        {
            m_converted.resize(kBlockFrames * channels);
            target = m_converted.data();
        }

        for (size_t i = 0; i < kBlockFrames; i++)
        {
            int16_t left = m_block[i * 2];
            int16_t right = m_block[i * 2 + 1];

            if (channels == 1)
            {
                target[i] = static_cast<int16_t>((left + right) / 2);
            }
            else
            {
                target[i * channels] = left;
                target[i * channels + 1] = right;

                for (size_t channel = 2; channel < channels; channel++)
                {
                    target[i * channels + channel] = 0;
                }
            }
        }

        if (samplesPerSec != kSampleRate)
        {
            m_resampler.Resample(
                webrtc::InterleavedView<const int16_t>(m_converted.data(), kBlockFrames, channels),
                webrtc::InterleavedView<int16_t>(output, nSamples, channels));
        }

        return 0;
    }

    void StreamPlayer::PullRenderData(int, int, size_t, size_t, void*, int64_t*, int64_t*)
    {
    }

    StreamSession::StreamSession(std::shared_ptr<TaskQueue> queue, std::shared_ptr<VideoHub> video, SessionHost host, std::string outputDeviceId)
        : m_queue(std::move(queue))
        , m_video(std::move(video))
        , m_host(std::move(host))
        , m_outputDeviceId(std::move(outputDeviceId))
    {
    }

    StreamSession::~StreamSession()
    {
        Stop();
    }

    void StreamSession::Start()
    {
        if (m_controller)
        {
            return;
        }

        std::weak_ptr<StreamSession> weak = weak_from_this();
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

        std::function<int64_t()> nowUs = broadcast_audio::defaultClockMicroseconds;
        auto host = MakeControllerHost(m_queue, nowUs, m_host, withController);

        host.deliver = [this](DeliveredSegment const& segment)
        {
            Deliver(segment);
        };

        host.replaceVideo = [](int64_t, LoadedPart const&) {};
        host.play = [](int64_t, int64_t, int32_t) {};

        host.setUnifiedPosition = [this](int64_t positionMs)
        {
            SetPosition(positionMs);
        };

        m_player = std::make_unique<StreamPlayer>(m_outputDeviceId, nowUs);
        m_controller = std::make_unique<Controller>(StreamFormat::Unified, std::move(host), BandwidthEstimator::Shared());
        m_controller->Start();

        Poll();
    }

    void StreamSession::Stop()
    {
        if (!m_controller)
        {
            return;
        }

        m_controller->Stop();
        m_controller.reset();
        m_player.reset();
        m_pending.clear();
        m_positioned = false;
        m_videoPlaying = false;
        m_announcedTs = INT64_MIN;
        m_video->Reset();
    }

    void StreamSession::Rejoined()
    {
        if (m_controller)
        {
            m_controller->Rejoined();
        }
    }

    void StreamSession::SetRequestedVideoChannels(std::vector<VideoChannelRequest>)
    {
    }

    void StreamSession::SetOutputDevice(std::string const& id)
    {
        m_outputDeviceId = id;

        if (m_player)
        {
            m_player->SetOutputDevice(id);
        }
    }

    void StreamSession::Deliver(DeliveredSegment const& segment)
    {
        auto const& data = segment.primary.data;
        if (!data)
        {
            return;
        }

        auto& part = m_pending[segment.timestampMs];
        part.video = data;
        m_decoder.Decode(data->data(), data->size(), part.samples);

        // The controller hands a start or a jump over as its parts followed by the position, within one task: the
        // parts wait for that task to end, so a position given right after them finds them here.
        if (!m_flushPosted)
        {
            m_flushPosted = true;
            m_queue->Post([weak = weak_from_this()]
            {
                if (auto strong = weak.lock())
                {
                    strong->m_flushPosted = false;
                    strong->Flush();
                }
            });
        }
    }

    void StreamSession::SetPosition(int64_t positionMs)
    {
        if (!m_player)
        {
            return;
        }

        // A new hub endpoint: it prunes parts older than what plays, so after the timeline starts over it would drop
        // the new parts while the old ones play, and keep the old ones for good.
        m_video->Reset();

        std::vector<int16_t> samples;

        for (auto& [timestamp, part] : m_pending)
        {
            if (timestamp + kPartMs <= positionMs)
            {
                continue;
            }

            size_t skip = 0;
            if (timestamp < positionMs)
            {
                skip = std::min(part.samples.size(), static_cast<size_t>((positionMs - timestamp) * kSampleRate / 1000) * 2);
            }

            samples.insert(samples.end(), part.samples.begin() + skip, part.samples.end());
            m_video->Deliver("unified", timestamp, part.video);
        }

        m_pending.clear();
        m_positioned = true;
        m_player->Reset(positionMs, std::move(samples));

        m_videoPlaying = false;
        m_announcedTs = INT64_MIN;
    }

    void StreamSession::Flush()
    {
        if (!m_player || !m_positioned)
        {
            return;
        }

        for (auto const& [timestamp, part] : m_pending)
        {
            m_video->Deliver("unified", timestamp, part.video);
            m_player->Append(part.samples);
        }

        m_pending.clear();
    }

    void StreamSession::Poll()
    {
        if (!m_controller || !m_player)
        {
            return;
        }

        if (m_positioned)
        {
            auto report = m_player->Current();
            m_controller->UpdatePlayback(report.positionMs, report.playing);

            if (report.playing)
            {
                // The tile follows the audio: the segment it is in, and the next one at its predicted start.
                int64_t timestamp = AlignDown(report.positionMs);
                if (!m_videoPlaying || timestamp != m_announcedTs)
                {
                    int64_t startUs = report.hostUs - (report.positionMs - timestamp) * 1000;
                    m_video->Play(timestamp, startUs, 0);
                    m_video->Play(timestamp + kPartMs, startUs + kPartMs * 1000, 0);
                    m_announcedTs = timestamp;
                    m_videoPlaying = true;
                }
            }
            else if (m_videoPlaying)
            {
                m_video->Pause();
                m_videoPlaying = false;
                m_announcedTs = INT64_MIN;
            }
        }

        m_queue->PostDelayed(kPollUs, [weak = weak_from_this()]
        {
            if (auto strong = weak.lock())
            {
                strong->Poll();
            }
        });
    }
}
