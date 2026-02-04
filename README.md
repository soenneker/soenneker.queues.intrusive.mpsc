[![](https://img.shields.io/nuget/v/soenneker.queues.intrusive.mpsc.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker.queues.intrusive.mpsc/)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.queues.intrusive.mpsc/publish-package.yml?style=for-the-badge)](https://github.com/soenneker/soenneker.queues.intrusive.mpsc/actions/workflows/publish-package.yml)
[![](https://img.shields.io/nuget/dt/soenneker.queues.intrusive.mpsc.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker.queues.intrusive.mpsc/)

# ![](https://user-images.githubusercontent.com/4441470/224455560-91ed3ee7-f510-4041-a8d2-3fc093025112.png) Soenneker.Queues.Intrusive.Mpsc

High-performance intrusive multi-producer / single-consumer (MPSC) queue primitive.

This package provides a lock-free, allocation-free MPSC queue designed for low-level concurrency primitives and async infrastructure. It is intended for advanced usage where control over memory layout, allocation, and synchronization semantics is required.

---

## Installation

```bash
dotnet add package Soenneker.Queues.Intrusive.Mpsc
````

---

## Overview

`IntrusiveMpscQueue<TNode>` implements a classic MPSC algorithm using a permanent sentinel (stub) node:

* Multiple producer threads may enqueue concurrently.
* Exactly one consumer thread may dequeue.
* Each enqueue performs a single atomic operation.
* No allocations are performed by the queue itself.
* Node linkage is stored directly on the node (intrusive).

This design is well suited for wait queues, schedulers, async locks, and similar primitives.

---

## Usage

### Define a node type

Nodes must implement `IIntrusiveNode<TNode>` or derive from `IntrusiveNode<TNode>`.

```csharp
public sealed class WorkItem : IntrusiveNode<WorkItem>
{
    public int Id;
}
```

### Create a queue with a permanent stub

```csharp
var stub = new WorkItem();
var queue = new IntrusiveMpscQueue<WorkItem>(stub);
```

### Enqueue (multi-producer)

```csharp
queue.Enqueue(new WorkItem { Id = 42 });
```

### Dequeue (single-consumer)

```csharp
if (queue.TryDequeue(out var item))
{
    // process item
}
```

If stronger dequeue semantics are required (for example, when a producer has advanced the tail but not yet published the link), use `TryDequeueSpin`.

---

## Correctness and constraints

* Exactly one consumer thread is supported.
* A node must not be enqueued while it is already enqueued.
* Node reuse is allowed only after the node has been dequeued.
* The sentinel (stub) node must remain alive for the lifetime of the queue.
* `TryDequeue` may return `false` even when a producer is mid-enqueue; this is expected behavior.

This type is not intended as a general-purpose collection.

---

## When to use this

This queue is appropriate when:

* You are building synchronization primitives (locks, semaphores, schedulers).
* Allocation-free behavior is required.
* You need precise control over memory ordering and concurrency behavior.
* You can enforce a single-consumer contract.

If you need a general-purpose, multi-consumer queue, use `System.Threading.Channels` or `ConcurrentQueue<T>` instead.
