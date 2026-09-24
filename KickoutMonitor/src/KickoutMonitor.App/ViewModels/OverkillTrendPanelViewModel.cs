using System.ComponentModel;
using KickoutMonitor.Infrastructure;

namespace KickoutMonitor.App.ViewModels;

public sealed class MachineLegend : INotifyPropertyChanged
{
    public string Line { get; }
    public string Color { get; }
    private bool _visible = true;
    public bool Visible { get => _visible; set { if (_visible == value) return; _visible = value; PropertyChanged?.Invoke(this, new(nameof(Visible))); } }
    public MachineLegend(string line, string color) { Line = line; Color = color; }
    public event PropertyChangedEventHandler? PropertyChanged;
}
public sealed record OverkillSeries(string Line, string Color, IReadOnlyList<MachineDayCount> Points);
public sealed record TrendHeatCell(string Line, string Field, int? Count, int CoveredDays, string Color, bool Selected)
{
    public string Text => Count?.ToString("N0") ?? "—";
    public string Tooltip => $"{Line} · {Field}\n과검 {Text}건 · 자료 {CoveredDays}일";
    public string Foreground => Color == "#2563EB" ? "White" : "#172B4D";
}
public sealed record TrendHeatRow(string Line, IReadOnlyList<TrendHeatCell> Cells);
public sealed record MachineTrendCard(string Line, string Color, IReadOnlyList<OverkillSeries> Series, IReadOnlyList<TrendHeatCell> Rows);
public sealed record TrendField(string Name, bool Selected);
public sealed record TrendDetailRequest(string Kind, string Line, string Field, DateOnly? Day);

public sealed class OverkillTrendPanelViewModel : INotifyPropertyChanged
{
    private OverkillTrendData _data = new([], []);
    private string _field = OverkillTrendService.All;
    public string Kind { get; }
    public string Caption => Kind == "DLNG" ? "리뷰된 샘플 기준" : "생성 보고서 기준";
    public IReadOnlyList<string> Fields { get; private set; } = [OverkillTrendService.All];
    public IReadOnlyList<MachineLegend> Machines { get; }
    public IReadOnlyList<OverkillSeries> Series { get; private set; } = [];
    public IReadOnlyList<TrendHeatRow> HeatRows { get; private set; } = [];
    public IReadOnlyList<IReadOnlyList<MachineTrendCard>> CardRows { get; private set; } = [];
    public IReadOnlyList<MachineTrendCard> Cards { get; private set; } = [];
    public IReadOnlyList<TrendField> Headers { get; private set; } = [];
    public int Maximum { get; private set; }
    public string ScaleLabel => $"과검 건수   0 — {Maximum:N0}";
    public bool HasFields => _data.Fields.Count > 0;
    public string SelectedField
    {
        get => _field;
        set { if (string.IsNullOrEmpty(value) || _field == value) return; _field = value; Rebuild(); PropertyChanged?.Invoke(this, new(nameof(SelectedField))); }
    }
    public OverkillTrendPanelViewModel(string kind)
    {
        Kind = kind;
        Machines = OverkillTrendService.Lines.Select((line, i) => new MachineLegend(line, OverkillTrendService.Colors[i])).ToArray();
        foreach (var machine in Machines) machine.PropertyChanged += (_, _) => Rebuild();
    }
    public void SetData(OverkillTrendData data)
    {
        _data = data;
        var selection = _field;
        var totals = data.Points.Where(p => p.Field != OverkillTrendService.All).GroupBy(p => p.Field)
            .ToDictionary(g => g.Key, g => g.Sum(p => p.Count ?? 0));
        Fields = data.Fields.OrderByDescending(f => totals.GetValueOrDefault(f)).ThenBy(f => f)
            .Prepend(OverkillTrendService.All).ToArray();
        _field = Fields.Contains(selection) ? selection : OverkillTrendService.All;
        Rebuild();
        PropertyChanged?.Invoke(this, new(nameof(Fields)));
        PropertyChanged?.Invoke(this, new(nameof(SelectedField)));
    }
    private void Rebuild()
    {
        var visible = Machines.Where(m => m.Visible).ToArray();
        Series = visible.Select(m => new OverkillSeries(m.Line, m.Color,
            _data.Points.Where(p => p.Line == m.Line && p.Field == _field).OrderBy(p => p.Day).ToArray())).ToArray();
        var cells = OverkillTrendService.Period(_data, visible.Select(m => m.Line));
        Maximum = cells.Select(c => c.Count ?? 0).DefaultIfEmpty(0).Max();
        Headers = Fields.Where(f => f != OverkillTrendService.All).Select(f => new TrendField(f, f == _field)).ToArray();
        HeatRows = visible.Select(m => new TrendHeatRow(m.Line, _data.Fields.Select(f =>
        {
            var c = cells.FirstOrDefault(c => c.Line == m.Line && c.Field == f);
            return new TrendHeatCell(m.Line, f, c?.Count, c?.CoveredDays ?? 0,
                OverkillTrendService.HeatColor(c?.Count, Maximum), f == _field);
        }).ToArray())).ToArray();
        Cards = visible.Select(m => new MachineTrendCard(m.Line, m.Color,
            Series.Where(s => s.Line == m.Line).ToArray(),
            HeatRows.Single(r => r.Line == m.Line).Cells.OrderByDescending(c => c.Count.HasValue)
                .ThenByDescending(c => c.Count).ThenBy(c => c.Field).ToArray())).ToArray();
        CardRows = Cards.GroupBy(c => c.Line.Split('(')[0]).Select(g => (IReadOnlyList<MachineTrendCard>)g.ToArray()).ToArray();
        foreach (var property in new[] { nameof(CardRows), nameof(Cards), nameof(Series), nameof(HeatRows), nameof(Headers), nameof(Maximum), nameof(ScaleLabel), nameof(HasFields) })
            PropertyChanged?.Invoke(this, new(property));
    }
    public event Action<TrendDetailRequest>? DetailRequested;
    public void ShowDetails(string line, string field, DateOnly? day = null) => DetailRequested?.Invoke(new(Kind, line, field, day));
    public event PropertyChangedEventHandler? PropertyChanged;
}
