namespace MyHomeSecurity.Core.HomeAssistant;

/// <summary>
/// Makes switches feel instant: after the user flips one, the tile shows the new state straight
/// away and ignores polled states for a few seconds, because Home Assistant may still report the
/// old one until the device confirms.
/// </summary>
public sealed class HaSwitchGuard(TimeSpan? holdFor = null, Func<DateTime>? clock = null)
{
    private readonly TimeSpan _holdFor = holdFor ?? TimeSpan.FromSeconds(4);
    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.UtcNow);
    private readonly Dictionary<string, (bool On, DateTime Until)> _pending = new(StringComparer.Ordinal);

    public void Set(string entityId, bool on)
    {
        lock (_pending)
            _pending[entityId] = (on, _clock() + _holdFor);
    }

    public void Clear(string entityId)
    {
        lock (_pending)
            _pending.Remove(entityId);
    }

    /// <summary>The state to show, given what Home Assistant just reported.</summary>
    public bool Resolve(string entityId, bool reported)
    {
        lock (_pending)
        {
            if (!_pending.TryGetValue(entityId, out var p))
                return reported;
            if (reported == p.On || _clock() >= p.Until)
            {
                _pending.Remove(entityId);
                return reported;
            }
            return p.On;
        }
    }
}
