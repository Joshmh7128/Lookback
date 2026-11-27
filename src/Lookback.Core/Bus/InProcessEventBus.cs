using System.Threading.Channels;
using Lookback.Contracts;
using Lookback.Contracts.V1;

namespace Lookback.Core.Bus;

/// <summary>
/// Fan-out bus over bounded channels. Publish takes no lock: it reads an
/// copy-on-write snapshot of subscribers, so subscribing or unsubscribing
/// never stalls a producer.
/// </summary>
public sealed class InProcessEventBus : IEventBus, IDisposable
{
    private readonly Lock _writeLock = new();
    private Subscription[] _subscriptions = [];
    private volatile bool _completed;

    public bool Publish(Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (_completed)
        {
            return false;
        }

        Subscription[] subs = Volatile.Read(ref _subscriptions);
        foreach (Subscription sub in subs)
        {
            if (Topics.Matches(sub.TopicPattern, envelope.Topic))
            {
                sub.Offer(envelope);
            }
        }

        return true;
    }

    public IEventSubscription Subscribe(string topicPattern, int capacity = 4096)
    {
        ArgumentException.ThrowIfNullOrEmpty(topicPattern);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ObjectDisposedException.ThrowIf(_completed, this);

        var sub = new Subscription(this, topicPattern, capacity);
        lock (_writeLock)
        {
            ObjectDisposedException.ThrowIf(_completed, this);
            Volatile.Write(ref _subscriptions, [.. _subscriptions, sub]);
        }

        return sub;
    }

    /// <summary>Completes every subscription; readers drain what is queued and then finish.</summary>
    public void Dispose()
    {
        Subscription[] subs;
        lock (_writeLock)
        {
            _completed = true;
            subs = _subscriptions;
            Volatile.Write(ref _subscriptions, []);
        }

        foreach (Subscription sub in subs)
        {
            sub.Complete();
        }
    }

    private void Remove(Subscription sub)
    {
        lock (_writeLock)
        {
            Volatile.Write(ref _subscriptions, Array.FindAll(_subscriptions, s => !ReferenceEquals(s, sub)));
        }
    }

    private sealed class Subscription : IEventSubscription
    {
        private readonly InProcessEventBus _owner;
        private readonly Channel<Envelope> _channel;
        private long _dropped;

        public Subscription(InProcessEventBus owner, string topicPattern, int capacity)
        {
            _owner = owner;
            TopicPattern = topicPattern;
            _channel = Channel.CreateBounded<Envelope>(
                new BoundedChannelOptions(capacity)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false,
                },
                _ => Interlocked.Increment(ref _dropped));
        }

        public string TopicPattern { get; }

        public long Dropped => Interlocked.Read(ref _dropped);

        public int Count => _channel.Reader.Count;

        public void Offer(Envelope envelope) => _channel.Writer.TryWrite(envelope);

        public void Complete() => _channel.Writer.TryComplete();

        public IAsyncEnumerable<Envelope> ReadAllAsync(CancellationToken cancellationToken = default) =>
            _channel.Reader.ReadAllAsync(cancellationToken);

        public bool TryRead(out Envelope? envelope) => _channel.Reader.TryRead(out envelope);

        public ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) =>
            _channel.Reader.WaitToReadAsync(cancellationToken);

        public void Dispose()
        {
            _owner.Remove(this);
            Complete();
        }
    }
}
