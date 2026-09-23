using System.Globalization;

namespace KickoutMonitor.Domain;

public sealed record QueueTimeRange(DateTime Start, DateTime End)
{
    public static QueueTimeRange DlngDefault(DateTime now) =>
        new(now.Date.AddDays(-1).AddHours(6), now);
    public static QueueTimeRange KickoutDefault(DateTime now)
    {
        var day = now.TimeOfDay >= TimeSpan.FromHours(6.5) ? now.Date : now.Date.AddDays(-1);
        return new(day.AddHours(6), day.AddDays(1).AddHours(6));
    }

    public bool IsValid => End > Start;
    public bool Contains(DateTime judgedAt) => judgedAt >= Start && judgedAt < End;
    public IEnumerable<DateOnly> Dates()
    {
        if (!IsValid) yield break;
        var first = DateOnly.FromDateTime(Start);
        var last = DateOnly.FromDateTime(End.AddTicks(-1));
        for (var day = first; ; day = day.AddDays(1))
        {
            yield return day;
            if (day == last) yield break;
        }
    }

    public static bool TryCreate(DateTime? startDate, string? startTime, DateTime? endDate, string? endTime,
        out QueueTimeRange? range, out string error)
    {
        range = null;
        error = "";
        if (startDate is null || endDate is null)
        {
            error = "Select both queue dates.";
            return false;
        }
        string[] formats = ["H:mm", "HH:mm", "H:mm:ss", "HH:mm:ss", "h:mm tt", "hh:mm tt"];
        if (!TimeOnly.TryParseExact(startTime?.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var start)
            || !TimeOnly.TryParseExact(endTime?.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var end))
        {
            error = "Enter valid queue times (HH:mm, for example 06:00 or 18:30).";
            return false;
        }
        var proposed = new QueueTimeRange(startDate.Value.Date.Add(start.ToTimeSpan()), endDate.Value.Date.Add(end.ToTimeSpan()));
        if (!proposed.IsValid)
        {
            error = "Queue end date/time must be later than start. For 06:00–06:00, select a later end date.";
            return false;
        }
        range = proposed;
        return true;
    }
}
