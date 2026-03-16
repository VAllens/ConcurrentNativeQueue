using BenchmarkDotNet.Attributes;
using ConcurrentNativeQueueLibrary;

namespace ConcurrentNativeQueueBenchmark;

/// <summary>
/// 不同段大小对入队吞吐量的影响对比
/// </summary>
[MemoryDiagnoser]
public class SegmentSizeBenchmark
{
    [Params(1_024, 65_536)]
    public int Count;

    [Params(false, true)]
    public bool PreferSteadyStateCapacity;

    private ConcurrentNativeQueue<long> _smallSegment;
    private ConcurrentNativeQueue<long> _largeSegment;

    [IterationSetup]
    public void Setup()
    {
        _smallSegment.Dispose();
        _largeSegment.Dispose();
        _smallSegment = new ConcurrentNativeQueue<long>(32, PreferSteadyStateCapacity);
        _largeSegment = new ConcurrentNativeQueue<long>(1024, PreferSteadyStateCapacity);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _smallSegment.Dispose();
        _largeSegment.Dispose();
    }

    [Benchmark(Baseline = true)]
    public void SmallSegments()
    {
        int n = Count;
        for (int i = 0; i < n; i++)
            _smallSegment.Enqueue(i);
    }

    [Benchmark]
    public void LargeSegments()
    {
        int n = Count;
        for (int i = 0; i < n; i++)
            _largeSegment.Enqueue(i);
    }
}
