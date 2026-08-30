[![](https://img.shields.io/nuget/v/soenneker.queues.intrusive.mpsc.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker.queues.intrusive.mpsc/)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.queues.intrusive.mpsc/publish-package.yml?style=for-the-badge)](https://github.com/soenneker/soenneker.queues.intrusive.mpsc/actions/workflows/publish-package.yml)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.queues.intrusive.mpsc/build-and-test.yml?label=Build&style=for-the-badge)](https://github.com/soenneker/soenneker.queues.intrusive.mpsc/actions/workflows/build-and-test.yml)
[![](https://img.shields.io/nuget/dt/soenneker.queues.intrusive.mpsc.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker.queues.intrusive.mpsc/)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.queues.intrusive.mpsc/codeql.yml?label=CodeQL&style=for-the-badge)](https://github.com/soenneker/soenneker.queues.intrusive.mpsc/actions/workflows/codeql.yml)

# Soenneker.Queues.Intrusive.Mpsc

A lock-free intrusive multi-producer, single-consumer queue for low-level scheduling and synchronization infrastructure.

The queue stores its forward link on each node, so enqueue and dequeue do not allocate wrapper objects. In exchange, callers must follow the node-ownership and single-consumer rules exactly.

## Install

```bash
dotnet add package Soenneker.Queues.Intrusive.Mpsc
```

## Usage

```csharp
using Soenneker.Queues.Intrusive.Abstractions;
using Soenneker.Queues.Intrusive.Mpsc;

public sealed class WorkItem : IntrusiveNode<WorkItem>
{
    public required string Payload { get; init; }
}

var stub = new WorkItem { Payload = "stub" };
var queue = new IntrusiveMpscQueue<WorkItem>(stub);

queue.Enqueue(new WorkItem { Payload = "one" });

if (queue.TryDequeue(out WorkItem item))
    Console.WriteLine(item.Payload);
```

Any number of producer threads may call `Enqueue`. Only one consumer thread may call dequeue methods, `Drain`, `IsEmpty`, or access `Head`.

## Dequeue choices

- `TryDequeue` does not spin. It can return `false` during the short window after a producer advances the tail and before it publishes the forward link.
- `TryDequeueSpin(node, maxSpins)` waits for that link for a bounded number of spins.
- `TryDequeueSpinUntilLinked` waits without a spin limit when a producer is already mid-enqueue. It does not wait for a future enqueue when the queue is empty.
- `Drain(action, max)` processes currently available nodes with non-spinning dequeue semantics.

After a dequeue method returns `true`, the returned node is detached and may be reused immediately. `Head` is different: it exposes an internal queue anchor and must not be modified or enqueued.

## Required ownership rules

- Keep the stub alive for the lifetime of the queue and never enqueue it yourself.
- Never enqueue a node that is already in this queue or any other intrusive structure.
- Do not read or write a node’s `Next` link while the queue owns it.
- Use exactly one consumer; the queue is not safe for concurrent dequeue operations.
- Treat `IsEmpty` as a point-in-time observation because producers may enqueue immediately afterward.

Use `System.Threading.Channels` or `ConcurrentQueue<T>` when multiple consumers, blocking coordination, or general collection semantics matter more than intrusive allocation control.
