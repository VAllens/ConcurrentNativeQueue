using System.Runtime.InteropServices;
using ConcurrentNativeQueueLibrary;

namespace ConcurrentNativeQueueUnitTest;

/// <summary>
/// <see cref="ConcurrentNativeQueue{T}"/> 单元测试
/// </summary>
public class ConcurrentNativeQueueUnitTest
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Vector3D
    {
        public double X, Y, Z;

        public Vector3D(double x, double y, double z) => (X, Y, Z) = (x, y, z);

        public override readonly bool Equals(object? obj) =>
            obj is Vector3D v && X == v.X && Y == v.Y && Z == v.Z;

        public override readonly int GetHashCode() => HashCode.Combine(X, Y, Z);
    }

    #region 构造与基础属性

    [Fact]
    public void Constructor_Default_CreatesEmptyQueue()
    {
        using var queue = new ConcurrentNativeQueue<int>();

        Assert.Equal(0, queue.Count);
        Assert.True(queue.IsEmpty);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_InvalidSegmentSize_Throws(int segmentSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConcurrentNativeQueue<int>(segmentSize));
    }

    [Fact]
    public void Constructor_SegmentSize1_Works()
    {
        using var queue = new ConcurrentNativeQueue<int>(1);

        queue.Enqueue(42);
        queue.Enqueue(99);
        Assert.Equal(2, queue.Count);

        Assert.True(queue.TryDequeue(out int a));
        Assert.Equal(42, a);
        Assert.True(queue.TryDequeue(out int b));
        Assert.Equal(99, b);
        Assert.True(queue.IsEmpty);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(-1, false)]
    [InlineData(-1, true)]
    public void Constructor_WithPreferSteadyStateCapacity_InvalidSegmentSize_Throws(int segmentSize, bool preferSteadyStateCapacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ConcurrentNativeQueue<int>(segmentSize, preferSteadyStateCapacity));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Constructor_WithPreferSteadyStateCapacity_Works(bool preferSteadyStateCapacity)
    {
        using var queue = new ConcurrentNativeQueue<int>(4, preferSteadyStateCapacity);

        for (int i = 0; i < 16; i++)
            queue.Enqueue(i);

        for (int i = 0; i < 16; i++)
        {
            Assert.True(queue.TryDequeue(out int item));
            Assert.Equal(i, item);
        }

        Assert.True(queue.IsEmpty);
        Assert.Equal(0, queue.Count);
    }

    #endregion

    #region 入队出队基础逻辑

    [Fact]
    public void Enqueue_SingleItem_CountBecomesOne()
    {
        using var queue = new ConcurrentNativeQueue<int>();

        queue.Enqueue(42);

        Assert.Equal(1, queue.Count);
        Assert.False(queue.IsEmpty);
    }

    [Fact]
    public void TryDequeue_EmptyQueue_ReturnsFalse()
    {
        using var queue = new ConcurrentNativeQueue<int>();

        bool result = queue.TryDequeue(out int item);

        Assert.False(result);
        Assert.Equal(0, item);
    }

    [Fact]
    public void TryDequeue_EmptyQueue_MultipleTimes_ThenEnqueue_StillWorks()
    {
        using var queue = new ConcurrentNativeQueue<int>();

        for (int i = 0; i < 50; i++)
            Assert.False(queue.TryDequeue(out _));

        queue.Enqueue(7);
        Assert.True(queue.TryDequeue(out int item));
        Assert.Equal(7, item);
    }

    [Fact]
    public void TryDequeue_SingleItem_ReturnsCorrectValue()
    {
        using var queue = new ConcurrentNativeQueue<int>();
        queue.Enqueue(99);

        bool result = queue.TryDequeue(out int item);

        Assert.True(result);
        Assert.Equal(99, item);
        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void EnqueueDequeue_FIFO_Order()
    {
        using var queue = new ConcurrentNativeQueue<int>();
        int count = 100;

        for (int i = 0; i < count; i++)
            queue.Enqueue(i);

        for (int i = 0; i < count; i++)
        {
            Assert.True(queue.TryDequeue(out int item));
            Assert.Equal(i, item);
        }

        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void EnqueueDequeue_Interleaved_MaintainsFIFO()
    {
        using var queue = new ConcurrentNativeQueue<int>();

        for (int round = 0; round < 10; round++)
        {
            for (int i = 0; i < 5; i++)
                queue.Enqueue(round * 5 + i);

            for (int i = 0; i < 5; i++)
            {
                Assert.True(queue.TryDequeue(out int item));
                Assert.Equal(round * 5 + i, item);
            }
        }

        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void EnqueueDequeue_LargeStruct_PreservesAllFields()
    {
        using var queue = new ConcurrentNativeQueue<Vector3D>();

        for (int i = 0; i < 50; i++)
            queue.Enqueue(new Vector3D(i, i * 1.5, i * 2.0));

        for (int i = 0; i < 50; i++)
        {
            Assert.True(queue.TryDequeue(out var item));
            Assert.Equal(new Vector3D(i, i * 1.5, i * 2.0), item);
        }
    }

    [Fact]
    public void Count_AccurateAfterMixedOperations()
    {
        using var queue = new ConcurrentNativeQueue<int>();

        Assert.Equal(0, queue.Count);

        for (int i = 0; i < 10; i++)
            queue.Enqueue(i);
        Assert.Equal(10, queue.Count);

        for (int i = 0; i < 3; i++)
            queue.TryDequeue(out _);
        Assert.Equal(7, queue.Count);

        for (int i = 0; i < 5; i++)
            queue.Enqueue(i);
        Assert.Equal(12, queue.Count);

        for (int i = 0; i < 12; i++)
            queue.TryDequeue(out _);
        Assert.Equal(0, queue.Count);
        Assert.True(queue.IsEmpty);
    }

    #endregion

    #region 段切换

    [Fact]
    public void SegmentTransition_SmallSegments_FIFO()
    {
        using var queue = new ConcurrentNativeQueue<int>(4);

        for (int i = 0; i < 20; i++)
            queue.Enqueue(i);

        Assert.Equal(20, queue.Count);

        for (int i = 0; i < 20; i++)
        {
            Assert.True(queue.TryDequeue(out int item));
            Assert.Equal(i, item);
        }

        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void SegmentTransition_Interleaved_CorrectBehavior()
    {
        using var queue = new ConcurrentNativeQueue<int>(4);

        for (int cycle = 0; cycle < 10; cycle++)
        {
            for (int i = 0; i < 3; i++)
                queue.Enqueue(cycle * 3 + i);

            for (int i = 0; i < 3; i++)
            {
                Assert.True(queue.TryDequeue(out int item));
                Assert.Equal(cycle * 3 + i, item);
            }
        }

        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void FillExactSegment_ThenDrainAll()
    {
        using var queue = new ConcurrentNativeQueue<int>(8);

        for (int i = 0; i < 8; i++)
            queue.Enqueue(i);

        Assert.Equal(8, queue.Count);

        for (int i = 0; i < 8; i++)
        {
            Assert.True(queue.TryDequeue(out int item));
            Assert.Equal(i, item);
        }

        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void RepeatedFullDrainCycles_HighPositionNumbers()
    {
        using var queue = new ConcurrentNativeQueue<int>(4);

        for (int cycle = 0; cycle < 100; cycle++)
        {
            for (int i = 0; i < 4; i++)
                queue.Enqueue(cycle * 4 + i);

            for (int i = 0; i < 4; i++)
            {
                Assert.True(queue.TryDequeue(out int item));
                Assert.Equal(cycle * 4 + i, item);
            }

            Assert.True(queue.IsEmpty);
        }
    }

    [Fact]
    public void MultipleSegments_LargeVolume_DataIntact()
    {
        using var queue = new ConcurrentNativeQueue<int>(2);

        int total = 100;
        for (int i = 0; i < total; i++)
            queue.Enqueue(i);

        for (int i = 0; i < total; i++)
        {
            Assert.True(queue.TryDequeue(out int item));
            Assert.Equal(i, item);
        }
    }

    [Fact]
    public void SegmentTransition_AfterPartialDequeue_DataIntact()
    {
        using var queue = new ConcurrentNativeQueue<int>(4);

        for (int i = 0; i < 3; i++)
            queue.Enqueue(i);
        for (int i = 0; i < 2; i++)
            queue.TryDequeue(out _);

        for (int i = 3; i < 7; i++)
            queue.Enqueue(i);

        for (int i = 2; i < 7; i++)
        {
            Assert.True(queue.TryDequeue(out int item));
            Assert.Equal(i, item);
        }
    }

    [Fact]
    public void SegmentTransition_LargeStruct_DataIntact()
    {
        using var queue = new ConcurrentNativeQueue<Vector3D>(2);

        for (int i = 0; i < 20; i++)
            queue.Enqueue(new Vector3D(i, -i, i * 0.1));

        for (int i = 0; i < 20; i++)
        {
            Assert.True(queue.TryDequeue(out var item));
            Assert.Equal(new Vector3D(i, -i, i * 0.1), item);
        }
    }

    #endregion

    #region TryPeek

    [Fact]
    public void TryPeek_EmptyQueue_ReturnsFalse()
    {
        using var queue = new ConcurrentNativeQueue<int>();

        Assert.False(queue.TryPeek(out int item));
        Assert.Equal(0, item);
    }

    [Fact]
    public void TryPeek_SingleItem_ReturnsValueWithoutRemoving()
    {
        using var queue = new ConcurrentNativeQueue<int>();
        queue.Enqueue(42);

        Assert.True(queue.TryPeek(out int item));
        Assert.Equal(42, item);
        Assert.Equal(1, queue.Count);

        Assert.True(queue.TryPeek(out int item2));
        Assert.Equal(42, item2);
    }

    [Fact]
    public void TryPeek_ThenDequeue_SameValue()
    {
        using var queue = new ConcurrentNativeQueue<int>();
        queue.Enqueue(10);
        queue.Enqueue(20);

        Assert.True(queue.TryPeek(out int peeked));
        Assert.True(queue.TryDequeue(out int dequeued));
        Assert.Equal(peeked, dequeued);
        Assert.Equal(10, dequeued);

        Assert.True(queue.TryPeek(out int next));
        Assert.Equal(20, next);
    }

    [Fact]
    public void TryPeek_AfterSegmentTransition_CorrectValue()
    {
        using var queue = new ConcurrentNativeQueue<int>(4);

        for (int i = 0; i < 6; i++)
            queue.Enqueue(i);
        for (int i = 0; i < 4; i++)
            queue.TryDequeue(out _);

        Assert.True(queue.TryPeek(out int item));
        Assert.Equal(4, item);
    }

    #endregion

    #region EnqueueRange

    [Fact]
    public void EnqueueRange_EmptySpan_NoEffect()
    {
        using var queue = new ConcurrentNativeQueue<int>();
        queue.EnqueueRange(ReadOnlySpan<int>.Empty);

        Assert.Equal(0, queue.Count);
        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void EnqueueRange_SingleItem_Works()
    {
        using var queue = new ConcurrentNativeQueue<int>();
        queue.EnqueueRange([42]);

        Assert.Equal(1, queue.Count);
        Assert.True(queue.TryDequeue(out int item));
        Assert.Equal(42, item);
    }

    [Fact]
    public void EnqueueRange_FIFO_Order()
    {
        using var queue = new ConcurrentNativeQueue<int>();
        int[] items = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        queue.EnqueueRange(items);

        Assert.Equal(10, queue.Count);

        for (int i = 0; i < items.Length; i++)
        {
            Assert.True(queue.TryDequeue(out int item));
            Assert.Equal(items[i], item);
        }
    }

    [Fact]
    public void EnqueueRange_SpanningMultipleSegments()
    {
        using var queue = new ConcurrentNativeQueue<int>(4);
        int[] items = new int[20];
        for (int i = 0; i < items.Length; i++)
            items[i] = i * 10;

        queue.EnqueueRange(items);

        Assert.Equal(20, queue.Count);
        for (int i = 0; i < items.Length; i++)
        {
            Assert.True(queue.TryDequeue(out int item));
            Assert.Equal(i * 10, item);
        }
    }

    [Fact]
    public void EnqueueRange_MixedWithSingleEnqueue()
    {
        using var queue = new ConcurrentNativeQueue<int>();

        queue.Enqueue(0);
        queue.EnqueueRange([1, 2, 3]);
        queue.Enqueue(4);
        queue.EnqueueRange([5, 6]);

        for (int i = 0; i <= 6; i++)
        {
            Assert.True(queue.TryDequeue(out int item));
            Assert.Equal(i, item);
        }
    }

    [Fact]
    public void EnqueueRange_LargeStruct_DataIntact()
    {
        using var queue = new ConcurrentNativeQueue<Vector3D>(4);
        var items = new Vector3D[15];
        for (int i = 0; i < items.Length; i++)
            items[i] = new Vector3D(i, -i, i * 0.5);

        queue.EnqueueRange(items);

        for (int i = 0; i < items.Length; i++)
        {
            Assert.True(queue.TryDequeue(out var item));
            Assert.Equal(new Vector3D(i, -i, i * 0.5), item);
        }
    }

    [Fact]
    public void EnqueueRange_MPSC_AllItemsReceived()
    {
        using var queue = new ConcurrentNativeQueue<long>(8);
        const int producerCount = 4;
        const int batchSize = 100;
        const int batchCount = 50;

        int totalExpected = producerCount * batchSize * batchCount;
        var barrier = new Barrier(producerCount + 1);
        var tasks = new Task[producerCount];

        for (int p = 0; p < producerCount; p++)
        {
            int pid = p;
            tasks[p] = Task.Run(() =>
            {
                barrier.SignalAndWait();
                long[] batch = new long[batchSize];
                for (int b = 0; b < batchCount; b++)
                {
                    for (int i = 0; i < batchSize; i++)
                        batch[i] = (long)pid * batchSize * batchCount + b * batchSize + i;
                    queue.EnqueueRange(batch);
                }
            });
        }

        var received = new HashSet<long>();
        var consumerTask = Task.Run(() =>
        {
            barrier.SignalAndWait();
            while (received.Count < totalExpected)
            {
                if (queue.TryDequeue(out long item))
                    received.Add(item);
                else
                    Thread.SpinWait(10);
            }
        });

        Task.WaitAll([.. tasks, consumerTask]);
        Assert.Equal(totalExpected, received.Count);
    }

    #endregion

    #region 并发 MPSC 测试

    [Fact]
    public void MPSC_MultipleProducers_SingleConsumer_AllItemsReceived()
    {
        using var queue = new ConcurrentNativeQueue<long>();
        const int producerCount = 4;
        const int itemsPerProducer = 10_000;

        var barrier = new Barrier(producerCount + 1);
        var producerTasks = new Task[producerCount];

        for (int p = 0; p < producerCount; p++)
        {
            int producerId = p;
            producerTasks[p] = Task.Run(() =>
            {
                barrier.SignalAndWait();
                for (int i = 0; i < itemsPerProducer; i++)
                    queue.Enqueue(producerId * itemsPerProducer + i);
            });
        }

        var received = new HashSet<long>();
        int totalExpected = producerCount * itemsPerProducer;
        var consumerTask = Task.Run(() =>
        {
            barrier.SignalAndWait();
            while (received.Count < totalExpected)
            {
                if (queue.TryDequeue(out long item))
                    received.Add(item);
                else
                    Thread.SpinWait(10);
            }
        });

        Task.WaitAll([.. producerTasks, consumerTask]);

        Assert.Equal(totalExpected, received.Count);
        for (int p = 0; p < producerCount; p++)
            for (int i = 0; i < itemsPerProducer; i++)
                Assert.Contains((long)p * itemsPerProducer + i, received);
    }

    [Fact]
    public void MPSC_ProducersFIFO_PerProducerOrdering()
    {
        using var queue = new ConcurrentNativeQueue<long>();
        const int producerCount = 4;
        const int itemsPerProducer = 5_000;

        var barrier = new Barrier(producerCount + 1);
        var producerTasks = new Task[producerCount];

        for (int p = 0; p < producerCount; p++)
        {
            int producerId = p;
            producerTasks[p] = Task.Run(() =>
            {
                barrier.SignalAndWait();
                for (int i = 0; i < itemsPerProducer; i++)
                    queue.Enqueue((long)producerId << 32 | (uint)i);
            });
        }

        int totalExpected = producerCount * itemsPerProducer;
        var lastSeen = new Dictionary<int, int>();
        int dequeued = 0;

        var consumerTask = Task.Run(() =>
        {
            barrier.SignalAndWait();
            while (dequeued < totalExpected)
            {
                if (queue.TryDequeue(out long item))
                {
                    int pid = (int)(item >> 32);
                    int seq = (int)(item & 0xFFFFFFFF);

                    if (lastSeen.TryGetValue(pid, out int last))
                        Assert.True(seq > last,
                            $"Producer {pid}: expected seq > {last}, got {seq}");

                    lastSeen[pid] = seq;
                    dequeued++;
                }
                else
                {
                    Thread.SpinWait(10);
                }
            }
        });

        Task.WaitAll([.. producerTasks, consumerTask]);
        Assert.Equal(totalExpected, dequeued);
    }

    [Fact]
    public void MPSC_WithSegmentTransitions_AllItemsReceived()
    {
        using var queue = new ConcurrentNativeQueue<int>(8);
        const int producerCount = 4;
        const int itemsPerProducer = 2_000;

        var barrier = new Barrier(producerCount + 1);
        var producerTasks = new Task[producerCount];

        for (int p = 0; p < producerCount; p++)
        {
            int producerId = p;
            producerTasks[p] = Task.Run(() =>
            {
                barrier.SignalAndWait();
                for (int i = 0; i < itemsPerProducer; i++)
                    queue.Enqueue(producerId * itemsPerProducer + i);
            });
        }

        int totalExpected = producerCount * itemsPerProducer;
        var received = new HashSet<int>();
        var consumerTask = Task.Run(() =>
        {
            barrier.SignalAndWait();
            while (received.Count < totalExpected)
            {
                if (queue.TryDequeue(out int item))
                    received.Add(item);
                else
                    Thread.SpinWait(10);
            }
        });

        Task.WaitAll([.. producerTasks, consumerTask]);
        Assert.Equal(totalExpected, received.Count);
    }

    [Fact]
    public void MPSC_MultipleBatches_DataIntegrity()
    {
        using var queue = new ConcurrentNativeQueue<long>(8);
        const int producerCount = 4;
        const int batchSize = 500;
        const int batchCount = 10;

        int totalExpected = producerCount * batchSize * batchCount;
        var received = new HashSet<long>();

        var consumerTask = Task.Run(() =>
        {
            while (received.Count < totalExpected)
            {
                if (queue.TryDequeue(out long item))
                    received.Add(item);
                else
                    Thread.SpinWait(10);
            }
        });

        for (int batch = 0; batch < batchCount; batch++)
        {
            var barrier = new Barrier(producerCount);
            var tasks = new Task[producerCount];

            for (int p = 0; p < producerCount; p++)
            {
                int producerId = p;
                int b = batch;
                tasks[p] = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    for (int i = 0; i < batchSize; i++)
                    {
                        long value = (long)b * producerCount * batchSize
                                   + (long)producerId * batchSize + i;
                        queue.Enqueue(value);
                    }
                });
            }

            Task.WaitAll(tasks);
            Thread.Sleep(10);
        }

        consumerTask.Wait();
        Assert.Equal(totalExpected, received.Count);
    }

    #endregion

    #region Dispose

    [Fact]
    public void Dispose_ReleasesMemory_DoesNotThrow()
    {
        var queue = new ConcurrentNativeQueue<int>();
        queue.Enqueue(1);
        queue.Enqueue(2);

        queue.Dispose();
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var queue = new ConcurrentNativeQueue<int>();
        queue.Enqueue(1);

        queue.Dispose();
        queue.Dispose();
    }

    #endregion
}
