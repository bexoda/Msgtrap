using System.Threading.Channels;

namespace Msgtrap.Core;

/// <summary>
/// Minimal in-process pub/sub used by the stores to feed the UI's event streams.
/// </summary>
/// <remarks>
/// Bounded with <see cref="BoundedChannelFullMode.DropOldest"/> on purpose: a
/// stalled browser tab must never block an API call or an SMTP transaction.
/// </remarks>
public sealed class EventHub<T>
{
    private readonly object _gate = new();
    private readonly List<Channel<T>> _subscribers = [];

    /// <summary>Registers a subscriber and hands back its reader plus a dispose handle.</summary>
    public IDisposable Subscribe(out ChannelReader<T> reader)
    {
        var channel = Channel.CreateBounded<T>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

        lock (_gate) _subscribers.Add(channel);
        reader = channel.Reader;
        return new Subscription(this, channel);
    }

    /// <summary>Pushes an event to every live subscriber, never blocking.</summary>
    public void Publish(T item)
    {
        lock (_gate)
            foreach (var subscriber in _subscribers)
                subscriber.Writer.TryWrite(item);
    }

    private sealed class Subscription(EventHub<T> hub, Channel<T> channel) : IDisposable
    {
        public void Dispose()
        {
            lock (hub._gate) hub._subscribers.Remove(channel);
            channel.Writer.TryComplete();
        }
    }
}
