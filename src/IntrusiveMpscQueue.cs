using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Soenneker.Queues.Intrusive.Mpsc.Abstract;

namespace Soenneker.Queues.Intrusive.Mpsc;

/// <summary>
/// An intrusive multi-producer, single-consumer (MPSC) queue.
///
/// This queue uses a permanent sentinel ("stub") node and a single atomic operation per enqueue.
/// Nodes carry their own linkage via <see cref="IIntrusiveNode{TNode}"/>, avoiding allocations.
///
/// Thread-safety:
/// - Multiple producers may call <see cref="Enqueue"/> concurrently.
/// - Exactly one consumer may call <see cref="TryDequeue"/>, <see cref="TryDequeueSpin"/>, or <see cref="IsEmpty"/>.
/// </summary>
/// <typeparam name="TNode">
/// The node type stored in the queue. Must be a reference type implementing
/// <see cref="IIntrusiveNode{TNode}"/> and must not be enqueued concurrently or more than once at a time.
/// </typeparam>
public sealed class IntrusiveMpscQueue<TNode> where TNode : class, IIntrusiveNode<TNode>
{
    // Consumer-owned head pointer (initially the stub).
    private TNode _head;

    // Producer-shared tail pointer.
    private TNode _tail;

    /// <summary>
    /// Initializes a new <see cref="IntrusiveMpscQueue{TNode}"/> using the provided stub node.
    /// </summary>
    /// <param name="stub">
    /// A permanent sentinel node that remains allocated for the lifetime of the queue.
    /// Its <see cref="IIntrusiveNode{TNode}.Next"/> reference must initially be <c>null</c>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown if <paramref name="stub"/> is <c>null</c>.
    /// </exception>
    public IntrusiveMpscQueue(TNode stub)
    {
        if (stub is null) throw new ArgumentNullException(nameof(stub));

        // The stub must start unlinked and remain alive for the queue lifetime.
        stub.Next = null;

        _head = stub;
        _tail = stub;
    }

    /// <summary>
    /// Enqueues a node into the queue.
    ///
    /// This method is safe to call concurrently from multiple producer threads.
    /// Exactly one atomic operation is performed per enqueue.
    /// </summary>
    /// <param name="node">The node to enqueue.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown if <paramref name="node"/> is <c>null</c>.
    /// </exception>
    /// <remarks>
    /// The provided node must not already be enqueued in this or any other queue.
    /// Node reuse is allowed only after the node has been dequeued by the consumer.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Enqueue(TNode node)
    {
        if (node is null) throw new ArgumentNullException(nameof(node));

        // Clear linkage before publication to avoid stale chains on reuse.
        node.Next = null;

        // Atomically swap the tail and link the previous tail to this node.
        TNode prev = Interlocked.Exchange(ref _tail, node);
        Volatile.Write(ref prev.Next, node);
    }

    /// <summary>
    /// Attempts to dequeue a node from the queue without spinning.
    ///
    /// This method must be called by the single consumer thread only.
    /// </summary>
    /// <param name="node">
    /// When this method returns <c>true</c>, contains the dequeued node.
    /// When this method returns <c>false</c>, contains <c>null</c>.
    /// </param>
    /// <returns>
    /// <c>true</c> if a node was successfully dequeued; otherwise, <c>false</c>.
    /// </returns>
    /// <remarks>
    /// A return value of <c>false</c> does not necessarily mean the queue is empty.
    /// It may also indicate that a producer has advanced the tail pointer but has not yet
    /// published the link to the next node.
    ///
    /// If stronger dequeue semantics are required, use <see cref="TryDequeueSpin"/>.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeue(out TNode node)
    {
        TNode head = _head;
        TNode? next = Volatile.Read(ref head.Next);

        if (next is null)
        {
            // If tail == head, the queue is truly empty.
            if (ReferenceEquals(head, Volatile.Read(ref _tail)))
            {
                node = null!;
                return false;
            }

            // Tail advanced but link not yet published.
            node = null!;
            return false;
        }

        _head = next;
        node = next;
        return true;
    }

    /// <summary>
    /// Attempts to dequeue a node from the queue, spinning briefly to wait for a pending link publish.
    ///
    /// This method must be called by the single consumer thread only.
    /// </summary>
    /// <param name="node">
    /// When this method returns <c>true</c>, contains the dequeued node.
    /// When this method returns <c>false</c>, contains <c>null</c>.
    /// </param>
    /// <param name="maxSpins">
    /// The maximum number of spin iterations to perform while waiting for a producer to
    /// publish the next link.
    /// </param>
    /// <returns>
    /// <c>true</c> if a node was successfully dequeued; otherwise, <c>false</c>.
    /// </returns>
    /// <remarks>
    /// This method provides stronger dequeue semantics than <see cref="TryDequeue"/>
    /// at the cost of potentially spinning under contention.
    /// No locks or additional atomic operations are performed on the consumer path.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeueSpin(out TNode node, int maxSpins = 16)
    {
        TNode head = _head;
        TNode? next = Volatile.Read(ref head.Next);

        if (next is null)
        {
            if (ReferenceEquals(head, Volatile.Read(ref _tail)))
            {
                node = null!;
                return false;
            }

            var sw = new SpinWait();
            for (int i = 0; i < maxSpins; i++)
            {
                sw.SpinOnce();
                next = Volatile.Read(ref head.Next);
                if (next is not null)
                    break;
            }

            if (next is null)
            {
                node = null!;
                return false;
            }
        }

        _head = next;
        node = next;
        return true;
    }

    /// <summary>
    /// Attempts to dequeue a node from the queue, spinning until a node becomes available or the queue is determined to
    /// be empty.
    /// </summary>
    /// <remarks>This method uses a spinning mechanism to wait for a node to become available if the queue is
    /// initially empty. It is designed for high-performance scenarios where blocking is not desirable.</remarks>
    /// <param name="node">When this method returns, contains the dequeued node if the operation was successful; otherwise, it is null.</param>
    /// <returns>true if a node was successfully dequeued; otherwise, false.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeueSpinUntilLinked(out TNode node)
    {
        TNode head = _head;
        TNode? next = Volatile.Read(ref head.Next);

        if (next is null)
        {
            // Truly empty
            if (ReferenceEquals(head, Volatile.Read(ref _tail)))
            {
                node = null!;
                return false;
            }

            // Not empty; a producer advanced tail but hasn't published head.Next yet.
            var sw = new SpinWait();
            do
            {
                sw.SpinOnce();
                next = Volatile.Read(ref head.Next);
            }
            while (next is null);
        }

        _head = next;
        node = next;
        return true;
    }

    /// <summary>
    /// Gets the current consumer head node.
    /// </summary>
    /// <remarks>
    /// Consumer-thread only. The returned node is typically the permanent stub or the most
    /// recently dequeued node and may be used for recycling or cleanup logic.
    /// </remarks>
    public TNode Head => _head;

    /// <summary>
    /// Determines whether the queue is currently empty.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the queue appears empty; otherwise, <c>false</c>.
    /// </returns>
    /// <remarks>
    /// Consumer-thread only. This is a best-effort check and may transiently return
    /// <c>true</c> while a producer is mid-enqueue (tail advanced but link not yet published).
    /// </remarks>
    public bool IsEmpty()
        => Volatile.Read(ref _head.Next) is null
        && ReferenceEquals(_head, Volatile.Read(ref _tail));
}
