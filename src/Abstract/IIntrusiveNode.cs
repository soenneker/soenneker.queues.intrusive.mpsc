namespace Soenneker.Queues.Intrusive.Mpsc.Abstract;

/// <summary>
/// Defines the intrusive linkage required by an intrusive queue node.
///
/// Implementations must provide storage for a single forward link that can be
/// accessed by reference to support lock-free publication via
/// <see cref="System.Threading.Volatile"/> and <see cref="System.Threading.Interlocked"/>.
/// </summary>
/// <typeparam name="TNode">
/// The node type. Must be a reference type and typically refers to the implementing type itself.
/// </typeparam>
/// <remarks>
/// Intrusive contract:
/// <list type="bullet">
/// <item>
/// The returned reference must point to real, stable storage (typically a field),
/// not a computed or temporary value.
/// </item>
/// <item>
/// The <see cref="Next"/> reference is owned by the queue while the node is enqueued
/// and must not be modified by user code during that time.
/// </item>
/// <item>
/// A node must not be enqueued more than once concurrently or while it is already
/// part of a queue.
/// </item>
/// </list>
/// </remarks>
public interface IIntrusiveNode<TNode> where TNode : class
{
    /// <summary>
    /// Gets a reference to the next node in the intrusive queue.
    /// </summary>
    /// <remarks>
    /// This property must return a reference to the underlying storage location
    /// so that lock-free algorithms can safely perform atomic and volatile
    /// operations on it.
    /// </remarks>
    ref TNode? Next { get; }
}