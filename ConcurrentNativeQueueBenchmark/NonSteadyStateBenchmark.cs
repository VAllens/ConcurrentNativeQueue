using System.Collections.Concurrent;
using BenchmarkDotNet.Attributes;
using ConcurrentNativeQueueLibrary;

namespace ConcurrentNativeQueueBenchmark;

/// <summary>
/// 非稳态容量负载：周期性突发入队 + 分阶段出队，
/// 让队列深度在低位与高位间持续波动（锯齿/脉冲），评估扩容与回收路径的开销。
/// </summary>
[MemoryDiagnoser]
public class NonSteadyStateBenchmark
{
    [Params(8_192, 65_536)]
    public int PeakDepth;

    [Params(32)]
    public int Cycles;

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
    public long ConcurrentQueue_NonSteady()
    {
        var q = _managedQueue;
        int peak = PeakDepth;
        int cycles = Cycles;

        long next = 0;
        long checksum = 0;

        for (int c = 0; c < cycles; c++)
        {
            // Phase A: 突发写入到峰值
            for (int i = 0; i < peak; i++)
                q.Enqueue(next++);

            // Phase B: 快速消费到 25%（模拟消费者追上）
            int drainToQuarter = peak - (peak / 4);
            for (int i = 0; i < drainToQuarter; i++)
            {
                q.TryDequeue(out long item);
                checksum += item;
            }

            // Phase C: 再次突发写入 50% 峰值（模拟下一波写入）
            int burst = peak / 2;
            for (int i = 0; i < burst; i++)
                q.Enqueue(next++);

            // Phase D: 完全排空
            int drainAll = (peak / 4) + burst;
            for (int i = 0; i < drainAll; i++)
            {
                q.TryDequeue(out long item);
                checksum += item;
            }
        }

        return checksum;
    }

    [Benchmark]
    public long ConcurrentNativeQueue_NonSteady()
    {
        int peak = PeakDepth;
        int cycles = Cycles;

        long next = 0;
        long checksum = 0;

        for (int c = 0; c < cycles; c++)
        {
            // Phase A: 突发写入到峰值
            for (int i = 0; i < peak; i++)
                _nativeQueue.Enqueue(next++);

            // Phase B: 快速消费到 25%
            int drainToQuarter = peak - (peak / 4);
            for (int i = 0; i < drainToQuarter; i++)
            {
                _nativeQueue.TryDequeue(out long item);
                checksum += item;
            }

            // Phase C: 再次突发写入 50% 峰值
            int burst = peak / 2;
            for (int i = 0; i < burst; i++)
                _nativeQueue.Enqueue(next++);

            // Phase D: 完全排空
            int drainAll = (peak / 4) + burst;
            for (int i = 0; i < drainAll; i++)
            {
                _nativeQueue.TryDequeue(out long item);
                checksum += item;
            }
        }

        return checksum;
    }
}
