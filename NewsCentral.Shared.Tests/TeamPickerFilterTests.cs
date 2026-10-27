using NewsCentral.Models;
using NewsCentral.Ui;
using Xunit;

namespace NewsCentral.Shared.Tests;

/// <summary>Covers TeamPickerFilter — the pure search / selected-only / ordering / count logic of the team picker.</summary>
public sealed class TeamPickerFilterTests
{
    private static Team T(string id, string? name, string? folder = "", string? desc = "") => new()
    {
        TeamID = id,
        Name = name!,
        FolderName = folder!,
        Description = desc!
    };

    private static readonly Team[] Teams =
    {
        T("1", "Zulu",  "cz-its",   "Prague IT"),
        T("2", "alpha", "us-sales", "Sales Americas"),
        T("3", "Bravo", "de-ops",   "Operations")
    };

    private static List<string> Ids(IEnumerable<Team> teams) => teams.Select(t => t.TeamID).ToList();

    [Fact]
    public void Search_MatchesName_CaseInsensitive()
    {
        var shown = TeamPickerFilter.Shown(Teams, new SelectionModel<string>(), false, "BRAV");
        Assert.Equal(new[] { "3" }, Ids(shown));
    }

    [Fact]
    public void Search_MatchesFolderName()
    {
        var shown = TeamPickerFilter.Shown(Teams, new SelectionModel<string>(), false, "CZ-ITS");
        Assert.Equal(new[] { "1" }, Ids(shown));
    }

    [Fact]
    public void Search_MatchesDescription()
    {
        var shown = TeamPickerFilter.Shown(Teams, new SelectionModel<string>(), false, "americas");
        Assert.Equal(new[] { "2" }, Ids(shown));
    }

    [Fact]
    public void Search_IsTrimmed()
    {
        var shown = TeamPickerFilter.Shown(Teams, new SelectionModel<string>(), false, "  alpha  ");
        Assert.Equal(new[] { "2" }, Ids(shown));
    }

    [Fact]
    public void Search_NullNameAndDescription_DoNotThrow()
    {
        var teams = new[] { T("1", null, null, null), T("2", "Match", null, null) };
        var shown = TeamPickerFilter.Shown(teams, new SelectionModel<string>(), false, "match");
        Assert.Equal(new[] { "2" }, Ids(shown));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Search_NullOrWhitespace_MeansNoFilter(string? search)
    {
        var shown = TeamPickerFilter.Shown(Teams, new SelectionModel<string>(), false, search);
        Assert.Equal(3, shown.Count);
    }

    [Fact]
    public void SelectedOnly_ReturnsOnlySelectedTeams()
    {
        var selection = new SelectionModel<string>();
        selection.Set("1", true);
        selection.Set("3", true);

        var shown = TeamPickerFilter.Shown(Teams, selection, true, null);

        Assert.Equal(new[] { "3", "1" }, Ids(shown));
    }

    [Fact]
    public void SelectedOnly_AndSearch_CombineWithAnd()
    {
        var selection = new SelectionModel<string>();
        selection.Set("1", true);
        selection.Set("2", true);

        var shown = TeamPickerFilter.Shown(Teams, selection, true, "sales");

        Assert.Equal(new[] { "2" }, Ids(shown));
    }

    [Fact]
    public void Result_IsOrderedByName_OrdinalIgnoreCase()
    {
        var shown = TeamPickerFilter.Shown(Teams, new SelectionModel<string>(), false, null);
        Assert.Equal(new[] { "alpha", "Bravo", "Zulu" }, shown.Select(t => t.Name));
    }

    [Fact]
    public void SelectedCount_IgnoresSelectedIdsNotInTeamList()
    {
        var selection = new SelectionModel<string>();
        selection.Set("2", true);
        selection.Set("current-team", true);

        Assert.Equal(1, TeamPickerFilter.SelectedCount(Teams, selection));
    }

    [Fact]
    public void Shown_NeverMutatesTheSelection()
    {
        var selection = new SelectionModel<string>();
        selection.Set("1", true);
        selection.Set("current-team", true);

        TeamPickerFilter.Shown(Teams, selection, true, "zulu");
        TeamPickerFilter.Shown(Teams, selection, false, "nomatch");
        TeamPickerFilter.SelectedCount(Teams, selection);

        Assert.Equal(2, selection.Count);
        Assert.True(selection.IsSelected("1"));
        Assert.True(selection.IsSelected("current-team"));
    }
}
