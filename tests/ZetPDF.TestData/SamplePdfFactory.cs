using System.Globalization;
using System.Numerics;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.Annotations;
using ZetPDF.Core.Documents;

namespace ZetPDF.TestData;

/// <summary>
/// Generator przykładowych PDF (kod, bez plików z sieci i bez prywatnych dokumentów).
/// Wszystkie metody zwracają bajty BEZ szyfrowania, chyba że nazwa mówi inaczej.
/// </summary>
public static class SamplePdfFactory
{
    public const string PolishPangram = "Zażółć gęślą jaźń";
    public const string UserPassword = "użytkownik-1";
    public const string OwnerPassword = "właściciel-1";

    private static XFont Font(double size, bool bold = false) =>
        new("Arial", size, bold ? XFontStyleEx.Bold : XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode));

    /// <summary>Dokument tekstowy: każda strona ma nagłówek "Strona N", pangram i akapit.</summary>
    public static byte[] Text(int pages = 3, string? extraLine = null)
    {
        PdfSharpEdit.EnsureInitialized();
        using var doc = new PdfDocument();
        doc.Info.Title = "Dokument testowy";
        doc.Info.Author = "ZetPDF Tests";
        for (int i = 0; i < pages; i++)
        {
            var page = doc.AddPage();
            using var g = XGraphics.FromPdfPage(page);
            g.DrawString($"Strona {i + 1}", Font(24, bold: true), XBrushes.Black, new XPoint(56, 80));
            g.DrawString($"{PolishPangram} – ZAŻÓŁĆ GĘŚLĄ JAŹŃ", Font(14), XBrushes.Black, new XPoint(56, 120));
            g.DrawString("The quick brown fox jumps over the lazy dog.", Font(12), XBrushes.Black, new XPoint(56, 150));
            if (extraLine is not null) g.DrawString(extraLine, Font(12), XBrushes.Black, new XPoint(56, 180));
        }
        return PdfSharpEdit.Save(doc);
    }

    /// <summary>Duży dokument (np. 500 stron) – test wydajności.</summary>
    public static byte[] Large(int pages = 500) => Text(pages);

    /// <summary>Zaszyfrowany AES-256 (V5/R6): hasło otwierania i uprawnień (bez modyfikacji i kopiowania).</summary>
    public static byte[] EncryptedAes256(string userPassword = UserPassword, string ownerPassword = OwnerPassword, int pages = 2)
    {
        return PdfSharpEdit.Modify(Text(pages), doc =>
        {
            doc.SecurityHandler.SetEncryptionToV5();
            doc.SecuritySettings.UserPassword = userPassword;
            doc.SecuritySettings.OwnerPassword = ownerPassword;
            doc.SecuritySettings.PermitPrint = true;
            doc.SecuritySettings.PermitModifyDocument = false;
            doc.SecuritySettings.PermitExtractContent = false;
            doc.SecuritySettings.PermitAnnotations = true;
        });
    }

    /// <summary>Zagnieżdżone zakładki: Rozdział N → Podrozdział N.1, N.2.</summary>
    public static byte[] WithBookmarks(int chapters = 3)
    {
        return PdfSharpEdit.Modify(Text(chapters * 2), doc =>
        {
            for (int c = 0; c < chapters; c++)
            {
                var o = doc.Outlines.Add($"Rozdział {c + 1}", doc.Pages[c * 2], true);
                o.Outlines.Add($"Podrozdział {c + 1}.1", doc.Pages[c * 2]);
                o.Outlines.Add($"Podrozdział {c + 1}.2", doc.Pages[c * 2 + 1]);
            }
        });
    }

    /// <summary>Adnotacje: notatka (Text) i stempel (RubberStamp).</summary>
    public static byte[] WithAnnotations()
    {
        return PdfSharpEdit.Modify(Text(2), doc =>
        {
            doc.Pages[0].Annotations.Add(new PdfTextAnnotation
            {
                Title = "Tester",
                Contents = "Notatka testowa – ąęś",
                Icon = PdfTextAnnotationIcon.Note,
                Rectangle = new PdfRectangle(new XRect(400, 700, 20, 20)),
            });
            doc.Pages[1].Annotations.Add(new PdfRubberStampAnnotation
            {
                Title = "Tester",
                Contents = "Zatwierdzono",
                Icon = PdfRubberStampAnnotationIcon.Approved,
                Rectangle = new PdfRectangle(new XRect(300, 600, 150, 50)),
            });
        });
    }

    /// <summary>Formularz AcroForm: tekst "imie", pole wyboru "zgoda", opcje "plec" (k/m), lista "miasto".</summary>
    public static byte[] WithForm()
    {
        return PdfSharpEdit.Modify(Text(1), doc =>
        {
            var page = doc.Pages[0];
            var helv = new PdfDictionary(doc);
            helv.Elements["/Type"] = new PdfName("/Font");
            helv.Elements["/Subtype"] = new PdfName("/Type1");
            helv.Elements["/BaseFont"] = new PdfName("/Helvetica");
            helv.Elements["/Encoding"] = new PdfName("/WinAnsiEncoding");
            doc.Internals.AddObject(helv);

            var annots = page.Elements.GetArray("/Annots") ?? new PdfArray(doc);
            var fields = new PdfArray(doc);

            PdfDictionary Widget(XRect r, PdfDictionary? parent = null)
            {
                var w = new PdfDictionary(doc);
                w.Elements["/Type"] = new PdfName("/Annot");
                w.Elements["/Subtype"] = new PdfName("/Widget");
                w.Elements["/Rect"] = new PdfRectangle(r);
                w.Elements["/F"] = new PdfInteger(4);
                w.Elements["/P"] = page.Reference!;
                if (parent is not null) w.Elements["/Parent"] = parent.Reference!;
                doc.Internals.AddObject(w);
                annots.Elements.Add(w.Reference!);
                return w;
            }

            // Pole tekstowe
            var text = Widget(new XRect(56, 600, 220, 22));
            text.Elements["/FT"] = new PdfName("/Tx");
            text.Elements["/T"] = new PdfString("imie");
            text.Elements["/DA"] = new PdfString("/Helv 12 Tf 0 g");
            fields.Elements.Add(text.Reference!);

            // Pole wyboru
            var check = Widget(new XRect(56, 560, 16, 16));
            check.Elements["/FT"] = new PdfName("/Btn");
            check.Elements["/T"] = new PdfString("zgoda");
            check.Elements["/V"] = new PdfName("/Off");
            check.Elements["/AS"] = new PdfName("/Off");
            fields.Elements.Add(check.Reference!);

            // Przyciski opcji (grupa)
            var radio = new PdfDictionary(doc);
            radio.Elements["/FT"] = new PdfName("/Btn");
            radio.Elements["/T"] = new PdfString("plec");
            radio.Elements["/Ff"] = new PdfInteger(1 << 15 | 1 << 14); // Radio + NoToggleToOff
            radio.Elements["/V"] = new PdfName("/Off");
            doc.Internals.AddObject(radio);
            var kids = new PdfArray(doc);
            foreach (var (name, x) in new[] { ("k", 56.0), ("m", 120.0) })
            {
                var w = Widget(new XRect(x, 520, 14, 14), radio);
                w.Elements["/AS"] = new PdfName("/Off");
                var ap = new PdfDictionary(doc);
                var n = new PdfDictionary(doc);
                n.Elements["/" + name] = new PdfDictionary(doc);
                ap.Elements["/N"] = n;
                w.Elements["/AP"] = ap;
                kids.Elements.Add(w.Reference!);
            }
            radio.Elements["/Kids"] = kids;
            fields.Elements.Add(radio.Reference!);

            // Lista rozwijana
            var combo = Widget(new XRect(56, 470, 160, 22));
            combo.Elements["/FT"] = new PdfName("/Ch");
            combo.Elements["/T"] = new PdfString("miasto");
            combo.Elements["/Ff"] = new PdfInteger(1 << 17); // Combo
            var opts = new PdfArray(doc);
            foreach (var c in new[] { "Warszawa", "Kraków", "Gdańsk" }) opts.Elements.Add(new PdfString(c, PdfStringEncoding.Unicode));
            combo.Elements["/Opt"] = opts;
            combo.Elements["/DA"] = new PdfString("/Helv 12 Tf 0 g");
            fields.Elements.Add(combo.Reference!);

            page.Elements["/Annots"] = annots;
            var acro = new PdfDictionary(doc);
            acro.Elements["/Fields"] = fields;
            acro.Elements["/NeedAppearances"] = new PdfBoolean(true);
            var dr = new PdfDictionary(doc);
            var fonts = new PdfDictionary(doc);
            fonts.Elements["/Helv"] = helv.Reference!;
            dr.Elements["/Font"] = fonts;
            acro.Elements["/DR"] = dr;
            acro.Elements["/DA"] = new PdfString("/Helv 0 Tf 0 g");
            doc.Internals.AddObject(acro);
            doc.Internals.Catalog.Elements["/AcroForm"] = acro.Reference!;
        });
    }

    /// <summary>Fikcyjne dane wrażliwe z poprawnymi sumami kontrolnymi (do redakcji).</summary>
    public static SensitiveSample WithSensitiveData()
    {
        var s = new SensitiveSample(
            Pesel: "44051401359",
            Nip: MakeNip("526025099"),
            Email: "jan.kowalski@example.com",
            Phone: "+48 600 700 800",
            Card: "4111 1111 1111 1111",
            Iban: MakeIbanPl("10901014", "0000071219812874"));
        PdfSharpEdit.EnsureInitialized();
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        using (var g = XGraphics.FromPdfPage(page))
        {
            double y = 80;
            foreach (var line in new[]
            {
                "Dane osobowe (fikcyjne):",
                $"PESEL: {s.Pesel}",
                $"NIP: {s.Nip}",
                $"E-mail: {s.Email}",
                $"Telefon: {s.Phone}",
                $"Karta: {s.Card}",
                $"Rachunek: {s.Iban}",
                PolishPangram,
            })
            {
                g.DrawString(line, Font(12), XBrushes.Black, new XPoint(56, y));
                y += 24;
            }
        }
        return s with { Pdf = PdfSharpEdit.Save(doc) };
    }

    /// <summary>Metadane (Info), załącznik (EmbeddedFiles), JavaScript (Names + OpenAction) – pod oczyszczanie.</summary>
    public static byte[] WithMetadataAttachmentAndJavaScript()
    {
        return PdfSharpEdit.Modify(Text(1), doc =>
        {
            doc.Info.Title = "Tajny tytuł";
            doc.Info.Subject = "Temat poufny";
            doc.Info.Keywords = "poufne; test";

            // Załącznik
            var data = Encoding.UTF8.GetBytes("Treść załącznika – zażółć");
            var ef = new PdfDictionary(doc);
            ef.Elements["/Type"] = new PdfName("/EmbeddedFile");
            ef.CreateStream(data);
            doc.Internals.AddObject(ef);
            var efDict = new PdfDictionary(doc);
            efDict.Elements["/F"] = ef.Reference!;
            var spec = new PdfDictionary(doc);
            spec.Elements["/Type"] = new PdfName("/Filespec");
            spec.Elements["/F"] = new PdfString("zalacznik.txt");
            spec.Elements["/UF"] = new PdfString("załącznik.txt", PdfStringEncoding.Unicode);
            spec.Elements["/EF"] = efDict;
            doc.Internals.AddObject(spec);
            var efNames = new PdfArray(doc);
            efNames.Elements.Add(new PdfString("załącznik.txt", PdfStringEncoding.Unicode));
            efNames.Elements.Add(spec.Reference!);
            var efTree = new PdfDictionary(doc);
            efTree.Elements["/Names"] = efNames;

            // JavaScript
            var js = new PdfDictionary(doc);
            js.Elements["/S"] = new PdfName("/JavaScript");
            js.Elements["/JS"] = new PdfString("app.alert('ZetPDF test');");
            doc.Internals.AddObject(js);
            var jsNames = new PdfArray(doc);
            jsNames.Elements.Add(new PdfString("init"));
            jsNames.Elements.Add(js.Reference!);
            var jsTree = new PdfDictionary(doc);
            jsTree.Elements["/Names"] = jsNames;

            var names = new PdfDictionary(doc);
            names.Elements["/EmbeddedFiles"] = efTree;
            names.Elements["/JavaScript"] = jsTree;
            doc.Internals.Catalog.Elements["/Names"] = names;
            doc.Internals.Catalog.Elements["/OpenAction"] = js.Reference!;
        });
    }

    /// <summary>"Skan": strona z samym obrazem tekstu (bez warstwy tekstu) – pod OCR. Obraz rysowany przez WPF.</summary>
    public static byte[] Scanned(string text = PolishPangram, int dpi = 300)
    {
        byte[] png = RunSta(() => RenderTextPng(text, dpi));
        PdfSharpEdit.EnsureInitialized();
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        using (var g = XGraphics.FromPdfPage(page))
        using (var ms = new MemoryStream(png))
        {
            var img = XImage.FromStream(ms);
            g.DrawImage(img, 0, 0, page.Width.Point, page.Height.Point);
        }
        return PdfSharpEdit.Save(doc);
    }

    /// <summary>Uszkodzony: obcięty PDF ze śmieciami na końcu.</summary>
    public static byte[] Corrupted()
    {
        var ok = Text(1);
        var cut = ok.AsSpan(0, ok.Length / 3).ToArray();
        var garbage = new byte[512];
        new Random(42).NextBytes(garbage);
        return cut.Concat(garbage).ToArray();
    }

    /// <summary>Nie-PDF.</summary>
    public static byte[] NotAPdf() => Encoding.UTF8.GetBytes("To nie jest PDF.");

    public sealed record SensitiveSample(string Pesel, string Nip, string Email, string Phone, string Card, string Iban, byte[]? Pdf = null);

    private static string MakeNip(string first9)
    {
        int[] w = { 6, 5, 7, 2, 3, 4, 5, 6, 7 };
        int sum = first9.Select((c, i) => (c - '0') * w[i]).Sum();
        int check = sum % 11;
        if (check == 10) throw new ArgumentException("Invalid NIP prefix.");
        return first9 + check.ToString(CultureInfo.InvariantCulture);
    }

    private static string MakeIbanPl(string bank8, string account16)
    {
        var bban = bank8 + account16;
        var numeric = bban + "2521" + "00"; // P=25, L=21
        var check = 98 - (int)(BigInteger.Parse(numeric, CultureInfo.InvariantCulture) % 97);
        var digits = $"{check:00}{bban}";
        return "PL" + digits;
    }

    private static T RunSta<T>(Func<T> f)
    {
        T result = default!;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { result = f(); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new InvalidOperationException("STA rendering failed.", error);
        return result;
    }

    private static byte[] RenderTextPng(string text, int dpi)
    {
        int w = (int)(8.27 * dpi), h = (int)(11.69 * dpi);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
            var ft = new FormattedText(text, CultureInfo.GetCultureInfo("pl-PL"), FlowDirection.LeftToRight,
                new Typeface("Arial"), 32 * 96.0 / 72.0, Brushes.Black, 1.0);
            dc.DrawText(ft, new Point(96, 96));
            var ft2 = new FormattedText("Faktura numer 12345 z dnia 16.10.2026", CultureInfo.GetCultureInfo("pl-PL"), FlowDirection.LeftToRight,
                new Typeface("Arial"), 24 * 96.0 / 72.0, Brushes.Black, 1.0);
            dc.DrawText(ft2, new Point(96, 200));
        }
        // Rysujemy w DIP przy 96 DPI w obrazie o rozdzielczości "dpi" – skalowanie przez RenderTargetBitmap.
        var rtb = new RenderTargetBitmap(w, h, dpi, dpi, PixelFormats.Pbgra32);
        var scaled = new DrawingVisual();
        using (var dc = scaled.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(96.0 / dpi, 96.0 / dpi));
            dc.DrawRectangle(new VisualBrush(visual), null, new Rect(0, 0, w, h));
            dc.Pop();
        }
        rtb.Render(scaled);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }
}

/// <summary>Foldery i pliki testowe (ze spacją i polskimi znakami w ścieżce).</summary>
public static class TestPaths
{
    /// <summary>Nowy pusty folder: %TEMP%\ZetPDF-tests\&lt;guid&gt;\Próbny folder\.</summary>
    public static string NewDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ZetPDF-tests", Guid.NewGuid().ToString("N")[..8], "Próbny folder");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Zapisuje bajty jako "zażółć gęślą.pdf" (lub podaną nazwę) w nowym folderze; zwraca ścieżkę.</summary>
    public static string WriteSample(byte[] pdf, string fileName = "zażółć gęślą.pdf")
    {
        var path = Path.Combine(NewDirectory(), fileName);
        File.WriteAllBytes(path, pdf);
        return path;
    }
}
