using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
namespace KickoutMonitor.Infrastructure;

// Unrelated workbook parts are copied verbatim. Only our sheet, drawings and charts are replaced.
public sealed class WeeklyReportWorkbook
{
    public const string SheetName = "●Kickout 과검";
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
    private int _chartNumber, _imageNumber, _dataRow = 1;
    private int _normalStyle, _headerStyle, _titleStyle;
    public void Update(string path, IReadOnlyList<KickoutHistorySnapshot> history, DateOnly start, DateOnly end, IReadOnlyList<WeeklyReportCase> examples)
    {
        if (end < start || end.DayNumber - start.DayNumber > 3660) throw new ArgumentException("시작일과 종료일을 확인하세요.");
        if (!File.Exists(path)) throw new FileNotFoundException("주간 보고 파일을 선택하세요.", path);
        _cells.Clear(); _merges.Clear(); _breaks.Clear(); _anchors.Clear(); _drawingRels.Clear(); _parts.Clear(); _chartNumber = 0; _imageNumber = 0; _dataRow = 1;
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            using (var source = ZipFile.OpenRead(path))
                foreach (var entry in source.Entries.Where(e => !e.FullName.StartsWith(Prefix, StringComparison.Ordinal)))
                { using var input = entry.Open(); using var buffer = new MemoryStream(); input.CopyTo(buffer); _parts[entry.FullName] = buffer.ToArray(); }
            var wb = Xml("xl/workbook.xml"); var rel = Xml("xl/_rels/workbook.xml.rels"); var types = Xml("[Content_Types].xml");
            var sheets = wb.Root!.Element(S + "sheets")!;
            var existing = sheets.Elements(S + "sheet").FirstOrDefault(s => (string?)s.Attribute("name") == SheetName);
            if (existing is not null)
            {
                var oldRel = rel.Root!.Elements().FirstOrDefault(r => (string?)r.Attribute("Id") == (string?)existing.Attribute(R + "id"));
                if ((string?)oldRel?.Attribute("Target") != "vmWeekly/sheet.xml")
                    throw new InvalidOperationException("같은 이름의 사용자 시트가 있습니다. 이름을 변경한 뒤 다시 시도하세요: " + SheetName);
            }
            else
            {
                var rid = UniqueId(rel.Root!);
                rel.Root!.Add(new XElement(P + "Relationship", new XAttribute("Id", rid), new XAttribute("Type", R.NamespaceName + "/worksheet"), new XAttribute("Target", "vmWeekly/sheet.xml")));
                sheets.Add(new XElement(S + "sheet", new XAttribute("name", SheetName), new XAttribute("sheetId", sheets.Elements().Max(e => (int?)e.Attribute("sheetId") ?? 0) + 1), new XAttribute(R + "id", rid)));
            }
            foreach (var t in types.Root!.Elements().Where(e => ((string?)e.Attribute("PartName"))?.StartsWith("/" + Prefix) == true).ToArray()) t.Remove();
            InstallStyles();
            Build(history, start, end, examples);
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
            Put("xl/workbook.xml", wb); Put("xl/_rels/workbook.xml.rels", rel); Put("[Content_Types].xml", types);
            using (var output = ZipFile.Open(temp, ZipArchiveMode.Create))
                foreach (var part in _parts) { using var target = output.CreateEntry(part.Key).Open(); target.Write(part.Value); }
            using (var check = ZipFile.OpenRead(temp))
                foreach (var entry in check.Entries.Where(e => e.FullName.EndsWith(".xml") || e.FullName.EndsWith(".rels"))) { using var stream = entry.Open(); XDocument.Load(stream); }
            File.Replace(temp, path, path + ".bak", true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private sealed record Styled(object Value, int Style);
    private void Text(int row, int col, object? value, int? style = null) { if (!_cells.ContainsKey(row)) _cells[row] = []; _cells[row][col] = new Styled(value ?? "—", style ?? _normalStyle); }
    private void Span(int row, int col, int width, int height = 1)
    {
        _merges.Add(new XElement(S + "mergeCell", new XAttribute("ref", Column(col) + row + ":" + Column(col + width - 1) + (row + height - 1))));
    }
    private void Build(IReadOnlyList<KickoutHistorySnapshot> history, DateOnly start, DateOnly end, IReadOnlyList<WeeklyReportCase> examples)
    {
        Text(1, 1, "Kickout 과검 검토 보고", _titleStyle);
        Text(2, 1, $"{start:yyyy-MM-dd} ~ {end:yyyy-MM-dd}    업데이트: {DateTime.Now:yyyy-MM-dd HH:mm}");
        Text(3, 1, "자료 없음은 — / 미검토는 실불량에서 제외 / 일별·주별 건수는 생성 보고서 ALL 기준");
        Span(1, 1, 26); Span(2, 1, 26); Span(3, 1, 26);
        var headers = new[] { "호기/극성", "투입셀", "NG", "리뷰 실불량", "과검", "미검토", "자료 일수" };
        for (var c = 0; c < headers.Length; c++) { Text(5, 1 + c * 2, headers[c], _headerStyle); Span(5, 1 + c * 2, 2); }
        for (var i = 0; i < 8; i++)
        {
            var s = WeeklyReportData.Summary(history, OverkillTrendService.Lines[i], start, end);
            object?[] values = [s.Line, s.Inspected, s.Ng, s.Real, s.Overkill, s.Pending, s.CoveredDays];
            for (var c = 0; c < values.Length; c++) { Text(6 + i, 1 + c * 2, values[c]); Span(6 + i, 1 + c * 2, 2); }
        }
        var data = OverkillTrendService.Kickout(history, start, end);
        var totals = OverkillTrendService.Period(data, OverkillTrendService.Lines);
        int row = 16;
        Span(row, 1, 26); Text(row++, 1, "선택 기간 · 일별 과검 / 과검 항목", _titleStyle);
        for (var pair = 0; pair < 4; pair++)
        {
            if (pair == 2) _breaks.Add(row);
            var height = 20;
            for (var side = 0; side < 2; side++)
            {
                int i = pair * 2 + side, col = side * 13 + 1; var line = OverkillTrendService.Lines[i];
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
        for (var pair = 0; pair < 4; pair++)
        {
            if (pair == 2) _breaks.Add(row);
            for (var side = 0; side < 2; side++)
            {
                int i = pair * 2 + side, col = side * 13 + 1; var line = OverkillTrendService.Lines[i];
                var weeks = WeeklyReportData.Weeks(history, line, end);
                Chart(line + " · 주별 과검", weeks.Select(w => w.Start.ToString("MM/dd") + (w.Partial ? "*" : "")).ToArray(), weeks.Select(w => w.Count).ToArray(), OverkillTrendService.Colors[i], row, col, 9, 17);
                Span(row, col + 9, 2); Text(row, col + 9, "주 시작", _headerStyle); Text(row, col + 11, "건수", _headerStyle); Text(row, col + 12, "자료일", _headerStyle);
                for (var w = 0; w < 8; w++) { Span(row + w + 1, col + 9, 2); Text(row + w + 1, col + 9, weeks[w].Start.ToString("MM/dd") + (weeks[w].Partial ? "*" : "")); Text(row + w + 1, col + 11, weeks[w].Count); Text(row + w + 1, col + 12, weeks[w].CoveredDays); }
                Span(row + 10, col + 9, 4); Text(row + 10, col + 9, "* 종료일까지만 집계");
            }
            row += 19;
        }
        _breaks.Add(row); var caseGroup = 0;
        Span(row, 1, 26); Text(row++, 1, "대표 과검 사례 · 사유 및 대책", _titleStyle);
        foreach (var group in examples.Where(x => x.Start == start && x.End == end).GroupBy(x => x.Line).OrderBy(g => Array.IndexOf(OverkillTrendService.Lines, g.Key)))
        {
            if (caseGroup++ > 0 && caseGroup % 2 == 1) _breaks.Add(row);
            Span(row, 1, 26); Text(row++, 1, group.Key, _headerStyle); var n = 0;
            foreach (var item in group.OrderBy(x => x.Order).ThenBy(x => x.Id).Take(3))
            {
                var col = n++ * 9 + 1; Span(row, col, 8); Text(row, col, $"{item.Day:yyyy-MM-dd} · {item.Field}", _headerStyle);
                if (File.Exists(item.LocalImage) && new FileInfo(item.LocalImage).Length > 0) Picture(item.LocalImage, row + 1, col);
                else Text(row + 2, col, "이미지 없음: " + item.Error);
                Span(row + 13, col, 8, 2); Span(row + 15, col, 8, 3); Text(row + 13, col, "사유: " + item.Reason); Text(row + 15, col, "대책: " + item.Action);
            }
            row += 19;
        }
        Text(row + 1, 1, "자동 생성 시트입니다. 대표 사례는 앱에서 수정하세요. 기존 사용자 시트는 보존됩니다.");
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
    private void Picture(string path, int row, int col)
    {
        var id = "image" + ++_imageNumber; var name = id + Path.GetExtension(path).ToLowerInvariant(); _parts[Prefix + name] = File.ReadAllBytes(path);
        _drawingRels.Add(new XElement(P + "Relationship", new XAttribute("Id", id), new XAttribute("Type", R.NamespaceName + "/image"), new XAttribute("Target", name)));
        var pic = new XElement(D + "pic", new XElement(D + "nvPicPr", new XElement(D + "cNvPr", new XAttribute("id", 1000 + _imageNumber), new XAttribute("name", id)), new XElement(D + "cNvPicPr", new XElement(A + "picLocks", new XAttribute("noChangeAspect", 1)))),
            new XElement(D + "blipFill", new XElement(A + "blip", new XAttribute(R + "embed", id)), new XElement(A + "stretch", new XElement(A + "fillRect"))),
            new XElement(D + "spPr", new XElement(A + "prstGeom", new XAttribute("prst", "rect"), new XElement(A + "avLst"))));
        _anchors.Add(Anchor(row, col, 11, 8, pic));
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
            if (_normalStyle >= 0) { _headerStyle = _normalStyle + 1; _titleStyle = _normalStyle + 2; return; }
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
    }
    private XDocument Xml(string name) => XDocument.Load(new MemoryStream(_parts[name]));
    private void Put(string name, XDocument doc) { using var stream = new MemoryStream(); doc.Save(stream); _parts[name] = stream.ToArray(); }
    private static string UniqueId(XElement root) { int n = 1; while (root.Elements().Any(r => (string?)r.Attribute("Id") == "rId" + n)) n++; return "rId" + n; }
    private static void AddType(XDocument types, string path, string type) => types.Root!.Add(new XElement(T + "Override", new XAttribute("PartName", "/" + path), new XAttribute("ContentType", type)));
}
