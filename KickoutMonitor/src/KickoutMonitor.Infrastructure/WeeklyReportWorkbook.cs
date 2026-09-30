using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
namespace KickoutMonitor.Infrastructure;

// Unrelated workbook parts are copied verbatim. Only our sheet, drawings and charts are replaced.
public sealed class WeeklyReportWorkbook
{
    public const string SheetName = "●Overkill 추이";
    private const string Prefix = "xl/vmWeekly/";
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace C = "http://schemas.openxmlformats.org/drawingml/2006/chart";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace D = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";
    private static readonly XNamespace T = "http://schemas.openxmlformats.org/package/2006/content-types";
    private readonly SortedDictionary<int, SortedDictionary<int, object>> _cells = [];
    private readonly List<XElement> _anchors = [], _drawingRels = [];
    private readonly Dictionary<string, byte[]> _parts = [];
    private readonly List<XElement> _merges = [];
    private readonly SortedSet<int> _breaks = [];
    private int _chartNumber, _dataRow = 1;
    private int _normalStyle, _headerStyle, _titleStyle, _percentStyle;
    public void Update(string path, IReadOnlyList<KickoutHistorySnapshot> history, DateOnly start, DateOnly end, IReadOnlyList<string> selectedLines)
    {
        if (end < start || end.DayNumber - start.DayNumber > 3660) throw new ArgumentException("시작일과 종료일을 확인하세요.");
        var lines = OverkillTrendService.Lines.Where(selectedLines.Contains).ToArray();
        if (lines.Length == 0) throw new ArgumentException("리포트에 포함할 호기를 하나 이상 선택하세요.");
        if (!File.Exists(path)) throw new FileNotFoundException("주간 보고 파일을 선택하세요.", path);
        _cells.Clear(); _merges.Clear(); _breaks.Clear(); _anchors.Clear(); _drawingRels.Clear(); _parts.Clear(); _chartNumber = 0; _dataRow = 1;
        var originalDigest = SafeWorkbookCommit.Digest(path);
        var commitStarted = false;
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            using (var source = ZipFile.OpenRead(path))
                foreach (var entry in source.Entries.Where(e => !e.FullName.StartsWith(Prefix, StringComparison.Ordinal)))
                { using var input = entry.Open(); using var buffer = new MemoryStream(); input.CopyTo(buffer); _parts[entry.FullName] = buffer.ToArray(); }
            var wb = Xml("xl/workbook.xml"); var rel = Xml("xl/_rels/workbook.xml.rels"); var types = Xml("[Content_Types].xml");
            var sheets = wb.Root!.Element(S + "sheets")!;
            var existing = sheets.Elements(S + "sheet").FirstOrDefault(s => new[] { SheetName, "Overkill 추이", "●Kickout 과검" }.Contains(((string?)s.Attribute("name"))?.Trim()));
            if (existing is not null)
            {
                var oldRel = rel.Root!.Elements().FirstOrDefault(r => (string?)r.Attribute("Id") == (string?)existing.Attribute(R + "id"));
                if (oldRel is null) throw new InvalidDataException("추이 시트의 관계 정보를 찾을 수 없습니다.");
                var oldTarget = Resolve("xl/workbook.xml", (string)oldRel.Attribute("Target")!);
                if (!oldTarget.StartsWith(Prefix)) { RemoveOldTrendParts(oldTarget); types = Xml("[Content_Types].xml"); }
                oldRel.SetAttributeValue("Target", "vmWeekly/sheet.xml");
                existing.SetAttributeValue("name", SheetName);
            }
            else
            {
                var rid = UniqueId(rel.Root!);
                rel.Root!.Add(new XElement(P + "Relationship", new XAttribute("Id", rid), new XAttribute("Type", R.NamespaceName + "/worksheet"), new XAttribute("Target", "vmWeekly/sheet.xml")));
                sheets.Add(new XElement(S + "sheet", new XAttribute("name", SheetName), new XAttribute("sheetId", sheets.Elements().Max(e => (int?)e.Attribute("sheetId") ?? 0) + 1), new XAttribute(R + "id", rid)));
            }
            foreach (var t in types.Root!.Elements().Where(e => ((string?)e.Attribute("PartName"))?.StartsWith("/" + Prefix) == true).ToArray()) t.Remove();
            InstallStyles();
            Build(history, start, end, lines);
            PopulateReport(wb, rel, types, history, start, end, lines);
            Put(Prefix + "drawing.xml", new XDocument(new XElement(D + "wsDr", new XAttribute(XNamespace.Xmlns + "a", A), new XAttribute(XNamespace.Xmlns + "r", R), _anchors)));
            Put(Prefix + "_rels/drawing.xml.rels", new XDocument(new XElement(P + "Relationships", _drawingRels)));
            Put(Prefix + "_rels/sheet.xml.rels", new XDocument(new XElement(P + "Relationships", new XElement(P + "Relationship", new XAttribute("Id", "rId1"), new XAttribute("Type", R.NamespaceName + "/drawing"), new XAttribute("Target", "drawing.xml")))));
            var sheetData = new XElement(S + "sheetData", _cells.Select(row => new XElement(S + "row", new XAttribute("r", row.Key), new XAttribute("ht", 18), new XAttribute("customHeight", 1), row.Value.Select(cell => Cell(row.Key, cell.Key, cell.Value)))));
            Put(Prefix + "sheet.xml", new XDocument(new XElement(S + "worksheet", new XAttribute(XNamespace.Xmlns + "r", R),
                new XElement(S + "sheetPr", new XElement(S + "pageSetUpPr", new XAttribute("fitToPage", 1))),
                new XElement(S + "sheetViews", new XElement(S + "sheetView", new XAttribute("workbookViewId", 0), new XAttribute("showGridLines", 0), new XElement(S + "pane", new XAttribute("ySplit", 3), new XAttribute("topLeftCell", "A4"), new XAttribute("state", "frozen")))),
                new XElement(S + "cols", new XElement(S + "col", new XAttribute("min", 1), new XAttribute("max", 26), new XAttribute("width", 10), new XAttribute("customWidth", 1)), new XElement(S + "col", new XAttribute("min", 41), new XAttribute("max", 43), new XAttribute("hidden", 1))),
                sheetData, new XElement(S + "mergeCells", new XAttribute("count", _merges.Count), _merges), new XElement(S + "pageMargins", new XAttribute("left", .25), new XAttribute("right", .25), new XAttribute("top", .4), new XAttribute("bottom", .4), new XAttribute("header", .2), new XAttribute("footer", .2)),
                new XElement(S + "pageSetup", new XAttribute("orientation", "landscape"), new XAttribute("paperSize", 9), new XAttribute("fitToWidth", 1), new XAttribute("fitToHeight", 0)),
                new XElement(S + "rowBreaks", new XAttribute("count", _breaks.Count), new XAttribute("manualBreakCount", _breaks.Count), _breaks.Select(n => new XElement(S + "brk", new XAttribute("id", n - 1), new XAttribute("max", 16383), new XAttribute("man", 1)))), new XElement(S + "drawing", new XAttribute(R + "id", "rId1")))));
            AddType(types, Prefix + "sheet.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
            AddType(types, Prefix + "drawing.xml", "application/vnd.openxmlformats-officedocument.drawing+xml");
            foreach (var name in _parts.Keys.Where(k => k.StartsWith(Prefix + "chart"))) AddType(types, name, "application/vnd.openxmlformats-officedocument.drawingml.chart+xml");
            foreach (var name in _parts.Keys.Where(k => k.StartsWith(Prefix + "image"))) AddType(types, name, name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg");
            var index = sheets.Elements().ToList().FindIndex(x => (string?)x.Attribute("name") == SheetName);
            var defined = wb.Root.Element(S + "definedNames");
            if (defined is null) { defined = new XElement(S + "definedNames"); var calc = wb.Root.Element(S + "calcPr"); if (calc is not null) calc.AddBeforeSelf(defined); else sheets.AddAfterSelf(defined); }
            foreach (var x in defined.Elements().Where(x => (string?)x.Attribute("name") == "_xlnm.Print_Area" && (int?)x.Attribute("localSheetId") == index).ToArray()) x.Remove();
            defined.Add(new XElement(S + "definedName", new XAttribute("name", "_xlnm.Print_Area"), new XAttribute("localSheetId", index),
                "'" + SheetName + "'!$A$1:$Z$" + _cells.Where(r => r.Value.Keys.Any(c => c <= 26)).Max(r => r.Key)));
            // Excel's cached calculation chain may point at formulas replaced by report values.
            // Drop only the cache; Excel reconstructs it without changing other sheets' formulas.
            foreach (var chain in rel.Root!.Elements().Where(e => ((string?)e.Attribute("Type"))?.EndsWith("/calcChain") == true).ToArray())
            {
                var target = Resolve("xl/workbook.xml", (string)chain.Attribute("Target")!);
                _parts.Remove(target);
                types.Root!.Elements().Where(e => (string?)e.Attribute("PartName") == "/" + target).Remove();
                chain.Remove();
            }
            wb.Root.Element(S + "calcPr")?.SetAttributeValue("fullCalcOnLoad", 1);
            Put("xl/workbook.xml", wb); Put("xl/_rels/workbook.xml.rels", rel); Put("[Content_Types].xml", types);
            using (var output = ZipFile.Open(temp, ZipArchiveMode.Create))
                foreach (var part in _parts) { using var target = output.CreateEntry(part.Key).Open(); target.Write(part.Value); }
            using (var check = ZipFile.OpenRead(temp))
                foreach (var entry in check.Entries.Where(e => e.FullName.EndsWith(".xml") || e.FullName.EndsWith(".rels"))) { using var stream = entry.Open(); XDocument.Load(stream); }
            commitStarted = true;
            SafeWorkbookCommit.Commit(temp, path, originalDigest);
        }
        finally { if (!commitStarted && File.Exists(temp)) File.Delete(temp); }
    }
    private sealed record Styled(object Value, int Style);
    private void Text(int row, int col, object? value, int? style = null) { if (!_cells.ContainsKey(row)) _cells[row] = []; _cells[row][col] = new Styled(value ?? "—", style ?? _normalStyle); }
    private void Span(int row, int col, int width, int height = 1)
    {
        _merges.Add(new XElement(S + "mergeCell", new XAttribute("ref", Column(col) + row + ":" + Column(col + width - 1) + (row + height - 1))));
    }
    private void Build(IReadOnlyList<KickoutHistorySnapshot> history, DateOnly start, DateOnly end, IReadOnlyList<string> lines)
    {
        Text(1, 1, "Kickout 과검 검토 보고", _titleStyle);
        Text(2, 1, $"{start:yyyy-MM-dd} ~ {end:yyyy-MM-dd}");

        Span(1, 1, 26); Span(2, 1, 26);
        var headers = new[] { "호기", "투입셀", "NG 배출", "실불량", "과검", "과검율", "자료 일수" };
        for (var c = 0; c < headers.Length; c++) { Text(5, 1 + c * 2, headers[c], _headerStyle); Span(5, 1 + c * 2, 2); }
        for (var i = 0; i < lines.Count; i++)
        {
            var s = WeeklyReportData.Summary(history, lines[i], start, end);
            object?[] values = [s.Line, s.Inspected, s.Ng, s.Real, s.Overkill, s.Inspected is > 0 && s.Overkill.HasValue ? (double)s.Overkill.Value / s.Inspected.Value : null, s.CoveredDays];
            for (var c = 0; c < values.Length; c++) { Text(6 + i, 1 + c * 2, values[c], c == 5 ? _percentStyle : _normalStyle); Span(6 + i, 1 + c * 2, 2); }
        }
        var data = OverkillTrendService.Kickout(history, start, end);
        var totals = OverkillTrendService.Period(data, OverkillTrendService.Lines);
        var pairs = lines.GroupBy(l => l.Split('(')[0]).Select(g => g.ToArray()).ToArray();
        int row = 8 + lines.Count;
        Span(row, 1, 26); Text(row++, 1, "선택 기간 · 일별 과검 / 과검 항목", _titleStyle);
        for (var pair = 0; pair < pairs.Length; pair++)
        {
            if (pair == 2) _breaks.Add(row);
            var height = 20;
            for (var side = 0; side < pairs[pair].Length; side++)
            {
                var line = pairs[pair][side]; int i = Array.IndexOf(OverkillTrendService.Lines, line), col = line.Contains("(+)") ? 14 : 1;
                Text(row, col, line, _headerStyle);
                var points = data.Points.Where(p => p.Line == line && p.Field == OverkillTrendService.All).ToArray();
                Chart(line + " · 일별 과검", points.Select(p => p.Day.ToString("MM/dd")).ToArray(), points.Select(p => p.Count).ToArray(), OverkillTrendService.Colors[i], row + 1, col, 9, 17);
                Span(row + 1, col + 9, 3); Text(row + 1, col + 9, "과검 항목", _headerStyle); Text(row + 1, col + 12, "건수", _headerStyle);
                var fields = totals.Where(p => p.Line == line && p.Count > 0).OrderByDescending(p => p.Count).ThenBy(p => p.Field).ToArray();
                for (var f = 0; f < fields.Length; f++) { Span(row + 2 + f, col + 9, 3); Text(row + 2 + f, col + 9, fields[f].Field); Text(row + 2 + f, col + 12, fields[f].Count); }
                if (fields.Length == 0) Text(row + 2, col + 9, points.Any(p => p.HasData) ? "과검 없음" : "자료 없음");
                height = Math.Max(height, fields.Length + 4);
            }
            row += height;
        }
        _breaks.Add(row);
        Span(row, 1, 26); Text(row++, 1, "최근 8주 · 주별 과검 (일~토)", _titleStyle);
        for (var pair = 0; pair < pairs.Length; pair++)
        {
            if (pair == 2) _breaks.Add(row);
            for (var side = 0; side < pairs[pair].Length; side++)
            {
                var line = pairs[pair][side]; int i = Array.IndexOf(OverkillTrendService.Lines, line), col = line.Contains("(+)") ? 14 : 1;
                var weeks = WeeklyReportData.Weeks(history, line, end);
                Chart(line + " · 주별 과검", weeks.Select(w => w.Start.ToString("MM/dd") + (w.Partial ? "*" : "")).ToArray(), weeks.Select(w => w.Count).ToArray(), OverkillTrendService.Colors[i], row, col, 9, 17);
                Span(row, col + 9, 2); Text(row, col + 9, "주 시작", _headerStyle); Text(row, col + 11, "건수", _headerStyle); Text(row, col + 12, "자료일", _headerStyle);
                for (var w = 0; w < 8; w++) { Span(row + w + 1, col + 9, 2); Text(row + w + 1, col + 9, weeks[w].Start.ToString("MM/dd") + (weeks[w].Partial ? "*" : "")); Text(row + w + 1, col + 11, weeks[w].Count); Text(row + w + 1, col + 12, weeks[w].CoveredDays); }
                Span(row + 10, col + 9, 4); Text(row + 10, col + 9, "* 종료일까지만 집계");
            }
            row += 19;
        }
        Text(row, 1, "");
    }
    private void Chart(string title, string[] labels, int?[] counts, string color, int row, int col, int width, int height)
    {
        var number = ++_chartNumber; var id = "chart" + number; var dataStart = _dataRow;
        for (var i = 0; i < labels.Length; i++) { Text(_dataRow, 41, labels[i]); if (counts[i].HasValue) Text(_dataRow, 42, counts[i]!.Value); _dataRow++; }
        var range = "'" + SheetName + "'!";
        var catCache = new XElement(C + "strCache", new XElement(C + "ptCount", new XAttribute("val", labels.Length)), labels.Select((x, i) => new XElement(C + "pt", new XAttribute("idx", i), new XElement(C + "v", x))));
        var numCache = new XElement(C + "numCache", new XElement(C + "formatCode", "0"), new XElement(C + "ptCount", new XAttribute("val", counts.Length)), counts.Select((x, i) => x.HasValue ? new XElement(C + "pt", new XAttribute("idx", i), new XElement(C + "v", x.Value)) : null));
        var line = new XElement(C + "lineChart", Val(C, "grouping", "standard"), Val(C, "varyColors", 0),
            new XElement(C + "ser", Val(C, "idx", 0), Val(C, "order", 0), new XElement(C + "tx", new XElement(C + "v", title)),
                new XElement(C + "spPr", new XElement(A + "ln", new XAttribute("w", 25400), new XElement(A + "solidFill", new XElement(A + "srgbClr", new XAttribute("val", color.TrimStart('#')))))),
                new XElement(C + "marker", Val(C, "symbol", "circle"), Val(C, "size", 4)),
                new XElement(C + "cat", new XElement(C + "strRef", new XElement(C + "f", range + "$AO$" + dataStart + ":$AO$" + (_dataRow - 1)), catCache)),
                new XElement(C + "val", new XElement(C + "numRef", new XElement(C + "f", range + "$AP$" + dataStart + ":$AP$" + (_dataRow - 1)), numCache)), Val(C, "smooth", 0)), Val(C, "axId", 1), Val(C, "axId", 2));
        var chart = new XElement(C + "chart",
            new XElement(C + "title", new XElement(C + "tx", new XElement(C + "rich", new XElement(A + "bodyPr"), new XElement(A + "lstStyle"), new XElement(A + "p", new XElement(A + "r", new XElement(A + "t", title))))), new XElement(C + "layout"), Val(C, "overlay", 0)),
            new XElement(C + "plotArea", new XElement(C + "layout"), line,
                new XElement(C + "catAx", Val(C, "axId", 1), new XElement(C + "scaling", Val(C, "orientation", "minMax")), Val(C, "delete", 0), Val(C, "axPos", "b"), Val(C, "tickLblPos", "nextTo"), Val(C, "crossAx", 2), Val(C, "crosses", "autoZero"), Val(C, "auto", 1), Val(C, "lblAlgn", "ctr"), Val(C, "lblOffset", 100)),
                new XElement(C + "valAx", Val(C, "axId", 2), new XElement(C + "scaling", Val(C, "orientation", "minMax"), Val(C, "min", 0)), Val(C, "delete", 0), Val(C, "axPos", "l"), new XElement(C + "majorGridlines", new XElement(C + "spPr", new XElement(A + "ln", new XAttribute("w", 6350), new XElement(A + "solidFill", new XElement(A + "srgbClr", new XAttribute("val", "DCE4ED")))))), new XElement(C + "numFmt", new XAttribute("formatCode", "0"), new XAttribute("sourceLinked", 0)), Val(C, "tickLblPos", "nextTo"), Val(C, "crossAx", 1), Val(C, "crosses", "autoZero"), Val(C, "crossBetween", "between"))),
            Val(C, "plotVisOnly", 0), Val(C, "dispBlanksAs", "gap"));
        Put(Prefix + id + ".xml", new XDocument(new XElement(C + "chartSpace", new XAttribute(XNamespace.Xmlns + "a", A), chart)));
        _drawingRels.Add(new XElement(P + "Relationship", new XAttribute("Id", id), new XAttribute("Type", R.NamespaceName + "/chart"), new XAttribute("Target", id + ".xml")));
        var graphic = new XElement(D + "graphicFrame", new XAttribute("macro", ""),
            new XElement(D + "nvGraphicFramePr", new XElement(D + "cNvPr", new XAttribute("id", number), new XAttribute("name", title)), new XElement(D + "cNvGraphicFramePr")),
            new XElement(D + "xfrm", new XElement(A + "off", new XAttribute("x", 0), new XAttribute("y", 0)), new XElement(A + "ext", new XAttribute("cx", 0), new XAttribute("cy", 0))),
            new XElement(A + "graphic", new XElement(A + "graphicData", new XAttribute("uri", C), new XElement(C + "chart", new XAttribute(R + "id", id)))));
        _anchors.Add(Anchor(row, col, height, width, graphic));
    }
    private static XElement Anchor(int row, int col, int height, int width, XElement body) => new(D + "twoCellAnchor", Marker("from", row - 1, col - 1), Marker("to", row - 1 + height, col - 1 + width), body, new XElement(D + "clientData"));
    private static XElement Marker(string name, int row, int col) => new(D + name, new XElement(D + "col", col), new XElement(D + "colOff", 0), new XElement(D + "row", row), new XElement(D + "rowOff", 0));
    private static XElement Val(XNamespace ns, string name, object value) => new(ns + name, new XAttribute("val", value));
    private static string Column(int number) { var result = ""; while (number > 0) { number--; result = (char)('A' + number % 26) + result; number /= 26; } return result; }
    private static XElement Cell(int row, int col, object value)
    {
        var styled = (Styled)value; var cell = new XElement(S + "c", new XAttribute("r", Column(col) + row), new XAttribute("s", styled.Style));
        if (styled.Value is int or double) cell.Add(new XElement(S + "v", Convert.ToString(styled.Value, CultureInfo.InvariantCulture)));
        else { cell.Add(new XAttribute("t", "inlineStr")); cell.Add(new XElement(S + "is", new XElement(S + "t", styled.Value.ToString()))); }
        return cell;
    }
    private void InstallStyles()
    {
        var styles = Xml("xl/styles.xml"); var root = styles.Root!; var fonts = root.Element(S + "fonts")!; var fills = root.Element(S + "fills")!; var xfs = root.Element(S + "cellXfs")!;
        var named = root.Element(S + "cellStyles");
        var own = named?.Elements().FirstOrDefault(e => (string?)e.Attribute("name") == "VisionMasterWeekly");
        var styleXfs = root.Element(S + "cellStyleXfs")!;
        if (own is not null)
        {
            var ownId = (int)own.Attribute("xfId")!;
            _normalStyle = xfs.Elements().ToList().FindIndex(x => (int?)x.Attribute("xfId") == ownId);
            if (_normalStyle >= 0) { _headerStyle = _normalStyle + 1; _titleStyle = _normalStyle + 2; EnsurePercentStyle(styles); return; }
        }
        var styleId = styleXfs.Elements().Count();
        styleXfs.Add(new XElement(S + "xf", new XAttribute("numFmtId", 0), new XAttribute("fontId", 0), new XAttribute("fillId", 0), new XAttribute("borderId", 0)));
        styleXfs.SetAttributeValue("count", styleXfs.Elements().Count());
        if (named is null) { named = new XElement(S + "cellStyles"); xfs.AddAfterSelf(named); }
        named.Add(new XElement(S + "cellStyle", new XAttribute("name", "VisionMasterWeekly"), new XAttribute("xfId", styleId)));
        named.SetAttributeValue("count", named.Elements().Count());
        var baseFont = fonts.Elements().Count();
        fonts.Add(new XElement(S + "font", new XElement(S + "sz", new XAttribute("val", 11)), new XElement(S + "name", new XAttribute("val", "맑은 고딕"))));
        fonts.Add(new XElement(S + "font", new XElement(S + "b"), new XElement(S + "sz", new XAttribute("val", 14)), new XElement(S + "name", new XAttribute("val", "맑은 고딕"))));
        var fill = fills.Elements().Count();
        fills.Add(new XElement(S + "fill", new XElement(S + "patternFill", new XAttribute("patternType", "solid"), new XElement(S + "fgColor", new XAttribute("rgb", "FFE2E8F0")), new XElement(S + "bgColor", new XAttribute("indexed", 64)))));
        _normalStyle = xfs.Elements().Count(); _headerStyle = _normalStyle + 1; _titleStyle = _normalStyle + 2;
        for (var i = 0; i < 3; i++) xfs.Add(new XElement(S + "xf", new XAttribute("numFmtId", 0), new XAttribute("fontId", baseFont + (i == 2 ? 1 : 0)), new XAttribute("fillId", i == 1 ? fill : 0), new XAttribute("borderId", 0), new XAttribute("xfId", styleId), new XAttribute("applyAlignment", 1), new XElement(S + "alignment", new XAttribute("vertical", "center"), new XAttribute("wrapText", 1))));
        fonts.SetAttributeValue("count", fonts.Elements().Count()); fills.SetAttributeValue("count", fills.Elements().Count()); xfs.SetAttributeValue("count", xfs.Elements().Count());
        Put("xl/styles.xml", styles);
        EnsurePercentStyle(styles);
    }
    private void EnsurePercentStyle(XDocument styles)
    {
        var xfs = styles.Root!.Element(S + "cellXfs")!;
        var prototype = new XElement(xfs.Elements().ElementAt(_normalStyle));
        prototype.SetAttributeValue("numFmtId", 10); prototype.SetAttributeValue("applyNumberFormat", 1);
        _percentStyle = xfs.Elements().ToList().FindIndex(x => XNode.DeepEquals(x, prototype));
        if (_percentStyle < 0) { _percentStyle = xfs.Elements().Count(); xfs.Add(prototype); xfs.SetAttributeValue("count", xfs.Elements().Count()); Put("xl/styles.xml", styles); }
    }
    private static string Resolve(string source, string target) =>
        new Uri(new Uri("http://package/" + source), target).AbsolutePath.TrimStart('/');
    private static string RelPath(string path) => Path.GetDirectoryName(path)!.Replace('\\', '/') + "/_rels/" + Path.GetFileName(path) + ".rels";
    private void RemoveOldTrendParts(string sheet)
    {
        var owned = new HashSet<string>(); var queue = new Queue<string>(); queue.Enqueue(sheet);
        while (queue.TryDequeue(out var part))
        {
            if (!owned.Add(part)) continue;
            var relPath = RelPath(part);
            if (!_parts.ContainsKey(relPath)) continue;
            owned.Add(relPath);
            foreach (var rel in Xml(relPath).Root!.Elements().Where(e => (string?)e.Attribute("TargetMode") != "External"))
                queue.Enqueue(Resolve(part, (string)rel.Attribute("Target")!));
        }
        // Shared images/styles referenced by other sheets remain untouched.
        var protectedParts = new HashSet<string>();
        foreach (var path in _parts.Keys.Where(p => p.EndsWith(".rels") && !owned.Contains(p) && p != "xl/_rels/workbook.xml.rels"))
        {
            var source = path.Replace("/_rels/", "/")[..^5];
            foreach (var rel in Xml(path).Root!.Elements().Where(e => (string?)e.Attribute("TargetMode") != "External"))
                protectedParts.Add(Resolve(source, (string)rel.Attribute("Target")!));
        }
        foreach (var part in owned.Where(p => !protectedParts.Contains(p))) _parts.Remove(part);
        var types = Xml("[Content_Types].xml");
        foreach (var item in types.Root!.Elements().Where(e => owned.Contains(((string?)e.Attribute("PartName"))?.TrimStart('/') ?? "") && !protectedParts.Contains(((string?)e.Attribute("PartName"))?.TrimStart('/') ?? "")).ToArray()) item.Remove();
        Put("[Content_Types].xml", types);
    }
    private void PopulateReport(XDocument workbook, XDocument relations, XDocument types, IReadOnlyList<KickoutHistorySnapshot> history,
        DateOnly start, DateOnly end, IReadOnlyList<string> lines)
    {
        var sheets = workbook.Root!.Element(S + "sheets")!;
        var report = sheets.Elements().FirstOrDefault(e => ((string?)e.Attribute("name"))?.Trim().TrimStart('●').Trim() == "Overkill 리포트");
        string part;
        XDocument doc;
        if (report is null)
        {
            part = "xl/worksheets/vmOverkillReport.xml";
            var rid = UniqueId(relations.Root!);
            relations.Root!.Add(new XElement(P + "Relationship", new XAttribute("Id", rid), new XAttribute("Type", R.NamespaceName + "/worksheet"), new XAttribute("Target", "worksheets/vmOverkillReport.xml")));
            sheets.Add(new XElement(S + "sheet", new XAttribute("name", "●Overkill 리포트"), new XAttribute("sheetId", sheets.Elements().Max(e => (int?)e.Attribute("sheetId") ?? 0) + 1), new XAttribute(R + "id", rid)));
            doc = new XDocument(new XElement(S + "worksheet", new XElement(S + "cols",
                new XElement(S + "col", new XAttribute("min", 1), new XAttribute("max", 7), new XAttribute("width", 13), new XAttribute("customWidth", 1)),
                new XElement(S + "col", new XAttribute("min", 8), new XAttribute("max", 10), new XAttribute("width", 38), new XAttribute("customWidth", 1)),
                new XElement(S + "col", new XAttribute("min", 11), new XAttribute("max", 11), new XAttribute("width", 75), new XAttribute("customWidth", 1))), new XElement(S + "sheetData")));
            AddType(types, part, "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
        }
        else
        {
            var relationship = relations.Root!.Elements().Single(e => (string?)e.Attribute("Id") == (string?)report.Attribute(R + "id"));
            part = Resolve("xl/workbook.xml", (string)relationship.Attribute("Target")!); doc = Xml(part);
        }
        var styles = Xml("xl/styles.xml");
        var formats = styles.Root!.Element(S + "cellXfs")!;
        int TopStyle(int original)
        {
            var format = new XElement(formats.Elements().ElementAt(original));
            format.SetAttributeValue("applyAlignment", 1);
            var alignment = format.Element(S + "alignment");
            if (alignment is null) { alignment = new XElement(S + "alignment"); format.Add(alignment); }
            alignment.SetAttributeValue("vertical", "top"); alignment.SetAttributeValue("wrapText", 1);
            var index = formats.Elements().ToList().FindIndex(x => XNode.DeepEquals(x, format));
            if (index >= 0) return index;
            index = formats.Elements().Count(); formats.Add(format); formats.SetAttributeValue("count", index + 1);
            return index;
        }
        var data = doc.Root!.Element(S + "sheetData")!;
        XElement Row(int index)
        {
            var row = data.Elements(S + "row").FirstOrDefault(r => (int?)r.Attribute("r") == index);
            if (row is not null) return row;
            row = new XElement(S + "row", new XAttribute("r", index), new XAttribute("ht", 165), new XAttribute("customHeight", 1));
            var after = data.Elements().FirstOrDefault(x => (int?)x.Attribute("r") > index);
            if (after is null) data.Add(row); else after.AddBeforeSelf(row);
            return row;
        }
        void Set(int rowIndex, int col, object? value, bool header = false)
        {
            var row = Row(rowIndex); var address = Column(col) + rowIndex;
            var cell = row.Elements(S + "c").FirstOrDefault(c => (string?)c.Attribute("r") == address);
            var replacement = Cell(rowIndex, col, new Styled(value ?? "—", header ? _headerStyle : _normalStyle));
            if (cell is not null)
            {
                if (cell.Attribute("s") is { } style) replacement.SetAttributeValue("s", style.Value);
                cell.ReplaceWith(replacement);
            }
            else
            {
                var after = row.Elements(S + "c").FirstOrDefault(c => string.CompareOrdinal(((string?)c.Attribute("r"))?.TrimEnd("0123456789".ToCharArray()), Column(col)) > 0);
                if (after is null) row.Add(replacement); else after.AddBeforeSelf(replacement);
            }
            if (!header && col is >= 8 and <= 10)
                replacement.SetAttributeValue("s", TopStyle((int?)replacement.Attribute("s") ?? _normalStyle));
        }
        var headers = new[] { "호기", "비전", "검사일", "투입셀", "NG", "실불량", "과검", "과검항목 1 / 사진", "과검항목 2 / 사진", "과검항목 3 / 사진" };
        for (var c = 0; c < headers.Length; c++) Set(2, c + 1, headers[c], true);
        if (report is null) { Set(2, 11, "사유 및 대책", true); Row(2).SetAttributeValue("ht", 24); }
        var totals = OverkillTrendService.Period(OverkillTrendService.Kickout(history, start, end), lines);
        for (var i = 0; i < OverkillTrendService.Lines.Length; i++)
        {
            var line = OverkillTrendService.Lines[i]; var row = Row(i + 3); row.SetAttributeValue("hidden", lines.Contains(line) ? null : 1);
            if (!lines.Contains(line)) continue;
            var summary = WeeklyReportData.Summary(history, line, start, end);
            var top = totals.Where(t => t.Line == line && t.Count > 0).OrderByDescending(t => t.Count).ThenBy(t => t.Field).Take(3).ToArray();
            object?[] values = [line.Split('(')[0], line.Contains("(+)") ? "Welding(+)" : "Welding(-)", $"{start:M/d} - {end:M/d}",
                summary.Inspected, summary.Ng, summary.Real, summary.Overkill];
            for (var c = 0; c < values.Length; c++) Set(i + 3, c + 1, values[c]);
            for (var c = 0; c < 3; c++) Set(i + 3, c + 8, c < top.Length ? $"{top[c].Field} · {top[c].Count}건" : "");
            // K and all drawing relationships stay untouched: handwritten measures and pictures are user-owned.
        }
        doc.Root.Element(S + "dimension")?.SetAttributeValue("ref", "A1:K10");
        Put("xl/styles.xml", styles);
        Put(part, doc);
    }
    private XDocument Xml(string name) => XDocument.Load(new MemoryStream(_parts[name]));
    private void Put(string name, XDocument doc) { using var stream = new MemoryStream(); doc.Save(stream); _parts[name] = stream.ToArray(); }
    private static string UniqueId(XElement root) { int n = 1; while (root.Elements().Any(r => (string?)r.Attribute("Id") == "rId" + n)) n++; return "rId" + n; }
    private static void AddType(XDocument types, string path, string type) => types.Root!.Add(new XElement(T + "Override", new XAttribute("PartName", "/" + path), new XAttribute("ContentType", type)));
}
