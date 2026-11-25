using System.Threading.Channels;
using Msgtrap.Core;

namespace Msgtrap.Store;

/// <summary>
/// In-memory store for captured email. Same contract as
/// <see cref="MessageStore"/>: everything is lost on restart, reads return deep
/// copies, and changes are published for the UI's live stream.
/// </summary>
public sealed class EmailStore
{
    private readonly object _gate = new();
    private readonly List<CapturedEmail> _emails = [];
    private readonly Dictionary<string, CapturedEmail> _byId = [];
    private readonly EventHub<EmailEvent> _events = new();

    /// <summary>Stores a received email and notifies subscribers.</summary>
    public void Add(CapturedEmail email)
    {
        lock (_gate)
        {
            _emails.Add(email);
            _byId[email.Id] = email;
        }
        _events.Publish(new EmailEvent("message", email.Clone()));
    }

    /// <summary>Returns a copy of one email, or <c>null</c> when it is gone.</summary>
    public CapturedEmail? Get(string id)
    {
        lock (_gate) return _byId.TryGetValue(id, out var email) ? email.Clone() : null;
    }

    /// <summary>Returns matching emails, newest first.</summary>
    public IReadOnlyList<CapturedEmail> List(EmailFilter filter)
    {
        var query = filter.Query?.Trim().ToLowerInvariant() ?? "";
        var from = filter.From?.Trim() ?? "";
        lock (_gate)
        {
            return _emails
                .Where(email => from.Length == 0 || email.From.Contains(from, StringComparison.OrdinalIgnoreCase))
                .Where(email => query.Length == 0 || Matches(email, query))
                .OrderByDescending(email => email.ReceivedAt)
                .Select(email => email.Clone())
                .ToList();
        }
    }

    /// <summary>Total number of captured emails.</summary>
    public int Count
    {
        get { lock (_gate) return _emails.Count; }
    }

    /// <summary>Removes one email.</summary>
    public bool Delete(string id)
    {
        lock (_gate)
        {
            if (!_byId.Remove(id)) return false;
            _emails.RemoveAll(email => email.Id == id);
        }
        _events.Publish(new EmailEvent("delete", Id: id));
        return true;
    }

    /// <summary>Drops every captured email.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _emails.Clear();
            _byId.Clear();
        }
        _events.Publish(new EmailEvent("clear"));
    }

    /// <summary>Subscribes to change events; dispose the result to unsubscribe.</summary>
    public Subscription Subscribe()
    {
        var handle = _events.Subscribe(out var reader);
        return new Subscription(reader, handle);
    }

    /// <summary>Handle for an SSE stream.</summary>
    public sealed class Subscription(ChannelReader<EmailEvent> reader, IDisposable handle) : IDisposable
    {
        /// <summary>Reader yielding every change while subscribed.</summary>
        public ChannelReader<EmailEvent> Reader => reader;

        /// <inheritdoc />
        public void Dispose() => handle.Dispose();
    }

    private static bool Matches(CapturedEmail email, string query) =>
        email.From.Contains(query, StringComparison.OrdinalIgnoreCase)
        || email.Subject.Contains(query, StringComparison.OrdinalIgnoreCase)
        || email.TextBody.Contains(query, StringComparison.OrdinalIgnoreCase)
        || email.HtmlBody.Contains(query, StringComparison.OrdinalIgnoreCase)
        || email.To.Any(to => to.Contains(query, StringComparison.OrdinalIgnoreCase))
        || email.Cc.Any(cc => cc.Contains(query, StringComparison.OrdinalIgnoreCase))
        || email.Bcc.Any(bcc => bcc.Contains(query, StringComparison.OrdinalIgnoreCase));
}
