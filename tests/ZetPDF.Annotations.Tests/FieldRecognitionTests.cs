using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Xunit.Abstractions;
using ZetPDF.Annotations.Forms.Recognition;
using ZetPDF.Annotations.UI.Dialogs;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Rendering;
using ZetPDF.TestData;

namespace ZetPDF.Annotations.Tests;

/// <summary>Rozpoznawanie pól na wygenerowanych formularzach: pola, pozycje, nazwy, brak fałszywych trafień, zmierzona dokładność.</summary>
public class FieldRecognitionTests
{
    private static readonly PdfiumRenderer Renderer = new();
    private static readonly FieldGenericNames Generic = new("Pole", "Pole_wyboru");
    private readonly ITestOutputHelper _output;

    public FieldRecognitionTests(ITestOutputHelper output) => _output = output;

    private sealed record Expected(FormFieldKind Kind, XRect Box, string Name);

    // Współrzędne XGraphics: Y w dół od góry strony A4 (842 pt). W wynikach PDF Y rośnie w górę.
    private static byte[] Build(Action<XGraphics, PdfDocument> draw, int rotate = 0)
    {
        PdfSharpEdit.EnsureInitialized();
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(595);
        page.Height = XUnit.FromPoint(842);
        using (var g = XGraphics.FromPdfPage(page)) draw(g, doc);
        if (rotate != 0) page.Rotate = rotate;
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static readonly XFont Font = new("Arial", 11);
    private static readonly XFont Heading = new("Arial", 18, XFontStyleEx.Bold);
    private static readonly XPen Thin = new(XColors.Black, 0.75);

    private static (byte[] Pdf, List<Expected> Fields) FormDocument()
    {
        var fields = new List<Expected>();
        var pdf = Build((g, _) =>
        {
            g.DrawString("Wniosek o wydanie karty", Heading, XBrushes.Black, 50, 60);
            void Line(string label, double y, double x0, double x1)
            {
                g.DrawString(label, Font, XBrushes.Black, x0 - g.MeasureString(label, Font).Width - 6, y);
                g.DrawLine(Thin, x0, y + 2, x1, y + 2);
            }
            Line("Imię i nazwisko:", 110, 150, 520);
            fields.Add(new(FormFieldKind.Text, new XRect(150, 90, 370, 22), "Imię_i_nazwisko"));
            Line("Adres e-mail:", 140, 150, 520);
            fields.Add(new(FormFieldKind.Text, new XRect(150, 120, 370, 22), "Adres_e-mail"));
            Line("Data:", 170, 150, 280);
            fields.Add(new(FormFieldKind.Text, new XRect(150, 150, 130, 22), "Data"));
            Line("Miasto:", 170, 380, 520);
            fields.Add(new(FormFieldKind.Text, new XRect(380, 150, 140, 22), "Miasto"));

            g.DrawRectangle(Thin, 50, 200, 12, 12);
            g.DrawString("Zgoda na przetwarzanie danych", Font, XBrushes.Black, 68, 211);
            fields.Add(new(FormFieldKind.CheckBox, new XRect(50, 200, 12, 12), "Zgoda_na_przetwarzanie_danych"));
            g.DrawRectangle(Thin, 50, 222, 12, 12);
            g.DrawString("Akceptuję regulamin", Font, XBrushes.Black, 68, 233);
            fields.Add(new(FormFieldKind.CheckBox, new XRect(50, 222, 12, 12), "Akceptuję_regulamin"));

            g.DrawString("Telefon", Font, XBrushes.Black, 50, 271);
            g.DrawRectangle(Thin, 110, 258, 180, 20);
            fields.Add(new(FormFieldKind.Text, new XRect(110, 258, 180, 20), "Telefon"));

            g.DrawString("Uwagi:", Font, XBrushes.Black, 50, 300);
            g.DrawRectangle(Thin, 50, 305, 470, 70);
            fields.Add(new(FormFieldKind.Text, new XRect(50, 305, 470, 70), "Uwagi"));

            g.DrawString("PESEL", Font, XBrushes.Black, 50, 411);
            for (int i = 0; i < 11; i++) g.DrawRectangle(Thin, 100 + i * 16, 398, 16, 18);
            fields.Add(new(FormFieldKind.Text, new XRect(100, 398, 176, 18), "PESEL"));

            g.DrawString("Numer karty: ______________________", Font, XBrushes.Black, 50, 450);
            {
                double x0 = 50 + g.MeasureString("Numer karty: ", Font).Width, x1 = 50 + g.MeasureString("Numer karty: ______________________", Font).Width;
                fields.Add(new(FormFieldKind.Text, new XRect(x0, 435, x1 - x0, 15), "Numer_karty"));
            }
            g.DrawString("Kod pocztowy: ...............................", Font, XBrushes.Black, 50, 480);
            {
                double x0 = 50 + g.MeasureString("Kod pocztowy: ", Font).Width, x1 = 50 + g.MeasureString("Kod pocztowy: ...............................", Font).Width;
                fields.Add(new(FormFieldKind.Text, new XRect(x0, 465, x1 - x0, 15), "Kod_pocztowy"));
            }

            g.DrawLine(Thin, 340, 560, 520, 560);
            g.DrawString("data i podpis", Font, XBrushes.Black, 395, 573);
            fields.Add(new(FormFieldKind.Text, new XRect(340, 540, 180, 20), "data_i_podpis"));
        });
        return (pdf, fields);
    }

    /// <summary>Tabela z siatką: część komórek pusta. Żadna nie może zostać polem.</summary>
    private static byte[] GridTable() => Build((g, _) =>
    {
        g.DrawString("Wykaz pozycji", Heading, XBrushes.Black, 50, 60);
        for (int r = 0; r <= 6; r++) g.DrawLine(Thin, 50, 90 + r * 24, 530, 90 + r * 24);
        for (int c = 0; c <= 4; c++) g.DrawLine(Thin, 50 + c * 120, 90, 50 + c * 120, 90 + 6 * 24);
        for (int r = 0; r < 6; r++)
            for (int c = 0; c < 4; c++)
                if ((r + c) % 3 != 0) g.DrawString($"K{r}{c}", Font, XBrushes.Black, 56 + c * 120, 107 + r * 24);
    });

    /// <summary>Tabela bez pionowych linii (tylko poziome) i wielokolumnowy nagłówek pod linią.</summary>
    private static byte[] RuledTable() => Build((g, _) =>
    {
        g.DrawLine(Thin, 80, 100, 400, 100);
        foreach (var (t, x) in new[] { ("Nazwa", 85d), ("Ilość", 200d), ("Cena", 320d) }) g.DrawString(t, Font, XBrushes.Black, x, 114);
        g.DrawLine(Thin, 80, 120, 400, 120);
        for (int r = 0; r < 4; r++)
        {
            g.DrawString($"Towar {r}", Font, XBrushes.Black, 85, 136 + r * 18);
            g.DrawString($"{r + 1}", Font, XBrushes.Black, 200, 136 + r * 18);
            g.DrawString($"{(r + 1) * 10},00", Font, XBrushes.Black, 320, 136 + r * 18);
        }
        g.DrawLine(Thin, 80, 200, 400, 200);
    });

    /// <summary>Zwykły tekst z podkreśleniami, liniami oddzielającymi, ramką z tekstem, spisem treści i wypełnionymi punktorami.</summary>
    private static byte[] ProseWithDecorations() => Build((g, _) =>
    {
        g.DrawString("Rozdział pierwszy", Heading, XBrushes.Black, 50, 60);
        g.DrawLine(new XPen(XColors.Black, 1.5), 50, 68, 545, 68);
        for (int i = 0; i < 8; i++)
            g.DrawString("Zwykły akapit tekstu, który nie zawiera żadnych pól do wypełnienia przez czytelnika.", Font, XBrushes.Black, 50, 90 + i * 15);
        g.DrawString("Tekst podkreślony w dokumencie", Font, XBrushes.Black, 50, 230);
        g.DrawLine(Thin, 50, 232, 210, 232);
        g.DrawRectangle(Thin, 50, 250, 300, 60);
        g.DrawString("Ramka z ważną informacją wewnątrz.", Font, XBrushes.Black, 60, 275);
        g.DrawRectangle(XBrushes.DarkGray, 50, 330, 200, 60);
        for (int i = 0; i < 3; i++)
        {
            g.DrawRectangle(XBrushes.Black, 50, 412 + i * 18, 6, 6);
            g.DrawString("Punkt listy numer " + i, Font, XBrushes.Black, 64, 420 + i * 18);
        }
        for (int i = 0; i < 4; i++)
            g.DrawString($"Rozdział {i + 1} ............................................ {i * 7 + 3}", Font, XBrushes.Black, 50, 500 + i * 16);
        g.DrawLine(Thin, 50, 600, 545, 600);
        g.DrawString("Stopka: dokument informacyjny, strona 1 z 1 i dalszy ciąg tego opisu", Font, XBrushes.Black, 50, 615);
    });

    /// <summary>Skan: strona jako obraz bez warstwy tekstu.</summary>
    private static async Task<byte[]> Scan(byte[] pdf, double dpi = 200)
    {
        using var doc = await Renderer.OpenAsync(pdf);
        var bmp = await doc.RenderPageAsync(0, RenderRequest.ForDpi(dpi, RenderFlags.None));
        var frame = System.Windows.Media.Imaging.BitmapSource.Create(bmp.Width, bmp.Height, dpi, dpi, System.Windows.Media.PixelFormats.Pbgra32, null, bmp.Bgra, bmp.Stride);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(frame));
        using var png = new MemoryStream();
        encoder.Save(png);
        png.Position = 0;
        return Build((g, _) =>
        {
            using var image = XImage.FromStream(png);
            g.DrawImage(image, 0, 0, 595, 842);
        });
    }

    private static Task<IReadOnlyList<FieldProposal>> Recognize(byte[] pdf) => new FormService().RecognizeFieldsAsync(pdf, Renderer, Generic);

    private static PdfRect ToPdf(XRect r, double pageHeight = 842) => new(r.X, pageHeight - r.Y - r.Height, r.X + r.Width, pageHeight - r.Y);

    private static bool Matches(FieldProposal p, Expected e, double tolerance)
    {
        var want = ToPdf(e.Box);
        // wysokość pola z linii wyznacza program: sprawdzamy poziom, dolną krawędź i rodzaj
        return p.Kind == e.Kind && Math.Abs(p.Rect.Left - want.Left) <= tolerance && Math.Abs(p.Rect.Right - want.Right) <= tolerance && Math.Abs(p.Rect.Bottom - want.Bottom) <= tolerance;
    }

    private static (int Tp, int Fp, int Fn, List<string> Missing, List<string> Extra) Score(IReadOnlyList<FieldProposal> found, IReadOnlyList<Expected> expected, double tolerance)
    {
        var free = found.ToList();
        var missing = new List<string>();
        int tp = 0;
        foreach (var e in expected)
        {
            var hit = free.FirstOrDefault(p => Matches(p, e, tolerance));
            if (hit is null) { missing.Add(e.Name); continue; }
            free.Remove(hit);
            tp++;
        }
        return (tp, free.Count, missing.Count, missing, free.Select(f => $"{f.Name}@{f.Rect}").ToList());
    }

    [Fact]
    public async Task Finds_fields_positions_and_names_on_generated_form()
    {
        var (pdf, expected) = FormDocument();
        var found = await Recognize(pdf);
        foreach (var f in found) _output.WriteLine($"{f.Kind} {f.Name} {f.Rect} {f.Source}");
        var score = Score(found, expected, 5);
        Assert.True(score.Fn == 0 && score.Fp == 0, $"brakujące: {string.Join(", ", score.Missing)}; nadmiarowe: {string.Join(", ", score.Extra)}");
        foreach (var e in expected)
            Assert.Contains(found, p => p.Name == e.Name);
        Assert.Contains(found, p => p.Source == FieldProposalSource.Comb);
        Assert.Contains(found, p => p.Source == FieldProposalSource.Leader);
        Assert.True(found.Single(p => p.Name == "Uwagi").Multiline);
    }

    [Theory]
    [InlineData("grid")]
    [InlineData("ruled")]
    [InlineData("prose")]
    [InlineData("sample")]
    public async Task No_false_positives_on_text_and_tables(string kind)
    {
        var pdf = kind switch
        {
            "grid" => GridTable(),
            "ruled" => RuledTable(),
            "prose" => ProseWithDecorations(),
            _ => SamplePdfFactory.Text(3),
        };
        var found = await Recognize(pdf);
        Assert.True(found.Count == 0, string.Join("; ", found.Select(f => $"{f.Kind} {f.Name} {f.Rect} {f.Source}")));
    }

    [Fact]
    public async Task Scan_without_text_gets_generic_names_and_positions()
    {
        var (pdf, expected) = FormDocument();
        var found = await Recognize(await Scan(pdf));
        foreach (var f in found) _output.WriteLine($"{f.Kind} {f.Name} {f.Rect} {f.Source}");
        Assert.All(found, f => Assert.Matches(@"^Pole(_wyboru)?_\d+$", f.Name));
        Assert.Equal(found.Count, found.Select(f => f.Name).Distinct().Count());
        // bez tekstu nie ma wielokropka (znaki) ani etykiet; kratki i prostokąty muszą zostać
        foreach (var name in new[] { "Telefon", "Uwagi", "PESEL", "Zgoda_na_przetwarzanie_danych", "Akceptuję_regulamin", "Imię_i_nazwisko", "Numer_karty" })
        {
            var e = expected.Single(x => x.Name == name);
            Assert.True(found.Any(p => Matches(p, e, 6)), "brak " + name);
        }
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task Rotated_page_keeps_positions_in_page_space(int rotate)
    {
        var (pdf, expected) = FormDocument();
        var rotated = PdfSharpEdit.Modify(pdf, d => d.Pages[0].Rotate = rotate);
        var plain = await Recognize(pdf);
        var found = await Recognize(rotated);
        var check = plain.Where(p => p.Kind == FormFieldKind.CheckBox).ToList();
        Assert.NotEmpty(check);
        foreach (var p in check)
            Assert.Contains(found, f => f.Kind == FormFieldKind.CheckBox && Math.Abs(f.Rect.Left - p.Rect.Left) < 3 && Math.Abs(f.Rect.Bottom - p.Rect.Bottom) < 3);
        Assert.NotEmpty(expected);
    }

    [Fact]
    public async Task Existing_fields_are_not_proposed_again_and_names_stay_unique()
    {
        var (pdf, _) = FormDocument();
        var first = await Recognize(pdf);
        var uwagi = first.Single(p => p.Name == "Uwagi");
        var withField = await new FormService().CreateFieldAsync(pdf, new FormFieldSpec { Kind = FormFieldKind.Text, Name = "Uwagi", PageIndex = 0, Rect = uwagi.Rect, Multiline = true });
        var telefon = first.Single(p => p.Name == "Telefon");
        // nazwa „Telefon” zajęta przez pole w innym miejscu strony
        withField = await new FormService().CreateFieldAsync(withField, new FormFieldSpec { Kind = FormFieldKind.Text, Name = "Telefon", PageIndex = 0, Rect = new PdfRect(400, 20, 500, 40) });
        var second = await Recognize(withField);
        Assert.DoesNotContain(second, p => p.Rect.Left == uwagi.Rect.Left && p.Rect.Top == uwagi.Rect.Top);
        Assert.Equal(first.Count - 1, second.Count);
        Assert.Contains(second, p => p.Name == "Telefon_2" && Math.Abs(p.Rect.Left - telefon.Rect.Left) < 1);
    }

    [Fact]
    public async Task Created_fields_are_readable_and_undo_redo_works()
    {
        var (pdf, _) = FormDocument();
        var found = await Recognize(pdf);
        var specs = found.Select(f => new FormFieldSpec { Kind = f.Kind, Name = f.Name, PageIndex = f.PageIndex, Rect = f.Rect, Tooltip = f.Label, Multiline = f.Multiline }).ToList();
        var dlgBytes = await new FormService().CreateFieldsAsync(pdf, specs);
        var fields = await new FormService().GetFieldsAsync(dlgBytes);
        Assert.Equal(found.Count, fields.Count);
        Assert.Contains(fields, f => f.FullName == "Imię_i_nazwisko" && f.Kind == FormFieldKind.Text);
        Assert.Contains(fields, f => f.FullName == "Akceptuję_regulamin" && f.Kind == FormFieldKind.CheckBox);

        using var session = new PdfDocumentSession(null, "t.pdf", DocumentSnapshot.FromBytes(pdf), PdfSecurityInfo.None, DocumentPermissions.All, new SnapshotStorageOptions(TestPaths.NewDirectory()));
        await session.ExecuteAsync(new DelegateDocumentCommand("Annot.Undo.RecognizeFields", (b, _, ct) => new FormService().CreateFieldsAsync(b, specs, ct), DocumentChangeKind.FormFields, DocumentPermissions.ModifyContent, null), null, default);
        Assert.Equal(found.Count, (await new FormService().GetFieldsAsync(session.Current.GetBytes())).Count);
        await session.History.UndoAsync();
        Assert.Empty(await new FormService().GetFieldsAsync(session.Current.GetBytes()));
        await session.History.RedoAsync();
        Assert.Equal(found.Count, (await new FormService().GetFieldsAsync(session.Current.GetBytes())).Count);
    }

    [Theory]
    [InlineData("Imię i nazwisko:", "Imię_i_nazwisko")]
    [InlineData("a.b/c[0]", "a_b_c_0")]
    [InlineData("  ...  ", "")]
    [InlineData(null, "")]
    public void Sanitize_removes_characters_not_allowed_in_field_names(string? input, string expected) =>
        Assert.Equal(expected, FieldNames.Sanitize(input));

    [Fact]
    public void Unique_names_and_parent_prefixes()
    {
        var used = FieldNames.UsedFrom(new[] { "Rodzic.Dziecko", "Imie" });
        Assert.Equal("Imie_2", FieldNames.Unique("Imie:", "Pole", used));
        Assert.Equal("imie_3", FieldNames.Unique("imie", "Pole", used));
        Assert.Equal("Rodzic_2", FieldNames.Unique("Rodzic", "Pole", used));
        Assert.Equal("Pole_1", FieldNames.Unique("   ", "Pole", used));
        Assert.Equal("Pole_2", FieldNames.Unique(null, "Pole", used));
        Assert.True(FieldNames.Unique(new string('x', 500), "Pole", used).Length <= FieldNames.MaxLength);
    }

    [Fact]
    public void Scale_is_bounded_for_huge_pages()
    {
        double s = FieldRecognizer.ScaleFor(new PdfSize(14400, 14400));
        Assert.True(14400 * s <= 3000 + 1);
        Assert.Equal(150.0 / 72, FieldRecognizer.ScaleFor(new PdfSize(595, 842)), 6);
    }

    [Fact]
    public async Task Huge_noisy_page_does_not_crash_or_allocate_without_bound()
    {
        var pdf = Build((g, _) =>
        {
            for (int y = 0; y < 842; y += 3)
                for (int x = 0; x < 595; x += 3)
                    g.DrawRectangle(XBrushes.Black, x, y, 1, 1);
        });
        var found = await Recognize(pdf);
        Assert.True(found.Count < 5);
    }

    [Fact]
    public void Dialog_returns_selected_fields_with_unique_edited_names()
    {
        var pdf = FormDocument().Pdf;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var doc = Renderer.OpenAsync(pdf).GetAwaiter().GetResult();
                var loc = new ZetPDF.Core.Localization.LocalizationService(initial: new System.Globalization.CultureInfo("en"));
                var p = new[]
                {
                    new FieldProposal(0, FormFieldKind.Text, new PdfRect(10, 10, 100, 30), "A", "A", FieldProposalSource.Box),
                    new FieldProposal(0, FormFieldKind.Text, new PdfRect(10, 40, 100, 60), "B", "B", FieldProposalSource.Box),
                    new FieldProposal(0, FormFieldKind.CheckBox, new PdfRect(10, 70, 20, 80), "C", "C", FieldProposalSource.CheckBox),
                };
                var dialog = new RecognizeFieldsDialog(loc, p, doc, new[] { "Zajete" });
                var grid = ((System.Windows.Controls.DockPanel)dialog.Content).Children.OfType<System.Windows.Controls.DataGrid>().Single();
                var rows = (IList<RecognizeFieldsDialog.Row>)grid.ItemsSource;
                rows[1].Name = "A.x/Zajete";
                rows[2].Create = false;
                rows[0].Name = "Zajete";
                var specs = dialog.SelectedSpecs();
                Assert.Equal(2, specs.Count);
                Assert.Equal("Zajete_2", specs[0].Name);
                Assert.Equal("A_x_Zajete", specs[1].Name);
                dialog.Close();
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    /// <summary>Przypadki trudne, bez progu: wynik ma tylko trafić do opisu ograniczeń, a nie stroić heurystyki pod test.</summary>
    [Fact]
    public async Task Measured_hard_cases_without_threshold()
    {
        var (form, expected) = FormDocument();
        using var doc = await Renderer.OpenAsync(form);
        var bmp = await doc.RenderPageAsync(0, RenderRequest.ForDpi(200, RenderFlags.None));
        var frame = System.Windows.Media.Imaging.BitmapSource.Create(bmp.Width, bmp.Height, 200, 200, System.Windows.Media.PixelFormats.Pbgra32, null, bmp.Bgra, bmp.Stride);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(frame));
        using var png = new MemoryStream();
        encoder.Save(png);
        foreach (var angle in new[] { 0.5, 1.5 })
        {
            png.Position = 0;
            var skewed = Build((g, _) =>
            {
                using var image = XImage.FromStream(png);
                g.RotateAtTransform(angle, new XPoint(297, 421));
                g.DrawImage(image, 0, 0, 595, 842);
            });
            var found = await Recognize(skewed);
            var s = Score(found, expected.Where(e => e.Name != "Kod_pocztowy").ToList(), 12);
            _output.WriteLine($"skan przekrzywiony {angle} st.: TP={s.Tp} FP={s.Fp} FN={s.Fn} brak=[{string.Join(",", s.Missing)}]");
        }
        var tableForm = Build((g, _) =>
        {
            for (int r = 0; r <= 4; r++) g.DrawLine(Thin, 50, 100 + r * 26, 530, 100 + r * 26);
            foreach (var x in new[] { 50d, 200d, 530d }) g.DrawLine(Thin, x, 100, x, 204);
            for (int r = 0; r < 4; r++) g.DrawString("Etykieta " + r, Font, XBrushes.Black, 56, 118 + r * 26);
        });
        var inTable = await Recognize(tableForm);
        _output.WriteLine($"formularz w tabeli (etykieta w komórce obok pustej), 4 pola: znaleziono {inTable.Count}");
        Assert.True(inTable.Count >= 0);
    }

    /// <summary>Zmierzona dokładność na zestawie dokumentów z tego pliku. Wynik idzie do docs/OGRANICZENIA.md.</summary>
    [Fact]
    public async Task Measured_precision_and_recall()
    {
        var (form, expected) = FormDocument();
        var sets = new List<(string Name, byte[] Pdf, IReadOnlyList<Expected> Truth, double Tolerance)>
        {
            ("formularz", form, expected, 5),
            ("formularz-skan", await Scan(form), expected.Where(e => e.Name != "Kod_pocztowy").ToList(), 6),
            ("tabela-siatka", GridTable(), Array.Empty<Expected>(), 5),
            ("tabela-linie", RuledTable(), Array.Empty<Expected>(), 5),
            ("tekst-ozdobniki", ProseWithDecorations(), Array.Empty<Expected>(), 5),
            ("tekst-prosty", SamplePdfFactory.Text(3), Array.Empty<Expected>(), 5),
        };
        int tp = 0, fp = 0, fn = 0;
        foreach (var set in sets)
        {
            var found = await Recognize(set.Pdf);
            var s = Score(found, set.Truth, set.Tolerance);
            tp += s.Tp;
            fp += s.Fp;
            fn += s.Fn;
            _output.WriteLine($"{set.Name}: TP={s.Tp} FP={s.Fp} FN={s.Fn} brak=[{string.Join(",", s.Missing)}] nadmiar=[{string.Join(",", s.Extra)}]");
        }
        double precision = tp / Math.Max(1.0, tp + fp), recall = tp / Math.Max(1.0, tp + fn);
        _output.WriteLine($"razem: TP={tp} FP={fp} FN={fn} precyzja={precision:0.000} czułość={recall:0.000}");
        Assert.True(precision >= 0.9 && recall >= 0.9, $"precyzja {precision:0.000}, czułość {recall:0.000}");
    }
}
