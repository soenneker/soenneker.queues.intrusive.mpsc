using Soenneker.Queues.Intrusive.Mpsc.Abstract;

namespace Soenneker.Queues.Intrusive.Mpsc;

/// <summary>
/// Base class for nodes used in intrusive MPSC queues.
///
/// This class provides a storage-backed implementation of
/// <see cref="IIntrusiveNode{TNode}"/>, exposing the required
/// <see cref="Next"/> linkage via a ref-returning property.
///
/// Deriving from this class avoids having to manually implement
/// the intrusive linkage in each node type.
/// </summary>
/// <typeparam name="TNode">
/// The concrete node type. This must be the deriving type itself
/// (i.e. a self-referential generic constraint).
/// </typeparam>
/// <remarks>
/// Intrusive queue contract:
/// <list type="bullet">
/// <item>
/// A node must not be enqueued while it is already enqueued in any queue.
/// </item>
/// <item>
/// A node may be reused only after it has been dequeued by the single consumer.
/// </item>
/// <item>
/// The <see cref="Next"/> reference is owned and manipulated by the queue and
/// must not be modified by user code while the node is enqueued.
/// </item>
/// </list>
/// </remarks>
public abstract class IntrusiveNode<TNode> : IIntrusiveNode<TNode>
    where TNode : class, IIntrusiveNode<TNode>
{
    private TNode? _next;

    /// <summary>
    /// Gets a reference to the next node in the intrusive queue.
    /// </summary>
    /// <remarks>
    /// This property returns a reference to the underlying storage field
    /// to allow lock-free algorithms to perform <see cref="System.Threading.Volatile"/>
    /// and <see cref="System.Threading.Interlocked"/> operations directly on it.
    ///
    /// User code should not read from or write to this property while the node
    /// is enqueued in a queue.
    /// </remarks>
    public ref TNode? Next => ref _next;
}