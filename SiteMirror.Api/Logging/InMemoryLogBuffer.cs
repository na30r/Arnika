namespace SiteMirror.Api.Logging;

public sealed class InMemoryLogBuffer
{
    private readonly int _capacity;
    private readonly object _lock = new();
    private readonly List<LogLineDto> _items = new();

    public InMemoryLogBuffer(int capacity = 2500)
    {
        _capacity = Math.Clamp(capacity, 100, 50_000);
    }

    public void Add(LogLineDto line)
    {
        lock (_lock)
        {
            _items.Add(line);
            var overflow = _items.Count - _capacity;
            if (overflow > 0)
                _items.RemoveRange(0, overflow);
        }
    }

    public IReadOnlyList<LogLineDto> Snapshot()
    {
        lock (_lock)
            return _items.ToArray();
    }
}
