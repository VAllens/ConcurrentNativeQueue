using BenchmarkDotNet.Attributes;
using System.Collections.Concurrent;
using ConcurrentNativeQueueLibrary;

namespace ConcurrentNativeQueueBenchmark;

/// <summary>
/// MPSC 多生产者单消费者吞吐量：NativeQueue vs ConcurrentQueue，
/// 变化生产者数量观察扩展性。
/// 使用专用 Thread + 三阶段同步（ready/start/done）消除线程池调度抖动。
/// </summary>
[MemoryDiagnoser]
public class MpscBenchmark
{
    [Params(1, 2, 4, 8)]
    public int ProducerCount;

    private const int TotalItems = 1_000_000;

    private ConcurrentNativeQueue<long> _nativeQueue;
    private ConcurrentQueue<long> _managedQueue = null!;

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
    public int ConcurrentQueue()
    {
        int itemsPerProducer = TotalItems / ProducerCount;
        int totalItems = itemsPerProducer * ProducerCount;
        var ready = new CountdownEvent(ProducerCount);
        var start = new ManualResetEventSlim(false);
        var done = new CountdownEvent(ProducerCount);
        var q = _managedQueue;

        for (int p = 0; p < ProducerCount; p++)
        {
            int pid = p;
            new Thread(() =>
            {
                ready.Signal();
                start.Wait();
                for (int i = 0; i < itemsPerProducer; i++)
                    q.Enqueue((long)pid * itemsPerProducer + i);
                done.Signal();
            }) { IsBackground = true }.Start();
        }

        ready.Wait();
        start.Set();

        int consumed = 0;
        while (consumed < totalItems)
        {
            if (q.TryDequeue(out _))
                consumed++;
        }

        done.Wait();
        ready.Dispose();
        start.Dispose();
        done.Dispose();
        return consumed;
    }

    [Benchmark]
    public int NativeQueue()
    {
        int itemsPerProducer = TotalItems / ProducerCount;
        int totalItems = itemsPerProducer * ProducerCount;
        var ready = new CountdownEvent(ProducerCount);
        var start = new ManualResetEventSlim(false);
        var done = new CountdownEvent(ProducerCount);

        for (int p = 0; p < ProducerCount; p++)
        {
            int pid = p;
            new Thread(() =>
            {
                ready.Signal();
                start.Wait();
                for (int i = 0; i < itemsPerProducer; i++)
                    _nativeQueue.Enqueue((long)pid * itemsPerProducer + i);
                done.Signal();
            }) { IsBackground = true }.Start();
        }

        ready.Wait();
        start.Set();

        int consumed = 0;
        while (consumed < totalItems)
        {
            if (_nativeQueue.TryDequeue(out _))
                consumed++;
        }

        done.Wait();
        ready.Dispose();
        start.Dispose();
        done.Dispose();
        return consumed;
    }
}
