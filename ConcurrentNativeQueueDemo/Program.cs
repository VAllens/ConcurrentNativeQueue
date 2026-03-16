using System.Text;
using System.Collections.Concurrent;
using ConcurrentNativeQueueLibrary;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
Console.OutputEncoding = Encoding.UTF8;

const int ProducerCount = 4;
const int ItemsPerProducer = 50_000;
int totalItems = ProducerCount * ItemsPerProducer;

using var queue = new ConcurrentNativeQueue<long>();

Console.WriteLine("=== ConcurrentNativeQueue<long> MPSC 示例 ===");
Console.WriteLine($"生产者: {ProducerCount}，每个生产者: {ItemsPerProducer} 条，总计: {totalItems} 条");
Console.WriteLine();

// --- 批量入队 & TryPeek 演示 ---
Console.WriteLine("--- 批量入队 & TryPeek 演示 ---");
using (var demoQueue = new ConcurrentNativeQueue<long>())
{
    long[] batch = [100, 200, 300, 400, 500];
    demoQueue.EnqueueRange(batch);
    Console.WriteLine($"EnqueueRange 入队 {batch.Length} 个元素，Count = {demoQueue.Count}");

    if (demoQueue.TryPeek(out long head))
        Console.WriteLine($"TryPeek 查看头部: {head}（不移除，Count = {demoQueue.Count}）");

    if (demoQueue.TryDequeue(out long first))
        Console.WriteLine($"TryDequeue 出队: {first}，Count = {demoQueue.Count}");

    if (demoQueue.TryPeek(out long newHead))
        Console.WriteLine($"TryPeek 查看新头部: {newHead}");
}
Console.WriteLine();

var barrier = new Barrier(ProducerCount + 1);
var producerTasks = new Task[ProducerCount];

for (int p = 0; p < ProducerCount; p++)
{
    int pid = p;
    producerTasks[p] = Task.Run(() =>
    {
        barrier.SignalAndWait();
        for (int i = 0; i < ItemsPerProducer; i++)
            queue.Enqueue((long)pid * ItemsPerProducer + i);
    });
}

var consumed = new long[totalItems];
int consumedCount = 0;

var sw = System.Diagnostics.Stopwatch.StartNew();
barrier.SignalAndWait();

while (consumedCount < totalItems)
{
    if (queue.TryDequeue(out long item))
        consumed[consumedCount++] = item;
}

Task.WaitAll(producerTasks);
sw.Stop();

Array.Sort(consumed);
bool allPresent = true;
for (int i = 0; i < totalItems; i++)
{
    if (consumed[i] != i)
    {
        allPresent = false;
        Console.WriteLine($"[错误] 缺失或重复: 索引 {i} 处期望 {i}，实际 {consumed[i]}");
        break;
    }
}

Console.WriteLine($"耗时: {sw.Elapsed.TotalMilliseconds:F2} ms");
Console.WriteLine($"吞吐量: {totalItems / sw.Elapsed.TotalSeconds:N0} ops/sec");
Console.WriteLine($"剩余元素: {queue.Count}");
Console.WriteLine($"数据完整性: {(allPresent ? "通过 ✓" : "失败 ✗")}");
Console.WriteLine();

// --- 固定队列深度循环验证（稳态留存量）---
Console.WriteLine("--- 固定队列深度循环验证（稳态留存量）---");
const int fixedDepth = 4096;
const int loopCount = 200_000;
const int fixedSegmentSize = 64;

// ConcurrentNativeQueue
using (var fixedNative = new ConcurrentNativeQueue<long>(fixedSegmentSize, true))
{
    for (int i = 0; i < fixedDepth; i++)
        fixedNative.Enqueue(i);

#if DEBUG
    var before = fixedNative.GetDebugSlotStats();
#endif

    var nativeSw = System.Diagnostics.Stopwatch.StartNew();
    long next = fixedDepth;
    long nativeChecksum = 0;
    for (int i = 0; i < loopCount; i++)
    {
        fixedNative.Enqueue(next++);
        fixedNative.TryDequeue(out long item);
        nativeChecksum += item;
    }
    nativeSw.Stop();

#if DEBUG
    var after = fixedNative.GetDebugSlotStats();
#endif

    Console.WriteLine($"[Native] Depth={fixedDepth}, Loop={loopCount}, SegmentSize={fixedSegmentSize}");
    Console.WriteLine($"[Native] 耗时: {nativeSw.Elapsed.TotalMilliseconds:F2} ms, 吞吐: {loopCount / nativeSw.Elapsed.TotalSeconds:N0} loop/s, 校验: {nativeChecksum}");
#if DEBUG
    Console.WriteLine($"[Native][DEBUG] SlotAlloc={after.SlotAllocCount}, SlotReuse={after.SlotReuseCount}, SlotFree={after.SlotFreeCount}");
    Console.WriteLine($"[Native][DEBUG] ΔReuse={after.SlotReuseCount - before.SlotReuseCount}, ΔAlloc={after.SlotAllocCount - before.SlotAllocCount}, ΔFree={after.SlotFreeCount - before.SlotFreeCount}");
#endif
}

// ConcurrentQueue
{
    var fixedManaged = new ConcurrentQueue<long>();
    for (int i = 0; i < fixedDepth; i++)
        fixedManaged.Enqueue(i);

    var managedSw = System.Diagnostics.Stopwatch.StartNew();
    long next = fixedDepth;
    long managedChecksum = 0;
    for (int i = 0; i < loopCount; i++)
    {
        fixedManaged.Enqueue(next++);
        fixedManaged.TryDequeue(out long item);
        managedChecksum += item;
    }
    managedSw.Stop();

    Console.WriteLine($"[Managed] Depth={fixedDepth}, Loop={loopCount}");
    Console.WriteLine($"[Managed] 耗时: {managedSw.Elapsed.TotalMilliseconds:F2} ms, 吞吐: {loopCount / managedSw.Elapsed.TotalSeconds:N0} loop/s, 校验: {managedChecksum}");
}
