using NewsCentral.Models;

namespace NewsCentral.Ui;

/// <summary>
/// Pure filter/count helpers for the multi-select team picker. A filter only changes what is SHOWN,
/// never what is selected, so neither method touches the <see cref="SelectionModel{TKey}"/>.
/// </summary>
public static class TeamPickerFilter
{
    /// <summary>
    /// The teams to show: optionally only the selected ones, optionally only those whose Name,
    /// FolderName or Description contains the trimmed <paramref name="search"/> (case-insensitive;
    /// a null or whitespace-only search means no filter), ordered by Name (ordinal, ignore case).
    /// </summary>
    public static List<Team> Shown(IEnumerable<Team> teams, SelectionModel<string> selection, bool selectedOnly, string? search)
    {
        IEnumerable<Team> query = teams;

        if (selectedOnly)
            query = query.Where(t => selection.IsSelected(t.TeamID));

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            query = query.Where(t =>
                (t.Name?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (t.FolderName?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (t.Description?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        return query.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// How many of <paramref name="teams"/> are selected. Selected ids that are not in the list
    /// (e.g. the current team on Create Assignment) are not counted.
    /// </summary>
    public static int SelectedCount(IEnumerable<Team> teams, SelectionModel<string> selection) =>
        teams.Count(t => selection.IsSelected(t.TeamID));
}
