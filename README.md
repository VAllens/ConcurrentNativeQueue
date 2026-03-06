# ConcurrentNativeQueue\<T\>

高性能无锁 MPSC（多生产者单消费者）原生队列，基于 CAS 环形缓冲区实现，适用于 .NET 10。

底层使用 `NativeMemory` 分配内存，**零 GC 压力**；支持自动扩容与收缩，无需手动管理缓冲区容量。

> 注意：这是一个完全使用 `Claude Opus 4.6 High Thinking` 编写的项目。

> 虽然通过单元测试，但暂未投入生产使用。

> 若要使用，请自行严谨测试，风险自负，本仓库作者和AI概不负责。

## 特性

- **无锁并发** — 基于 CAS（Compare-And-Swap）的 MPSC 队列，多线程入队无需加锁
- **原生内存** — 通过 `NativeMemory.AllocZeroed` 分配环形缓冲区，不产生托管堆分配
- **自动伸缩** — 缓冲区满时自动翻倍扩容；利用率低于 25% 时自动缩容至一半（不低于默认容量 16）
- **抖动抑制** — 扩容阈值（100%）与缩容阈值（25%）之间设有滞后区间，防止频繁伸缩
- **FIFO 保序** — 单生产者视角下严格先入先出，多生产者时每个生产者的消息顺序不变
- **非托管类型约束** — `where T : unmanaged`，支持 `int`、`long`、自定义值类型结构体等

## 快速开始

```csharp
using ConcurrentNativeQueueLibrary;

// 创建队列（默认初始容量 16）
using var queue = new ConcurrentNativeQueue<long>();

// 生产者线程入队
queue.Enqueue(42);
queue.Enqueue(100);

// 消费者线程出队
if (queue.TryDequeue(out long item))
    Console.WriteLine(item); // 42
```

### 指定初始容量

```csharp
// 容量会自动向上取整到最近的 2 的幂
using var queue = new ConcurrentNativeQueue<int>(1000); // 实际容量 1024
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
| `ConcurrentNativeQueue()` | 以默认容量 16 创建队列 |
| `ConcurrentNativeQueue(int capacity)` | 以指定容量创建队列（向上取整为 2 的幂，最小为 2） |
| `void Enqueue(T item)` | 入队。线程安全，支持多生产者并发调用。缓冲区满时自动扩容 |
| `bool TryDequeue(out T item)` | 出队。成功返回 `true`；队列为空返回 `false`。仅限单消费者调用 |
| `int Count` | 当前元素数量（并发场景下为近似值） |
| `bool IsEmpty` | 队列是否为空 |
| `int Capacity` | 当前环形缓冲区容量 |
| `void Dispose()` | 释放底层原生内存。可多次调用，不会抛出异常 |

## 设计原理

### 环形缓冲区 + 序列号

每个槽位包含一个 `Sequence` 字段，用于编码状态：

- `Sequence == 入队位置`：槽位空闲，可写入
- `Sequence == 入队位置 + 1`：数据已就绪，可读取

生产者通过 CAS 竞争 `_enqueuePos`，成功后写入数据并推进序列号；消费者检查序列号是否就绪后读取数据。

### 自动扩容

当缓冲区满（`diff < 0`）时触发扩容：

1. 唯一胜出线程设置 `_resizing` 标志
2. 等待所有正在执行的操作（`_activeOps`）退出
3. 分配双倍容量新缓冲区，迁移有效元素
4. 释放旧缓冲区，清除 `_resizing` 标志

### 自动缩容

每次出队后检查利用率，当元素数 < 容量 / 4 且容量超过默认值时触发缩容。缩容失败（另一线程正在 resize）时直接放弃，下次再试。

## 项目结构

```
ConcurrentNativeQueue/
├── ConcurrentNativeQueueLibrary/     # 核心库
│   └── ConcurrentNativeQueue.cs
├── ConcurrentNativeQueueDemo/        # MPSC 演示程序（支持 AOT 发布）
│   └── Program.cs
├── ConcurrentNativeQueueBenchmark/   # BenchmarkDotNet 性能基准
│   ├── SequentialBenchmark.cs        #   单线程顺序吞吐量 vs ConcurrentQueue
│   ├── MpscBenchmark.cs              #   多生产者单消费者并发吞吐量 vs ConcurrentQueue
│   └── GrowthBenchmark.cs            #   预分配 vs 自动扩容开销
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

## 要求

- .NET 10 SDK
- 允许 `unsafe` 代码（已在项目文件中启用）

## 许可证

MIT
