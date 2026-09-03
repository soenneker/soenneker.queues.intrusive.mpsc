using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Soenneker.Queues.Intrusive.Abstractions;

namespace Soenneker.Queues.Intrusive.Mpsc;

/// <summary>
/// An intrusive multi-producer, single-consumer (MPSC) queue.
///
/// This queue uses a permanent sentinel ("stub") node and a single atomic operation per enqueue.
/// Nodes carry their own linkage via <see cref="IIntrusiveNode{TNode}"/>, avoiding allocations.
///
/// Thread-safety:
/// - Multiple producers may call <see cref="Enqueue"/> concurrently.
/// - Exactly one consumer may call <see cref="TryDequeue"/>,
///   <see cref="TryDequeueSpinUntilLinked"/>, or <see cref="IsEmpty"/>.
/// </summary>
/// <typeparam name="TNode">
/// The node type stored in the queue. Must be a reference type implementing
/// <see cref="IIntrusiveNode{TNode}"/> and must not be enqueued concurrently or more than once at a time.
/// </typeparam>
/// <remarks>
/// This is a reference type. Do not access it concurrently except as documented
/// (multi-producer, single-consumer).
/// </remarks>
public sealed class IntrusiveMpscQueue<TNode> where TNode : class, IIntrusiveNode<TNode>
{
    private readonly TNode _stub;

    // The storage is non-generic because the CLR packs managed references in explicitly laid-out
    // generic types. Its fields always contain TNode instances after construction.
    private CacheLineSeparatedReferences _state;

    public IntrusiveMpscQueue(TNode stub)
    {
        ArgumentNullException.ThrowIfNull(stub);

        _state = default;
        stub.Next = null;

        _stub = stub;
        _state.Head = stub;
        _state.Tail = stub;
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
        ArgumentNullException.ThrowIfNull(node);

        // Clear linkage before publication to avoid stale chains on reuse.
        node.Next = null;

        // Atomically swap the tail and link the previous tail to this node.
        TNode prev = (TNode) Interlocked.Exchange(ref _state.Tail, node)!;
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
    /// If stronger dequeue semantics are required, use <see cref="TryDequeueSpin"/>
    /// or <see cref="TryDequeueSpinUntilLinked"/>.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool TryDequeue(out TNode node)
    {
        TNode head = Unsafe.As<object?, TNode?>(ref _state.Head)!;
        TNode? next = Volatile.Read(ref head.Next);

        if (next is null)
        {
            if (ReferenceEquals(head, _stub))
            {
                node = null!;
                return false;
            }

            return TryDequeueSlow(head, null, out node);
        }

        if (!ReferenceEquals(head, _stub))
        {
            _state.Head = next;
            head.Next = null;
            node = head;
            return true;
        }

        return TryDequeueSlow(head, next, out node);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool TryDequeueSlow(TNode head, TNode? next, out TNode node)
    {
        if (ReferenceEquals(head, _stub))
        {
            if (next is null)
            {
                node = null!;
                return false;
            }

            _state.Head = next;
            head = next;
            next = Volatile.Read(ref head.Next);

            if (next is not null)
            {
                _state.Head = next;
                head.Next = null;
                node = head;
                return true;
            }
        }

        if (!ReferenceEquals(head, Volatile.Read(ref _state.Tail)))
        {
            node = null!;
            return false;
        }

        EnqueueStub();
        next = Volatile.Read(ref head.Next);

        if (next is null)
        {
            node = null!;
            return false;
        }

        _state.Head = next;
        head.Next = null;
        node = head;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnqueueStub()
    {
        _stub.Next = null;
        TNode previous = (TNode) Interlocked.Exchange(ref _state.Tail, _stub)!;
        Volatile.Write(ref previous.Next, _stub);
    }

    /// <summary>
    /// Attempts to dequeue a node from the queue, spinning up to <paramref name="maxSpins"/>
    /// only to cover the producer link-publish window.
    /// </summary>
    /// <param name="node">Node to inspect or transform.</param>
    /// <param name="maxSpins">Max Spins for the try dequeue spin operation.</param>
    /// <returns>true if the requested update was applied; otherwise, false.</returns>
    /// <remarks>
    /// If the queue is truly empty, returns <c>false</c>.
    /// If a producer has advanced the tail but has not yet published the link from the current head,
    /// this method spins up to <paramref name="maxSpins"/> times before returning <see langword="false"/>.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeueSpin(out TNode node, int maxSpins)
    {
        if (TryDequeue(out node))
            return true;

        return TryDequeueSpinSlow(out node, maxSpins);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool TryDequeueSpinSlow(out TNode node, int maxSpins)
    {
        TNode head = Unsafe.As<object?, TNode?>(ref _state.Head)!;
        if (maxSpins <= 0 || ReferenceEquals(head, Volatile.Read(ref _state.Tail)))
        {
            node = null!;
            return false;
        }

        var spinWait = new SpinWait();
        for (var i = 0; i < maxSpins; i++)
        {
            spinWait.SpinOnce();

            if (Volatile.Read(ref head.Next) is not null)
            {
                if (TryDequeue(out node))
                    return true;

                head = Unsafe.As<object?, TNode?>(ref _state.Head)!;
                if (ReferenceEquals(head, Volatile.Read(ref _state.Tail)))
                    break;
            }
        }

        node = null!;
        return false;
    }

    /// <summary>
    /// Attempts to dequeue a node from the queue, spinning only in the producer link-publish window.
    /// </summary>
    /// <param name="node">Node to inspect or transform.</param>
    /// <returns>true if the requested update was applied; otherwise, false.</returns>
    /// <remarks>
    /// If the queue is truly empty, this returns <c>false</c>.
    /// If a producer has advanced the tail pointer but has not yet published the link from the current head,
    /// this method spins until the link is observed and then dequeues the node.
    ///
    /// This method does not wait for producers to enqueue new nodes.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeueSpinUntilLinked(out TNode node)
    {
        if (TryDequeue(out node))
            return true;

        return TryDequeueSpinUntilLinkedSlow(out node);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool TryDequeueSpinUntilLinkedSlow(out TNode node)
    {
        TNode head = Unsafe.As<object?, TNode?>(ref _state.Head)!;
        if (ReferenceEquals(head, Volatile.Read(ref _state.Tail)))
        {
            node = null!;
            return false;
        }

        var spinWait = new SpinWait();

        while (true)
        {
            do
            {
                spinWait.SpinOnce();
            }
            while (Volatile.Read(ref head.Next) is null);

            if (TryDequeue(out node))
                return true;

            head = Unsafe.As<object?, TNode?>(ref _state.Head)!;
            if (ReferenceEquals(head, Volatile.Read(ref _state.Tail)))
            {
                node = null!;
                return false;
            }
        }
    }

    /// <summary>
    /// Gets the current consumer head node.
    /// </summary>
    /// <remarks>
    /// Consumer-thread only. The returned node is an internal queue anchor and remains owned by the queue.
    /// Do not mutate, reuse, or enqueue it.
    /// </remarks>
    public TNode Head
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            return Unsafe.As<object?, TNode?>(ref _state.Head)!;
        }
    }

    /// <summary>
    /// Processes up to the specified number of nodes by invoking the provided action for each dequeued node.
    /// </summary>
    /// <remarks>Throws an exception if the queue is not initialized. Processing stops if the queue becomes
    /// empty before reaching the specified maximum.</remarks>
    /// <param name="action">The action to perform on each node that is dequeued from the queue. This delegate is called once for each node
    /// processed.</param>
    /// <param name="max">The maximum number of nodes to process. Must be a non-negative integer. If not specified, all available nodes
    /// are processed.</param>
    /// <returns>The number of nodes that were processed by the action. This value will be less than or equal to the specified
    /// maximum.</returns>
    public int Drain(Action<TNode> action, int max = int.MaxValue)
    {
        if (action is null) 
            throw new ArgumentNullException(nameof(action));

        if (max < 0) 
            throw new ArgumentOutOfRangeException(nameof(max));

        var count = 0;

        while (count < max && TryDequeue(out TNode n))
        {
            action(n);
            count++;
        }

        return count;
    }

    /// <summary>
    /// Determines whether the queue is currently empty.
    /// </summary>
    /// <returns>true if the queue is currently empty; otherwise, false.</returns>
    /// <remarks>
    /// Consumer-thread only. A producer in the exchange/link publication window makes this return
    /// <c>false</c>, even though a non-spinning dequeue may not observe the link yet.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsEmpty()
    {
        TNode head = Unsafe.As<object?, TNode?>(ref _state.Head)!;
        return Volatile.Read(ref head.Next) is null
            && ReferenceEquals(head, Volatile.Read(ref _state.Tail));
    }
}
