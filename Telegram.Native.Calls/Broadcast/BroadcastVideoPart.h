#pragma once

#include "api/scoped_refptr.h"
#include "api/video/i420_buffer.h"
#include "api/video/video_rotation.h"

#include <cstddef>
#include <cstdint>
#include <deque>
#include <memory>
#include <optional>
#include <string>

namespace broadcast
{
    struct VideoPartFrame
    {
        int index = 0;
        // Seconds after the part's first frame.
        double pts = 0;
        webrtc::VideoRotation rotation = webrtc::kVideoRotation_0;
        webrtc::scoped_refptr<webrtc::I420Buffer> buffer;
    };

    struct VideoDecoderState;

    // One endpoint's decoder, in software. It keeps its codec while parts keep the same codec parameters and time
    // base, and is flushed between parts. A decoder and its parts belong to one thread.
    class VideoDecoder
    {
    public:
        VideoDecoder();
        ~VideoDecoder();

        VideoDecoder(VideoDecoder const&) = delete;
        VideoDecoder& operator=(VideoDecoder const&) = delete;

    private:
        friend class VideoPart;
        std::unique_ptr<VideoDecoderState> m_state;
    };

    struct VideoPartState;

    // One video or unified part (header, then a media slice). Frames decode lazily, at most two ahead of the one shown.
    class VideoPart
    {
    public:
        // nullptr when the header is unreadable, its container unknown, or the slice has no video stream.
        static std::unique_ptr<VideoPart> Open(std::shared_ptr<VideoDecoder> decoder, uint8_t const* data, size_t size);
        ~VideoPart();

        VideoPart(VideoPart const&) = delete;
        VideoPart& operator=(VideoPart const&) = delete;

        // The latest frame whose pts is at or before `seconds`, the first frame before its pts, the last after the
        // end; nullptr when the part has no frames. Valid until the next call.
        VideoPartFrame const* FrameAt(double seconds);

        std::string const& EndpointId() const;

    private:
        VideoPart();
        std::optional<VideoPartFrame> NextFrame();

        std::unique_ptr<VideoPartState> m_state;
        std::deque<VideoPartFrame> m_available;
        bool m_finished = false;
    };
}
