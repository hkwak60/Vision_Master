using System.Collections;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Data;
using KickoutMonitor.App.ViewModels;

namespace KickoutMonitor.App.Services;

public static class ReworkGrid
{
    public static void Sort(object sender, DataGridSortingEventArgs e)
    {
        if (sender is not DataGrid grid || CollectionViewSource.GetDefaultView(grid.ItemsSource) is not ListCollectionView view) return;
        var rows = view.Cast<IReworkRow>().ToArray();
        var property = e.Column.SortMemberPath;
        if (string.IsNullOrEmpty(property) && e.Column is DataGridBoundColumn column && column.Binding is Binding binding)
            property = binding.Path.Path;
        var descending = e.Column.SortDirection != ListSortDirection.Descending && e.Column.SortDirection is not null;
        var groups = rows.GroupBy(x => x.ReworkGroup).Select(g => new {
            Key = g.Key, Rows = g.OrderBy(x => x.InspectionTime).ThenBy(x => x.InspectionKey).ToArray()
        }).ToArray();
        object? Value(IReworkRow row) => property == "Time" ? row.InspectionTime :
            row.GetType().GetProperty(property)?.GetValue(row);
        var ordered = descending ? groups.OrderByDescending(g => Value(g.Rows[0]), Comparer<object?>.Create((a, b) => Comparer.DefaultInvariant.Compare(a, b)))
            : groups.OrderBy(g => Value(g.Rows[0]), Comparer<object?>.Create((a, b) => Comparer.DefaultInvariant.Compare(a, b)));
        var ranks = ordered.ThenBy(g => g.Key).SelectMany(g => g.Rows).Select((row, index) => (row, index))
            .ToDictionary(x => x.row, x => x.index);
        foreach (var c in grid.Columns) c.SortDirection = null;
        e.Column.SortDirection = descending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        view.CustomSort = new RankComparer(ranks);
        e.Handled = true;
    }
    private sealed class RankComparer(Dictionary<IReworkRow, int> ranks) : IComparer
    {
        public int Compare(object? x, object? y) => ranks.GetValueOrDefault((IReworkRow)x!, int.MaxValue)
            .CompareTo(ranks.GetValueOrDefault((IReworkRow)y!, int.MaxValue));
    }
}
