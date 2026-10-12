namespace NewsViewer.Models;

public class ViewerState
{
    public string? LastShownDate { get; set; }
    public string? LastShownPresentationId { get; set; }

    /// <summary>
    /// "Last content this user saw or was notified about" (<c>ContentKeys.For</c>). Null on first run
    /// after upgrade, which the launch decision treats as MarkerInitialized.
    /// </summary>
    public string? LastContentKey { get; set; }
}
