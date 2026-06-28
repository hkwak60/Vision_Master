using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using KickoutMonitor.Application;
using KickoutMonitor.Domain;

namespace KickoutMonitor.Infrastructure;

public sealed class SummaryReportWriter : ISummaryReportWriter
{
    private readonly AppStorage _storage;

    public SummaryReportWriter(AppStorage storage)
    {
        _storage = storage;
    }

    public async Task<string> WriteAsync(
        DateOnly reportDate,
        DateTime windowStart,
        DateTime windowEndExclusive,
        IReadOnlyList<SummaryReportRow> rows,
        IReadOnlyList<SummaryDetailRow> details,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_storage.Summary);
        var folderName = $"NG_Summary_{reportDate:yyyyMMdd}";
        var reportFolder = Path.Combine(_storage.Summary, folderName);
        RefreshReportFolder(reportFolder);

        var summaryPath = Path.Combine(reportFolder, $"{folderName}.xlsx");
        WriteSummaryWorkbook(summaryPath, folderName, reportDate, windowStart, windowEndExclusive, rows);

        await WriteDetailsAsync(reportFolder, details, cancellationToken);
        CopyReviewedImages(reportFolder, details, cancellationToken);
        return summaryPath;
    }

    private static void WriteSummaryWorkbook(
        string path,
        string sheetName,
        DateOnly reportDate,
        DateTime windowStart,
        DateTime windowEndExclusive,
        IReadOnlyList<SummaryReportRow> rows)
    {
        WriteWorkbookPackage(
            path,
            sheetName,
            SummaryWorksheetXml(reportDate, windowStart, windowEndExclusive, rows),
            SummaryStylesXml());
    }

    private static string SummaryWorksheetXml(
        DateOnly reportDate,
        DateTime windowStart,
        DateTime windowEndExclusive,
        IReadOnlyList<SummaryReportRow> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        builder.AppendLine("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");
        builder.AppendLine("""<cols><col min="2" max="2" width="13.33203125" customWidth="1"/></cols>""");
        builder.AppendLine("<sheetData>");
        builder.Append("<row r=\"1\">");
        AppendTextCell(builder, 0, 1, "Report Date", 0);
        AppendNumberCell(builder, 1, 1, reportDate.ToDateTime(TimeOnly.MinValue).ToOADate(), 1);
        builder.AppendLine("</row>");
        builder.Append("<row r=\"2\">");
        AppendTextCell(builder, 0, 2, "Window Start", 0);
        AppendNumberCell(builder, 1, 2, windowStart.ToOADate(), 2);
        builder.AppendLine("</row>");
        builder.Append("<row r=\"3\">");
        AppendTextCell(builder, 0, 3, "Window End", 0);
        AppendNumberCell(builder, 1, 3, windowEndExclusive.AddMinutes(-1).ToOADate(), 2);
        builder.AppendLine("</row>");
        builder.AppendLine("""<row r="4"></row>""");
        builder.Append("<row r=\"5\">");
        var headers = new[] { "Line", "검출 항목", "총", "배출", "실 불량", "과검", "배출(%)", "실불량(%)" };
        for (var column = 0; column < headers.Length; column++)
        {
            AppendTextCell(builder, column, 5, headers[column], 0);
        }
        builder.AppendLine("</row>");

        for (var index = 0; index < rows.Count; index++)
        {
            var reportRow = rows[index];
            var excelRow = index + 6;
            var isAll = reportRow.Defect.Equals("ALL", StringComparison.OrdinalIgnoreCase);
            builder.Append(CultureInfo.InvariantCulture, $"<row r=\"{excelRow}\">");
            if (isAll)
            {
                AppendTextCell(builder, 0, excelRow, reportRow.LinePolarity, 4);
                AppendTextCell(builder, 1, excelRow, reportRow.Defect, 4);
                AppendNumberCell(builder, 2, excelRow, reportRow.TotalInspected, 4);
            }
            else
            {
                AppendTextCell(builder, 1, excelRow, reportRow.Defect, 0);
            }

            AppendNumberCell(builder, 3, excelRow, reportRow.InitialNg, isAll ? 4 : 0);
            AppendNumberCell(builder, 4, excelRow, reportRow.RealNg, isAll ? 4 : 0);
            AppendNumberCell(builder, 5, excelRow, reportRow.Overkill, isAll ? 4 : 0);
            AppendNumberCell(builder, 6, excelRow, reportRow.InitialNgRate, isAll ? 5 : 3);
            AppendNumberCell(builder, 7, excelRow, reportRow.ConfirmedNgRate, isAll ? 5 : 3);
            builder.AppendLine("</row>");
        }

        builder.AppendLine("</sheetData>");
        builder.AppendLine("</worksheet>");

        return builder.ToString();
    }

    private static async Task WriteDetailsAsync(
        string reportFolder,
        IReadOnlyList<SummaryDetailRow> details,
        CancellationToken cancellationToken)
    {
        foreach (var group in details.GroupBy(x => x.LinePolarity, StringComparer.OrdinalIgnoreCase))
        {
            var safeLine = SafeName(group.Key);
            var csvPath = Path.Combine(reportFolder, $"NG_Details_{safeLine}.csv");
            var xlsxPath = Path.Combine(reportFolder, $"NG_Details_{safeLine}.xlsx");
            var ordered = group.ToArray();
            var headers = ordered.FirstOrDefault()?.Headers ?? [];
            await File.WriteAllTextAsync(
                csvPath,
                BuildDetailsCsv(headers, ordered),
                Encoding.UTF8,
                cancellationToken);
            WriteDetailsWorkbook(xlsxPath, headers, ordered);
        }
    }

    private static string BuildDetailsCsv(
        IReadOnlyList<string> headers,
        IReadOnlyList<SummaryDetailRow> details)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(",", headers.Select(Escape)));
        foreach (var detail in details)
        {
            builder.AppendLine(string.Join(",", detail.Values.Select(Escape)));
        }

        return builder.ToString();
    }

    private static void CopyReviewedImages(
        string reportFolder,
        IReadOnlyList<SummaryDetailRow> details,
        CancellationToken cancellationToken)
    {
        foreach (var detail in details)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (detail.Decision is not (ReviewDecision.Overkill or ReviewDecision.MultiDefectNg)
                || string.IsNullOrWhiteSpace(detail.LocalFolder)
                || !Directory.Exists(detail.LocalFolder))
            {
                continue;
            }

            var classification = detail.Decision == ReviewDecision.Overkill
                ? "OVERKILL"
                : "NG";
            var defect = detail.Decision == ReviewDecision.MultiDefectNg
                ? SummaryReportService.MultiNgDefectName
                : KickoutRules.NormalizeDefect(detail.Defect);
            var destinationParent = Path.Combine(
                reportFolder,
                classification,
                SafeName(detail.LinePolarity),
                defect);
            Directory.CreateDirectory(destinationParent);
            var destination = Path.Combine(destinationParent, Path.GetFileName(detail.LocalFolder));
            if (Directory.Exists(destination)) Directory.Delete(destination, true);
            CopyDirectory(detail.LocalFolder, destination, cancellationToken);
        }
    }

    private static void CopyDirectory(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    private static void RefreshReportFolder(string reportFolder)
    {
        if (Directory.Exists(reportFolder)) Directory.Delete(reportFolder, true);
        Directory.CreateDirectory(reportFolder);
        Directory.CreateDirectory(Path.Combine(reportFolder, "NG"));
        Directory.CreateDirectory(Path.Combine(reportFolder, "OVERKILL"));
    }

    private static void WriteDetailsWorkbook(
        string path,
        IReadOnlyList<string> headers,
        IReadOnlyList<SummaryDetailRow> details)
    {
        WriteWorkbookPackage(path, "NG_Details", WorksheetXml(headers, details), StylesXml());
    }

    private static void WriteWorkbookPackage(
        string path,
        string sheetName,
        string worksheetXml,
        string stylesXml)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(
            archive,
            "[Content_Types].xml",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
              <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
              <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
            </Types>
            """);
        WriteEntry(
            archive,
            "_rels/.rels",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """);
        WriteEntry(
            archive,
            "xl/_rels/workbook.xml.rels",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
              <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
            </Relationships>
            """);
        WriteEntry(
            archive,
            "xl/workbook.xml",
            $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets><sheet name="{SecurityElement.Escape(sheetName)}" sheetId="1" r:id="rId1"/></sheets>
            </workbook>
            """);
        WriteEntry(archive, "xl/styles.xml", stylesXml);
        WriteEntry(archive, "xl/worksheets/sheet1.xml", worksheetXml);
    }

    private static string WorksheetXml(
        IReadOnlyList<string> headers,
        IReadOnlyList<SummaryDetailRow> details)
    {
        var builder = new StringBuilder();
        builder.AppendLine("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        builder.AppendLine("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");
        builder.AppendLine("<sheetData>");
        builder.Append("<row r=\"1\">");
        for (var column = 0; column < headers.Count; column++)
        {
            AppendCell(builder, column, 1, headers[column], 1);
        }
        builder.AppendLine("</row>");

        for (var row = 0; row < details.Count; row++)
        {
            var excelRow = row + 2;
            var style = details[row].Decision switch
            {
                ReviewDecision.Overkill => 3,
                ReviewDecision.MultiDefectNg => 4,
                ReviewDecision.RealNg => 2,
                _ => 0
            };
            builder.Append(CultureInfo.InvariantCulture, $"<row r=\"{excelRow}\">");
            for (var column = 0; column < headers.Count; column++)
            {
                var value = column < details[row].Values.Count ? details[row].Values[column] : string.Empty;
                AppendCell(builder, column, excelRow, value, style);
            }
            builder.AppendLine("</row>");
        }

        builder.AppendLine("</sheetData>");
        builder.AppendLine("</worksheet>");
        return builder.ToString();
    }

    private static string StylesXml() =>
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
          <fonts count="2">
            <font><sz val="11"/><name val="Calibri"/></font>
            <font><b/><sz val="11"/><name val="Calibri"/></font>
          </fonts>
          <fills count="6">
            <fill><patternFill patternType="none"/></fill>
            <fill><patternFill patternType="gray125"/></fill>
            <fill><patternFill patternType="solid"><fgColor rgb="FFD9EAD3"/><bgColor indexed="64"/></patternFill></fill>
            <fill><patternFill patternType="solid"><fgColor rgb="FFF4CCCC"/><bgColor indexed="64"/></patternFill></fill>
            <fill><patternFill patternType="solid"><fgColor rgb="FFE1E5E9"/><bgColor indexed="64"/></patternFill></fill>
            <fill><patternFill patternType="solid"><fgColor rgb="FFCFE2F3"/><bgColor indexed="64"/></patternFill></fill>
          </fills>
          <borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
          <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
          <cellXfs count="5">
            <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
            <xf numFmtId="0" fontId="1" fillId="4" borderId="0" xfId="0" applyFont="1" applyFill="1"/>
            <xf numFmtId="0" fontId="0" fillId="2" borderId="0" xfId="0" applyFill="1"/>
            <xf numFmtId="0" fontId="0" fillId="3" borderId="0" xfId="0" applyFill="1"/>
            <xf numFmtId="0" fontId="0" fillId="5" borderId="0" xfId="0" applyFill="1"/>
          </cellXfs>
          <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
        </styleSheet>
        """;

    private static string SummaryStylesXml() =>
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
          <numFmts count="2">
            <numFmt numFmtId="164" formatCode="mm-dd-yy"/>
            <numFmt numFmtId="165" formatCode="m/d/yy h:mm"/>
          </numFmts>
          <fonts count="2">
            <font><sz val="11"/><name val="Aptos Narrow"/></font>
            <font><b/><sz val="11"/><name val="Aptos Narrow"/></font>
          </fonts>
          <fills count="2">
            <fill><patternFill patternType="none"/></fill>
            <fill><patternFill patternType="gray125"/></fill>
          </fills>
          <borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
          <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
          <cellXfs count="6">
            <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
            <xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
            <xf numFmtId="165" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
            <xf numFmtId="10" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
            <xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/>
            <xf numFmtId="10" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1" applyNumberFormat="1"/>
          </cellXfs>
          <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
        </styleSheet>
        """;

    private static void AppendCell(
        StringBuilder builder,
        int column,
        int row,
        string value,
        int style)
    {
        builder
            .Append("<c r=\"")
            .Append(ColumnName(column))
            .Append(row.ToString(CultureInfo.InvariantCulture))
            .Append("\" t=\"inlineStr\"");
        if (style > 0)
        {
            builder.Append(" s=\"").Append(style.ToString(CultureInfo.InvariantCulture)).Append('"');
        }
        builder
            .Append("><is><t>")
            .Append(SecurityElement.Escape(value) ?? string.Empty)
            .Append("</t></is></c>");
    }

    private static void AppendTextCell(
        StringBuilder builder,
        int column,
        int row,
        string value,
        int style) =>
        AppendCell(builder, column, row, value, style);

    private static void AppendNumberCell(
        StringBuilder builder,
        int column,
        int row,
        double value,
        int style)
    {
        builder
            .Append("<c r=\"")
            .Append(ColumnName(column))
            .Append(row.ToString(CultureInfo.InvariantCulture))
            .Append('"');
        if (style > 0)
        {
            builder.Append(" s=\"").Append(style.ToString(CultureInfo.InvariantCulture)).Append('"');
        }
        builder
            .Append("><v>")
            .Append(value.ToString("G17", CultureInfo.InvariantCulture))
            .Append("</v></c>");
    }

    private static string ColumnName(int index)
    {
        var name = string.Empty;
        index++;
        while (index > 0)
        {
            var remainder = (index - 1) % 26;
            name = (char)('A' + remainder) + name;
            index = (index - 1) / 26;
        }
        return name;
    }

    private static void WriteEntry(ZipArchive archive, string name, string contents)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(contents);
    }

    private static string SafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(invalid.Contains(character) ? '_' : character);
        }
        return builder.ToString();
    }

    private static string Escape(string value)
    {
        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n')) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
