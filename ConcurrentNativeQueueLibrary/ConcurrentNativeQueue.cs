using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ConcurrentNativeQueueLibrary;

/// <summary>
/// 独占缓存行的 long 值。前 64 字节填充确保 Value 不与相邻字段共享缓存行。
/// 必须定义在命名空间级别，因为 CLR 不允许泛型类型的嵌套类型使用 explicit layout。
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 128)]
internal struct PaddedLong
{
    [FieldOffset(64)] public long Value;
}

/// <summary>
/// 一个完全原生化的线程安全无锁队列，用于在并发环境中存储非托管类型。
/// </summary>
/// <remarks>
/// 此结构是一个 MPSC（多生产者单消费者）CAS 无锁队列，零 GC 压力。<br/>
/// 所有内存（段头 + 槽位数组）均通过 <see cref="NativeMemory"/> 分配，不产生任何托管堆分配：<br/>
/// - 入队快速路径：Volatile.Read 检测可用位置 → CAS 占位 → 写入数据；<br/>
/// - 段满检测为纯读操作，不产生原子写开销；<br/>
/// - 段满时自动分配新段并链接到尾部，段大小指数增长（减少段切换频率）；<br/>
/// - 槽位数组在段被完全消费后由消费者回收到单槽缓存，分配新段时优先复用，
///   使工作内存常驻 CPU 缓存，显著提升稳态吞吐（类似 <c>ConcurrentQueue&lt;T&gt;</c> 的内存复用策略）；<br/>
/// - 段头结构体延迟到 <see cref="Dispose"/> 时统一释放（生产者可能持有旧段指针，
///   无法在消费后立即安全回收段头——这是无锁 Native 设计中最核心的生命周期问题）。<br/>
/// 生产者与消费者的热点字段通过缓存行填充隔离，消除 false sharing。
/// </remarks>
/// <typeparam name="T">指定队列中元素的类型。该类型必须是非托管类型。</typeparam>
public unsafe struct ConcurrentNativeQueue<T> : IDisposable where T : unmanaged
{
    private const int DefaultSegmentSize = 32;
    private const int MaxSegmentSize = 1024 * 1024;
    private const int PreBuildSlots = 16;

#if DEBUG
    private long _debugSlotAllocCount;
    private long _debugSlotReuseCount;
    private long _debugSlotFreeCount;
#endif

    private struct Slot
    {
        public int State;
        public T Value;
    }

    /// <summary>
    /// 段头 — 通过 NativeMemory 分配的纯原生结构体。<br/>
    /// 槽位数组在段消费完后由消费者回收到单槽缓存以供复用
    /// （安全：所有 State==1 意味着所有生产者已写完，且持有旧段指针的生产者
    /// 不会访问已消费段的槽位——它们会检测到段已满并重新读取 _tail）。<br/>
    /// 段头本身延迟到 Dispose 统一释放，因为生产者可能在任意时刻被抢占并持有旧段指针，
    /// 在此期间仍需读取 EnqueuePos / Next 等元数据字段。<br/>
    /// EnqueuePos 通过 <see cref="PaddedLong"/> 隔离到独立缓存行。
    /// </summary>
    private struct SegmentHeader
    {
        internal Slot* Slots;
        internal int Capacity;
        internal int NextCapacity;
        internal long BaseIndex;
        internal nint Next;
        internal PaddedLong EnqueuePos;
    }

    // ── 消费者缓存行 ──
    private SegmentHeader* _head;
    private long _dequeuePos;
    private SegmentHeader* _origin;

#pragma warning disable CS0169
    private long _p0, _p1, _p2, _p3, _p4, _p5, _p6, _p7;
#pragma warning restore CS0169

    // ── 生产者缓存行 ──
    private nint _tail;
    private nint _cachedSlots;
    private readonly bool _preferSteadyStateCapacity;

    public ConcurrentNativeQueue() : this(DefaultSegmentSize, false) { }

    public ConcurrentNativeQueue(int segmentSize) : this(segmentSize, false) { }

    public ConcurrentNativeQueue(bool preferSteadyStateCapacity) : this(DefaultSegmentSize, preferSteadyStateCapacity) { }

    /// <summary>
    /// 初始化队列。
    /// </summary>
    /// <param name="segmentSize">初始段容量（最小为 1，内部至少提升到 2）。</param>
    /// <param name="preferSteadyStateCapacity">
    /// 是否偏向稳态容量模式：
    /// false（默认）= 指数扩容，优先高吞吐峰值；
    /// true = 段容量保持稳定，优先槽位复用与稳态缓存命中。
    /// </param>
    public ConcurrentNativeQueue(int segmentSize, bool preferSteadyStateCapacity)
    {
#if NET8_0_OR_GREATER
        ArgumentOutOfRangeException.ThrowIfLessThan(segmentSize, 1);
#else
        if (segmentSize < 1)
            throw new ArgumentOutOfRangeException(nameof(segmentSize), segmentSize, "Segment size must be greater than 0.");
#endif
        this = default;
        _preferSteadyStateCapacity = preferSteadyStateCapacity;

        int cap = Math.Max(2, segmentSize);
        int nextCap = preferSteadyStateCapacity ? cap : Math.Min(cap * 2, MaxSegmentSize);
        SegmentHeader* initial = AllocSegment(cap, 0, nextCap);
        _head = initial;
        _tail = (nint)initial;
        _origin = initial;
    }

    private SegmentHeader* AllocSegment(int capacity, long baseIndex, int nextCapacity)
    {
        var seg = (SegmentHeader*)NativeMemory.AllocZeroed(1, (nuint)sizeof(SegmentHeader));

        // 优先复用缓存中的槽位数组，使工作内存常驻 CPU 缓存
        Slot* cached = (Slot*)Interlocked.Exchange(ref _cachedSlots, (nint)0);
        if (cached != null && *(int*)cached >= capacity)
        {
            new Span<byte>(cached, capacity * sizeof(Slot)).Clear();
            seg->Slots = cached;
            DebugIncSlotReuse();
        }
        else
        {
            if (cached != null)
            {
                NativeMemory.Free(cached);
                DebugIncSlotFree();
            }

            seg->Slots = (Slot*)NativeMemory.AllocZeroed((nuint)capacity, (nuint)sizeof(Slot));
            DebugIncSlotAlloc();
        }

        seg->Capacity = capacity;
        seg->NextCapacity = nextCapacity;
        seg->BaseIndex = baseIndex;
        seg->EnqueuePos.Value = baseIndex;
        return seg;
    }

#if DEBUG
    /// <summary>
    /// 获取槽位数组的调试统计信息（用于验证是否发生复用）。
    /// </summary>
    public (long SlotAllocCount, long SlotReuseCount, long SlotFreeCount) GetDebugSlotStats()
    {
        return (
            Volatile.Read(ref _debugSlotAllocCount),
            Volatile.Read(ref _debugSlotReuseCount),
            Volatile.Read(ref _debugSlotFreeCount));
    }
#endif

    [Conditional("DEBUG")]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DebugIncSlotAlloc()
    {
#if DEBUG
        Interlocked.Increment(ref _debugSlotAllocCount);
#endif
    }

    [Conditional("DEBUG")]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DebugIncSlotReuse()
    {
#if DEBUG
        Interlocked.Increment(ref _debugSlotReuseCount);
#endif
    }

    [Conditional("DEBUG")]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DebugIncSlotFree()
    {
#if DEBUG
        Interlocked.Increment(ref _debugSlotFreeCount);
#endif
    }

    /// <summary>
    /// 将已消费完毕的段的槽位数组回收到单槽缓存中，以便后续段分配时复用。
    /// 若缓存中已有旧数组，则释放被置换出的旧数组。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RecycleSlots(SegmentHeader* seg)
    {
        Slot* s = seg->Slots;
        if (s == null) return;
        seg->Slots = null;

        // 在槽位数组起始处存入容量信息，供后续复用时判断是否足够大
        *(int*)s = seg->Capacity;

        // 原子交换到单槽缓存；若有旧条目则释放
        Slot* old = (Slot*)Interlocked.Exchange(ref _cachedSlots, (nint)s);
        if (old != null)
        {
            NativeMemory.Free(old);
            DebugIncSlotFree();
        }
    }

    /// <summary>当前队列中的元素数量（近似值，在并发场景下可能瞬间不精确）。</summary>
    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            SegmentHeader* tail = (SegmentHeader*)Volatile.Read(ref _tail);
            long enqPos = Volatile.Read(ref tail->EnqueuePos.Value);
            long cap = tail->BaseIndex + tail->Capacity;
            long count = Math.Min(enqPos, cap) - Volatile.Read(ref _dequeuePos);
            return (int)Math.Max(count, 0);
        }
    }

    /// <summary>队列是否为空。</summary>
    public bool IsEmpty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Count <= 0;
    }

    /// <summary>
    /// 将元素入队。此方法是线程安全的，可由多个生产者并发调用。
    /// 当段已满时自动分配新段（容量指数增长），无需缓冲区迁移或全局暂停。
    /// </summary>
    public void Enqueue(T item)
    {
        SpinWait spin = default;

        while (true)
        {
            SegmentHeader* tail = (SegmentHeader*)Volatile.Read(ref _tail);
            long pos = Volatile.Read(ref tail->EnqueuePos.Value);
            long offset = pos - tail->BaseIndex;

            if ((ulong)offset >= (ulong)tail->Capacity)
            {
                TryAdvanceTail(tail);
                spin.SpinOnce();
                continue;
            }

            if (Interlocked.CompareExchange(ref tail->EnqueuePos.Value, pos + 1, pos) == pos)
            {
                tail->Slots[offset].Value = item;
                Volatile.Write(ref tail->Slots[offset].State, 1);

                if (offset + 1 >= tail->Capacity - PreBuildSlots
                    && Volatile.Read(ref tail->Next) == (nint)0)
                    EnsureNextSegment(tail);

                return;
            }

            spin.SpinOnce();
        }
    }

    /// <summary>
    /// 将多个元素批量入队。此方法是线程安全的，可由多个生产者并发调用。
    /// 通过单次 CAS 占位多个槽位，分摊原子操作开销；跨段时自动分批写入。
    /// </summary>
    public void EnqueueRange(ReadOnlySpan<T> items)
    {
        if (items.IsEmpty) return;

        int index = 0;
        SpinWait spin = default;

        while (index < items.Length)
        {
            SegmentHeader* tail = (SegmentHeader*)Volatile.Read(ref _tail);
            long pos = Volatile.Read(ref tail->EnqueuePos.Value);
            long offset = pos - tail->BaseIndex;

            if ((ulong)offset >= (ulong)tail->Capacity)
            {
                TryAdvanceTail(tail);
                spin.SpinOnce();
                continue;
            }

            int available = (int)(tail->Capacity - offset);
            int batchSize = Math.Min(items.Length - index, available);

            if (Interlocked.CompareExchange(ref tail->EnqueuePos.Value, pos + batchSize, pos) == pos)
            {
                Slot* slots = tail->Slots;
                int baseSlot = (int)offset;

                for (int i = 0; i < batchSize; i++)
                    slots[baseSlot + i].Value = items[index + i];

                Thread.MemoryBarrier();

                for (int i = 0; i < batchSize; i++)
                    slots[baseSlot + i].State = 1;

                if (baseSlot + batchSize >= tail->Capacity - PreBuildSlots
                    && Volatile.Read(ref tail->Next) == (nint)0)
                    EnsureNextSegment(tail);

                index += batchSize;
                spin = default;
            }
            else
            {
                spin.SpinOnce();
            }
        }
    }

    /// <summary>
    /// 确保段的 Next 已分配。由生产者在段接近满（预建）或已满时调用。
    /// 新段容量指数增长（上限 <see cref="MaxSegmentSize"/>），减少段切换频率。
    /// 失败的 CAS 会将未使用的槽位数组回收到缓存（该段从未被发布，无并发访问风险）。
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void EnsureNextSegment(SegmentHeader* seg)
    {
        int nextCap = seg->NextCapacity;
        SegmentHeader* newSeg = AllocSegment(
            nextCap,
            seg->BaseIndex + seg->Capacity,
            _preferSteadyStateCapacity ? nextCap : Math.Min(nextCap * 2, MaxSegmentSize));

        if (Interlocked.CompareExchange(ref seg->Next, (nint)newSeg, (nint)0) != (nint)0)
        {
            // CAS 失败：将未使用的槽位数组回收到缓存，而非直接释放
            Slot* lostSlots = newSeg->Slots;
            *(int*)lostSlots = newSeg->Capacity;
            Slot* old = (Slot*)Interlocked.Exchange(ref _cachedSlots, (nint)lostSlots);
            if (old != null)
            {
                NativeMemory.Free(old);
                DebugIncSlotFree();
            }
            NativeMemory.Free(newSeg);
        }
    }

    /// <summary>
    /// 段满时确保下一段存在并推进 _tail。由生产者调用。
    /// 若下一段已通过预建完成分配，此方法仅执行一次 CAS 推进尾指针。
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void TryAdvanceTail(SegmentHeader* tail)
    {
        if (Volatile.Read(ref tail->Next) == (nint)0)
            EnsureNextSegment(tail);

        nint next = Volatile.Read(ref tail->Next);
        if (next != 0)
            Interlocked.CompareExchange(ref _tail, next, (nint)tail);
    }

    /// <summary>
    /// 尝试查看队列头部的元素但不移除它。此方法仅供单个消费者调用。
    /// </summary>
    /// <returns>如果成功查看返回 <c>true</c>，队列为空时返回 <c>false</c>。</returns>
    public readonly bool TryPeek(out T item)
    {
        SegmentHeader* head = _head;
        long pos = _dequeuePos;
        long offset = pos - head->BaseIndex;

        while (offset >= head->Capacity)
        {
            nint next = Volatile.Read(ref head->Next);
            if (next == 0)
            {
                item = default;
                return false;
            }
            head = (SegmentHeader*)next;
            offset = pos - head->BaseIndex;
        }

        if (Volatile.Read(ref head->Slots[offset].State) != 1)
        {
            item = default;
            return false;
        }

        item = head->Slots[offset].Value;
        return true;
    }

    /// <summary>
    /// 尝试从队列出队一个元素。此方法仅供单个消费者调用。
    /// 当段被完全消费后立即释放其槽位数组的原生内存，并前进到下一段。
    /// 段头结构体保留（生产者可能仍持有其指针），在 <see cref="Dispose"/> 时统一释放。
    /// </summary>
    /// <returns>如果成功出队返回 <c>true</c>，队列为空时返回 <c>false</c>。</returns>
    public bool TryDequeue(out T item)
    {
        SegmentHeader* head = _head;
        long pos = _dequeuePos;
        long offset = pos - head->BaseIndex;

        while (offset >= head->Capacity)
        {
            nint next = Volatile.Read(ref head->Next);
            if (next == 0)
            {
                item = default;
                return false;
            }
            RecycleSlots(head);
            head = (SegmentHeader*)next;
            _head = head;
            offset = pos - head->BaseIndex;
        }

        if (Volatile.Read(ref head->Slots[offset].State) != 1)
        {
            item = default;
            return false;
        }

        item = head->Slots[offset].Value;
        _dequeuePos = pos + 1;
        return true;
    }

    /// <summary>
    /// 释放所有段的原生内存（槽位数组 + 段头结构体）。
    /// 从 <c>_origin</c>（首段）遍历整条链表，确保不遗漏已消费但段头仍存活的旧段。
    /// 可多次调用，不会抛出异常。
    /// </summary>
    public void Dispose()
    {
        Slot* cached = (Slot*)_cachedSlots;
        _cachedSlots = 0;
        if (cached != null)
        {
            NativeMemory.Free(cached);
            DebugIncSlotFree();
        }

        SegmentHeader* seg = _origin;
        while (seg != null)
        {
            SegmentHeader* next = (SegmentHeader*)seg->Next;
            if (seg->Slots != null)
            {
                NativeMemory.Free(seg->Slots);
                DebugIncSlotFree();
            }
            NativeMemory.Free(seg);
            seg = next;
        }
        _head = null;
        _tail = 0;
        _origin = null;
    }
}
