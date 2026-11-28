#pragma once

#include <condition_variable>
#include <cstdint>
#include <functional>
#include <map>
#include <memory>
#include <mutex>
#include <thread>

namespace broadcast
{
    // A serial queue on its own thread, with delayed tasks on the steady clock (the audio core's). Posting after
    // Close is ignored, so callbacks from other threads can post without checking whether the call ended.
    class TaskQueue
    {
    public:
        TaskQueue();
        ~TaskQueue();

        TaskQueue(TaskQueue const&) = delete;
        TaskQueue& operator=(TaskQueue const&) = delete;

        void Post(std::function<void()> task);
        void PostDelayed(int64_t delayUs, std::function<void()> task);
        bool IsCurrent() const;

        // Lets the running task finish, drops the rest, runs `last` on the queue's thread and joins it. From the
        // queue's own thread (a task releasing the queue's last owner) it runs `last` there and then returns at once,
        // and the thread ends after that task.
        void Close(std::function<void()> last = nullptr);

    private:
        struct State
        {
            std::mutex lock;
            std::condition_variable wake;
            // Keyed by due time, then by order of posting.
            std::multimap<int64_t, std::function<void()>> tasks;
            bool closed = false;
            std::function<void()> last;
        };

        static void Run(std::shared_ptr<State> state);

        // Shared with the thread, which may outlive the queue when the queue is destroyed by one of its tasks.
        std::shared_ptr<State> m_state;
        std::thread m_thread;
    };
}
