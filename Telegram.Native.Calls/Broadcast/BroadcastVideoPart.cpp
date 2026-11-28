#include "BroadcastVideoPart.h"

#include "macos/Sources/BroadcastMedia/BroadcastMediaIO.h"
#include "macos/Sources/BroadcastMedia/BroadcastPartHeader.h"

extern "C"
{
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/pixdesc.h>
}

#include <cstring>

namespace broadcast
{
    namespace
    {
        bool SameCodecParameters(AVCodecParameters const& lhs, AVCodecParameters const& rhs)
        {
            if (lhs.codec_id != rhs.codec_id || lhs.extradata_size != rhs.extradata_size)
            {
                return false;
            }
            else if (lhs.extradata_size != 0 && std::memcmp(lhs.extradata, rhs.extradata, static_cast<size_t>(lhs.extradata_size)) != 0)
            {
                return false;
            }

            return lhs.format == rhs.format
                && lhs.profile == rhs.profile
                && lhs.level == rhs.level
                && lhs.width == rhs.width
                && lhs.height == rhs.height
                && lhs.sample_aspect_ratio.num == rhs.sample_aspect_ratio.num
                && lhs.sample_aspect_ratio.den == rhs.sample_aspect_ratio.den
                && lhs.field_order == rhs.field_order
                && lhs.color_range == rhs.color_range
                && lhs.color_primaries == rhs.color_primaries
                && lhs.color_trc == rhs.color_trc
                && lhs.color_space == rhs.color_space
                && lhs.chroma_location == rhs.chroma_location;
        }

        webrtc::VideoRotation ToRotation(int32_t rotation)
        {
            switch (rotation)
            {
            case 90:
                return webrtc::kVideoRotation_90;
            case 180:
                return webrtc::kVideoRotation_180;
            case 270:
                return webrtc::kVideoRotation_270;
            default:
                return webrtc::kVideoRotation_0;
            }
        }
    }

    struct VideoDecoderState
    {
        AVCodecContext* context = nullptr;
        AVCodecParameters* parameters = nullptr;
        AVRational timeBase = { 0, 1 };
        // The part whose frames the codec holds.
        void const* activePart = nullptr;

        ~VideoDecoderState()
        {
            Release();
        }

        void Release()
        {
            if (context)
            {
                avcodec_free_context(&context);
            }

            if (parameters)
            {
                avcodec_parameters_free(&parameters);
            }
        }

        bool Prepare(AVCodecParameters const* newParameters, AVRational newTimeBase)
        {
            if (context && parameters && SameCodecParameters(*parameters, *newParameters) && timeBase.num == newTimeBase.num && timeBase.den == newTimeBase.den)
            {
                return true;
            }

            Release();

            AVCodec const* codec = avcodec_find_decoder(newParameters->codec_id);
            if (!codec)
            {
                return false;
            }

            context = avcodec_alloc_context3(codec);
            if (!context || avcodec_parameters_to_context(context, newParameters) < 0)
            {
                Release();
                return false;
            }

            context->pkt_timebase = newTimeBase;

            if (avcodec_open2(context, codec, nullptr) < 0)
            {
                Release();
                return false;
            }

            parameters = avcodec_parameters_alloc();
            avcodec_parameters_copy(parameters, newParameters);
            timeBase = newTimeBase;
            return true;
        }
    };

    VideoDecoder::VideoDecoder()
        : m_state(std::make_unique<VideoDecoderState>())
    {
    }

    VideoDecoder::~VideoDecoder() = default;

    struct VideoPartState
    {
        std::shared_ptr<VideoDecoder> decoder;
        std::unique_ptr<broadcast_media::MemoryIO> io;
        AVFormatContext* format = nullptr;
        int streamIndex = -1;
        AVCodecParameters* parameters = nullptr;
        AVRational timeBase = { 0, 1 };
        std::string endpointId;
        webrtc::VideoRotation rotation = webrtc::kVideoRotation_0;
        AVPacket* packet = nullptr;
        AVFrame* frame = nullptr;
        int frameIndex = 0;
        bool hasFirstPts = false;
        double firstPts = 0;
        bool sentEnd = false;

        ~VideoPartState()
        {
            if (format)
            {
                avformat_close_input(&format);
            }

            if (parameters)
            {
                avcodec_parameters_free(&parameters);
            }

            av_packet_free(&packet);
            av_frame_free(&frame);
        }
    };

    VideoPart::VideoPart()
        : m_state(std::make_unique<VideoPartState>())
    {
    }

    VideoPart::~VideoPart()
    {
        if (m_state->decoder)
        {
            auto& decoder = *m_state->decoder->m_state;
            if (decoder.activePart == m_state.get())
            {
                decoder.activePart = nullptr;
            }
        }
    }

    std::unique_ptr<VideoPart> VideoPart::Open(std::shared_ptr<VideoDecoder> decoder, uint8_t const* data, size_t size)
    {
        if (!decoder)
        {
            return nullptr;
        }

        auto header = broadcast_media::parsePartHeader(data, size);
        if (!header)
        {
            return nullptr;
        }

        AVInputFormat const* inputFormat = av_find_input_format(header->container.c_str());
        if (!inputFormat)
        {
            return nullptr;
        }

        auto part = std::unique_ptr<VideoPart>(new VideoPart());
        auto& state = *part->m_state;
        state.decoder = std::move(decoder);
        state.endpointId = header->endpointId;
        state.rotation = ToRotation(header->rotation);
        state.io = std::make_unique<broadcast_media::MemoryIO>(data + header->sliceOffset, header->sliceSize);
        state.packet = av_packet_alloc();
        state.frame = av_frame_alloc();

        if (!state.io->context() || !state.packet || !state.frame)
        {
            return nullptr;
        }

        state.format = avformat_alloc_context();
        if (!state.format)
        {
            return nullptr;
        }

        state.format->pb = state.io->context();

        // avformat_open_input frees the context when it fails.
        if (avformat_open_input(&state.format, "", inputFormat, nullptr) < 0)
        {
            state.format = nullptr;
            return nullptr;
        }
        else if (avformat_find_stream_info(state.format, nullptr) < 0)
        {
            return nullptr;
        }

        for (unsigned int i = 0; i < state.format->nb_streams; i++)
        {
            AVStream* stream = state.format->streams[i];
            if (stream->codecpar->codec_type != AVMEDIA_TYPE_VIDEO)
            {
                continue;
            }

            state.streamIndex = static_cast<int>(i);
            state.parameters = avcodec_parameters_alloc();
            avcodec_parameters_copy(state.parameters, stream->codecpar);
            state.timeBase = stream->time_base;
            break;
        }

        if (state.streamIndex < 0)
        {
            return nullptr;
        }

        return part;
    }

    std::string const& VideoPart::EndpointId() const
    {
        return m_state->endpointId;
    }

    std::optional<VideoPartFrame> VideoPart::NextFrame()
    {
        auto& state = *m_state;
        auto& decoder = *state.decoder->m_state;

        auto finish = [&]
        {
            if (decoder.context)
            {
                avcodec_flush_buffers(decoder.context);
            }

            if (decoder.activePart == &state)
            {
                decoder.activePart = nullptr;
            }

            return std::optional<VideoPartFrame>();
        };

        if (decoder.activePart != &state || !decoder.context)
        {
            if (!decoder.Prepare(state.parameters, state.timeBase))
            {
                decoder.activePart = nullptr;
                return std::nullopt;
            }

            // Whatever an earlier part left in the codec is not this part's.
            avcodec_flush_buffers(decoder.context);
            decoder.activePart = &state;
        }

        while (true)
        {
            int received = avcodec_receive_frame(decoder.context, state.frame);
            if (received == 0)
            {
                AVFrame* source = state.frame;
                std::optional<VideoPartFrame> frame;

                if (source->format == AV_PIX_FMT_YUV420P || source->format == AV_PIX_FMT_YUVJ420P)
                {
                    int64_t timestamp = source->pts != AV_NOPTS_VALUE ? source->pts : source->best_effort_timestamp;
                    double seconds = static_cast<double>(timestamp) * av_q2d(state.timeBase);

                    if (!state.hasFirstPts)
                    {
                        state.hasFirstPts = true;
                        state.firstPts = seconds;
                    }

                    frame.emplace();
                    frame->index = state.frameIndex++;
                    frame->pts = seconds - state.firstPts;
                    frame->rotation = state.rotation;
                    frame->buffer = webrtc::I420Buffer::Copy(source->width, source->height,
                        source->data[0], source->linesize[0],
                        source->data[1], source->linesize[1],
                        source->data[2], source->linesize[2]);
                }

                av_frame_unref(source);

                if (frame)
                {
                    return frame;
                }

                continue;
            }
            else if (received != AVERROR(EAGAIN) || state.sentEnd)
            {
                return finish();
            }

            while (true)
            {
                if (av_read_frame(state.format, state.packet) < 0)
                {
                    avcodec_send_packet(decoder.context, nullptr);
                    state.sentEnd = true;
                    break;
                }
                else if (state.packet->stream_index != state.streamIndex)
                {
                    av_packet_unref(state.packet);
                    continue;
                }

                int sent = avcodec_send_packet(decoder.context, state.packet);
                av_packet_unref(state.packet);

                if (sent < 0 && sent != AVERROR(EAGAIN))
                {
                    return finish();
                }

                break;
            }
        }
    }

    VideoPartFrame const* VideoPart::FrameAt(double seconds)
    {
        while (true)
        {
            while (m_available.size() >= 2 && seconds >= m_available[1].pts)
            {
                m_available.pop_front();
            }

            if (m_available.size() < 2 && !m_finished)
            {
                if (auto frame = NextFrame())
                {
                    m_available.push_back(std::move(*frame));
                }
                else
                {
                    m_finished = true;
                }

                continue;
            }
            else if (m_available.empty())
            {
                return nullptr;
            }

            for (size_t i = 1; i < m_available.size(); i++)
            {
                if (seconds < m_available[i].pts)
                {
                    return &m_available[i - 1];
                }
            }

            return &m_available.back();
        }
    }
}
