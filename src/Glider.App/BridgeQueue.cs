using Glider.Core.Contracts;

namespace Glider.App;

/// <summary>
/// Host → UI message queue (not thread-safe: the bridge locks around it). Delivery is FIFO, with these folds:
/// <list type="bullet">
/// <item><c>state</c>, <c>progress</c>, <c>window</c>: only the latest survives, always (each is a full snapshot).</item>
/// <item>While <see cref="Held"/> (the page is suspended or not navigated yet) <c>library</c>, <c>store</c>, <c>freeGames</c>,
/// <c>navigate</c> and <c>caption</c> keep only the latest too: a library delta with a revision gap makes the UI call
/// <c>library.get</c> once, instead of replaying hundreds of deltas as a render storm.</item>
/// <item>While held, at most <see cref="HeldNoticeCap"/> newest <c>notice</c> events are kept.</item>
/// </list>
/// Responses and unknown events are never dropped. Every operation is O(1) except <see cref="Held"/> turning on (one pass).
/// </summary>
internal sealed class BridgeQueue
{
    public const int HeldNoticeCap = 20;

    private const string EventPrefix = "{\"type\":\"event\",\"event\":\"";

    private enum Policy
    {
        None,
        Latest,
        LatestWhenHeld,
        Notice,
    }

    private sealed record Entry(string Json, string? Key, Policy Policy);

    private readonly LinkedList<Entry> _list = [];
    private readonly Dictionary<string, LinkedListNode<Entry>> _latest = new(StringComparer.Ordinal);
    private readonly Queue<LinkedListNode<Entry>> _notices = new();
    private bool _held;

    public int Count => _list.Count;

    /// <summary>While held, messages accumulate (folded) instead of being delivered. Turning it on folds what is already queued.</summary>
    public bool Held
    {
        get => _held;
        set
        {
            var folding = value && !_held;
            _held = value;
            if (folding)
            {
                Fold();
            }
        }
    }

    public void Enqueue(string json)
    {
        var (key, policy) = Classify(json);
        if (key is not null && (policy == Policy.Latest || (policy == Policy.LatestWhenHeld && _held)) && _latest.Remove(key, out var old))
        {
            _list.Remove(old);
        }

        var node = _list.AddLast(new Entry(json, key, policy));
        if (key is not null && (policy == Policy.Latest || (policy == Policy.LatestWhenHeld && _held)))
        {
            _latest[key] = node;
        }
        else if (policy == Policy.Notice)
        {
            _notices.Enqueue(node);
            if (_held)
            {
                TrimNotices();
            }
        }
    }

    public bool TryDequeue(out string json)
    {
        var node = _list.First;
        if (node is null)
        {
            json = "";
            return false;
        }

        _list.RemoveFirst();
        var entry = node.Value;
        if (entry.Key is not null && _latest.TryGetValue(entry.Key, out var latest) && ReferenceEquals(latest, node))
        {
            _latest.Remove(entry.Key);
        }

        if (entry.Policy == Policy.Notice)
        {
            _notices.Dequeue(); // notices leave in FIFO order, so this is the node just removed
        }

        json = entry.Json;
        return true;
    }

    public void Clear()
    {
        _list.Clear();
        _latest.Clear();
        _notices.Clear();
    }

    /// <summary>Holding just started: queued held-folded kinds are reduced to their latest, notices to the cap.</summary>
    private void Fold()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var node = _list.Last; node is not null;)
        {
            var previous = node.Previous;
            var entry = node.Value;
            if (entry.Key is not null && entry.Policy is Policy.Latest or Policy.LatestWhenHeld)
            {
                if (seen.Add(entry.Key))
                {
                    _latest[entry.Key] = node;
                }
                else
                {
                    _list.Remove(node);
                }
            }

            node = previous;
        }

        TrimNotices();
    }

    private void TrimNotices()
    {
        while (_notices.Count > HeldNoticeCap)
        {
            _list.Remove(_notices.Dequeue());
        }
    }

    private static (string? Key, Policy Policy) Classify(string json)
    {
        if (!json.StartsWith(EventPrefix, StringComparison.Ordinal))
        {
            return (null, Policy.None);
        }

        var end = json.IndexOf('"', EventPrefix.Length);
        if (end < 0)
        {
            return (null, Policy.None);
        }

        var name = json.AsSpan(EventPrefix.Length, end - EventPrefix.Length);
        if (name.SequenceEqual(RpcEvents.State))
        {
            return (RpcEvents.State, Policy.Latest);
        }

        if (name.SequenceEqual(RpcEvents.Progress))
        {
            return (RpcEvents.Progress, Policy.Latest);
        }

        if (name.SequenceEqual(RpcEvents.Window))
        {
            return (RpcEvents.Window, Policy.Latest);
        }

        if (name.SequenceEqual(RpcEvents.Library))
        {
            return (RpcEvents.Library, Policy.LatestWhenHeld);
        }

        if (name.SequenceEqual(RpcEvents.Store))
        {
            return (RpcEvents.Store, Policy.LatestWhenHeld);
        }

        if (name.SequenceEqual(RpcEvents.FreeGames))
        {
            return (RpcEvents.FreeGames, Policy.LatestWhenHeld);
        }

        if (name.SequenceEqual(RpcEvents.Navigate))
        {
            return (RpcEvents.Navigate, Policy.LatestWhenHeld);
        }

        if (name.SequenceEqual(RpcEvents.Caption))
        {
            return (RpcEvents.Caption, Policy.LatestWhenHeld);
        }

        return name.SequenceEqual(RpcEvents.Notice) ? (null, Policy.Notice) : (null, Policy.None);
    }
}
