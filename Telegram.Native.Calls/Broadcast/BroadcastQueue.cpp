#include "BroadcastQueue.h"

#include <algorithm>
#include <chrono>

namespace broadcast
{
    namespace
    {
        int64_t NowUs()
        {
            return std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
        }
    }

    TaskQueue::TaskQueue()
        : m_state(std::make_shared<State>())
        , m_thread(&TaskQueue::Run, m_state)
    {
    }

    TaskQueue::~TaskQueue()
    {
        Close();
    }

    void TaskQueue::Post(std::function<void()> task)
    {
        PostDelayed(0, std::move(task));
    }

    void TaskQueue::PostDelayed(int64_t delayUs, std::function<void()> task)
    {
        {
            std::lock_guard lock(m_state->lock);

            if (m_state->closed)
            {
                return;
            }

            m_state->tasks.emplace(NowUs() + std::max<int64_t>(delayUs, 0), std::move(task));
        }

        m_state->wake.notify_one();
    }

    bool TaskQueue::IsCurrent() const
    {
        return std::this_thread::get_id() == m_thread.get_id();
    }

    void TaskQueue::Close(std::function<void()> last)
    {
        // Tasks hold captures that may own whatever posted them, so they are released off the lock.
        std::multimap<int64_t, std::function<void()>> dropped;

        {
            std::lock_guard lock(m_state->lock);

            if (m_state->closed)
            {
                return;
            }

            m_state->closed = true;
            dropped.swap(m_state->tasks);

            if (!IsCurrent())
            {
                m_state->last = std::move(last);
            }
        }

        m_state->wake.notify_one();

        if (IsCurrent())
        {
            if (last)
            {
                last();
            }

            m_thread.detach();
        }
        else if (m_thread.joinable())
        {
            m_thread.join();
        }
    }

    void TaskQueue::Run(std::shared_ptr<State> state)
    {
        std::unique_lock lock(state->lock);

        while (!state->closed)
        {
            if (state->tasks.empty())
            {
                state->wake.wait(lock);
                continue;
            }

            auto next = state->tasks.begin();
            int64_t now = NowUs();

            if (next->first > now)
            {
                state->wake.wait_for(lock, std::chrono::microseconds(next->first - now));
                continue;
            }

            auto task = std::move(next->second);
            state->tasks.erase(next);

            lock.unlock();
            task();
            task = nullptr;
            lock.lock();
        }

        auto last = std::move(state->last);
        lock.unlock();

        if (last)
        {
            last();
        }
    }
}
