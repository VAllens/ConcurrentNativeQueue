English | **[简体中文](README.md)**

# ConcurrentNativeQueue\<T\>

A high-performance, lock-free MPSC (Multi-Producer, Single-Consumer) native queue built on a segmented linked-list design for .NET 6+.

All memory (segment headers + slot arrays) is allocated via `NativeMemory` with **zero GC pressure and zero managed heap allocations**. New segments are allocated on demand; consumed slot arrays are first recycled into a single-slot cache for reuse; segment headers are reclaimed on `Dispose`.

> Note: This project was entirely written using `Claude Opus 4.6 High Thinking`.

> Although it passes unit tests, it has not yet been used in production.

> Use at your own risk after thorough testing. Neither the repository author nor the AI assumes any responsibility.

## Features

- **Lock-free concurrency** — Enqueue claims a slot via `Volatile.Read` + `Interlocked.CompareExchange` (CAS); full-segment detection is a pure read with no atomic write overhead
- **Batch enqueue** — `EnqueueRange` claims multiple slots in a single CAS, amortizing atomic operation cost; automatically splits across segments
- **Segmented linked list** — Segments are allocated on demand and linked in O(1); default mode uses exponential growth (cap 1M), steady-state mode keeps capacity stable
- **Capacity mode switch** — `preferSteadyStateCapacity=false` (default) favors peak throughput; `true` favors reuse in stable-depth workloads
- **Fully native memory** — Segment headers and slot arrays are allocated via `NativeMemory`; no managed heap allocation
- **Single-slot reuse cache** — Consumed slot arrays are recycled into a one-entry cache; next segment allocation reuses first; replaced cache entries are freed
- **Deferred header reclamation** — Segment headers can be held by preempted producers and are therefore reclaimed at `Dispose`
- **False sharing prevention** — Producer/consumer hot fields are isolated via cache-line padding; `EnqueuePos` uses a 128-byte exclusive layout
- **FIFO ordering** — Strict FIFO from a single producer's perspective; per-producer ordering is preserved across multiple producers
- **Unmanaged type constraint** — `where T : unmanaged`; supports `int`, `long`, custom value-type structs, etc.

## Quick Start

```csharp
using ConcurrentNativeQueueLibrary;

// Create a queue (default segment size: 32, throughput mode)
using var queue = new ConcurrentNativeQueue<long>();

// Producer thread enqueues
queue.Enqueue(42);
queue.Enqueue(100);

// Batch enqueue
queue.EnqueueRange(new long[] { 200, 300, 400 });

// Peek at the head element (without removing)
if (queue.TryPeek(out long head))
    Console.WriteLine(head); // 42

// Consumer thread dequeues
if (queue.TryDequeue(out long item))
    Console.WriteLine(item); // 42
```

### Constructor Variants

```csharp
// Initial segment size (default growth mode: exponential up to 1M)
using var q1 = new ConcurrentNativeQueue<int>(128);

// Stable-capacity mode (better reuse hit rate in steady-state depth)
using var q2 = new ConcurrentNativeQueue<int>(128, preferSteadyStateCapacity: true);

// Default segment size + explicit mode
using var q3 = new ConcurrentNativeQueue<int>(preferSteadyStateCapacity: true);
```

### MPSC Concurrent Pattern

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

| Member | Description |
|---|---|
| `ConcurrentNativeQueue()` | Creates a queue with default segment size 32, throughput growth mode |
| `ConcurrentNativeQueue(int segmentSize)` | Creates a queue with the specified initial segment size (minimum internally clamped to 2) |
| `ConcurrentNativeQueue(bool preferSteadyStateCapacity)` | Uses default segment size 32 with explicit capacity mode |
| `ConcurrentNativeQueue(int segmentSize, bool preferSteadyStateCapacity)` | Uses explicit initial segment size and capacity mode |
| `void Enqueue(T item)` | Enqueues an item. Thread-safe for concurrent producers. Allocates a new segment when full |
| `void EnqueueRange(ReadOnlySpan<T> items)` | Batch enqueue. Claims multiple slots in a single CAS; auto-splits across segments. Thread-safe |
| `bool TryPeek(out T item)` | Peeks at the head element without removing it. Returns `true` on success; `false` if empty. Single-consumer only |
| `bool TryDequeue(out T item)` | Dequeues an item. Returns `true` on success; `false` if empty. Single-consumer only |
| `int Count` | Current element count (approximate under concurrency) |
| `bool IsEmpty` | Whether the queue is empty |
| `void Dispose()` | Frees native memory for all segments. Safe to call multiple times |
| `GetDebugSlotStats()` | Available in `DEBUG` builds only. Returns slot allocation/reuse/free counters |

## Design

### Segmented Linked List + State Flags

The queue consists of native segments, each containing a `NativeMemory`-allocated slot array. Each slot has a `State` field indicating write status:

- `State == 0` — slot is empty, not yet written
- `State == 1` — data is ready for consumption

Producers read the current `EnqueuePos` via `Volatile.Read`, then atomically claim slots using `Interlocked.CompareExchange` (CAS). Full-segment detection is a pure read with no atomic write overhead. After writing data, a producer sets `State = 1` via `Volatile.Write`.

`EnqueueRange` further optimizes this: one CAS claims N slots, values are written in bulk, followed by `Thread.MemoryBarrier()`, then all states are published — reducing N atomic operations to ⌈N / remaining segment capacity⌉.

### Segment Capacity Modes

- **Throughput mode (default)**: `preferSteadyStateCapacity=false`; `NextCapacity` grows exponentially (up to 1M) to reduce segment transitions
- **Steady-state mode**: `preferSteadyStateCapacity=true`; segment capacity remains stable to improve reuse hit rate

### Segment Lifecycle (Fully Native + Single-Slot Reuse Cache)

Both segment headers and slot arrays are allocated via `NativeMemory`.
Core lifetime challenge: **a producer may be preempted while holding a stale segment pointer**, so headers cannot be reclaimed immediately after consumption.

1. **Segment full** — Producer detects `offset >= capacity`, creates and links next segment via CAS, then advances `_tail`. If CAS loses, the unpublished header is freed and the unused slot array is recycled into the single-slot cache
2. **Segment consumed** — Consumer detects `offset >= capacity`, recycles this segment's slot array into the single-slot cache (freeing old cache entry if replaced), then advances to `Next`
3. **New segment allocation** — Allocation first tries to reuse from cache; if capacity is insufficient, it falls back to fresh allocation
4. **Dispose** — Walks from `_origin` and frees all headers and remaining slot arrays (including cached one)

### Improvements over the Previous Ring Buffer Design

| Aspect | Old (Ring Buffer) | New (Segmented Linked List) |
|---|---|---|
| Atomic ops per operation | `Interlocked.Inc` + CAS + `Interlocked.Dec` = 3 | CAS to claim slot = 1 (`EnqueueRange`: 1 CAS for N slots) |
| Growth strategy | Stop-the-world + O(N) migration | Allocate new segment O(1), CAS link |
| Capacity strategy | Single strategy | Switchable: throughput mode (exponential) / steady-state mode (stable) |
| Slot reclamation | Explicit shrink + migration | Recycle after consumption with cache-first reuse |

## Project Structure

```bash
ConcurrentNativeQueue/
├── ConcurrentNativeQueueLibrary/     # Core library
│   └── ConcurrentNativeQueue.cs
├── ConcurrentNativeQueueDemo/        # MPSC demo app (AOT-publishable)
│   └── Program.cs
├── ConcurrentNativeQueueBenchmark/   # BenchmarkDotNet benchmarks
│   ├── MpscBenchmark.cs              #   MPSC concurrent throughput vs ConcurrentQueue
│   ├── SequentialBenchmark.cs        #   Single-thread sequential throughput vs ConcurrentQueue (with mode parameter)
│   ├── BatchEnqueueBenchmark.cs      #   EnqueueRange vs per-item Enqueue throughput
│   ├── SegmentSizeBenchmark.cs       #   Segment size impact on throughput
│   ├── FixedDepthLoopBenchmark.cs    #   Fixed-depth steady-state workload (with mode parameter)
│   └── NonSteadyStateBenchmark.cs    #   Non-steady burst/sawtooth workload (with mode parameter)
└── ConcurrentNativeQueueUnitTest/    # xUnit unit tests
    └── ConcurrentNativeQueueUnitTest.cs
```

## Running

### Run the Demo

```bash
dotnet run --project ConcurrentNativeQueueDemo
```

### Run Tests

```bash
dotnet test
```

### Run Benchmarks

```bash
dotnet run --project ConcurrentNativeQueueBenchmark -c Release -- --filter *
```

For historical reports, result descriptions, and big-core affinity settings, see [benchmark-results/README.en-US.md](benchmark-results/README.en-US.md).

## Requirements

- .NET SDK 6.0 or later.
- `unsafe` code must be enabled (already configured in project files)

## License

MIT
