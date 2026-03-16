**[English](README.en-US.md)** | 简体中文

# ConcurrentNativeQueue\<T\>

高性能无锁 MPSC（多生产者单消费者）原生队列，基于分段链表设计，适用于 .NET 6+。

所有内存（段头结构体 + 槽位数组）均通过 `NativeMemory` 分配，**零 GC 压力、零托管堆分配**；段满时自动分配新段链接到尾部，槽位数组在段消费完后优先回收到单槽缓存（用于复用），段头在 `Dispose` 时统一回收。

> 注意：这是一个完全使用 `Claude Opus 4.6 High Thinking` 编写的项目。

> 虽然通过单元测试，但暂未投入生产使用。

> 若要使用，请自行严谨测试，风险自负，本仓库作者和AI概不负责。

## 特性

- **无锁并发** — 入队通过 `Volatile.Read` + `Interlocked.CompareExchange` (CAS) 占位，段满检测为纯读操作，不产生原子写开销
- **批量入队** — `EnqueueRange` 单次 CAS 占位多个槽位，分摊原子操作开销；跨段时自动分批写入
- **分段链表** — 段按需分配并链接；默认模式容量指数增长（上限 1M），稳态模式容量保持稳定
- **容量策略开关** — `preferSteadyStateCapacity=false`（默认）偏向峰值吞吐；`true` 偏向稳态复用与缓存命中
- **全 Native 内存** — 段头结构体与槽位数组均通过 `NativeMemory` 分配，不产生托管堆分配
- **单槽复用缓存** — 段消费完后槽位数组回收到单槽缓存；新段分配优先复用；缓存被替换时释放旧槽位数组
- **段头延迟回收** — 段头可能被生产者暂时持有，统一在 `Dispose` 释放
- **False Sharing 防护** — 生产者/消费者热点字段通过缓存行填充隔离，`EnqueuePos` 使用 128 字节独占布局
- **FIFO 保序** — 单生产者视角下严格先入先出，多生产者时每个生产者的消息顺序不变
- **非托管类型约束** — `where T : unmanaged`，支持 `int`、`long`、自定义值类型结构体等

## 快速开始

```csharp
using ConcurrentNativeQueueLibrary;

// 创建队列（默认段大小 32，默认高吞吐扩容模式）
using var queue = new ConcurrentNativeQueue<long>();

// 生产者线程入队
queue.Enqueue(42);
queue.Enqueue(100);

// 批量入队
queue.EnqueueRange(new long[] { 200, 300, 400 });

// 查看头部元素（不移除）
if (queue.TryPeek(out long head))
    Console.WriteLine(head); // 42

// 消费者线程出队
if (queue.TryDequeue(out long item))
    Console.WriteLine(item); // 42
```

### 构造参数示例

```csharp
// 指定初始段大小（默认：后续段容量指数增长，上限 1M）
using var q1 = new ConcurrentNativeQueue<int>(128);

// 指定稳态容量模式（后续段保持同容量，提升复用命中）
using var q2 = new ConcurrentNativeQueue<int>(128, preferSteadyStateCapacity: true);

// 使用默认段大小 + 指定模式
using var q3 = new ConcurrentNativeQueue<int>(preferSteadyStateCapacity: true);
```

### MPSC 并发模式

```csharp
using var queue = new ConcurrentNativeQueue<long>();

const int producerCount = 4;
const int itemsPerProducer = 50_000;
int totalItems = producerCount * itemsPerProducer;

var barrier = new Barrier(producerCount + 1);
var tasks = new Task[producerCount];

for (int p = 0; p < producerCount; p++)
{
    int pid = p;
    tasks[p] = Task.Run(() =>
    {
        barrier.SignalAndWait();
        for (int i = 0; i < itemsPerProducer; i++)
            queue.Enqueue((long)pid * itemsPerProducer + i);
    });
}

barrier.SignalAndWait();

int consumed = 0;
while (consumed < totalItems)
{
    if (queue.TryDequeue(out long item))
        consumed++;
}

Task.WaitAll(tasks);
```

## API

| 成员 | 说明 |
|---|---|
| `ConcurrentNativeQueue()` | 以默认段大小 32 创建队列，默认高吞吐扩容模式 |
| `ConcurrentNativeQueue(int segmentSize)` | 指定初始段大小创建队列（最小为 2） |
| `ConcurrentNativeQueue(bool preferSteadyStateCapacity)` | 使用默认段大小 32，并指定容量策略模式 |
| `ConcurrentNativeQueue(int segmentSize, bool preferSteadyStateCapacity)` | 指定初始段大小与容量策略模式 |
| `void Enqueue(T item)` | 入队。线程安全，支持多生产者并发调用。段满时自动分配新段 |
| `void EnqueueRange(ReadOnlySpan<T> items)` | 批量入队。单次 CAS 占位多个槽位，跨段时自动分批。线程安全 |
| `bool TryPeek(out T item)` | 查看头部元素但不移除。成功返回 `true`；队列为空返回 `false`。仅限单消费者调用 |
| `bool TryDequeue(out T item)` | 出队。成功返回 `true`；队列为空返回 `false`。仅限单消费者调用 |
| `int Count` | 当前元素数量（并发场景下为近似值） |
| `bool IsEmpty` | 队列是否为空 |
| `void Dispose()` | 释放所有段的原生内存。可多次调用，不会抛出异常 |
| `GetDebugSlotStats()` | 仅 `DEBUG` 编译下可用，返回槽位分配/复用/释放计数（用于验证复用） |

## 设计原理

### 分段链表 + 状态标记

队列由原生段组成，每个段包含一个 `NativeMemory` 分配的槽位数组。每个槽位有 `State` 字段标记写入状态：

- `State == 0`：槽位空闲，尚未写入
- `State == 1`：数据已就绪，可读取

生产者通过 `Volatile.Read` 读取当前 `EnqueuePos`，再用 `Interlocked.CompareExchange` (CAS) 原子占位。段满检测为纯读操作，不产生原子写开销。写入数据后通过 `Volatile.Write` 设置 `State = 1` 通知消费者。

`EnqueueRange` 进一步优化：单次 CAS 占位 N 个槽位，先批量写入 Value，经 `Thread.MemoryBarrier()` 后再批量设置 State，将 N 次原子操作分摊为 ⌈N/段剩余容量⌉ 次。

### 段容量模式

- **吞吐模式（默认）**：`preferSteadyStateCapacity=false`，`NextCapacity` 指数增长（上限 1M），减少段切换频率
- **稳态模式**：`preferSteadyStateCapacity=true`，段容量保持稳定，更容易命中复用缓存

### 段生命周期（全 Native + 单槽复用缓存）

段头结构体与槽位数组均通过 `NativeMemory` 分配，无托管对象参与。
核心生命周期挑战：**生产者可能在任意时刻被抢占并持有旧段指针**，因此段头不能在消费后立即释放。

1. **段满** — 生产者检测到 `offset >= capacity`，通过 CAS 创建新段并链接到 `Next`，再推进 `_tail`。若 CAS 失败，未发布的新段头会立即释放，未使用的槽位数组回收到单槽缓存
2. **段消费完毕** — 消费者检测到 `offset >= capacity`，将该段槽位数组回收到单槽缓存（若缓存已有旧槽位则释放旧槽位），再前进到 `Next`
3. **新段分配** — 优先从单槽缓存取回槽位数组复用；不满足容量条件时再申请新内存
4. **Dispose** — 从 `_origin` 遍历整条链表，释放所有段头和剩余槽位数组（含缓存中的槽位）

### 相比旧版环形缓冲区的改进

| 维度 | 旧实现（环形缓冲区） | 新实现（分段链表） |
|---|---|---|
| 每次操作原子指令 | `Interlocked.Inc` + CAS + `Interlocked.Dec` = 3次 | CAS 占位 = 1次（`EnqueueRange` 可 1 次占 N 个） |
| 扩容方式 | Stop-the-world + O(N) 迁移 | 分配新段 O(1)，CAS 链接 |
| 容量策略 | 单一策略 | 吞吐模式（指数增长）/稳态模式（固定容量）可切换 |
| 槽位回收 | 显式缩容 + 迁移 | 消费后回收到单槽缓存，优先复用 |

## 项目结构

```bash
ConcurrentNativeQueue/
├── ConcurrentNativeQueueLibrary/     # 核心库
│   └── ConcurrentNativeQueue.cs
├── ConcurrentNativeQueueDemo/        # MPSC 演示程序（支持 AOT 发布）
│   └── Program.cs
├── ConcurrentNativeQueueBenchmark/   # BenchmarkDotNet 性能基准
│   ├── MpscBenchmark.cs              #   多生产者单消费者并发吞吐量 vs ConcurrentQueue
│   ├── SequentialBenchmark.cs        #   单线程顺序吞吐量 vs ConcurrentQueue（含模式参数）
│   ├── BatchEnqueueBenchmark.cs      #   EnqueueRange vs 逐条 Enqueue 吞吐量对比
│   ├── SegmentSizeBenchmark.cs       #   不同段大小对吞吐量的影响
│   ├── FixedDepthLoopBenchmark.cs    #   固定深度稳态场景（含模式参数）
│   └── NonSteadyStateBenchmark.cs    #   非稳态波动场景（含模式参数）
└── ConcurrentNativeQueueUnitTest/    # xUnit 单元测试
    └── ConcurrentNativeQueueUnitTest.cs
```

## 运行

### 运行演示

```bash
dotnet run --project ConcurrentNativeQueueDemo
```

### 运行测试

```bash
dotnet test
```

### 运行基准测试

```bash
dotnet run --project ConcurrentNativeQueueBenchmark -c Release -- --filter *
```

历史报告、结果说明及大小核绑定方式见 [benchmark-results/README.md](benchmark-results/README.md)。

## 要求

- .NET SDK 6.0 或更高。
- 允许 `unsafe` 代码（已在项目文件中启用）

## 许可证

MIT
