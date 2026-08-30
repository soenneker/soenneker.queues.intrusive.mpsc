
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Queues.Intrusive.Abstractions;

namespace Soenneker.Queues.Intrusive.Mpsc.Tests;

public sealed class IntrusiveMpscQueueTests
{
    [Test]
    public void Dequeued_node_can_be_reenqueued_immediately()
    {
        var queue = new IntrusiveMpscQueue<Node>(new Node(-1));
        var node = new Node(1);

        queue.Enqueue(node);

        if (!queue.TryDequeue(out Node first) || !ReferenceEquals(node, first))
            throw new InvalidOperationException("The first dequeue did not return the enqueued node.");

        queue.Enqueue(first);

        if (!queue.TryDequeue(out Node second) || !ReferenceEquals(node, second))
            throw new InvalidOperationException("The reused node was not dequeued successfully.");

        if (!queue.IsEmpty())
            throw new InvalidOperationException("The queue should be empty after dequeuing the reused node.");
    }

    [Test]
    public void Dequeues_nodes_in_fifo_order()
    {
        var queue = new IntrusiveMpscQueue<Node>(new Node(-1));
        var first = new Node(1);
        var second = new Node(2);

        queue.Enqueue(first);
        queue.Enqueue(second);

        if (!queue.TryDequeue(out Node dequeuedFirst) || !ReferenceEquals(first, dequeuedFirst))
            throw new InvalidOperationException("The first node was not dequeued first.");

        if (!queue.TryDequeue(out Node dequeuedSecond) || !ReferenceEquals(second, dequeuedSecond))
            throw new InvalidOperationException("The second node was not dequeued second.");

        if (queue.TryDequeue(out _))
            throw new InvalidOperationException("An empty queue reported another node.");
    }

    [Test]
    public async Task Concurrent_producers_publish_every_node_once()
    {
        const int producerCount = 4;
        const int nodesPerProducer = 1_000;
        const int total = producerCount * nodesPerProducer;

        var queue = new IntrusiveMpscQueue<Node>(new Node(-1));
        var producers = new Task[producerCount];

        for (var producer = 0; producer < producerCount; producer++)
        {
            int producerId = producer;
            producers[producer] = Task.Run(() =>
            {
                int start = producerId * nodesPerProducer;
                for (var offset = 0; offset < nodesPerProducer; offset++)
                    queue.Enqueue(new Node(start + offset));
            });
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var observed = new HashSet<int>();

        while (observed.Count < total)
        {
            timeout.Token.ThrowIfCancellationRequested();

            if (queue.TryDequeueSpin(out Node node, 64))
            {
                if (!observed.Add(node.Id))
                    throw new InvalidOperationException($"Node {node.Id} was dequeued more than once.");
            }
            else
            {
                Thread.Yield();
            }
        }

        await Task.WhenAll(producers);

        if (!queue.IsEmpty())
            throw new InvalidOperationException("The queue should be empty after consuming every published node.");
    }

    private sealed class Node(int id) : IntrusiveNode<Node>
    {
        public int Id { get; } = id;
    }
}
