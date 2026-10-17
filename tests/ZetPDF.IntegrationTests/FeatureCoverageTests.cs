using CultureInfo = System.Globalization.CultureInfo;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using DocumentFormat.OpenXml.Packaging;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.IntegrationTests.Infrastructure;
using ZetPDF.Security;
using ZetPDF.Security.Actions;
using ZetPDF.TestData;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace ZetPDF.IntegrationTests;

/// <summary>Pozostałe funkcje etapów 1 i 2 sprawdzane czarną skrzynką (usługi z DI, wynik w pliku, polskie ścieżki).</summary>
public class FeatureCoverageTests
{
    // ================================================================== 1.1 wiele kart, ostatnie pliki, Przywróć

    [Fact]
    public async Task Several_documents_open_in_tabs_recent_files_and_revert()
    {
        using var host = new TestHost();
        var m = host.Documents;
        var a = TestPaths.WriteSample(SamplePdfFactory.Text(1), "pierwszy ą.pdf");
        var b = TestPaths.WriteSample(SamplePdfFactory.Text(2), "drugi ę.pdf");
        var opened = new List<string>();
        m.DocumentOpened += (_, s) => opened.Add(s.DisplayName);
        var sa = (await m.OpenAsync(a))!;
        var sb = (await m.OpenAsync(b))!;
        Assert.Equal(2, m.Documents.Count);
        Assert.Same(sb, m.ActiveDocument);
        Assert.Equal(new[] { "pierwszy ą.pdf", "drugi ę.pdf" }, opened);
        m.ActiveDocument = sa;
        Assert.Same(sa, m.ActiveDocument);
        var recent = host.Get<IRecentFilesService>().Files.Select(Path.GetFullPath).ToList();
        Assert.Contains(Path.GetFullPath(a), recent);
        Assert.Contains(Path.GetFullPath(b), recent);
        Assert.True(recent.IndexOf(Path.GetFullPath(b)) < recent.IndexOf(Path.GetFullPath(a)), "ostatnio otwarty plik powinien być pierwszy");

        // Przywróć: odrzuca niezapisane zmiany i czyści historię
        var op = Operations.Get("Pages.Delete");
        await sb.ExecuteAsync(new DelegateDocumentCommand("Test", (pdf, _, _) => op.Run(host, pdf), DocumentChangeKind.All, op.Permission));
        Assert.Equal(1, Inspect.PageCount(sb.Current.GetBytes()));
        await m.RevertAsync(sb);
        Assert.Equal(2, Inspect.PageCount(sb.Current.GetBytes()));
        Assert.False(sb.IsModified);
        Assert.False(sb.History.CanUndo);

        m.Close(sa);
        Assert.Single(m.Documents);
        // nowy dokument (np. wynik konwersji) wymaga Zapisz jako
        var n = await m.CreateNewAsync(SamplePdfFactory.Text(1), "Nowy ąę");
        Assert.Null(n.FilePath);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => m.SaveAsync(n));
        var target = Path.Combine(TestPaths.NewDirectory(), "nowy zapisany ść.pdf");
        await m.SaveAsync(n, target);
        Assert.Equal(target, n.FilePath);
        Assert.Equal("nowy zapisany ść.pdf", n.DisplayName);
    }

    // ================================================================== 1.11 ustawienia: tożsamość, domyślny widok, język

    [Fact]
    public void Author_identity_from_settings_is_used_for_new_comments_and_default_view_settings_apply() => Sta.Run(async () =>
    {
        using var host = new TestHost(initializeModules: true);
        host.Get<ISettingsService>().Update(s =>
        {
            s.AuthorName = "Zofia Żak-Łęcka";
            s.DefaultZoomMode = ZoomMode.ActualSize;
            s.DefaultLayout = PageLayoutMode.SinglePage;
        });
        var session = await host.Documents.CreateNewAsync(SamplePdfFactory.Text(2), "autor.pdf");
        host.Documents.ActiveDocument = session;
        var view = host.Get<IDocumentViewFactory>().CreateView(session);
        Sta.Layout(view.Element);
        await Sta.WaitFor(() => view.GetPageTransform(0) is not null, 20000, "strona");
        Assert.Equal(ZoomMode.ActualSize, view.ZoomMode);
        Assert.Equal(PageLayoutMode.SinglePage, view.Layout);

        Assert.True(host.Get<ICommandRegistry>().TryExecute(CommandIds.Comment.Rectangle));
        var tool = host.Get<IToolService>().ActiveTool;
        var t = view.GetPageTransform(0)!;
        PagePointerEventArgs Args(PdfPoint p, MouseButton? btn) => new(0, p, t.ToView(p), btn, ModifierKeys.None, 1);
        tool.OnPointerPressed(view, Args(new PdfPoint(100, 500), MouseButton.Left));
        tool.OnPointerMoved(view, Args(new PdfPoint(200, 400), null));
        tool.OnPointerReleased(view, Args(new PdfPoint(200, 400), MouseButton.Left));
        await Sta.WaitFor(() => session.History.CanUndo, 20000, "adnotacja");
        var a = Assert.Single(await host.Get<IAnnotationService>().GetAnnotationsAsync(session.Current.GetBytes()));
        Assert.Equal("Zofia Żak-Łęcka", a.Author);
        Assert.Contains("Zofia Żak-Łęcka", Inspect.Deep(session.Current.GetBytes()));
        view.Dispose();
    });

    [Fact]
    public void Language_switches_live_between_polish_and_english() => Sta.Run(() =>
    {
        using var host = new TestHost(initializeModules: true);
        var loc = host.Get<ILocalizationService>();
        loc.RegisterResources(new System.Resources.ResourceManager("ZetPDF.App.Resources.Strings", typeof(ZetPDF.App.ModuleCatalog).Assembly));
        int changed = 0;
        loc.PropertyChanged += (_, e) => { if (e.PropertyName == "Item[]") changed++; };
        loc.SetCulture(new CultureInfo("pl"));
        Assert.Equal("Otwórz", loc["Cmd.File.Open"]);
        var plRedact = loc["Edit.Redact.Warning.Title"];
        loc.SetCulture(new CultureInfo("en"));
        Assert.Equal("Open", loc["Cmd.File.Open"]);
        Assert.NotEqual(plRedact, loc["Edit.Redact.Warning.Title"]);
        loc.SetCulture(new CultureInfo("pl"));
        Assert.Equal(plRedact, loc["Edit.Redact.Warning.Title"]);
        Assert.True(changed >= 2, $"PropertyChanged(Item[]) podniesione {changed} razy przy dwóch zmianach języka");
        return Task.CompletedTask;
    });

    // ================================================================== 2.2 edycja obiektów

    [Fact]
    public async Task Image_object_can_be_moved_scaled_rotated_replaced_and_deleted()
    {
        using var host = new TestHost();
        var svc = host.Get<IObjectEditService>();
        var pdf = await svc.AddImageAsync(SamplePdfFactory.Text(1), 0, ExtraSamples.Png(120, 80), new PdfRect(100, 200, 220, 280));
        PageObjectInfo Img(IReadOnlyList<PageObjectInfo> list) => list.Single(o => o.Kind == PageObjectKind.Image);
        static UglyToad.PdfPig.Core.PdfRectangle PigImage(byte[] p) { using var d = PigDocument.Open(p); return d.GetPage(1).GetImages().Single().BoundingBox; }

        var img = Img(await svc.GetObjectsAsync(pdf, 0));
        var moved = await svc.TransformObjectAsync(pdf, 0, img.Id, PdfMatrix.Translation(50, 100));
        var mb = PigImage(moved);
        Assert.InRange(mb.Left, 149, 151);
        Assert.InRange(mb.Bottom, 299, 301);

        img = Img(await svc.GetObjectsAsync(moved, 0));
        var scaled = await svc.TransformObjectAsync(moved, 0, img.Id, PdfMatrix.Translation(-150, -300).Then(PdfMatrix.Scaling(2, 2)).Then(PdfMatrix.Translation(150, 300)));
        var sb = PigImage(scaled);
        Assert.InRange(sb.Width, 239, 241);
        Assert.InRange(sb.Height, 159, 161);

        img = Img(await svc.GetObjectsAsync(scaled, 0));
        var rotated = await svc.TransformObjectAsync(scaled, 0, img.Id, PdfMatrix.Translation(-150, -300).Then(PdfMatrix.Rotation(90)).Then(PdfMatrix.Translation(150, 300)));
        var rb = Img(await svc.GetObjectsAsync(rotated, 0)).Bounds;
        Assert.InRange(rb.Width, 159, 161); // szerokość i wysokość zamienione
        Assert.InRange(rb.Height, 239, 241);

        img = Img(await svc.GetObjectsAsync(rotated, 0));
        var replaced = await svc.ReplaceImageAsync(rotated, 0, img.Id, ExtraSamples.Png(40, 40));
        using (var d = PigDocument.Open(replaced)) Assert.Equal(40, d.GetPage(1).GetImages().Single().WidthInSamples);

        img = Img(await svc.GetObjectsAsync(replaced, 0));
        var deleted = await svc.DeleteObjectAsync(replaced, 0, img.Id);
        using (var d = PigDocument.Open(deleted)) Assert.Empty(d.GetPage(1).GetImages());
        Assert.Contains("gęślą", Inspect.PigText(deleted)); // tekst nietknięty
    }

    // ================================================================== 2.3 łącza z polskimi znakami w adresie

    [Fact]
    public async Task Link_uri_with_polish_characters_is_stored_as_valid_ascii_uri()
    {
        // /URI w PDF to ciąg ASCII (ISO 32000-1, 12.6.4.7) – znaki spoza ASCII muszą być zakodowane (%C5%BC…).
        using var host = new TestHost();
        var links = host.Get<ILinkService>();
        var expected = "https://example.com/za%C5%BC%C3%B3%C5%82%C4%87";

        var added = await links.AddLinkAsync(SamplePdfFactory.Text(1), 0, new PdfRect(56, 60, 200, 90), new LinkTarget(Uri: "https://example.com/zażółć"));
        using (var doc = await Inspect.Renderer.OpenAsync(added))
        {
            var l = Assert.Single(await doc.GetLinksAsync(0));
            Assert.Equal(expected, l.Uri);
        }

        var (auto, created) = await links.CreateLinksFromUrlsAsync(ExtraSamples.WithUrls("zażółć"));
        Assert.Equal(2, created);
        using (var doc = await Inspect.Renderer.OpenAsync(auto))
            Assert.Contains(await doc.GetLinksAsync(0), l => l.Uri == expected);
    }

    // ================================================================== 2.4 konwersje z polskimi ścieżkami

    private static string WriteImage(string dir, string name, BitmapEncoder enc)
    {
        var src = BitmapFrame.Create(new MemoryStream(ExtraSamples.Png(200, 100)));
        enc.Frames.Add(BitmapFrame.Create(src));
        var path = Path.Combine(dir, name);
        using var fs = File.Create(path);
        enc.Save(fs);
        return path;
    }

    [Fact]
    public async Task Images_in_polish_folder_convert_to_pdf_and_pdf_exports_to_images_text_and_docx()
    {
        using var host = new TestHost();
        var conv = host.Get<IConversionService>();
        var dir = TestPaths.NewDirectory();
        var images = new[]
        {
            WriteImage(dir, "obraz źdźbło.png", new PngBitmapEncoder()),
            WriteImage(dir, "zdjęcie ąę.jpg", new JpegBitmapEncoder()),
            WriteImage(dir, "skan łódź.tif", new TiffBitmapEncoder()),
            WriteImage(dir, "mapa ść.bmp", new BmpBitmapEncoder()),
        };
        var fromImages = await conv.ImagesToPdfAsync(images, new ImageToPdfOptions { PageSize = ImagePageSize.A4 });
        Assert.Equal(4, Inspect.PageCount(fromImages));
        using (var d = PigDocument.Open(fromImages))
            Assert.All(d.GetPages(), p => Assert.Single(p.GetImages()));

        var pdf = SamplePdfFactory.Text(3);
        var outDir = Path.Combine(dir, "eksport ąę");
        Directory.CreateDirectory(outDir);
        var pngs = await conv.PdfToImagesAsync(pdf, new ImageExportOptions { Format = ImageFormat.Png, Dpi = 72, PageRange = "1,3" }, outDir, "strona żółw");
        Assert.Equal(2, pngs.Count);
        Assert.All(pngs, p =>
        {
            Assert.True(File.Exists(p), p);
            Assert.StartsWith(outDir, p);
            Assert.Contains("żółw", Path.GetFileName(p));
            var frame = BitmapDecoder.Create(new Uri(p), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            Assert.InRange(frame.PixelWidth, 590, 600); // A4 przy 72 DPI
        });
        var tiff = await conv.PdfToImagesAsync(pdf, new ImageExportOptions { Format = ImageFormat.Tiff, Dpi = 50, MultiPageTiff = true }, outDir, "wielostronicowy ść");
        var tf = Assert.Single(tiff);
        Assert.Equal(3, BitmapDecoder.Create(new Uri(tf), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames.Count);

        var txt = await conv.PdfToTextAsync(pdf, new TextExportOptions());
        Assert.Contains(SamplePdfFactory.PolishPangram, txt);
        Assert.Contains("Strona 3", txt);

        var docx = Path.Combine(outDir, "dokument zażółć.docx");
        await conv.PdfToDocxAsync(pdf, docx, new DocxExportOptions());
        using (var w = WordprocessingDocument.Open(docx, false))
        {
            var body = w.MainDocumentPart!.Document!.Body!.InnerText;
            Assert.Contains(SamplePdfFactory.PolishPangram, body);
            Assert.Contains("Strona 2", body);
        }
    }

    [Fact]
    public async Task Combine_files_of_different_types_and_create_from_clipboard_data()
    {
        using var host = new TestHost();
        var conv = host.Get<IConversionService>();
        var dir = TestPaths.NewDirectory();
        var pdfPath = TestPaths.WriteSample(SamplePdfFactory.Text(2), "część pdf ą.pdf");
        var png = WriteImage(dir, "obrazek ż.png", new PngBitmapEncoder());
        var txt = Path.Combine(dir, "notatka ść.txt");
        File.WriteAllText(txt, "Tekst z pliku TXT: źdźbło żółć\nDruga linia");
        var combined = await conv.CombineFilesAsync(new[] { pdfPath, png, txt });
        Assert.Equal(4, Inspect.PageCount(combined));
        Assert.Contains("Strona 1", Inspect.PigPageText(combined, 0));
        using (var d = PigDocument.Open(combined)) Assert.Single(d.GetPage(3).GetImages());
        Assert.Contains("źdźbłożółć", Inspect.Squash(Inspect.PigPageText(combined, 3)));

        var fromClipboardText = await conv.TextToPdfAsync("Ze schowka: zażółć gęślą jaźń");
        Assert.Contains("Zeschowka:zażółćgęśląjaźń", Inspect.Squash(await Inspect.PdfiumText(fromClipboardText)));
        var fromClipboardImage = await conv.ImageDataToPdfAsync(ExtraSamples.Png(), new ImageToPdfOptions());
        using (var d = PigDocument.Open(fromClipboardImage)) Assert.Single(d.GetPage(1).GetImages());
    }

    // ================================================================== 2.10 tworzenie pól formularza, kolejność tabulacji

    [Fact]
    public async Task All_form_field_kinds_can_be_created_filled_and_reordered()
    {
        using var host = new TestHost();
        var forms = host.Get<IFormService>();
        var pdf = SamplePdfFactory.Text(1);
        var specs = new FormFieldSpec[]
        {
            new() { Kind = FormFieldKind.Text, Name = "nazwisko", PageIndex = 0, Rect = new PdfRect(56, 600, 256, 622) },
            new() { Kind = FormFieldKind.CheckBox, Name = "zgoda", PageIndex = 0, Rect = new PdfRect(56, 560, 72, 576) },
            new() { Kind = FormFieldKind.RadioButton, Name = "płeć", ExportValue = "kobieta", PageIndex = 0, Rect = new PdfRect(56, 520, 70, 534) },
            new() { Kind = FormFieldKind.RadioButton, Name = "płeć", ExportValue = "mężczyzna", PageIndex = 0, Rect = new PdfRect(120, 520, 134, 534) },
            new() { Kind = FormFieldKind.ComboBox, Name = "miasto", PageIndex = 0, Rect = new PdfRect(56, 470, 216, 492), Options = new[] { "Łódź", "Kraków", "Gdańsk" } },
            new() { Kind = FormFieldKind.ListBox, Name = "języki", PageIndex = 0, Rect = new PdfRect(56, 380, 216, 450), Options = new[] { "polski", "angielski" } },
            new() { Kind = FormFieldKind.PushButton, Name = "wyczyść", PageIndex = 0, Rect = new PdfRect(300, 380, 400, 405), ButtonAction = FormButtonAction.ResetForm, ButtonCaption = "Wyczyść" },
            new() { Kind = FormFieldKind.Signature, Name = "podpis", PageIndex = 0, Rect = new PdfRect(300, 200, 500, 260) },
            new() { Kind = FormFieldKind.Date, Name = "data", PageIndex = 0, Rect = new PdfRect(300, 600, 400, 622), DateFormat = "yyyy-MM-dd" },
        };
        foreach (var s in specs) pdf = await forms.CreateFieldAsync(pdf, s);
        var fields = await forms.GetFieldsAsync(pdf);
        foreach (var (name, kind) in new[] { ("nazwisko", FormFieldKind.Text), ("zgoda", FormFieldKind.CheckBox), ("płeć", FormFieldKind.RadioButton), ("miasto", FormFieldKind.ComboBox), ("języki", FormFieldKind.ListBox), ("wyczyść", FormFieldKind.PushButton), ("podpis", FormFieldKind.Signature), ("data", FormFieldKind.Date) })
            Assert.Contains(fields, f => f.FullName == name && f.Kind == kind);
        Assert.Equal(new[] { "kobieta", "mężczyzna" }, fields.First(f => f.FullName == "płeć").ExportValues.Where(v => v != "Off").OrderBy(x => x));

        // niezależnie: PdfPig widzi formularz z polami
        using (var pig = PigDocument.Open(pdf))
        {
            Assert.True(pig.TryGetForm(out var form));
            Assert.True(form.Fields.Count >= 8, $"PdfPig widzi {form.Fields.Count} pól");
        }
        // wypełnienie z polskimi znakami
        var check = fields.First(f => f.FullName == "zgoda").ExportValues.First(v => v != "Off");
        var filled = await forms.SetValuesAsync(pdf, new Dictionary<string, string> { ["nazwisko"] = "Żółkiewski", ["zgoda"] = check, ["płeć"] = "mężczyzna", ["miasto"] = "Łódź", ["data"] = "2026-10-17" });
        var values = (await forms.GetFieldsAsync(filled)).GroupBy(f => f.FullName).ToDictionary(g => g.Key, g => g.First().Value);
        Assert.Equal("Żółkiewski", values["nazwisko"]);
        Assert.Equal(check, values["zgoda"]);
        Assert.Equal("mężczyzna", values["płeć"]);
        Assert.Equal("Łódź", values["miasto"]);
        Assert.Contains("Żółkiewski", await Inspect.PdfiumText(await host.Get<IPageOperations>().FlattenAsync(filled, new FlattenOptions())));

        // kolejność tabulacji
        var order = new[] { "data", "nazwisko", "miasto", "zgoda" };
        var reordered = await forms.SetTabOrderAsync(filled, 0, order);
        var tab = (await forms.GetFieldsAsync(reordered)).Where(f => order.Contains(f.FullName)).OrderBy(f => f.TabOrder).Select(f => f.FullName).Distinct().ToArray();
        Assert.Equal(order, tab);
        var widgets = Inspect.Read(reordered, d =>
        {
            var annots = d.Pages[0].Elements.GetArray("/Annots")!;
            return annots.Elements.Select(e => ((e as PdfSharp.Pdf.Advanced.PdfReference)?.Value as PdfSharp.Pdf.PdfDictionary)?.Elements.GetString("/T") ?? "").Where(order.Contains).ToArray();
        });
        Assert.Equal(order, widgets);
    }

    // ================================================================== 2.11 porównanie – raport w polskiej ścieżce

    [Theory]
    [InlineData(CompareReportFormat.Html, "raport porównania ąę.html")]
    [InlineData(CompareReportFormat.Text, "raport porównania ąę.txt")]
    public async Task Compare_report_is_written_to_polish_path_with_changes(CompareReportFormat format, string name)
    {
        using var host = new TestHost();
        var cmp = host.Get<ICompareService>();
        var a = SamplePdfFactory.Text(2, "Cena wynosi sto złotych");
        var b = SamplePdfFactory.Text(2, "Cena wynosi dwieście złotych");
        var result = await cmp.CompareAsync(a, "przed ą.pdf", b, "po ę.pdf", new CompareOptions());
        Assert.Contains(result.Changes, c => c.TextA.Contains("sto") && c.TextB.Contains("dwieście"));
        var path = Path.Combine(TestPaths.NewDirectory(), name);
        await cmp.ExportReportAsync(result, path, format);
        var text = File.ReadAllText(path);
        Assert.Contains("dwieście", text);
        Assert.Contains("po ę.pdf", text);
    }

    // ================================================================== 2.12 Kreator – własne działanie

    [Fact]
    public async Task Custom_action_with_watermark_metadata_ocr_and_encryption_runs_on_polish_files()
    {
        using var host = new TestHost();
        var wizard = host.Get<IActionWizard>();
        var action = new ActionDefinition
        {
            Id = ActionWizard.NewId(),
            Name = "Moje działanie źdźbło",
            Steps = new[]
            {
                ActionWizard.Step(ActionStepTypes.Ocr, new OcrOptions()),
                ActionWizard.Step(ActionStepTypes.Watermark, new WatermarkOptions { Text = "WEWNĘTRZNE" }),
                ActionWizard.Step(ActionStepTypes.Metadata, new MetadataStepOptions { Title = "{file}", Author = "Dział Łączności", OnlyIfEmpty = false }),
                ActionWizard.Step(ActionStepTypes.Encrypt, new EncryptStepOptions { UserPassword = "otwórz-ą", OwnerPassword = "zmień-ę", Permissions = DocumentPermissions.Print }),
            },
        };
        await wizard.SaveActionAsync(action with { Steps = action.Steps });
        Assert.Contains(await wizard.GetActionsAsync(), x => x.Name == "Moje działanie źdźbło");

        var input = TestPaths.WriteSample(SamplePdfFactory.Scanned(), "skan do kreatora ą.pdf");
        var outDir = Path.Combine(TestPaths.NewDirectory(), "kreator wynik ść");
        var report = await wizard.RunAsync(action, new[] { input }, new ActionRunOptions { OutputDirectory = outDir, FileNameSuffix = "_gotowe" });
        var r = Assert.Single(report.Files);
        Assert.True(r.Success, r.Error);
        var output = File.ReadAllBytes(r.OutputPath!);
        await Assert.ThrowsAsync<PdfPasswordRequiredException>(() => Inspect.Renderer.OpenAsync(output));
        var text = await Inspect.PdfiumText(output, "otwórz-ą");
        Assert.Contains("Faktura", text);
        Assert.Contains("WEWNĘTRZNE", text);
        using var pig = PigDocument.Open(output, new UglyToad.PdfPig.ParsingOptions { Password = "otwórz-ą" });
        Assert.Equal("skan do kreatora ą", pig.Information.Title);
        Assert.Equal("Dział Łączności", pig.Information.Author);
        var probe = await Inspect.Renderer.ProbeAsync(output, "otwórz-ą");
        Assert.False(probe.Permissions.HasFlag(DocumentPermissions.ModifyContent));
    }
}
