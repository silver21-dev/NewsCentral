using System.Text.Json;
using NewsCentral.Models;
using NewsViewer.Models;

namespace NewsViewer.Services;

public sealed class ViewerStateService
{
    private readonly string _statePath;
    private readonly int _logicalDayStartHour;

    public ViewerStateService(string userStatePath, int logicalDayStartHour)
    {
        _statePath = Path.Combine(userStatePath, "viewerstate.json");
        _logicalDayStartHour = logicalDayStartHour;
    }

    // Date-only gate: disturb the user once per logical day, regardless of which presentation is
    // active. A new presentation no longer earns an automatic same-day escape — that becomes
    // author-controlled (Priority) in a later release.
    public bool AlreadyShownToday()
    {
        var state = Read();
        return state.LastShownDate == LogicalDayCalculator.LogicalDay(DateTime.Now, _logicalDayStartHour);
    }

    public void RecordShown(string presentationId)
    {
        // Read-modify-write so the content marker survives the gate update.
        var state = Read();
        state.LastShownDate = LogicalDayCalculator.LogicalDay(DateTime.Now, _logicalDayStartHour);
        state.LastShownPresentationId = presentationId;
        Write(state);
    }

    /// <summary>The "last content this user saw or was notified about" key; null when never recorded.</summary>
    public string? ReadContentMarker() => Read().LastContentKey;

    public void WriteContentMarker(string contentKey)
    {
        var state = Read();
        state.LastContentKey = contentKey;
        Write(state);
    }

    /// <summary>Debug-only reset support: clears the gate and the marker.</summary>
    public void Clear()
    {
        try { File.Delete(_statePath); } catch { }
    }

    private ViewerState Read()
    {
        try
        {
            if (!File.Exists(_statePath)) return new();
            var json = File.ReadAllText(_statePath);
            return JsonSerializer.Deserialize<ViewerState>(json, JsonDefaults.Options) ?? new();
        }
        catch { return new(); }
    }

    // Atomic: temp file in the same folder, then an overwriting move, so a crash never leaves a
    // half-written state file (which Read would silently treat as "never shown").
    private void Write(ViewerState state)
    {
        var temp = _statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonDefaults.Options));
            File.Move(temp, _statePath, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { }
        }
    }
}
