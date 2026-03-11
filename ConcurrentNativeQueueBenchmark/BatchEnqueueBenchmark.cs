using System.Collections.Concurrent;
using BenchmarkDotNet.Attributes;
using ConcurrentNativeQueueLibrary;

namespace ConcurrentNativeQueueBenchmark;

/// <summary>
/// 批量入队（EnqueueRange）与逐条入队（Enqueue）吞吐量对比。
/// 同时与 ConcurrentQueue 的逐条入队做参照。
/// </summary>
[MemoryDiagnoser]
public class BatchEnqueueBenchmark
{
    [Params(1_024, 65_536)]
    public int Count;

    private long[] _data = null!;
    private ConcurrentNativeQueue<long> _nativeQueue;
    private ConcurrentQueue<long> _managedQueue = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _data = new long[Count];
        for (int i = 0; i < Count; i++)
            _data[i] = i;
    }

    [IterationSetup]
    public void Setup()
    {
        _nativeQueue.Dispose();
        _nativeQueue = new ConcurrentNativeQueue<long>();
        _managedQueue = new ConcurrentQueue<long>();
    }

    [GlobalCleanup]
    public void Cleanup() => _nativeQueue.Dispose();

    [Benchmark(Baseline = true)]
    public void ConcurrentQueue_EnqueueLoop()
    {
        var q = _managedQueue;
        var data = _data;
        for (int i = 0; i < data.Length; i++)
            q.Enqueue(data[i]);
    }

    [Benchmark]
    public void ConcurrentNativeQueue_EnqueueLoop()
    {
        var data = _data;
        for (int i = 0; i < data.Length; i++)
            _nativeQueue.Enqueue(data[i]);
    }

    [Benchmark]
    public void ConcurrentNativeQueue_EnqueueRange()
    {
        _nativeQueue.EnqueueRange(_data);
    }
}
