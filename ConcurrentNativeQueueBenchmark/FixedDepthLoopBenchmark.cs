using System.Collections.Concurrent;
using BenchmarkDotNet.Attributes;
using ConcurrentNativeQueueLibrary;

namespace ConcurrentNativeQueueBenchmark;

/// <summary>
/// 固定队列深度循环：先预填充到指定深度，再执行 (Enqueue + Dequeue) 循环，
/// 始终保持近似固定留存量，模拟稳态工作负载。
/// </summary>
[MemoryDiagnoser]
public class FixedDepthLoopBenchmark
{
    [Params(256, 4096)]
    public int Depth;

    [Params(200_000)]
    public int LoopCount;

    [Params(64)]
    public int SegmentSize;

    [Params(false, true)]
    public bool PreferSteadyStateCapacity;

    private ConcurrentNativeQueue<long> _nativeQueue;
    private ConcurrentQueue<long> _managedQueue = null!;

    [IterationSetup]
    public void Setup()
    {
        _nativeQueue.Dispose();
        _nativeQueue = new ConcurrentNativeQueue<long>(SegmentSize, PreferSteadyStateCapacity);
        _managedQueue = new ConcurrentQueue<long>();
    }

    [GlobalCleanup]
    public void Cleanup() => _nativeQueue.Dispose();

    [Benchmark(Baseline = true)]
    public long ConcurrentQueue_FixedDepthLoop()
    {
        var q = _managedQueue;
        int depth = Depth;
        int loops = LoopCount;

        for (int i = 0; i < depth; i++)
            q.Enqueue(i);

        long checksum = 0;
        long next = depth;

        for (int i = 0; i < loops; i++)
        {
            q.Enqueue(next++);
            q.TryDequeue(out long item);
            checksum += item;
        }

        return checksum;
    }

    [Benchmark]
    public long ConcurrentNativeQueue_FixedDepthLoop()
    {
        int depth = Depth;
        int loops = LoopCount;

        for (int i = 0; i < depth; i++)
            _nativeQueue.Enqueue(i);

        long checksum = 0;
        long next = depth;

        for (int i = 0; i < loops; i++)
        {
            _nativeQueue.Enqueue(next++);
            _nativeQueue.TryDequeue(out long item);
            checksum += item;
        }

        return checksum;
    }
}
