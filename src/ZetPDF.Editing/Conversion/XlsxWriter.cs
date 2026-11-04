using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using PigPage = UglyToad.PdfPig.Content.Page;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Utilities;

namespace ZetPDF.Editing.Conversion;

/// <summary>Nazwy arkuszy i tekst dla stron bez tekstu (z lokalizacji).</summary>
internal sealed record XlsxLabels(Func<int, string> Page, Func<int, int, string> Table, string Text, Func<int, string> PageWithoutText);

/// <summary>
/// PDF → XLSX: tabele z układu tekstu (<see cref="TableDetector"/>), liczby jako liczby, reszta jako tekst.
/// Treść pochodzi z dokumentu, więc komórka to zawsze tekst (t="inlineStr") albo liczba; element formuły nie powstaje nigdy.
/// Tekst zaczynający się jak formuła dostaje dodatkowo styl tekstowy z quotePrefix, żeby przetrwał też ręczną edycję w Excelu.
/// </summary>
internal static class XlsxWriter
{
    private const int MaxCellLength = 32767;
    private const int MaxSheetName = 31;
    private const uint FormulaLikeTextStyle = 1;

    public static XlsxExportResult Write(byte[] pdf, string path, XlsxExportOptions options, XlsxLabels labels, IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        using var pig = UglyToad.PdfPig.PdfDocument.Open(pdf);
        var pages = string.IsNullOrWhiteSpace(options.PageRange) ? Enumerable.Range(0, pig.NumberOfPages).ToList() : PageRange.Parse(options.PageRange, pig.NumberOfPages).ToList();
        var analyzed = new List<(int Page, List<LayoutBlock> Blocks)>();
        for (int i = 0; i < pages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(OperationProgress.Of(i, pages.Count));
            analyzed.Add((pages[i], TableDetector.Analyze(Words(pig.GetPage(pages[i] + 1)))));
        }
        var hint = CellNumberParser.DetectHint(analyzed.SelectMany(p => p.Blocks).SelectMany(b => b.Rows).SelectMany(r => r));
        var sheets = options.SheetMode == XlsxSheetMode.PerTable ? SheetsPerTable(analyzed, labels) : SheetsPerPage(analyzed, labels);

        using (var xl = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
        {
            var wb = xl.AddWorkbookPart();
            wb.Workbook = new Workbook();
            wb.AddNewPart<WorkbookStylesPart>().Stylesheet = BuildStyles();
            var list = wb.Workbook.AppendChild(new Sheets());
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            uint id = 1;
            foreach (var (name, rows) in sheets)
            {
                ct.ThrowIfCancellationRequested();
                var part = wb.AddNewPart<WorksheetPart>();
                part.Worksheet = BuildSheet(rows, hint);
                list.Append(new Sheet { Id = wb.GetIdOfPart(part), SheetId = id++, Name = UniqueName(name, used) });
            }
            wb.Workbook.Save();
        }
        progress?.Report(OperationProgress.Of(pages.Count, pages.Count));
        return new XlsxExportResult(sheets.Count, analyzed.Sum(p => p.Blocks.Count(b => b.IsTable)), analyzed.Count(p => p.Blocks.Count == 0));
    }

    private static IEnumerable<LayoutWord> Words(PigPage page) =>
        page.GetWords().Where(w => w.Letters.Count > 0).Select(w => new LayoutWord(w.Text, w.BoundingBox.Left, w.BoundingBox.Right, w.Letters[0].StartBaseLine.Y, w.Letters[0].PointSize));

    private static List<(string Name, List<string[]> Rows)> SheetsPerPage(List<(int Page, List<LayoutBlock> Blocks)> pages, XlsxLabels labels)
    {
        var sheets = new List<(string, List<string[]>)>();
        foreach (var (page, blocks) in pages)
        {
            var rows = new List<string[]>();
            foreach (var block in blocks)
            {
                if (rows.Count > 0) rows.Add(Array.Empty<string>());
                rows.AddRange(block.Rows);
            }
            if (rows.Count == 0) rows.Add(new[] { labels.PageWithoutText(page + 1) });
            sheets.Add((labels.Page(page + 1), rows));
        }
        return sheets;
    }

    private static List<(string Name, List<string[]> Rows)> SheetsPerTable(List<(int Page, List<LayoutBlock> Blocks)> pages, XlsxLabels labels)
    {
        var sheets = new List<(string, List<string[]>)>();
        var text = new List<string[]>();
        int table = 0;
        foreach (var (page, blocks) in pages)
        {
            if (blocks.Count == 0) text.Add(new[] { labels.PageWithoutText(page + 1) });
            foreach (var block in blocks)
            {
                if (block.IsTable) sheets.Add((labels.Table(++table, page + 1), block.Rows.ToList()));
                else text.AddRange(block.Rows);
            }
        }
        if (text.Count > 0 || sheets.Count == 0) sheets.Add((labels.Text, text));
        return sheets;
    }

    private static Worksheet BuildSheet(List<string[]> rows, DecimalSeparatorHint hint)
    {
        var data = new SheetData();
        var widths = new Dictionary<int, int>();
        uint rowIndex = 0;
        foreach (var cells in rows)
        {
            rowIndex++;
            if (cells.Length == 0) continue;
            var row = new Row { RowIndex = rowIndex };
            for (int c = 0; c < cells.Length; c++)
            {
                if (string.IsNullOrEmpty(cells[c])) continue;
                row.Append(BuildCell(ColumnName(c) + rowIndex.ToString(CultureInfo.InvariantCulture), cells[c], hint));
                widths[c] = Math.Max(widths.GetValueOrDefault(c), cells[c].Length);
            }
            if (row.HasChildren) data.Append(row);
        }
        var sheet = new Worksheet();
        if (widths.Count > 0)
        {
            var cols = new Columns();
            foreach (var (c, w) in widths.OrderBy(k => k.Key))
                cols.Append(new Column { Min = (uint)c + 1, Max = (uint)c + 1, Width = Math.Clamp(w + 2, 8, 80), CustomWidth = true });
            sheet.Append(cols);
        }
        sheet.Append(data);
        return sheet;
    }

    internal static Cell BuildCell(string reference, string text, DecimalSeparatorHint hint)
    {
        if (CellNumberParser.TryParse(text, hint, out var number))
            return new Cell { CellReference = reference, DataType = CellValues.Number, CellValue = new CellValue(number.ToString("R", CultureInfo.InvariantCulture)) };
        var clean = CleanText(text);
        var cell = new Cell { CellReference = reference, DataType = CellValues.InlineString, InlineString = new InlineString(new DocumentFormat.OpenXml.Spreadsheet.Text(clean) { Space = SpaceProcessingModeValues.Preserve }) };
        if (LooksLikeFormula(clean)) cell.StyleIndex = FormulaLikeTextStyle;
        return cell;
    }

    /// <summary>Początek, który Excel lub inny arkusz odczytałby przy edycji jako formułę (także znaki pełnej szerokości).</summary>
    internal static bool LooksLikeFormula(string text)
    {
        if (text.Length == 0) return false;
        return text[0] is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n' or (char)0xFF1D or (char)0xFF0B or (char)0xFF0D or (char)0xFF20;
    }

    /// <summary>Usuwa znaki niedozwolone w XML (także samotne połówki surogatów) i przycina do limitu komórki.</summary>
    internal static string CleanText(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length && sb.Length < MaxCellLength; i++)
        {
            char ch = text[i];
            if (char.IsHighSurrogate(ch))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) && sb.Length + 1 < MaxCellLength) { sb.Append(ch).Append(text[++i]); }
                continue;
            }
            if (char.IsLowSurrogate(ch)) continue;
            if (ch is '\t' or '\n' or '\r' || (ch >= ' ' && ch != (char)0xFFFE && ch != (char)0xFFFF)) sb.Append(ch);
        }
        return sb.ToString();
    }

    private static string ColumnName(int index)
    {
        var s = string.Empty;
        for (int n = index + 1; n > 0; n = (n - 1) / 26) s = (char)('A' + (n - 1) % 26) + s;
        return s;
    }

    /// <summary>Nazwa arkusza: bez znaków zakazanych w Excelu, do 31 znaków, niepowtarzalna.</summary>
    internal static string UniqueName(string name, HashSet<string> used)
    {
        var clean = new string(name.Select(c => c is '[' or ']' or ':' or '*' or '?' or '/' or '\\' || char.IsControl(c) ? '_' : c).ToArray()).Trim('\'', ' ');
        if (clean.Length == 0) clean = "_";
        if (clean.Length > MaxSheetName) clean = clean[..MaxSheetName];
        var candidate = clean;
        for (int n = 2; !used.Add(candidate); n++)
        {
            var suffix = " " + n.ToString(CultureInfo.InvariantCulture);
            candidate = clean[..Math.Min(clean.Length, MaxSheetName - suffix.Length)] + suffix;
        }
        return candidate;
    }

    private static Stylesheet BuildStyles() => new(
        new Fonts(new Font()) { Count = 1 },
        new Fills(
            new Fill(new PatternFill { PatternType = PatternValues.None }),
            new Fill(new PatternFill { PatternType = PatternValues.Gray125 })) { Count = 2 },
        new Borders(new Border()) { Count = 1 },
        new CellStyleFormats(new CellFormat { NumberFormatId = 0, FontId = 0, FillId = 0, BorderId = 0 }) { Count = 1 },
        new CellFormats(
            new CellFormat { NumberFormatId = 0, FontId = 0, FillId = 0, BorderId = 0, FormatId = 0 },
            new CellFormat { NumberFormatId = 49, FontId = 0, FillId = 0, BorderId = 0, FormatId = 0, ApplyNumberFormat = true, QuotePrefix = true }) { Count = 2 });
}
