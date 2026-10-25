namespace NewsCentral.Services;

public enum UserNoticeLevel { Warning, Error }

/// <summary>One message for the user that did not come from the page they are looking at.</summary>
public sealed record UserNotice(Guid Id, UserNoticeLevel Level, string Message);

/// <summary>
/// Messages from work that happens behind an operation which itself succeeded, for example the index regeneration
/// after a delete. The main layout shows them at the top of every page until dismissed, so they are not lost when
/// the page that triggered the work has no place to show them.
/// </summary>
public sealed class UserNotices
{
    private readonly object _gate = new();
    private readonly List<UserNotice> _items = new();

    /// <summary>Raised after a notice is added or dismissed. May be raised on any thread.</summary>
    public event Action? Changed;

    public IReadOnlyList<UserNotice> Items
    {
        get { lock (_gate) return _items.ToList(); }
    }

    public void Add(UserNoticeLevel level, string message)
    {
        lock (_gate)
        {
            // The same warning twice in a row (a presentation saved twice) is one notice, not two.
            if (_items.Any(n => n.Level == level && n.Message == message)) return;
            _items.Add(new UserNotice(Guid.NewGuid(), level, message));
        }
        Changed?.Invoke();
    }

    public void Dismiss(Guid id)
    {
        bool removed;
        lock (_gate) removed = _items.RemoveAll(n => n.Id == id) > 0;
        if (removed) Changed?.Invoke();
    }
}
