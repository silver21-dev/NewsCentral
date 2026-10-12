namespace NewsCentral.Models.IndexFile;

/// <summary>
/// Pure selection helpers over <see cref="PublishedAssignmentIndex"/>: determine whether an
/// assignment is active for a given instant, and pick the newest active assignment matching a
/// predicate. Shared so both NewsService (lock screen) and NewsViewer (poster + wallpaper) apply
/// identical active-window/day-of-week semantics and "newest by PresentationLastModified" tie-break.
/// </summary>
public static class ActiveAssignmentSelector
{
    /// <summary>
    /// An assignment is active when <paramref name="now"/> falls within its schedule window and
    /// today's day number (1=Mon … 7=Sun) is listed in <see cref="PublishedAssignmentIndex.DaysOfWeek"/>.
    /// Schedules use client local time — no timezone conversion.
    /// </summary>
    public static bool IsActive(PublishedAssignmentIndex assignment, DateTime now)
    {
        if (assignment.ScheduleStart > now || assignment.ScheduleEnd < now) return false;

        // DayOfWeek: Sunday=0, Monday=1 … Saturday=6 → map to 1=Mon … 7=Sun
        var todayKey = now.DayOfWeek == DayOfWeek.Sunday ? "7" : ((int)now.DayOfWeek).ToString();
        return assignment.DaysOfWeek.Split(',').Contains(todayKey);
    }

    /// <summary>
    /// Returns the active assignment with the most recent <see cref="PublishedAssignmentIndex.PresentationLastModified"/>
    /// that also satisfies <paramref name="predicate"/>, or <c>null</c> when none qualify.
    /// </summary>
    public static PublishedAssignmentIndex? PickNewestActive(
        IEnumerable<PublishedAssignmentIndex> assignments,
        DateTime now,
        Func<PublishedAssignmentIndex, bool> predicate)
    {
        PublishedAssignmentIndex? best = null;

        foreach (var a in assignments)
        {
            if (!IsActive(a, now)) continue;
            if (!predicate(a)) continue;

            if (best is null || a.PresentationLastModified > best.PresentationLastModified)
                best = a;
        }

        return best;
    }

    /// <summary>
    /// Poster ranking: "newest to the team" — the later of <see cref="PublishedAssignmentIndex.PresentationLastModified"/>
    /// and the assignment's <see cref="PublishedAssignmentIndex.PublishedDate"/> (both normalized to UTC; a
    /// missing publish date is <c>default</c> and so falls back to last-modified). Ties break on
    /// PresentationLastModified descending, then AssignmentId ordinal ascending, so the result is
    /// independent of enumeration order. Qualification (active window, day, predicate) is identical to
    /// <see cref="PickNewestActive"/>, which other consumers (lock screen, wallpaper) keep using.
    /// </summary>
    public static PublishedAssignmentIndex? PickNewestToTeam(
        IEnumerable<PublishedAssignmentIndex> assignments,
        DateTime now,
        Func<PublishedAssignmentIndex, bool> predicate)
    {
        PublishedAssignmentIndex? best = null;

        foreach (var a in assignments)
        {
            if (!IsActive(a, now)) continue;
            if (!predicate(a)) continue;

            if (best is null || IsBetter(a, best))
                best = a;
        }

        return best;
    }

    private static DateTime Utc(DateTime d) => d.ToUniversalTime();

    private static DateTime Rank(PublishedAssignmentIndex a) =>
        Utc(a.PublishedDate) > Utc(a.PresentationLastModified) ? Utc(a.PublishedDate) : Utc(a.PresentationLastModified);

    private static bool IsBetter(PublishedAssignmentIndex candidate, PublishedAssignmentIndex current)
    {
        var c = Rank(candidate).CompareTo(Rank(current));
        if (c != 0) return c > 0;
        c = Utc(candidate.PresentationLastModified).CompareTo(Utc(current.PresentationLastModified));
        if (c != 0) return c > 0;
        return string.CompareOrdinal(candidate.AssignmentId, current.AssignmentId) < 0;
    }
}
