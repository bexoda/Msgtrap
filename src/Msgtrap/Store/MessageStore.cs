using System.Threading.Channels;
using Msgtrap.Core;

namespace Msgtrap.Store;

/// <summary>
/// In-memory message store with pub/sub for the live UI.
/// </summary>
/// <remarks>
/// Everything is lost on restart, which is deliberate: this is a development
/// trap, not a database. All members are safe to call concurrently; reads return
/// deep copies so callers never observe a message mid-update.
/// </remarks>
public sealed class MessageStore
{
    private readonly object _gate = new();
    private readonly List<CapturedMessage> _messages = [];
    private readonly Dictionary<string, CapturedMessage> _byId = [];
    private readonly EventHub<StoreEvent> _events = new();
    private readonly SimulatorSettings _settings;

    /// <summary>Creates an empty store with the given simulation defaults.</summary>
    public MessageStore(SimulatorSettings settings) => _settings = settings;

    /// <summary>Snapshot of the current simulation settings.</summary>
    public SimulatorSettings Settings
    {
        get { lock (_gate) return _settings.Clone(); }
    }

    /// <summary>Stores a new message and notifies subscribers.</summary>
    public void Add(CapturedMessage message)
    {
        lock (_gate)
        {
            _messages.Add(message);
            _byId[message.Id] = message;
        }
        Publish(new StoreEvent("message", message.Clone()));
    }

    /// <summary>Mutates a stored message under the lock and notifies subscribers.</summary>
    /// <returns><c>false</c> when the message no longer exists (e.g. inbox cleared).</returns>
    public bool Update(string id, Action<CapturedMessage> mutate)
    {
        CapturedMessage copy;
        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out var message)) return false;
            mutate(message);
            copy = message.Clone();
        }
        Publish(new StoreEvent("update", copy));
        return true;
    }

    /// <summary>Returns a copy of one message, or <c>null</c> when it is gone.</summary>
    public CapturedMessage? Get(string id)
    {
        lock (_gate)
            return _byId.TryGetValue(id, out var message) ? message.Clone() : null;
    }

    /// <summary>Returns matching messages, newest first.</summary>
    public IReadOnlyList<CapturedMessage> List(MessageFilter filter)
    {
        var query = filter.Query?.Trim().ToLowerInvariant() ?? "";
        lock (_gate)
        {
            return _messages
                .Where(m => string.IsNullOrEmpty(filter.Status) || m.Status == filter.Status)
                .Where(m => string.IsNullOrEmpty(filter.Sender) || string.Equals(m.Sender, filter.Sender, StringComparison.OrdinalIgnoreCase))
                .Where(m => string.IsNullOrEmpty(filter.Recipient) || m.Recipients.Any(r => r.Number == filter.Recipient))
                .Where(m => query.Length == 0 || MatchesQuery(m, query))
                .OrderByDescending(m => m.CreatedAt)
                .Select(m => m.Clone())
                .ToList();
        }
    }

    /// <summary>Removes one message. Returns <c>false</c> when it was not found.</summary>
    public bool Delete(string id)
    {
        lock (_gate)
        {
            if (!_byId.Remove(id)) return false;
            _messages.RemoveAll(m => m.Id == id);
        }
        Publish(new StoreEvent("delete", Id: id));
        return true;
    }

    /// <summary>Drops every captured message.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _messages.Clear();
            _byId.Clear();
        }
        Publish(new StoreEvent("clear"));
    }

    /// <summary>Applies a partial settings update, clamping values to sane ranges.</summary>
    public void UpdateSettings(Action<SimulatorSettings> mutate)
    {
        lock (_gate)
        {
            mutate(_settings);
            // Guard rails so a bad UI/API payload cannot break the simulator.
            _settings.ForceError = NormalizeForceError(_settings.ForceError);
            _settings.DeliverAfterMs = Math.Clamp(_settings.DeliverAfterMs, 0, 60_000);
        }
        Publish(new StoreEvent("settings"));
    }

    /// <summary>Subscribes to change events; dispose the result to unsubscribe.</summary>
    public Subscription Subscribe()
    {
        var handle = _events.Subscribe(out var reader);
        return new Subscription(reader, handle);
    }

    /// <summary>Handle for an SSE stream; disposing it unregisters the channel.</summary>
    public sealed class Subscription(ChannelReader<StoreEvent> reader, IDisposable handle) : IDisposable
    {
        /// <summary>Reader yielding every change while subscribed.</summary>
        public ChannelReader<StoreEvent> Reader => reader;

        /// <inheritdoc />
        public void Dispose() => handle.Dispose();
    }

    private void Publish(StoreEvent evt) => _events.Publish(evt);

    private static bool MatchesQuery(CapturedMessage m, string query) =>
        m.Sender.Contains(query, StringComparison.OrdinalIgnoreCase)
        || m.Body.Contains(query, StringComparison.OrdinalIgnoreCase)
        || m.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
        || m.Recipients.Any(r => r.Number.Contains(query, StringComparison.OrdinalIgnoreCase)
                              || r.Id.Contains(query, StringComparison.OrdinalIgnoreCase));

    private static string NormalizeForceError(string? value) => value switch
    {
        "402" or "403" or "500" => value,
        _ => "",
    };
}
