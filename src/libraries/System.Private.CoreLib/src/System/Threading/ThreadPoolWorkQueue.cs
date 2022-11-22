// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

/*=============================================================================
**
**
**
** Purpose: Class for creating and managing a threadpool
**
**
=============================================================================*/

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Internal.Runtime.CompilerServices;
using Microsoft.Win32.SafeHandles;

namespace System.Threading
{
    internal sealed partial class ThreadPoolWorkQueue
    {
        #region WorkStealingQueueList

        /// <summary>
        /// 本地队列列表，与线程绑定
        /// </summary>
        internal static class WorkStealingQueueList
        {
#pragma warning disable CA1825 // avoid the extra generic instantation for Array.Empty<T>(); this is the only place we'll ever create this array
            private static volatile WorkStealingQueue[] _queues = new WorkStealingQueue[0];
#pragma warning restore CA1825

            /// <summary>
            /// 所有线程本地队列
            /// </summary>
            public static WorkStealingQueue[] Queues => _queues;

            /// <summary>
            /// 添加一个队列，ThreadPoolWorkQueueThreadLocals 创建时调用
            /// </summary>
            /// <param name="queue"></param>
            public static void Add(WorkStealingQueue queue)
            {
                Debug.Assert(queue != null);
                while (true)
                {
                    //所以创建线程还有额外 WorkStealingQueue 开销
                    WorkStealingQueue[] oldQueues = _queues;
                    Debug.Assert(Array.IndexOf(oldQueues, queue) == -1);

                    var newQueues = new WorkStealingQueue[oldQueues.Length + 1];
                    Array.Copy(oldQueues, newQueues, oldQueues.Length);
                    newQueues[^1] = queue;
                    if (Interlocked.CompareExchange(ref _queues, newQueues, oldQueues) == oldQueues)
                    {
                        break;
                    }
                }
            }

            public static void Remove(WorkStealingQueue queue)
            {
                Debug.Assert(queue != null);
                while (true)
                {
                    WorkStealingQueue[] oldQueues = _queues;
                    if (oldQueues.Length == 0)
                    {
                        return;
                    }

                    int pos = Array.IndexOf(oldQueues, queue);
                    if (pos == -1)
                    {
                        Debug.Fail("Should have found the queue");
                        return;
                    }

                    var newQueues = new WorkStealingQueue[oldQueues.Length - 1];
                    if (pos == 0)
                    {
                        Array.Copy(oldQueues, 1, newQueues, 0, newQueues.Length);
                    }
                    else if (pos == oldQueues.Length - 1)
                    {
                        Array.Copy(oldQueues, newQueues, newQueues.Length);
                    }
                    else
                    {
                        Array.Copy(oldQueues, newQueues, pos);
                        Array.Copy(oldQueues, pos + 1, newQueues, pos, newQueues.Length - pos);
                    }

                    if (Interlocked.CompareExchange(ref _queues, newQueues, oldQueues) == oldQueues)
                    {
                        break;
                    }
                }
            }
        }
        #endregion

        #region WorkStealingQueue

        /// <summary>
        /// 线程本地队列，Array，内有头尾指针
        /// </summary>
        internal sealed class WorkStealingQueue
        {
            private const int INITIAL_SIZE = 32;
            internal volatile object?[] m_array = new object[INITIAL_SIZE]; // SOS's ThreadPool command depends on this name
            /// <summary>
            /// 数组容量 -1
            /// </summary>
            private volatile int m_mask = INITIAL_SIZE - 1;

#if DEBUG
            // in debug builds, start at the end so we exercise the index reset logic.
            private const int START_INDEX = int.MaxValue;
#else
            private const int START_INDEX = 0;
#endif

            private volatile int m_headIndex = START_INDEX;
            private volatile int m_tailIndex = START_INDEX;

            private SpinLock m_foreignLock = new SpinLock(enableThreadOwnerTracking: false);

            /// <summary>
            /// Work 入队
            /// </summary>
            /// <param name="obj"></param>
            public void LocalPush(object obj)
            {
                int tail = m_tailIndex;

                // We're going to increment the tail; if we'll overflow, then we need to reset our counts
                if (tail == int.MaxValue)
                {
                    tail = LocalPush_HandleTailOverflow();
                }

                //存在两个空位直接插入，较小的并发冲突？
                // When there are at least 2 elements' worth of space, we can take the fast path.
                if (tail < m_headIndex + m_mask)
                {
                    Volatile.Write(ref m_array[tail & m_mask], obj);
                    m_tailIndex = tail + 1;
                }
                else
                {
                    //不足两个空位时扩容，容量翻倍
                    // We need to contend with foreign pops, so we lock.
                    bool lockTaken = false;
                    try
                    {
                        m_foreignLock.Enter(ref lockTaken);

                        int head = m_headIndex;
                        int count = m_tailIndex - m_headIndex;

                        // If there is still space (one left), just add the element.
                        if (count >= m_mask)
                        {
                            // We're full; expand the queue by doubling its size.
                            var newArray = new object?[m_array.Length << 1]; //q: 位移负数如何处理？Array.MaxLength => 0X7FFFFFC7;
                                                                             // will throw OutOfMemoryException
                            for (int i = 0; i < m_array.Length; i++)
                                newArray[i] = m_array[(i + head) & m_mask];

                            // Reset the field values, incl. the mask.
                            m_array = newArray;
                            m_headIndex = 0;
                            m_tailIndex = tail = count;
                            m_mask = (m_mask << 1) | 1; //m_mask 为数组容量减1，翻倍之后差2，所以此处加1
                        }

                        Volatile.Write(ref m_array[tail & m_mask], obj);
                        m_tailIndex = tail + 1;
                    }
                    finally
                    {
                        if (lockTaken)
                            m_foreignLock.Exit(useMemoryBarrier: false);
                    }
                }
            }

            /// <summary>
            /// 尾部溢出重置头尾指针，m_tailIndex == int.MaxValue
            /// </summary>
            /// <returns></returns>
            [MethodImpl(MethodImplOptions.NoInlining)]
            private int LocalPush_HandleTailOverflow()
            {
                bool lockTaken = false;
                try
                {
                    m_foreignLock.Enter(ref lockTaken);

                    int tail = m_tailIndex;
                    if (tail == int.MaxValue)
                    {
                        //
                        // Rather than resetting to zero, we'll just mask off the bits we don't care about.
                        // This way we don't need to rearrange the items already in the queue; they'll be found
                        // correctly exactly where they are.  One subtlety here is that we need to make sure that
                        // if head is currently < tail, it remains that way.  This happens to just fall out from
                        // the bit-masking, because we only do this if tail == int.MaxValue, meaning that all
                        // bits are set, so all of the bits we're keeping will also be set.  Thus it's impossible
                        // for the head to end up > than the tail, since you can't set any more bits than all of
                        // them.

                        // 头尾指针使用掩码重置
                        m_headIndex &= m_mask;
                        m_tailIndex = tail = m_tailIndex & m_mask;
                        Debug.Assert(m_headIndex <= m_tailIndex);
                    }

                    return tail;
                }
                finally
                {
                    if (lockTaken)
                        m_foreignLock.Exit(useMemoryBarrier: true);
                }
            }

            public bool LocalFindAndPop(object obj)
            {
                // Fast path: check the tail. If equal, we can skip the lock.
                if (m_array[(m_tailIndex - 1) & m_mask] == obj)
                {
                    object? unused = LocalPop();
                    Debug.Assert(unused == null || unused == obj);
                    return unused != null;
                }

                // Else, do an O(N) search for the work item. The theory of work stealing and our
                // inlining logic is that most waits will happen on recently queued work.  And
                // since recently queued work will be close to the tail end (which is where we
                // begin our search), we will likely find it quickly.  In the worst case, we
                // will traverse the whole local queue; this is typically not going to be a
                // problem (although degenerate cases are clearly an issue) because local work
                // queues tend to be somewhat shallow in length, and because if we fail to find
                // the work item, we are about to block anyway (which is very expensive).
                for (int i = m_tailIndex - 2; i >= m_headIndex; i--)
                {
                    if (m_array[i & m_mask] == obj)
                    {
                        // If we found the element, block out steals to avoid interference.
                        bool lockTaken = false;
                        try
                        {
                            m_foreignLock.Enter(ref lockTaken);

                            // If we encountered a race condition, bail.
                            if (m_array[i & m_mask] == null)
                                return false;

                            // Otherwise, null out the element.
                            Volatile.Write(ref m_array[i & m_mask], null);

                            // And then check to see if we can fix up the indexes (if we're at
                            // the edge).  If we can't, we just leave nulls in the array and they'll
                            // get filtered out eventually (but may lead to superfluous resizing).
                            if (i == m_tailIndex)
                                m_tailIndex--;
                            else if (i == m_headIndex)
                                m_headIndex++;

                            return true;
                        }
                        finally
                        {
                            if (lockTaken)
                                m_foreignLock.Exit(useMemoryBarrier: false);
                        }
                    }
                }

                return false;
            }

            /// <summary>
            /// 线程本地队列获取任务，从队尾取
            /// </summary>
            /// <returns></returns>
            public object? LocalPop() => m_headIndex < m_tailIndex ? LocalPopCore() : null;

            private object? LocalPopCore()
            {
                while (true)
                {
                    int tail = m_tailIndex;
                    if (m_headIndex >= tail)
                    {
                        return null;
                    }

                    // Decrement the tail using a fence to ensure subsequent read doesn't come before.
                    tail--;
                    //使用栅栏来确保 m_headIndex 的读取不会出现在前面。
                    Interlocked.Exchange(ref m_tailIndex, tail);//q: 为什么不使用 Interlocked.Increment

                    // If there is no interaction with a take, we can head down the fast path.
                    if (m_headIndex <= tail)
                    {
                        int idx = tail & m_mask;
                        object? obj = Volatile.Read(ref m_array[idx]);//局部变量内存屏使用 Volatile

                        // Check for nulls in the array.
                        if (obj == null) continue;

                        m_array[idx] = null;
                        return obj;
                    }
                    else
                    {
                        // Interaction with takes: 0 or 1 elements left.
                        bool lockTaken = false;
                        try
                        {
                            //偷窃 TrySteal 时会产生并发，只在临界条件处加锁
                            m_foreignLock.Enter(ref lockTaken);

                            if (m_headIndex <= tail)
                            {
                                // Element still available. Take it.
                                int idx = tail & m_mask;
                                object? obj = Volatile.Read(ref m_array[idx]);

                                // Check for nulls in the array.
                                if (obj == null) continue;

                                m_array[idx] = null;
                                return obj;
                            }
                            else
                            {
                                // If we encountered a race condition and element was stolen, restore the tail.
                                //如果我们遇到竞态条件，元素被偷，恢复尾部。
                                m_tailIndex = tail + 1;
                                return null;
                            }
                        }
                        finally
                        {
                            if (lockTaken)
                                m_foreignLock.Exit(useMemoryBarrier: false);
                        }
                    }
                }
            }

            public bool CanSteal => m_headIndex < m_tailIndex;

            /// <summary>
            /// 窃取任务，从队头取
            /// <para>missedSteal 争抢冲突时赋值 true</para>
            /// </summary>
            /// <param name="missedSteal">偷窃失败，争抢冲突时赋值 true</param>
            /// <returns></returns>
            public object? TrySteal(ref bool missedSteal)
            {
                while (true)
                {
                    if (CanSteal)
                    {
                        bool taken = false;
                        try
                        {
                            m_foreignLock.TryEnter(ref taken);
                            if (taken)
                            {
                                // Increment head, and ensure read of tail doesn't move before it (fence).
                                //使用栅栏来确保 m_tailIndex 的读取不会出现在前面。
                                int head = m_headIndex;
                                Interlocked.Exchange(ref m_headIndex, head + 1);

                                if (head < m_tailIndex)
                                {
                                    int idx = head & m_mask;
                                    object? obj = Volatile.Read(ref m_array[idx]);

                                    // Check for nulls in the array.
                                    if (obj == null) continue;

                                    m_array[idx] = null;
                                    return obj;
                                }
                                else
                                {
                                    // Failed, restore head.
                                    m_headIndex = head;
                                }
                            }
                        }
                        finally
                        {
                            if (taken)
                                m_foreignLock.Exit(useMemoryBarrier: false);
                        }

                        //争抢冲突（失败）时赋值 true
                        missedSteal = true;
                    }

                    return null;
                }
            }

            public int Count
            {
                get
                {
                    bool lockTaken = false;
                    try
                    {
                        m_foreignLock.Enter(ref lockTaken);
                        return Math.Max(0, m_tailIndex - m_headIndex);
                    }
                    finally
                    {
                        if (lockTaken)
                        {
                            m_foreignLock.Exit(useMemoryBarrier: false);
                        }
                    }
                }
            }
        }
        #endregion

        internal bool loggingEnabled;
        private bool _dispatchTimeSensitiveWorkFirst;

        /// <summary>
        /// 全局队列 ConcurrentQueue
        /// </summary>
        internal readonly ConcurrentQueue<object> workItems = new ConcurrentQueue<object>(); // SOS's ThreadPool command depends on this name
        /// <summary>
        /// 时间敏感任务队列
        /// </summary>
        internal readonly ConcurrentQueue<IThreadPoolWorkItem>? timeSensitiveWorkQueue =
            ThreadPool.SupportsTimeSensitiveWorkItems ? new ConcurrentQueue<IThreadPoolWorkItem>() : null;

        [StructLayout(LayoutKind.Sequential)]
        private struct CacheLineSeparated
        {
            private readonly Internal.PaddingFor32 pad1;

            /// <summary>
            /// 未完成的线程请求数量
            /// </summary>
            public volatile int numOutstandingThreadRequests;

            private readonly Internal.PaddingFor32 pad2;
        }

        private CacheLineSeparated _separated;

        public ThreadPoolWorkQueue()
        {
            RefreshLoggingEnabled();
        }

        public ThreadPoolWorkQueueThreadLocals GetOrCreateThreadLocals() =>
            ThreadPoolWorkQueueThreadLocals.threadLocals ?? CreateThreadLocals();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private ThreadPoolWorkQueueThreadLocals CreateThreadLocals()
        {
            Debug.Assert(ThreadPoolWorkQueueThreadLocals.threadLocals == null);

            return ThreadPoolWorkQueueThreadLocals.threadLocals = new ThreadPoolWorkQueueThreadLocals(this);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RefreshLoggingEnabled()
        {
            if (!FrameworkEventSource.Log.IsEnabled())
            {
                if (loggingEnabled)
                {
                    loggingEnabled = false;
                }
                return;
            }

            RefreshLoggingEnabledFull();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void RefreshLoggingEnabledFull()
        {
            loggingEnabled = FrameworkEventSource.Log.IsEnabled(EventLevel.Verbose, FrameworkEventSource.Keywords.ThreadPool | FrameworkEventSource.Keywords.ThreadTransfer);
        }

        /// <summary>
        /// 增加 未完成的线程请求数量
        /// </summary>
        internal void EnsureThreadRequested()
        {
            //
            // If we have not yet requested #procs threads, then request a new thread.
            //
            // CoreCLR: Note that there is a separate count in the VM which has already been incremented
            // by the VM by the time we reach this point.
            //
            int count = _separated.numOutstandingThreadRequests;
            while (count < Environment.ProcessorCount)
            {
                int prev = Interlocked.CompareExchange(ref _separated.numOutstandingThreadRequests, count + 1, count);
                if (prev == count)
                {
                    ThreadPool.RequestWorkerThread();
                    break;
                }
                count = prev;
            }
        }

        /// <summary>
        /// 减少 未完成的线程请求数量
        /// </summary>
        internal void MarkThreadRequestSatisfied()
        {
            //
            // One of our outstanding thread requests has been satisfied.
            // Decrement the count so that future calls to EnsureThreadRequested will succeed.
            //
            // CoreCLR: Note that there is a separate count in the VM which has already been decremented
            // by the VM by the time we reach this point.
            //
            int count = _separated.numOutstandingThreadRequests;
            while (count > 0)
            {
                int prev = Interlocked.CompareExchange(ref _separated.numOutstandingThreadRequests, count - 1, count);
                if (prev == count)
                {
                    break;
                }
                count = prev;
            }
        }

        /// <summary>
        /// 增加时间敏感 work
        /// </summary>
        public void EnqueueTimeSensitiveWorkItem(IThreadPoolWorkItem timeSensitiveWorkItem)
        {
            Debug.Assert(ThreadPool.SupportsTimeSensitiveWorkItems);

            if (loggingEnabled && FrameworkEventSource.Log.IsEnabled())
            {
                FrameworkEventSource.Log.ThreadPoolEnqueueWorkObject(timeSensitiveWorkItem);
            }

            timeSensitiveWorkQueue!.Enqueue(timeSensitiveWorkItem);
            EnsureThreadRequested();
        }

        /// <summary>
        /// 获取时间敏感 work
        /// </summary>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public IThreadPoolWorkItem? TryDequeueTimeSensitiveWorkItem()
        {
            Debug.Assert(ThreadPool.SupportsTimeSensitiveWorkItems);

            bool success = timeSensitiveWorkQueue!.TryDequeue(out IThreadPoolWorkItem? timeSensitiveWorkItem);
            Debug.Assert(success == (timeSensitiveWorkItem != null));
            return timeSensitiveWorkItem;
        }

        public void Enqueue(object callback, bool forceGlobal)
        {
            Debug.Assert((callback is IThreadPoolWorkItem) ^ (callback is Task));

            if (loggingEnabled && FrameworkEventSource.Log.IsEnabled())
                FrameworkEventSource.Log.ThreadPoolEnqueueWorkObject(callback);

            ThreadPoolWorkQueueThreadLocals? tl = null;
            if (!forceGlobal)
                tl = ThreadPoolWorkQueueThreadLocals.threadLocals;

            if (null != tl)
            {
                //使用偷窃队列，本地队列
                tl.workStealingQueue.LocalPush(callback);
            }
            else
            {
                workItems.Enqueue(callback);
            }

            EnsureThreadRequested();
        }

        internal static bool LocalFindAndPop(object callback)
        {
            ThreadPoolWorkQueueThreadLocals? tl = ThreadPoolWorkQueueThreadLocals.threadLocals;
            return tl != null && tl.workStealingQueue.LocalFindAndPop(callback);
        }

        /// <summary>
        /// 1. 线程本地队列获取
        /// 2. 全局队列获取
        /// 3. 从其他线程队列窃取任务
        /// </summary>
        /// <param name="tl"></param>
        /// <param name="missedSteal">偷窃失败，争抢冲突时赋值 true</param>
        /// <returns></returns>
        public object? Dequeue(ThreadPoolWorkQueueThreadLocals tl, ref bool missedSteal)
        {
            WorkStealingQueue localWsq = tl.workStealingQueue;
            object? callback;

            // localWsq 理论上不会有并发
            if ((callback = localWsq.LocalPop()) == null && // first try the local queue
                !workItems.TryDequeue(out callback)) // then try the global queue
            {
                // finally try to steal from another thread's local queue
                //线程本地队列、全局队列都获取失败，从其他线程队列窃取任务
                WorkStealingQueue[] queues = WorkStealingQueueList.Queues;
                int c = queues.Length;
                Debug.Assert(c > 0, "There must at least be a queue for this thread.");
                int maxIndex = c - 1;
                uint i = tl.random.NextUInt32() % (uint)c;
                while (c > 0)
                {
                    i = (i < maxIndex) ? i + 1 : 0;//q: 循环 c 次但是不改变取值的目的是？
                    WorkStealingQueue otherQueue = queues[i];
                    if (otherQueue != localWsq && otherQueue.CanSteal)
                    {
                        callback = otherQueue.TrySteal(ref missedSteal);
                        if (callback != null)
                        {
                            return callback;
                        }
                    }
                    c--;
                }

                Debug.Assert(callback == null);

#pragma warning disable CS0162 // Unreachable code detected. SupportsTimeSensitiveWorkItems may be a constant in some runtimes.
                // No work in the normal queues, check for time-sensitive work items
                if (ThreadPool.SupportsTimeSensitiveWorkItems)
                {
                    callback = TryDequeueTimeSensitiveWorkItem();
                }
#pragma warning restore CS0162
            }

            return callback;
        }

        public static long LocalCount
        {
            get
            {
                long count = 0;
                foreach (WorkStealingQueue workStealingQueue in WorkStealingQueueList.Queues)
                {
                    count += workStealingQueue.Count;
                }
                return count;
            }
        }

        public long GlobalCount =>
            (ThreadPool.SupportsTimeSensitiveWorkItems ? timeSensitiveWorkQueue!.Count : 0) + workItems.Count;

        // Time in ms for which ThreadPoolWorkQueue.Dispatch keeps executing normal work items before either returning from
        // Dispatch (if SupportsTimeSensitiveWorkItems is false), or checking for and dispatching a time-sensitive work item
        // before continuing with normal work items
        private const uint DispatchQuantumMs = 30;

        /// <summary>
        /// Dispatches work items to this thread.
        /// <para>将工作项分派给此线程。</para>
        /// </summary>
        /// <returns>
        /// <c>true</c> 保留当前线程，线程返回线程池。
        /// <c>false</c> 不在需要当前线程，释放线程。
        /// </returns>
        internal static bool Dispatch()
        {
            ThreadPoolWorkQueue outerWorkQueue = ThreadPool.s_workQueue;

            //
            // Update our records to indicate that an outstanding request for a thread has now been fulfilled.
            // From this point on, we are responsible for requesting another thread if we stop working for any
            // reason, and we believe there might still be work in the queue.
            //
            // CoreCLR: Note that if this thread is aborted before we get a chance to request another one, the VM will
            // record a thread request on our behalf.  So we don't need to worry about getting aborted right here.
            //
            outerWorkQueue.MarkThreadRequestSatisfied();

            // Has the desire for logging changed since the last time we entered?
            outerWorkQueue.RefreshLoggingEnabled();

            //
            // Assume that we're going to need another thread if this one returns to the VM.  We'll set this to
            // false later, but only if we're absolutely certain that the queue is empty.
            //
            bool needAnotherThread = true;
            try
            {
                //
                // Set up our thread-local data
                //
                // Use operate on workQueue local to try block so it can be enregistered
                ThreadPoolWorkQueue workQueue = outerWorkQueue;
                ThreadPoolWorkQueueThreadLocals tl = workQueue.GetOrCreateThreadLocals();//仅在此处创建 ThreadPoolWorkQueueThreadLocals
                object? threadLocalCompletionCountObject = tl.threadLocalCompletionCountObject;
                Thread currentThread = tl.currentThread;

                // Start on clean ExecutionContext and SynchronizationContext
                currentThread._executionContext = null;
                currentThread._synchronizationContext = null;

                //
                // Save the start time
                //
                int startTickCount = Environment.TickCount;

                object? workItem = null;
#pragma warning disable CS0162 // Unreachable code detected. SupportsTimeSensitiveWorkItems may be a constant in some runtimes.
                if (ThreadPool.SupportsTimeSensitiveWorkItems)
                {
                    // Alternate between checking for time-sensitive work or other work first, that way both sets of work items
                    // get a chance to run in situations where worker threads are starved and work items that run also take over
                    // the thread, sustaining starvation. For example, if time-sensitive work is always checked last here, timer
                    // callbacks may not run when worker threads are continually starved.

                    //在先检查时间敏感的工作或其他工作之间交替进行，保证线程饥饿时定时器能有机会被执行
                    bool dispatchTimeSensitiveWorkFirst = workQueue._dispatchTimeSensitiveWorkFirst;
                    workQueue._dispatchTimeSensitiveWorkFirst = !dispatchTimeSensitiveWorkFirst;
                    if (dispatchTimeSensitiveWorkFirst)
                    {
                        workItem = workQueue.TryDequeueTimeSensitiveWorkItem();
                    }
                }
#pragma warning restore CS0162

                //
                // Loop until our quantum expires or there is no work.
                //
                while (true)
                {
                    if (workItem == null)
                    {
                        bool missedSteal = false;//是否偷窃失败，争抢冲突时赋值 true
                        // Operate on 'workQueue' instead of 'outerWorkQueue', as 'workQueue' is local to the try block and it
                        // may be enregistered
                        workItem = workQueue.Dequeue(tl, ref missedSteal);

                        //不存在需要执行的任务
                        if (workItem == null)
                        {
                            //
                            // No work.
                            // If we missed a steal, though, there may be more work in the queue.
                            // Instead of looping around and trying again, we'll just request another thread.  Hopefully the thread
                            // that owns the contended work-stealing queue will pick up its own workitems in the meantime,
                            // which will be more efficient than this thread doing it anyway.
                            // 窃取失败说明被窃取任务由原线程处理
                            needAnotherThread = missedSteal;

                            // Tell the VM we're returning normally, not because Hill Climbing asked us to return.
                            return true;
                        }
                    }

                    if (workQueue.loggingEnabled && FrameworkEventSource.Log.IsEnabled())
                        FrameworkEventSource.Log.ThreadPoolDequeueWorkObject(workItem);

                    //
                    // If we found work, there may be more work.  Ask for another thread so that the other work can be processed
                    // in parallel.  Note that this will only ask for a max of #procs threads, so it's safe to call it for every dequeue.
                    //
                    workQueue.EnsureThreadRequested();

                    //
                    // Execute the workitem outside of any finally blocks, so that it can be aborted if needed.
                    //
#if FEATURE_OBJCMARSHAL
                    if (AutoreleasePool.EnableAutoreleasePool)
                    {
                        DispatchItemWithAutoreleasePool(workItem, currentThread);
                    }
                    else
#endif
#pragma warning disable CS0162 // Unreachable code detected. EnableWorkerTracking may be a constant in some runtimes.
                    if (ThreadPool.EnableWorkerTracking)
                    {
                        DispatchWorkItemWithWorkerTracking(workItem, currentThread);
                    }
                    else
                    {
                        DispatchWorkItem(workItem, currentThread);
                    }
#pragma warning restore CS0162

                    // Release refs
                    workItem = null;

                    // Return to clean ExecutionContext and SynchronizationContext. This may call user code (AsyncLocal value
                    // change notifications).
                    ExecutionContext.ResetThreadPoolThread(currentThread);

                    // Reset thread state after all user code for the work item has completed
                    currentThread.ResetThreadPoolThread();

                    //
                    // Notify the VM that we executed this workitem.  This is also our opportunity to ask whether Hill Climbing wants
                    // us to return the thread to the pool or not.
                    //
                    int currentTickCount = Environment.TickCount;
                    if (!ThreadPool.NotifyWorkItemComplete(threadLocalCompletionCountObject, currentTickCount))//q: 梳理一下？线程新增释放逻辑
                    {
                        // This thread is being parked and may remain inactive for a while. Transfer any thread-local work items
                        // to ensure that they would not be heavily delayed.
                        tl.TransferLocalWork();
                        return false;
                    }

                    // Check if the dispatch quantum has expired
                    // 如果执行时间小于 30ms，则继续执行任务，这是因为大多数机器线程切换大概在 30ms
                    if ((uint)(currentTickCount - startTickCount) < DispatchQuantumMs)
                    {
                        continue;
                    }

                    // The quantum expired, do any necessary periodic activities

#pragma warning disable CS0162 // Unreachable code detected. SupportsTimeSensitiveWorkItems may be a constant in some runtimes.
                    if (!ThreadPool.SupportsTimeSensitiveWorkItems)
                    {
                        // The runtime-specific thread pool implementation does not support managed time-sensitive work, need to
                        // return to the VM to let it perform its own time-sensitive work. Tell the VM we're returning normally.
                        //runtime-specific 线程池实现不支持托管的时间敏感 work, return true 让 VM 执行时间敏感 work
                        return true;
                    }

                    // This method will continue to dispatch work items. Refresh the start tick count for the next dispatch
                    // quantum and do some periodic activities.
                    startTickCount = currentTickCount;

                    // Periodically refresh whether logging is enabled
                    workQueue.RefreshLoggingEnabled();

                    // Consistent with CoreCLR currently, only one time-sensitive work item is run periodically between quantums
                    // of time spent running work items in the normal thread pool queues, until the normal queues are depleted.
                    // These are basically lower-priority but time-sensitive work items.
                    workItem = workQueue.TryDequeueTimeSensitiveWorkItem();
#pragma warning restore CS0162
                }
            }
            finally
            {
                //
                // If we are exiting for any reason other than that the queue is definitely empty, ask for another
                // thread to pick up where we left off.
                //
                if (needAnotherThread)
                    outerWorkQueue.EnsureThreadRequested();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void DispatchWorkItemWithWorkerTracking(object workItem, Thread currentThread)
        {
            Debug.Assert(ThreadPool.EnableWorkerTracking);
            Debug.Assert(currentThread == Thread.CurrentThread);

            bool reportedStatus = false;
            try
            {
                ThreadPool.ReportThreadStatus(isWorking: true);
                reportedStatus = true;
                DispatchWorkItem(workItem, currentThread);
            }
            finally
            {
                if (reportedStatus)
                    ThreadPool.ReportThreadStatus(isWorking: false);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void DispatchWorkItem(object workItem, Thread currentThread)
        {
            if (workItem is Task task)
            {
                task.ExecuteFromThreadPool(currentThread);
            }
            else
            {
                Debug.Assert(workItem is IThreadPoolWorkItem);
                Unsafe.As<IThreadPoolWorkItem>(workItem).Execute();
            }
        }
    }

    #region ThreadPoolWorkQueueThreadLocals

    // Holds a WorkStealingQueue, and removes it from the list when this object is no longer referenced.
    /// <summary>
    /// 保持WorkStealingQueue，并在不再引用该对象时将其从列表中移除。
    /// </summary>
    internal sealed class ThreadPoolWorkQueueThreadLocals
    {
        /// <summary>
        /// thread static，lazy 单例
        /// </summary>
        [ThreadStatic]
        public static ThreadPoolWorkQueueThreadLocals? threadLocals;

        public readonly ThreadPoolWorkQueue workQueue;
        /// <summary>
        /// 线程本地队列
        /// </summary>
        public readonly ThreadPoolWorkQueue.WorkStealingQueue workStealingQueue;
        public readonly Thread currentThread;
        /// <summary>
        /// 当前线程的 ThreadLocalNode，不使用可移植线程池时为 null
        /// </summary>
        public readonly object? threadLocalCompletionCountObject;
        public readonly Random.XoshiroImpl random = new Random.XoshiroImpl();

        public ThreadPoolWorkQueueThreadLocals(ThreadPoolWorkQueue tpq)
        {
            workQueue = tpq;
            workStealingQueue = new ThreadPoolWorkQueue.WorkStealingQueue();
            ThreadPoolWorkQueue.WorkStealingQueueList.Add(workStealingQueue);
            currentThread = Thread.CurrentThread;
            threadLocalCompletionCountObject = ThreadPool.GetOrCreateThreadLocalCompletionCountObject();
        }

        /// <summary>
        /// 转移 LocalWork to Global
        /// </summary>
        public void TransferLocalWork()
        {
            while (workStealingQueue.LocalPop() is object cb)
            {
                workQueue.Enqueue(cb, forceGlobal: true);
            }
        }

        ~ThreadPoolWorkQueueThreadLocals()
        {
            // Transfer any pending workitems into the global queue so that they will be executed by another thread
            if (null != workStealingQueue)
            {
                TransferLocalWork();
                ThreadPoolWorkQueue.WorkStealingQueueList.Remove(workStealingQueue);
            }
        }
    }
    #endregion

    public delegate void WaitCallback(object? state);

    public delegate void WaitOrTimerCallback(object? state, bool timedOut);  // signaled or timed out

    internal abstract class QueueUserWorkItemCallbackBase : IThreadPoolWorkItem
    {
#if DEBUG
        private int executed;

        ~QueueUserWorkItemCallbackBase()
        {
            Interlocked.MemoryBarrier(); // ensure that an old cached value is not read below
            Debug.Assert(
                executed != 0, "A QueueUserWorkItemCallback was never called!");
        }
#endif

        public virtual void Execute()
        {
#if DEBUG
            GC.SuppressFinalize(this);
            Debug.Assert(
                0 == Interlocked.Exchange(ref executed, 1),
                "A QueueUserWorkItemCallback was called twice!");
#endif
        }
    }

    internal sealed class QueueUserWorkItemCallback : QueueUserWorkItemCallbackBase
    {
        private WaitCallback? _callback; // SOS's ThreadPool command depends on this name
        private readonly object? _state;
        private readonly ExecutionContext _context;

        private static readonly Action<QueueUserWorkItemCallback> s_executionContextShim = quwi =>
        {
            Debug.Assert(quwi._callback != null);
            WaitCallback callback = quwi._callback;
            quwi._callback = null;

            callback(quwi._state);
        };

        internal QueueUserWorkItemCallback(WaitCallback callback, object? state, ExecutionContext context)
        {
            Debug.Assert(context != null);

            _callback = callback;
            _state = state;
            _context = context;
        }

        public override void Execute()
        {
            base.Execute();

            ExecutionContext.RunForThreadPoolUnsafe(_context, s_executionContextShim, this);
        }
    }

    internal sealed class QueueUserWorkItemCallback<TState> : QueueUserWorkItemCallbackBase
    {
        private Action<TState>? _callback; // SOS's ThreadPool command depends on this name
        private readonly TState _state;
        private readonly ExecutionContext _context;

        internal QueueUserWorkItemCallback(Action<TState> callback, TState state, ExecutionContext context)
        {
            Debug.Assert(callback != null);

            _callback = callback;
            _state = state;
            _context = context;
        }

        public override void Execute()
        {
            base.Execute();

            Debug.Assert(_callback != null);
            Action<TState> callback = _callback;
            _callback = null;

            ExecutionContext.RunForThreadPoolUnsafe(_context, callback, in _state);
        }
    }

    internal sealed class QueueUserWorkItemCallbackDefaultContext : QueueUserWorkItemCallbackBase
    {
        private WaitCallback? _callback; // SOS's ThreadPool command depends on this name
        private readonly object? _state;

        internal QueueUserWorkItemCallbackDefaultContext(WaitCallback callback, object? state)
        {
            Debug.Assert(callback != null);

            _callback = callback;
            _state = state;
        }

        public override void Execute()
        {
            ExecutionContext.CheckThreadPoolAndContextsAreDefault();
            base.Execute();

            Debug.Assert(_callback != null);
            WaitCallback callback = _callback;
            _callback = null;

            callback(_state);

            // ThreadPoolWorkQueue.Dispatch will handle notifications and reset EC and SyncCtx back to default
        }
    }

    internal sealed class QueueUserWorkItemCallbackDefaultContext<TState> : QueueUserWorkItemCallbackBase
    {
        private Action<TState>? _callback; // SOS's ThreadPool command depends on this name
        private readonly TState _state;

        internal QueueUserWorkItemCallbackDefaultContext(Action<TState> callback, TState state)
        {
            Debug.Assert(callback != null);

            _callback = callback;
            _state = state;
        }

        public override void Execute()
        {
            ExecutionContext.CheckThreadPoolAndContextsAreDefault();
            base.Execute();

            Debug.Assert(_callback != null);
            Action<TState> callback = _callback;
            _callback = null;

            callback(_state);

            // ThreadPoolWorkQueue.Dispatch will handle notifications and reset EC and SyncCtx back to default
        }
    }

    internal sealed class _ThreadPoolWaitOrTimerCallback
    {
        private readonly WaitOrTimerCallback _waitOrTimerCallback;
        private readonly ExecutionContext? _executionContext;
        private readonly object? _state;
        private static readonly ContextCallback _ccbt = new ContextCallback(WaitOrTimerCallback_Context_t);
        private static readonly ContextCallback _ccbf = new ContextCallback(WaitOrTimerCallback_Context_f);

        internal _ThreadPoolWaitOrTimerCallback(WaitOrTimerCallback waitOrTimerCallback, object? state, bool flowExecutionContext)
        {
            _waitOrTimerCallback = waitOrTimerCallback;
            _state = state;

            if (flowExecutionContext)
            {
                // capture the exection context
                _executionContext = ExecutionContext.Capture();
            }
        }

        private static void WaitOrTimerCallback_Context_t(object? state) =>
            WaitOrTimerCallback_Context(state, timedOut: true);

        private static void WaitOrTimerCallback_Context_f(object? state) =>
            WaitOrTimerCallback_Context(state, timedOut: false);

        private static void WaitOrTimerCallback_Context(object? state, bool timedOut)
        {
            _ThreadPoolWaitOrTimerCallback helper = (_ThreadPoolWaitOrTimerCallback)state!;
            helper._waitOrTimerCallback(helper._state, timedOut);
        }

        // call back helper
        internal static void PerformWaitOrTimerCallback(_ThreadPoolWaitOrTimerCallback helper, bool timedOut)
        {
            Debug.Assert(helper != null, "Null state passed to PerformWaitOrTimerCallback!");
            // call directly if it is an unsafe call OR EC flow is suppressed
            ExecutionContext? context = helper._executionContext;
            if (context == null)
            {
                WaitOrTimerCallback callback = helper._waitOrTimerCallback;
                callback(helper._state, timedOut);
            }
            else
            {
                ExecutionContext.Run(context, timedOut ? _ccbt : _ccbf, helper);
            }
        }
    }

    public static partial class ThreadPool
    {
        internal const string WorkerThreadName = ".NET ThreadPool Worker";

        /// <summary>
        /// static，工作队列，内有全局队列+本地队列+时间敏感队列
        /// </summary>
        internal static readonly ThreadPoolWorkQueue s_workQueue = new ThreadPoolWorkQueue();

        /// <summary>Shim used to invoke <see cref="IAsyncStateMachineBox.MoveNext"/> of the supplied <see cref="IAsyncStateMachineBox"/>.</summary>
        internal static readonly Action<object?> s_invokeAsyncStateMachineBox = static state =>
        {
            if (!(state is IAsyncStateMachineBox box))
            {
                ThrowHelper.ThrowArgumentOutOfRangeException(ExceptionArgument.state);
                return;
            }

            box.MoveNext();
        };

        internal static bool EnableWorkerTracking => IsWorkerTrackingEnabledInConfig && EventSource.IsSupported;

        [CLSCompliant(false)]
        [UnsupportedOSPlatform("browser")]
        public static RegisteredWaitHandle RegisterWaitForSingleObject(
             WaitHandle waitObject,
             WaitOrTimerCallback callBack,
             object? state,
             uint millisecondsTimeOutInterval,
             bool executeOnlyOnce    // NOTE: we do not allow other options that allow the callback to be queued as an APC
             )
        {
            if (millisecondsTimeOutInterval > (uint)int.MaxValue && millisecondsTimeOutInterval != uint.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeOutInterval), SR.ArgumentOutOfRange_LessEqualToIntegerMaxVal);
            return RegisterWaitForSingleObject(waitObject, callBack, state, millisecondsTimeOutInterval, executeOnlyOnce, true);
        }

        [CLSCompliant(false)]
        [UnsupportedOSPlatform("browser")]
        public static RegisteredWaitHandle UnsafeRegisterWaitForSingleObject(
             WaitHandle waitObject,
             WaitOrTimerCallback callBack,
             object? state,
             uint millisecondsTimeOutInterval,
             bool executeOnlyOnce    // NOTE: we do not allow other options that allow the callback to be queued as an APC
             )
        {
            if (millisecondsTimeOutInterval > (uint)int.MaxValue && millisecondsTimeOutInterval != uint.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeOutInterval), SR.ArgumentOutOfRange_NeedNonNegOrNegative1);
            return RegisterWaitForSingleObject(waitObject, callBack, state, millisecondsTimeOutInterval, executeOnlyOnce, false);
        }

        [UnsupportedOSPlatform("browser")]
        public static RegisteredWaitHandle RegisterWaitForSingleObject(
             WaitHandle waitObject,
             WaitOrTimerCallback callBack,
             object? state,
             int millisecondsTimeOutInterval,
             bool executeOnlyOnce    // NOTE: we do not allow other options that allow the callback to be queued as an APC
             )
        {
            if (millisecondsTimeOutInterval < -1)
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeOutInterval), SR.ArgumentOutOfRange_NeedNonNegOrNegative1);
            return RegisterWaitForSingleObject(waitObject, callBack, state, (uint)millisecondsTimeOutInterval, executeOnlyOnce, true);
        }

        [UnsupportedOSPlatform("browser")]
        public static RegisteredWaitHandle UnsafeRegisterWaitForSingleObject(
             WaitHandle waitObject,
             WaitOrTimerCallback callBack,
             object? state,
             int millisecondsTimeOutInterval,
             bool executeOnlyOnce    // NOTE: we do not allow other options that allow the callback to be queued as an APC
             )
        {
            if (millisecondsTimeOutInterval < -1)
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeOutInterval), SR.ArgumentOutOfRange_NeedNonNegOrNegative1);
            return RegisterWaitForSingleObject(waitObject, callBack, state, (uint)millisecondsTimeOutInterval, executeOnlyOnce, false);
        }

        [UnsupportedOSPlatform("browser")]
        public static RegisteredWaitHandle RegisterWaitForSingleObject(
            WaitHandle waitObject,
            WaitOrTimerCallback callBack,
            object? state,
            long millisecondsTimeOutInterval,
            bool executeOnlyOnce    // NOTE: we do not allow other options that allow the callback to be queued as an APC
        )
        {
            if (millisecondsTimeOutInterval < -1)
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeOutInterval), SR.ArgumentOutOfRange_NeedNonNegOrNegative1);
            if (millisecondsTimeOutInterval > (uint)int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeOutInterval), SR.ArgumentOutOfRange_LessEqualToIntegerMaxVal);
            return RegisterWaitForSingleObject(waitObject, callBack, state, (uint)millisecondsTimeOutInterval, executeOnlyOnce, true);
        }

        [UnsupportedOSPlatform("browser")]
        public static RegisteredWaitHandle UnsafeRegisterWaitForSingleObject(
            WaitHandle waitObject,
            WaitOrTimerCallback callBack,
            object? state,
            long millisecondsTimeOutInterval,
            bool executeOnlyOnce    // NOTE: we do not allow other options that allow the callback to be queued as an APC
        )
        {
            if (millisecondsTimeOutInterval < -1)
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeOutInterval), SR.ArgumentOutOfRange_NeedNonNegOrNegative1);
            if (millisecondsTimeOutInterval > (uint)int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeOutInterval), SR.ArgumentOutOfRange_LessEqualToIntegerMaxVal);
            return RegisterWaitForSingleObject(waitObject, callBack, state, (uint)millisecondsTimeOutInterval, executeOnlyOnce, false);
        }

        [UnsupportedOSPlatform("browser")]
        public static RegisteredWaitHandle RegisterWaitForSingleObject(
                          WaitHandle waitObject,
                          WaitOrTimerCallback callBack,
                          object? state,
                          TimeSpan timeout,
                          bool executeOnlyOnce
                          )
        {
            long tm = (long)timeout.TotalMilliseconds;
            if (tm < -1)
                throw new ArgumentOutOfRangeException(nameof(timeout), SR.ArgumentOutOfRange_NeedNonNegOrNegative1);
            if (tm > (long)int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(timeout), SR.ArgumentOutOfRange_LessEqualToIntegerMaxVal);
            return RegisterWaitForSingleObject(waitObject, callBack, state, (uint)tm, executeOnlyOnce, true);
        }

        [UnsupportedOSPlatform("browser")]
        public static RegisteredWaitHandle UnsafeRegisterWaitForSingleObject(
                          WaitHandle waitObject,
                          WaitOrTimerCallback callBack,
                          object? state,
                          TimeSpan timeout,
                          bool executeOnlyOnce
                          )
        {
            long tm = (long)timeout.TotalMilliseconds;
            if (tm < -1)
                throw new ArgumentOutOfRangeException(nameof(timeout), SR.ArgumentOutOfRange_NeedNonNegOrNegative1);
            if (tm > (long)int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(timeout), SR.ArgumentOutOfRange_LessEqualToIntegerMaxVal);
            return RegisterWaitForSingleObject(waitObject, callBack, state, (uint)tm, executeOnlyOnce, false);
        }

        #region 线程池入口

        public static bool QueueUserWorkItem(WaitCallback callBack) =>
            QueueUserWorkItem(callBack, null);

        public static bool QueueUserWorkItem(WaitCallback callBack, object? state)
        {
            if (callBack == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.callBack);
            }

            ExecutionContext? context = ExecutionContext.Capture();

            object tpcallBack = (context == null || context.IsDefault) ?
                new QueueUserWorkItemCallbackDefaultContext(callBack!, state) :
                (object)new QueueUserWorkItemCallback(callBack!, state, context);

            s_workQueue.Enqueue(tpcallBack, forceGlobal: true);

            return true;
        }

        /// <summary>
        /// 线程池入口
        /// </summary>
        /// <typeparam name="TState">The type of the state.</typeparam>
        /// <param name="callBack">The call back.</param>
        /// <param name="state">The state.</param>
        /// <param name="preferLocal">是否使用本地队列</param>
        /// <returns></returns>
        public static bool QueueUserWorkItem<TState>(Action<TState> callBack, TState state, bool preferLocal)
        {
            if (callBack == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.callBack);
            }

            ExecutionContext? context = ExecutionContext.Capture();

            object tpcallBack = (context == null || context.IsDefault) ?
                new QueueUserWorkItemCallbackDefaultContext<TState>(callBack!, state) :
                (object)new QueueUserWorkItemCallback<TState>(callBack!, state, context);

            s_workQueue.Enqueue(tpcallBack, forceGlobal: !preferLocal);

            return true;
        }

        public static bool UnsafeQueueUserWorkItem<TState>(Action<TState> callBack, TState state, bool preferLocal)
        {
            if (callBack == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.callBack);
            }

            // If the callback is the runtime-provided invocation of an IAsyncStateMachineBox,
            // then we can queue the Task state directly to the ThreadPool instead of
            // wrapping it in a QueueUserWorkItemCallback.
            //
            // This occurs when user code queues its provided continuation to the ThreadPool;
            // internally we call UnsafeQueueUserWorkItemInternal directly for Tasks.
            if (ReferenceEquals(callBack, ThreadPool.s_invokeAsyncStateMachineBox))
            {
                // 如果是 IAsyncStateMachineBox runtime 内部调用，不需要包装 QueueUserWorkItemCallback
                if (!(state is IAsyncStateMachineBox))
                {
                    // The provided state must be the internal IAsyncStateMachineBox (Task) type
                    ThrowHelper.ThrowArgumentOutOfRangeException(ExceptionArgument.state);
                }

                UnsafeQueueUserWorkItemInternal((object)state!, preferLocal);
                return true;
            }

            s_workQueue.Enqueue(
                new QueueUserWorkItemCallbackDefaultContext<TState>(callBack!, state), forceGlobal: !preferLocal);

            return true;
        }

        public static bool UnsafeQueueUserWorkItem(WaitCallback callBack, object? state)
        {
            if (callBack == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.callBack);
            }

            object tpcallBack = new QueueUserWorkItemCallbackDefaultContext(callBack!, state);

            s_workQueue.Enqueue(tpcallBack, forceGlobal: true);

            return true;
        }

        public static bool UnsafeQueueUserWorkItem(IThreadPoolWorkItem callBack, bool preferLocal)
        {
            if (callBack == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.callBack);
            }
            if (callBack is Task)
            {
                // Prevent code from queueing a derived Task that also implements the interface,
                // as that would bypass Task.Start and its safety checks.
                ThrowHelper.ThrowArgumentOutOfRangeException(ExceptionArgument.callBack);
            }

            UnsafeQueueUserWorkItemInternal(callBack!, preferLocal);
            return true;
        }
        #endregion

        internal static void UnsafeQueueUserWorkItemInternal(object callBack, bool preferLocal)
        {
            Debug.Assert((callBack is IThreadPoolWorkItem) ^ (callBack is Task));

            s_workQueue.Enqueue(callBack, forceGlobal: !preferLocal);
        }

        internal static void UnsafeQueueTimeSensitiveWorkItem(IThreadPoolWorkItem timeSensitiveWorkItem)
        {
#pragma warning disable CS0162 // Unreachable code detected. SupportsTimeSensitiveWorkItems may be constant true in some runtimes.
            if (SupportsTimeSensitiveWorkItems)
            {
                UnsafeQueueTimeSensitiveWorkItemInternal(timeSensitiveWorkItem);
                return;
            }

            UnsafeQueueUserWorkItemInternal(timeSensitiveWorkItem, preferLocal: false);
#pragma warning restore CS0162
        }
        /// <summary>
        /// 增加时间敏感 work
        /// <remarks>目前用于非托管代码调用和 clr 内部使用</remarks>
        /// </summary>
        internal static void UnsafeQueueTimeSensitiveWorkItemInternal(IThreadPoolWorkItem timeSensitiveWorkItem) =>
            s_workQueue.EnqueueTimeSensitiveWorkItem(timeSensitiveWorkItem);

        // This method tries to take the target callback out of the current thread's queue.
        internal static bool TryPopCustomWorkItem(object workItem)
        {
            Debug.Assert(null != workItem);
            return ThreadPoolWorkQueue.LocalFindAndPop(workItem);
        }

        // Get all workitems.  Called by TaskScheduler in its debugger hooks.
        internal static IEnumerable<object> GetQueuedWorkItems()
        {
#pragma warning disable CS0162 // Unreachable code detected. SupportsTimeSensitiveWorkItems may be a constant in some runtimes.
            if (ThreadPool.SupportsTimeSensitiveWorkItems)
            {
                // Enumerate time-sensitive work item queue
                foreach (object workItem in s_workQueue.timeSensitiveWorkQueue!)
                {
                    yield return workItem;
                }
            }
#pragma warning restore CS0162

            // Enumerate global queue
            foreach (object workItem in s_workQueue.workItems)
            {
                yield return workItem;
            }

            // Enumerate each local queue
            foreach (ThreadPoolWorkQueue.WorkStealingQueue wsq in ThreadPoolWorkQueue.WorkStealingQueueList.Queues)
            {
                if (wsq != null && wsq.m_array != null)
                {
                    object?[] items = wsq.m_array;
                    for (int i = 0; i < items.Length; i++)
                    {
                        object? item = items[i];
                        if (item != null)
                        {
                            yield return item;
                        }
                    }
                }
            }
        }

        internal static IEnumerable<object> GetLocallyQueuedWorkItems()
        {
            ThreadPoolWorkQueue.WorkStealingQueue? wsq = ThreadPoolWorkQueueThreadLocals.threadLocals?.workStealingQueue;
            if (wsq != null && wsq.m_array != null)
            {
                object?[] items = wsq.m_array;
                for (int i = 0; i < items.Length; i++)
                {
                    object? item = items[i];
                    if (item != null)
                        yield return item;
                }
            }
        }

        internal static IEnumerable<object> GetGloballyQueuedWorkItems()
        {
#pragma warning disable CS0162 // Unreachable code detected. SupportsTimeSensitiveWorkItems may be a constant in some runtimes.
            if (ThreadPool.SupportsTimeSensitiveWorkItems)
            {
                // Enumerate time-sensitive work item queue
                foreach (object workItem in s_workQueue.timeSensitiveWorkQueue!)
                {
                    yield return workItem;
                }
            }
#pragma warning restore CS0162

            // Enumerate global queue
            foreach (object workItem in s_workQueue.workItems)
            {
                yield return workItem;
            }
        }

        private static object[] ToObjectArray(IEnumerable<object> workitems)
        {
            int i = 0;
            foreach (object item in workitems)
            {
                i++;
            }

            object[] result = new object[i];
            i = 0;
            foreach (object item in workitems)
            {
                if (i < result.Length) // just in case someone calls us while the queues are in motion
                    result[i] = item;
                i++;
            }

            return result;
        }

        // This is the method the debugger will actually call, if it ends up calling
        // into ThreadPool directly.  Tests can use this to simulate a debugger, as well.
        internal static object[] GetQueuedWorkItemsForDebugger() =>
            ToObjectArray(GetQueuedWorkItems());

        internal static object[] GetGloballyQueuedWorkItemsForDebugger() =>
            ToObjectArray(GetGloballyQueuedWorkItems());

        internal static object[] GetLocallyQueuedWorkItemsForDebugger() =>
            ToObjectArray(GetLocallyQueuedWorkItems());

        /// <summary>
        /// Gets the number of work items that are currently queued to be processed.
        /// <para>排队的数量</para>
        /// </summary>
        /// <remarks>
        /// For a thread pool implementation that may have different types of work items, the count includes all types that can
        /// be tracked, which may only be the user work items including tasks. Some implementations may also include queued
        /// timer and wait callbacks in the count. On Windows, the count is unlikely to include the number of pending IO
        /// completions, as they get posted directly to an IO completion port.
        /// </remarks>
        public static long PendingWorkItemCount
        {
            get
            {
                ThreadPoolWorkQueue workQueue = s_workQueue;
                return ThreadPoolWorkQueue.LocalCount + workQueue.GlobalCount + PendingUnmanagedWorkItemCount;
            }
        }
    }
}
