using System.Collections.Concurrent;
using BenchmarkDotNet.Attributes;
using ConcurrentNativeQueueLibrary;

namespace ConcurrentNativeQueueBenchmark;

/// <summary>
/// 单线程顺序入队+出队吞吐量：NativeQueue vs ConcurrentQueue
/// </summary>
[MemoryDiagnoser]
public class SequentialBenchmark
{
    [Params(1_024, 65_536)]
    public int Count;

    [Params(false, true)]
    public bool PreferSteadyStateCapacity;

    private ConcurrentNativeQueue<long> _nativeQueue;
    private ConcurrentQueue<long> _managedQueue = null!;

    [IterationSetup]
    public void Setup()
    {
        _nativeQueue.Dispose();
        _nativeQueue = new ConcurrentNativeQueue<long>(PreferSteadyStateCapacity);
        _managedQueue = new ConcurrentQueue<long>();
    }

    [GlobalCleanup]
    public void Cleanup() => _nativeQueue.Dispose();

    [Benchmark(Baseline = true)]
    public void ConcurrentQueue()
    {
        var q = _managedQueue;
        int n = Count;
        for (int i = 0; i < n; i++)
            q.Enqueue(i);
        for (int i = 0; i < n; i++)
            q.TryDequeue(out _);
    }

    [Benchmark]
    public void ConcurrentNativeQueue()
    {
        int n = Count;
        for (int i = 0; i < n; i++)
            _nativeQueue.Enqueue(i);
        for (int i = 0; i < n; i++)
            _nativeQueue.TryDequeue(out _);
    }

    [Benchmark]
    public void ConcurrentNativeQueue_Batch()
    {
        int n = Count;
        Span<long> buf = stackalloc long[Math.Min(n, 256)];
        int remaining = n;
        long seq = 0;

        while (remaining > 0)
        {
            int batch = Math.Min(remaining, buf.Length);
            for (int i = 0; i < batch; i++)
                buf[i] = seq++;
            _nativeQueue.EnqueueRange(buf[..batch]);
            remaining -= batch;
        }

        for (int i = 0; i < n; i++)
            _nativeQueue.TryDequeue(out _);
    }
}
