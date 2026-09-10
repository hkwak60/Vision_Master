using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using C = DocumentFormat.OpenXml.Drawing.Charts;
using Xdr = DocumentFormat.OpenXml.Drawing.Spreadsheet;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace KickoutMonitor.App;

public static class ExcelRetryReportWriter
{
    private const string SheetName = "Image NG Retry";
    private const string AnodeColor = "F59E0B";
    private const string CathodeColor = "3B82F6";

    public static void Write(string path, IReadOnlyCollection<LogRetryResult> results)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new S.Workbook();
        var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = CreateStyles();

        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var sheetData = new S.SheetData();
        var worksheet = new S.Worksheet(
            new S.SheetProperties(new S.OutlineProperties { SummaryBelow = false }),
            new S.SheetViews(new S.SheetView(new S.Pane { VerticalSplit = 1D, TopLeftCell = "A2", ActivePane = S.PaneValues.BottomLeft, State = S.PaneStateValues.Frozen }) { WorkbookViewId = 0U }),
            new S.Columns(
                Column(1, 1, 13), Column(2, 2, 15), Column(3, 3, 14), Column(4, 4, 13),
                Column(5, 5, 18), Column(6, 6, 3), Column(7, 7, 13), Column(8, 9, 12)),
            sheetData);
        worksheetPart.Worksheet = worksheet;

        sheetData.Append(Row(1,
            Text("Date", 1), Text("Time", 1), Text("Electrode", 1), Text("Retry Count", 1), Text("Duration Seconds", 1),
            Blank(), Text("Date", 1), Text("Anode", 1), Text("Cathode", 1)));

        uint rowIndex = 2;
        foreach (var item in results.OrderBy(x => x.Started))
        {
            var anode = item.Side == "Anode";
            var cathode = item.Side == "Cathode";
            var dateStyle = anode ? 7U : cathode ? 8U : 2U;
            var timeStyle = anode ? 9U : cathode ? 10U : 3U;
            var textStyle = anode ? 5U : cathode ? 6U : 0U;
            var numberStyle = anode ? 13U : cathode ? 14U : 0U;
            var decimalStyle = anode ? 11U : cathode ? 12U : 4U;
            sheetData.Append(Row(rowIndex++,
                Number(item.Started.Date.ToOADate(), dateStyle), Number(item.Started.TimeOfDay.TotalDays, timeStyle),
                Text(item.Side, textStyle), Number(item.MaximumCount, numberStyle), Number(item.Duration.TotalSeconds, decimalStyle)));
        }

        var daily = results.GroupBy(x => x.Date).OrderBy(x => x.Key)
            .Select(x => new { Date = x.Key, Anode = x.Count(y => y.Side == "Anode"), Cathode = x.Count(y => y.Side == "Cathode") }).ToList();
        for (var index = 0; index < daily.Count; index++)
        {
            var rowNumber = (uint)index + 2;
            var existing = sheetData.Elements<S.Row>().FirstOrDefault(x => x.RowIndex?.Value == rowNumber);
            if (existing is null) { existing = new S.Row { RowIndex = rowNumber }; sheetData.Append(existing); }
            PadToColumn(existing, 7);
            existing.Append(Number(daily[index].Date.ToOADate(), 2), Number(daily[index].Anode, 15), Number(daily[index].Cathode, 16));
        }

        AssignCellReferences(sheetData);

        worksheet.Append(new S.AutoFilter { Reference = $"A1:E{Math.Max(1, results.Count + 1)}" });

        if (daily.Count > 0) AddCharts(worksheetPart, daily.Count + 1);

        var sheets = workbookPart.Workbook.AppendChild(new S.Sheets());
        sheets.Append(new S.Sheet { Id = workbookPart.GetIdOfPart(worksheetPart), SheetId = 1U, Name = SheetName });
        workbookPart.Workbook.Save();
    }

    private static void AddCharts(WorksheetPart worksheetPart, int lastDailyRow)
    {
        var drawingsPart = worksheetPart.AddNewPart<DrawingsPart>();
        var drawing = new Xdr.WorksheetDrawing();
        drawing.Append(CreateChartAnchor(drawingsPart, "Anode retry frequency", "H", AnodeColor, 1, 15, lastDailyRow, 1U));
        drawing.Append(CreateChartAnchor(drawingsPart, "Cathode retry frequency", "I", CathodeColor, 16, 30, lastDailyRow, 2U));
        drawingsPart.WorksheetDrawing = drawing;
        worksheetPart.Worksheet.Append(new S.Drawing { Id = worksheetPart.GetIdOfPart(drawingsPart) });
    }

    private static Xdr.TwoCellAnchor CreateChartAnchor(DrawingsPart drawingsPart, string title, string valueColumn, string color, int topRow, int bottomRow, int lastRow, uint id)
    {
        var chartPart = drawingsPart.AddNewPart<ChartPart>();
        var categoryFormula = $"'{SheetName}'!$G$2:$G${lastRow}";
        var valueFormula = $"'{SheetName}'!${valueColumn}$2:${valueColumn}${lastRow}";
        var seriesFormula = $"'{SheetName}'!${valueColumn}$1";
        const uint categoryAxisId = 48650112U;
        var valueAxisId = 48650112U + id;

        var series = new C.LineChartSeries(
            new C.Index { Val = 0U }, new C.Order { Val = 0U },
            new C.SeriesText(new C.StringReference(new C.Formula(seriesFormula))),
            new C.ChartShapeProperties(new A.Outline(new A.SolidFill(new A.RgbColorModelHex { Val = color })) { Width = 28575 }),
            new C.Marker(new C.Symbol { Val = C.MarkerStyleValues.None }),
            new C.CategoryAxisData(new C.NumberReference(new C.Formula(categoryFormula))),
            new C.Values(new C.NumberReference(new C.Formula(valueFormula))));
        var lineChart = new C.LineChart(new C.Grouping { Val = C.GroupingValues.Standard }, new C.VaryColors { Val = false }, series,
            new C.DataLabels(new C.ShowLegendKey { Val = false }, new C.ShowValue { Val = false }),
            new C.AxisId { Val = categoryAxisId }, new C.AxisId { Val = valueAxisId });
        var categoryAxis = new C.DateAxis(new C.AxisId { Val = categoryAxisId }, new C.Scaling(new C.Orientation { Val = C.OrientationValues.MinMax }),
            new C.Delete { Val = false }, new C.AxisPosition { Val = C.AxisPositionValues.Bottom }, new C.NumberingFormat { FormatCode = "mmm d", SourceLinked = false },
            new C.TickLabelPosition { Val = C.TickLabelPositionValues.NextTo }, new C.CrossingAxis { Val = valueAxisId }, new C.Crosses { Val = C.CrossesValues.AutoZero },
            new C.AutoLabeled { Val = true }, new C.LabelOffset { Val = (ushort)100 }, new C.BaseTimeUnit { Val = C.TimeUnitValues.Days });
        var valueAxis = new C.ValueAxis(new C.AxisId { Val = valueAxisId }, new C.Scaling(new C.Orientation { Val = C.OrientationValues.MinMax }),
            new C.Delete { Val = false }, new C.AxisPosition { Val = C.AxisPositionValues.Left }, new C.MajorGridlines(),
            new C.NumberingFormat { FormatCode = "0", SourceLinked = false }, new C.TickLabelPosition { Val = C.TickLabelPositionValues.NextTo },
            new C.CrossingAxis { Val = categoryAxisId }, new C.Crosses { Val = C.CrossesValues.AutoZero }, new C.CrossBetween { Val = C.CrossBetweenValues.Between });
        chartPart.ChartSpace = new C.ChartSpace(new C.EditingLanguage { Val = "en-US" }, new C.Chart(
            CreateChartTitle(title), new C.PlotArea(new C.Layout(), lineChart, categoryAxis, valueAxis), new C.PlotVisibleOnly { Val = true }, new C.DisplayBlanksAs { Val = C.DisplayBlanksAsValues.Gap }));

        var frame = new Xdr.GraphicFrame(
            new Xdr.NonVisualGraphicFrameProperties(new Xdr.NonVisualDrawingProperties { Id = id, Name = title }, new Xdr.NonVisualGraphicFrameDrawingProperties()),
            new Xdr.Transform(), new A.Graphic(new A.GraphicData(new C.ChartReference { Id = drawingsPart.GetIdOfPart(chartPart) }) { Uri = "http://schemas.openxmlformats.org/drawingml/2006/chart" }));
        return new Xdr.TwoCellAnchor(FromMarker(9, topRow), ToMarker(17, bottomRow), frame, new Xdr.ClientData());
    }

    private static C.Title CreateChartTitle(string title) => new(new C.ChartText(new C.RichText(
        new A.BodyProperties(), new A.ListStyle(), new A.Paragraph(new A.Run(new A.RunProperties { Language = "en-US", FontSize = 1400 }, new A.Text(title))))), new C.Overlay { Val = false });
    private static Xdr.FromMarker FromMarker(int column, int row) => new(new Xdr.ColumnId(column.ToString()), new Xdr.ColumnOffset("0"), new Xdr.RowId(row.ToString()), new Xdr.RowOffset("0"));
    private static Xdr.ToMarker ToMarker(int column, int row) => new(new Xdr.ColumnId(column.ToString()), new Xdr.ColumnOffset("0"), new Xdr.RowId(row.ToString()), new Xdr.RowOffset("0"));

    private static S.Stylesheet CreateStyles()
    {
        var numbering = new S.NumberingFormats(
            new S.NumberingFormat { NumberFormatId = 164U, FormatCode = "yyyy-mm-dd" },
            new S.NumberingFormat { NumberFormatId = 165U, FormatCode = "hh:mm:ss.000" },
            new S.NumberingFormat { NumberFormatId = 166U, FormatCode = "0.000" }) { Count = 3U };
        var fonts = new S.Fonts(new S.Font(), new S.Font(new S.Bold(), new S.Color { Rgb = "FFFFFFFF" })) { Count = 2U };
        var fills = new S.Fills(new S.Fill(new S.PatternFill { PatternType = S.PatternValues.None }), new S.Fill(new S.PatternFill { PatternType = S.PatternValues.Gray125 }),
            Fill("263746"), Fill("FFF4E5"), Fill("EAF4FF"), Fill("FDE7C3"), Fill("D9ECFF")) { Count = 7U };
        var borders = new S.Borders(new S.Border(), new S.Border(new S.LeftBorder { Style = S.BorderStyleValues.Thin, Color = new S.Color { Rgb = "FFD8DEE4" } }, new S.RightBorder { Style = S.BorderStyleValues.Thin, Color = new S.Color { Rgb = "FFD8DEE4" } }, new S.TopBorder { Style = S.BorderStyleValues.Thin, Color = new S.Color { Rgb = "FFD8DEE4" } }, new S.BottomBorder { Style = S.BorderStyleValues.Thin, Color = new S.Color { Rgb = "FFD8DEE4" } })) { Count = 2U };
        var formats = new S.CellFormats(
            Format(), Format(1, 2, 1, 0, true), Format(0, 0, 1, 164), Format(0, 0, 1, 165), Format(0, 0, 1, 166),
            Format(0, 3, 1), Format(0, 4, 1), Format(0, 3, 1, 164), Format(0, 4, 1, 164), Format(0, 3, 1, 165), Format(0, 4, 1, 165),
            Format(0, 3, 1, 166), Format(0, 4, 1, 166), Format(0, 3, 1), Format(0, 4, 1), Format(0, 5, 1), Format(0, 6, 1)) { Count = 17U };
        return new S.Stylesheet(numbering, fonts, fills, borders, formats);
    }

    private static S.CellFormat Format(uint font = 0, uint fill = 0, uint border = 0, uint number = 0, bool alignment = false) => new()
    { FontId = font, FillId = fill, BorderId = border, NumberFormatId = number, ApplyFill = fill > 0, ApplyBorder = border > 0, ApplyNumberFormat = number > 0, ApplyAlignment = alignment, Alignment = alignment ? new S.Alignment { Horizontal = S.HorizontalAlignmentValues.Center } : null };
    private static S.Fill Fill(string rgb) => new(new S.PatternFill(new S.ForegroundColor { Rgb = "FF" + rgb }, new S.BackgroundColor { Indexed = 64U }) { PatternType = S.PatternValues.Solid });
    private static S.Column Column(uint min, uint max, double width) => new() { Min = min, Max = max, Width = width, CustomWidth = true };
    private static S.Row Row(uint index, params S.Cell[] cells) { var row = new S.Row { RowIndex = index }; row.Append(cells); return row; }
    private static S.Cell Text(string value, uint style = 0) => new() { DataType = S.CellValues.InlineString, InlineString = new S.InlineString(new S.Text(value)), StyleIndex = style };
    private static S.Cell Number(double value, uint style = 0) => new() { CellValue = new S.CellValue(value.ToString(CultureInfo.InvariantCulture)), DataType = S.CellValues.Number, StyleIndex = style };
    private static S.Cell Blank() => new();
    private static void PadToColumn(S.Row row, int column) { while (row.Elements<S.Cell>().Count() < column - 1) row.Append(new S.Cell()); }
    private static void AssignCellReferences(S.SheetData sheetData)
    {
        foreach (var row in sheetData.Elements<S.Row>())
        {
            var column = 1;
            foreach (var cell in row.Elements<S.Cell>()) cell.CellReference = ColumnName(column++) + row.RowIndex!.Value;
        }
    }
    private static string ColumnName(int column)
    {
        var name = string.Empty;
        while (column > 0) { column--; name = (char)('A' + column % 26) + name; column /= 26; }
        return name;
    }
}
