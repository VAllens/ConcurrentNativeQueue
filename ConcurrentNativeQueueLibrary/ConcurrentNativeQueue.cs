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
/// 一个线程安全的无锁原生队列，用于在并发环境中存储非托管类型。
/// </summary>
/// <remarks>
/// 此结构是一个MPSC多生产者单消费者的CAS无锁队列，旨在为非托管类型的队列提供高效的并发访问。<br/>
/// 使用分段链表设计，每个段是固定大小的原生内存槽位数组：<br/>
/// - 入队快速路径：Volatile.Read 检测可用位置 → CAS 占位 → 写入数据；<br/>
/// - 段满检测为纯读操作（Volatile.Read），不产生任何原子写开销；<br/>
/// - 段满时自动分配新段并链接到尾部，无需全局暂停或缓冲区迁移；<br/>
/// - 段被完全消费后自动释放其原生内存，不产生 GC 压力。<br/>
/// 段元数据为托管对象（由 GC 管理生命周期，确保并发安全），槽位数据使用 <see cref="NativeMemory"/> 分配。<br/>
/// 生产者与消费者的热点字段通过缓存行填充隔离，消除 false sharing。
/// </remarks>
/// <typeparam name="T">指定队列中元素的类型。该类型必须是非托管类型，即不包含任何引用类型。</typeparam>
public unsafe struct ConcurrentNativeQueue<T> : IDisposable where T : unmanaged
{
    private const int DefaultSegmentSize = 32;

    private struct Slot
    {
        public int State;
        public T Value;
    }

    /// <summary>
    /// 段元数据使用托管对象，由 GC 跟踪引用：即使生产者持有对旧段的引用，
    /// 该段也不会被回收，从而避免 use-after-free。
    /// 槽位数组使用 NativeMemory 分配，在段消费完毕后手动释放。
    /// EnqueuePos 通过 <see cref="PaddedLong"/> 隔离到独立缓存行，
    /// 避免与只读元数据（Capacity、BaseIndex 等）产生 false sharing。
    /// </summary>
    private sealed class Segment
    {
        internal Slot* Slots;
        internal readonly int Capacity;
        internal readonly long BaseIndex;
        internal Segment? Next;

        internal PaddedLong EnqueuePos;

        internal Segment(int capacity, long baseIndex)
        {
            Capacity = capacity;
            BaseIndex = baseIndex;
            EnqueuePos.Value = baseIndex;
            Slots = (Slot*)NativeMemory.AllocZeroed((nuint)capacity, (nuint)sizeof(Slot));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void FreeSlots()
        {
            Slot* s = Slots;
            if (s != null)
            {
                Slots = null;
                NativeMemory.Free(s);
            }
        }
    }

    // == 消费者缓存行 ==
    private Segment _head;
    private long _dequeuePos;
    private readonly int _segmentSize;

#pragma warning disable CS0169
    private long _p0, _p1, _p2, _p3, _p4, _p5, _p6, _p7;
#pragma warning restore CS0169

    // == 生产者缓存行 ==
    private Segment _tail;

    public ConcurrentNativeQueue() : this(DefaultSegmentSize) { }

    public ConcurrentNativeQueue(int segmentSize)
    {
#if NET8_0_OR_GREATER
        ArgumentOutOfRangeException.ThrowIfLessThan(segmentSize, 1);
#else
        if (segmentSize < 1)
            throw new ArgumentOutOfRangeException(nameof(segmentSize), segmentSize, "Segment size must be greater than 0.");
#endif
        this = default;
        _segmentSize = Math.Max(2, segmentSize);
        var initial = new Segment(_segmentSize, 0);
        _head = initial;
        _tail = initial;
    }

    /// <summary>当前队列中的元素数量（近似值，在并发场景下可能瞬间不精确）。</summary>
    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            Segment tail = Volatile.Read(ref _tail);
            long enqPos = Volatile.Read(ref tail.EnqueuePos.Value);
            long cap = tail.BaseIndex + tail.Capacity;
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
    /// 当段已满时自动分配新段，无需缓冲区迁移或全局暂停。
    /// </summary>
    public void Enqueue(T item)
    {
        SpinWait spin = default;

        while (true)
        {
            Segment tail = Volatile.Read(ref _tail);
            long pos = Volatile.Read(ref tail.EnqueuePos.Value);
            long offset = pos - tail.BaseIndex;

            if ((ulong)offset >= (ulong)tail.Capacity)
            {
                if (Volatile.Read(ref tail.Next) == null)
                {
                    var newSeg = new Segment(_segmentSize, tail.BaseIndex + tail.Capacity);
                    if (Interlocked.CompareExchange(ref tail.Next, newSeg, null) != null)
                        newSeg.FreeSlots();
                }

                Segment? next = Volatile.Read(ref tail.Next);
                if (next != null)
                    Interlocked.CompareExchange(ref _tail, next, tail);

                spin.SpinOnce();
                continue;
            }

            if (Interlocked.CompareExchange(ref tail.EnqueuePos.Value, pos + 1, pos) == pos)
            {
                tail.Slots[offset].Value = item;
                Volatile.Write(ref tail.Slots[offset].State, 1);
                return;
            }

            spin.SpinOnce();
        }
    }

    /// <summary>
    /// 尝试从队列出队一个元素。此方法仅供单个消费者调用。
    /// 当段被完全消费后自动释放其原生内存并前进到下一段。
    /// </summary>
    /// <returns>如果成功出队返回 <c>true</c>，队列为空时返回 <c>false</c>。</returns>
    public bool TryDequeue(out T item)
    {
        Segment head = _head;
        long pos = _dequeuePos;
        long offset = pos - head.BaseIndex;

        while (offset >= head.Capacity)
        {
            Segment? next = Volatile.Read(ref head.Next);
            if (next == null)
            {
                item = default;
                return false;
            }
            head.FreeSlots();
            _head = next;
            head = next;
            offset = pos - head.BaseIndex;
        }

        if (Volatile.Read(ref head.Slots[offset].State) != 1)
        {
            item = default;
            return false;
        }

        item = head.Slots[offset].Value;
        _dequeuePos = pos + 1;
        return true;
    }

    public void Dispose()
    {
        Segment? seg = _head;
        while (seg != null)
        {
            Segment? next = seg.Next;
            seg.FreeSlots();
            seg = next;
        }
        _head = null!;
        _tail = null!;
    }
}
