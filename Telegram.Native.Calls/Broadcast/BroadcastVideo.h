#pragma once

#include "BroadcastController.h"
#include "BroadcastQueue.h"
#include "BroadcastVideoPart.h"

#include "api/video/video_frame.h"
#include "api/video/video_sink_interface.h"

#include <deque>
#include <functional>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

namespace broadcast
{
    using VideoSink = std::weak_ptr<webrtc::VideoSinkInterface<webrtc::VideoFrame>>;

    class VideoEndpoint;

    // The broadcast's video tiles: each endpoint with parts decodes on its own thread and shows, at the host clock's
    // now, the frame of the segment the playhead is in. Every method may be called from any thread.
    class VideoHub
    {
    public:
        explicit VideoHub(std::function<int64_t()> nowUs);
        ~VideoHub();

        VideoHub(VideoHub const&) = delete;
        VideoHub& operator=(VideoHub const&) = delete;

        void AddSink(std::string const& endpointId, VideoSink sink);
        // A part for the segment `timestampMs`; a part given again for the same segment replaces it.
        void Deliver(std::string const& endpointId, int64_t timestampMs, Bytes data);
        // The playhead enters the segment at `hostUs`, `offsetMs` into it, for every endpoint.
        void Play(int64_t timestampMs, int64_t hostUs, int32_t offsetMs);
        // Every tile holds its frame until the next Play.
        void Pause();
        // Forgets every part and play; the sinks stay.
        void Reset();
        void Close();

    private:
        std::function<int64_t()> m_nowUs;

        std::mutex m_lock;
        bool m_closed = false;
        std::map<std::string, std::vector<VideoSink>> m_sinks;
        std::map<std::string, std::shared_ptr<VideoEndpoint>> m_endpoints;
    };

    class VideoEndpoint : public std::enable_shared_from_this<VideoEndpoint>
    {
    public:
        VideoEndpoint(std::function<int64_t()> nowUs, std::vector<VideoSink> sinks);

        void AddSink(VideoSink sink);
        void Deliver(int64_t timestampMs, Bytes data);
        void Play(int64_t timestampMs, int64_t hostUs, int32_t offsetMs);
        void Pause();
        void Close();

    private:
        struct Entry
        {
            Bytes data;
            std::unique_ptr<VideoPart> part;
            bool opened = false;
        };

        struct PlayRecord
        {
            int64_t timestampMs = 0;
            int64_t hostUs = 0;
            int32_t offsetMs = 0;
        };

        void Wake();
        void Tick();
        void Show(VideoPartFrame const& frame);

        std::function<int64_t()> m_nowUs;
        TaskQueue m_queue;

        // Queue thread.
        std::vector<VideoSink> m_sinks;
        std::shared_ptr<VideoDecoder> m_decoder;
        std::map<int64_t, Entry> m_parts;
        std::deque<PlayRecord> m_plays;
        bool m_ticking = false;
        int64_t m_shownTimestampMs = INT64_MIN;
        int m_shownIndex = -1;
    };
}
