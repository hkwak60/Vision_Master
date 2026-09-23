using KickoutMonitor.Domain;
namespace KickoutMonitor.App.ViewModels;

public interface IReworkRow
{
    string ReworkGroup { get; }
    string InspectionKey { get; }
    DateTime InspectionTime { get; }
    string ReworkLabel { get; set; }
}

public static class ReworkQueue
{
    public static IReadOnlyList<T> ReworkOrder<T>(this IEnumerable<T> rows, Func<T, bool> pending) where T : IReworkRow
    {
        var result = InspectionIdentity.GroupQueue(rows, x => x.ReworkGroup, x => x.InspectionTime, pending);
        foreach (var group in result.GroupBy(x => x.ReworkGroup))
        {
            var count = group.Select(x => x.InspectionKey).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            foreach (var row in group) row.ReworkLabel = count > 1 ? $"{count} inputs" : "";
        }
        return result;
    }
}
