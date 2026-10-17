using PdfSharp.Pdf;
using ZetPDF.Core.Contracts;
using ZetPDF.IntegrationTests.Infrastructure;
using ZetPDF.TestData;

namespace ZetPDF.IntegrationTests;

/// <summary>
/// Katalog operacji „bajty → bajty” każdego modułu (przez kontrakty z DI) z próbką wejściową i sprawdzeniem wyniku
/// W PLIKU. Używany przez testy Cofnij/Ponów, zapisu w ścieżkach z polskimi znakami i wejścia zaszyfrowanego.
/// </summary>
internal sealed record Operation(
    string Name,
    string Module,
    Func<byte[]> Sample,
    Func<TestHost, byte[], Task<byte[]>> Run,
    Func<TestHost, byte[], Task> Verify,
    DocumentPermissions Permission = DocumentPermissions.ModifyContent);

internal static class Operations
{
    public const string Polish = "Zażółć gęślą jaźń";

    private static readonly PdfRect Area = new(56, 690, 300, 730);

    public static IReadOnlyList<Operation> All { get; } = new Operation[]
    {
        // ---------------------------------------------------------------- pages-engine
        new("Pages.Rotate", "pages-engine", () => SamplePdfFactory.Text(3),
            (h, pdf) => h.Get<IPageOperations>().RotateAsync(pdf, new[] { 1 }, 90),
            (_, pdf) => { Assert.Equal(new[] { 0, 90, 0 }, Inspect.Read(pdf, d => d.Pages.Cast<PdfPage>().Select(p => p.Rotate).ToArray())); return Task.CompletedTask; },
            DocumentPermissions.AssembleDocument),
        new("Pages.Delete", "pages-engine", () => SamplePdfFactory.Text(3),
            (h, pdf) => h.Get<IPageOperations>().DeleteAsync(pdf, new[] { 1 }),
            (_, pdf) =>
            {
                Assert.Equal(2, Inspect.PageCount(pdf));
                Assert.DoesNotContain("Strona 2", Inspect.PigText(pdf));
                return Task.CompletedTask;
            }, DocumentPermissions.AssembleDocument),
        new("Pages.InsertBlank", "pages-engine", () => SamplePdfFactory.Text(2),
            (h, pdf) => h.Get<IPageOperations>().InsertBlankAsync(pdf, 1, 2, PdfSize.A4),
            (_, pdf) => { Assert.Equal(4, Inspect.PageCount(pdf)); Assert.Contains("Strona 2", Inspect.PigPageText(pdf, 3)); return Task.CompletedTask; },
            DocumentPermissions.AssembleDocument),
        new("Pages.Duplicate", "pages-engine", () => SamplePdfFactory.Text(2),
            (h, pdf) => h.Get<IPageOperations>().DuplicateAsync(pdf, new[] { 0 }),
            (_, pdf) => { Assert.Equal(3, Inspect.PageCount(pdf)); Assert.Contains("Strona 1", Inspect.PigPageText(pdf, 1)); return Task.CompletedTask; },
            DocumentPermissions.AssembleDocument),
        new("Pages.Reverse", "pages-engine", () => SamplePdfFactory.Text(3),
            (h, pdf) => h.Get<IPageOperations>().ReverseAsync(pdf, null),
            (_, pdf) => { Assert.Contains("Strona 3", Inspect.PigPageText(pdf, 0)); Assert.Contains("Strona 1", Inspect.PigPageText(pdf, 2)); return Task.CompletedTask; },
            DocumentPermissions.AssembleDocument),
        new("Pages.Crop", "pages-engine", () => SamplePdfFactory.Text(1),
            (h, pdf) => h.Get<IPageOperations>().CropAsync(pdf, new[] { 0 }, new CropOptions { Left = 50, Right = 50, Top = 20, Bottom = 20 }),
            (_, pdf) =>
            {
                var (w, hgt) = Inspect.Read(pdf, d => (d.Pages[0].CropBox.Width, d.Pages[0].CropBox.Height));
                Assert.InRange(w, PdfSize.A4.Width - 101, PdfSize.A4.Width - 99);
                Assert.InRange(hgt, PdfSize.A4.Height - 41, PdfSize.A4.Height - 39);
                return Task.CompletedTask;
            }),
        new("Pages.Watermark", "pages-engine", () => SamplePdfFactory.Text(2),
            (h, pdf) => h.Get<IStampingService>().AddWatermarkAsync(pdf, new WatermarkOptions { Text = "POUFNE ŻÓŁĆ" }),
            async (h, pdf) =>
            {
                Assert.Contains("POUFNEŻÓŁĆ", Inspect.Squash(Inspect.PigText(pdf)));
                Assert.Contains("POUFNEŻÓŁĆ", Inspect.Squash(await Inspect.PdfiumText(pdf)));
                Assert.True((await h.Get<IStampingService>().DetectMarksAsync(pdf)).HasFlag(PageMarkKind.Watermark));
            }),
        new("Pages.HeaderFooter", "pages-engine", () => SamplePdfFactory.Text(2),
            (h, pdf) => h.Get<IStampingService>().AddHeaderFooterAsync(pdf, new HeaderFooterOptions { HeaderLeft = "Nagłówek źdźbło", FooterRight = "Str. <<page>> z <<pages>>" }),
            (_, pdf) =>
            {
                var t = Inspect.Squash(Inspect.PigPageText(pdf, 1));
                Assert.Contains("Nagłówekźdźbło", t);
                Assert.Contains("Str.2z2", t);
                return Task.CompletedTask;
            }),
        new("Pages.Bates", "pages-engine", () => SamplePdfFactory.Text(3),
            async (h, pdf) => (await h.Get<IStampingService>().AddBatesAsync(pdf, new BatesOptions { Prefix = "ŻÓŁW-", Digits = 4 }, 7)).Pdf,
            (_, pdf) => { Assert.Contains("ŻÓŁW-0009", Inspect.PigPageText(pdf, 2)); return Task.CompletedTask; }),
        new("Pages.PageNumbers", "pages-engine", () => SamplePdfFactory.Text(3),
            (h, pdf) => h.Get<IStampingService>().AddPageNumbersAsync(pdf, new PageNumberOptions { Format = "Strona nr <<page>> z <<pages>>" }),
            (_, pdf) => { Assert.Contains("Stronanr3z3", Inspect.Squash(Inspect.PigPageText(pdf, 2))); return Task.CompletedTask; }),
        new("Pages.Grayscale", "pages-engine", () => ExtraSamples.ColoredPages(2, 0),
            (h, pdf) => h.Get<IOptimizerService>().ConvertToGrayscaleAsync(pdf, false),
            async (_, pdf) =>
            {
                var (_, color, _) = await Inspect.RenderStats(pdf, 0);
                Assert.True(color < 0.01, $"kolorowe piksele: {color:P1}");
                Assert.Contains("Strona 1", Inspect.PigText(pdf));
            }),
        new("Pages.Sanitize", "pages-engine", ExtraSamples.WithEverythingHidden,
            async (h, pdf) => (await h.Get<ISanitizeService>().SanitizeAsync(pdf, new SanitizeOptions())).Pdf,
            (_, pdf) =>
            {
                Assert.False(Inspect.ContainsAnywhere(pdf, "Tajny tytuł"));
                Assert.False(Inspect.ContainsAnywhere(pdf, ExtraSamples.XmpSecret));
                Assert.False(Inspect.ContainsAnywhere(pdf, ExtraSamples.JsMarker));
                return Task.CompletedTask;
            }),
        new("Pages.Optimize", "pages-engine", () => SamplePdfFactory.Scanned(),
            async (h, pdf) => (await h.Get<IOptimizerService>().OptimizeAsync(pdf, new OptimizeOptions { MaxImageDpi = 72, JpegQuality = 50 })).Pdf,
            async (_, pdf) => { Assert.Equal(1, await Inspect.PdfiumPageCount(pdf)); }),

        // ---------------------------------------------------------------- renderer-viewer (zakładki)
        new("Viewer.Bookmarks", "renderer-viewer", () => SamplePdfFactory.Text(3),
            (h, pdf) => h.Get<IBookmarkService>().SetBookmarksAsync(pdf, new[]
            {
                new BookmarkNode("Rozdział źdźbło", 0, new[] { new BookmarkNode("Podrozdział ąę", 2, BookmarkNode.NoChildren) }, IsOpen: true),
            }),
            async (_, pdf) =>
            {
                using var doc = await Inspect.Renderer.OpenAsync(pdf);
                var tree = await doc.GetBookmarksAsync();
                var root = Assert.Single(tree);
                Assert.Equal("Rozdział źdźbło", root.Title);
                Assert.Equal(0, root.PageIndex);
                var child = Assert.Single(root.Children);
                Assert.Equal("Podrozdział ąę", child.Title);
                Assert.Equal(2, child.PageIndex);
            }, DocumentPermissions.AssembleDocument),

        // ---------------------------------------------------------------- annotations-forms
        new("Annot.Highlight", "annotations-forms", () => SamplePdfFactory.Text(1),
            async (h, pdf) => (await h.Get<IAnnotationService>().AddAsync(pdf, new AnnotationSpec
            {
                Kind = AnnotationKind.Highlight, PageIndex = 0, Contents = "Wyróżnienie – żółć",
                Quads = new[] { PdfQuad.FromRect(Area) }, Author = "Tester Ąę",
            })).Pdf,
            (_, pdf) =>
            {
                Assert.Contains((0, "/Highlight"), Inspect.Annotations(pdf));
                Assert.True(Inspect.ContainsAnywhere(pdf, "Wyróżnienie – żółć"));
                return Task.CompletedTask;
            }, DocumentPermissions.Annotate),
        new("Annot.Note", "annotations-forms", () => SamplePdfFactory.Text(1),
            async (h, pdf) => (await h.Get<IAnnotationService>().AddAsync(pdf, new AnnotationSpec
            {
                Kind = AnnotationKind.Note, PageIndex = 0, Rect = new PdfRect(400, 700, 420, 720), Contents = "Notatka źdźbło",
            })).Pdf,
            async (h, pdf) =>
            {
                Assert.Contains((0, "/Text"), Inspect.Annotations(pdf));
                var a = Assert.Single(await h.Get<IAnnotationService>().GetAnnotationsAsync(pdf), x => x.Kind == AnnotationKind.Note);
                Assert.Equal("Notatka źdźbło", a.Contents);
            }, DocumentPermissions.Annotate),
        new("Annot.Ink", "annotations-forms", () => SamplePdfFactory.Text(1),
            async (h, pdf) => (await h.Get<IAnnotationService>().AddAsync(pdf, new AnnotationSpec
            {
                Kind = AnnotationKind.Ink, PageIndex = 0, Color = PdfColor.Red, LineWidth = 3,
                InkStrokes = new[] { (IReadOnlyList<PdfPoint>)new[] { new PdfPoint(100, 300), new PdfPoint(150, 350), new PdfPoint(200, 300) } },
            })).Pdf,
            async (_, pdf) =>
            {
                Assert.Contains((0, "/Ink"), Inspect.Annotations(pdf));
                var (_, color, _) = await Inspect.RenderStats(pdf, 0, 72);
                Assert.True(color > 0.0005, "kreska ołówka niewidoczna w renderze");
            }, DocumentPermissions.Annotate),
        new("Annot.DeleteAll", "annotations-forms", SamplePdfFactory.WithAnnotations,
            (h, pdf) => h.Get<IAnnotationService>().DeleteAllAsync(pdf),
            (_, pdf) => { Assert.Empty(Inspect.Annotations(pdf)); return Task.CompletedTask; }, DocumentPermissions.Annotate),
        new("Annot.FormFill", "annotations-forms", SamplePdfFactory.WithForm,
            (h, pdf) => h.Get<IFormService>().SetValuesAsync(pdf, new Dictionary<string, string> { ["imie"] = "Łukasz Żółć", ["miasto"] = "Kraków" }),
            async (h, pdf) =>
            {
                var f = await h.Get<IFormService>().GetFieldsAsync(pdf);
                Assert.Equal("Łukasz Żółć", f.Single(x => x.FullName == "imie").Value);
                Assert.Equal("Kraków", f.Single(x => x.FullName == "miasto").Value);
                using var pig = UglyToad.PdfPig.PdfDocument.Open(pdf);
                Assert.True(pig.TryGetForm(out var form));
                Assert.Contains(form.Fields, x => x.Information.PartialName == "imie");
            }, DocumentPermissions.FillForms),
        new("Annot.CreateField", "annotations-forms", () => SamplePdfFactory.Text(1),
            (h, pdf) => h.Get<IFormService>().CreateFieldAsync(pdf, new FormFieldSpec { Kind = FormFieldKind.Text, Name = "nazwisko", PageIndex = 0, Rect = new PdfRect(56, 400, 256, 422), DefaultValue = "Gęślą" }),
            async (h, pdf) =>
            {
                var f = Assert.Single(await h.Get<IFormService>().GetFieldsAsync(pdf));
                Assert.Equal("nazwisko", f.FullName);
                Assert.Contains((0, "/Widget"), Inspect.Annotations(pdf));
            }),
        new("Annot.Attachment", "annotations-forms", () => SamplePdfFactory.Text(1),
            (h, pdf) =>
            {
                var file = Path.Combine(TestPaths.NewDirectory(), "załącznik źdźbło.txt");
                File.WriteAllText(file, "treść załącznika");
                return h.Get<IAttachmentService>().AddAsync(pdf, file, "opis ąę");
            },
            async (h, pdf) =>
            {
                var a = Assert.Single(await h.Get<IAttachmentService>().GetAttachmentsAsync(pdf));
                Assert.Equal("załącznik źdźbło.txt", a.Name);
                Assert.Equal("treść załącznika", System.Text.Encoding.UTF8.GetString(await h.Get<IAttachmentService>().ExtractAsync(pdf, a.Name)));
            }, DocumentPermissions.Annotate),

        // ---------------------------------------------------------------- edit-convert-ocr
        new("Edit.AddText", "edit-convert-ocr", () => SamplePdfFactory.Text(1),
            (h, pdf) => h.Get<ITextEditService>().AddTextAsync(pdf, 0, new PdfPoint(60, 300), "Dodany tekst źdźbło", new TextStyle { FontSize = 16, Color = PdfColor.Red }),
            async (_, pdf) =>
            {
                Assert.Contains("Dodanytekstźdźbło", Inspect.Squash(Inspect.PigText(pdf)));
                Assert.Contains("Dodanytekstźdźbło", Inspect.Squash(await Inspect.PdfiumText(pdf)));
            }),
        new("Edit.ReplaceTextBlock", "edit-convert-ocr", () => SamplePdfFactory.Text(1),
            async (h, pdf) =>
            {
                var svc = h.Get<ITextEditService>();
                var block = (await svc.GetTextBlocksAsync(pdf, 0)).First(b => b.Text.Contains("quick brown"));
                return await svc.ReplaceTextBlockAsync(pdf, 0, block.Id, "Nowa treść źrebię", new TextStyle { FontSize = 12 });
            },
            async (_, pdf) =>
            {
                var pig = Inspect.PigText(pdf);
                Assert.Contains("Nowatreśćźrebię", Inspect.Squash(pig));
                Assert.DoesNotContain("quick brown", pig);
                Assert.DoesNotContain("quick brown", await Inspect.PdfiumText(pdf));
            }),
        new("Edit.FindReplace", "edit-convert-ocr", () => SamplePdfFactory.Text(2),
            async (h, pdf) => (await h.Get<ITextEditService>().FindReplaceAsync(pdf, "lazy dog", "leniwy żółw", TextSearchOptions.None)).Pdf,
            (_, pdf) =>
            {
                var t = Inspect.PigText(pdf);
                Assert.DoesNotContain("lazy dog", t);
                Assert.Equal(2, CountOf(Inspect.Squash(t), "leniwyżółw") / 2); // Text + słowa na każdej z 2 stron
                return Task.CompletedTask;
            }),
        new("Edit.AddImage", "edit-convert-ocr", () => SamplePdfFactory.Text(1),
            (h, pdf) => h.Get<IObjectEditService>().AddImageAsync(pdf, 0, ExtraSamples.Png(), new PdfRect(100, 200, 300, 333)),
            async (h, pdf) =>
            {
                var objs = await h.Get<IObjectEditService>().GetObjectsAsync(pdf, 0);
                Assert.Contains(objs, o => o.Kind == PageObjectKind.Image && Math.Abs(o.Bounds.Left - 100) < 1 && Math.Abs(o.Bounds.Top - 333) < 1);
                using var pig = UglyToad.PdfPig.PdfDocument.Open(pdf);
                Assert.Single(pig.GetPage(1).GetImages());
            }),
        new("Edit.AddShape", "edit-convert-ocr", () => SamplePdfFactory.Text(1),
            (h, pdf) => h.Get<IObjectEditService>().AddShapeAsync(pdf, 0, new ShapeSpec { Kind = ShapeKind.Rectangle, Rect = new PdfRect(0, 0, 595, 400), Fill = PdfColor.Red, Stroke = null }),
            async (_, pdf) =>
            {
                var (_, color, _) = await Inspect.RenderStats(pdf, 0);
                Assert.True(color > 0.3, $"czerwony prostokąt: {color:P0}");
            }),
        new("Edit.AddLink", "edit-convert-ocr", () => SamplePdfFactory.Text(2),
            (h, pdf) => h.Get<ILinkService>().AddLinkAsync(pdf, 0, new PdfRect(56, 60, 200, 90), new LinkTarget(PageIndex: 1)),
            async (_, pdf) =>
            {
                using var doc = await Inspect.Renderer.OpenAsync(pdf);
                var link = Assert.Single(await doc.GetLinksAsync(0));
                Assert.Equal(PdfLinkKind.GoToPage, link.Kind);
                Assert.Equal(1, link.TargetPageIndex);
            }, DocumentPermissions.Annotate),
        new("Edit.LinksFromUrls", "edit-convert-ocr", () => ExtraSamples.WithUrls(),
            async (h, pdf) => (await h.Get<ILinkService>().CreateLinksFromUrlsAsync(pdf)).Pdf,
            async (_, pdf) =>
            {
                using var doc = await Inspect.Renderer.OpenAsync(pdf);
                var links = await doc.GetLinksAsync(0);
                Assert.Contains(links, l => l.Kind == PdfLinkKind.Uri && l.Uri!.StartsWith("https://example.com", StringComparison.Ordinal));
                Assert.Contains(links, l => l.Kind == PdfLinkKind.Uri && l.Uri!.Contains("kontakt@example.com", StringComparison.Ordinal));
            }, DocumentPermissions.Annotate),
        new("Edit.BookmarksFromHeadings", "edit-convert-ocr", () => SamplePdfFactory.Text(3),
            async (h, pdf) =>
            {
                var tree = await h.Get<ILinkService>().GenerateBookmarksFromHeadingsAsync(pdf, new HeadingDetectionOptions());
                return await h.Get<IBookmarkService>().SetBookmarksAsync(pdf, tree);
            },
            async (_, pdf) =>
            {
                using var doc = await Inspect.Renderer.OpenAsync(pdf);
                var titles = Flatten(await doc.GetBookmarksAsync()).Select(b => (b.Title, b.PageIndex)).ToList();
                Assert.Contains(("Strona 3", 2), titles);
            }, DocumentPermissions.AssembleDocument),
        new("Edit.Redact", "edit-convert-ocr", () => SamplePdfFactory.WithSensitiveData().Pdf!,
            async (h, pdf) =>
            {
                var red = h.Get<IRedactionService>();
                var found = await red.FindAsync(pdf, new RedactionSearch { Patterns = SensitiveDataPattern.Pesel | SensitiveDataPattern.Email });
                Assert.Equal(2, found.Count);
                var marked = await red.MarkAsync(pdf, found.Select(c => new RedactionMark(c.PageIndex, c.Quads)).ToList());
                return await red.ApplyAsync(marked, new RedactionApplyOptions());
            },
            async (_, pdf) =>
            {
                var s = SamplePdfFactory.WithSensitiveData();
                Assert.DoesNotContain(s.Pesel, Inspect.PigText(pdf));
                Assert.DoesNotContain(s.Pesel, await Inspect.PdfiumText(pdf));
                Assert.DoesNotContain(s.Email, Inspect.PigText(pdf));
                Assert.Contains("NIP", Inspect.PigText(pdf)); // reszta zostaje
            }),
        new("Ocr.Document", "edit-convert-ocr", () => SamplePdfFactory.Scanned(),
            (h, pdf) => h.Get<IOcrService>().OcrDocumentAsync(pdf, new OcrOptions()),
            async (_, pdf) =>
            {
                var pig = Inspect.PigText(pdf);
                Assert.Contains("Faktura", pig);
                Assert.Contains("12345", pig);
                Assert.Contains("Faktura", await Inspect.PdfiumText(pdf));
            }),

        // ---------------------------------------------------------------- core (metadane)
        new("Core.Metadata", "core", () => SamplePdfFactory.Text(1),
            (h, pdf) => h.Get<IMetadataService>().SetAsync(pdf, new DocumentMetadata { Title = "Tytuł źdźbło", Author = "Autor Ćma", Subject = "Temat", Keywords = "ą; ę" }),
            async (h, pdf) =>
            {
                var m = await h.Get<IMetadataService>().GetAsync(pdf);
                Assert.Equal("Tytuł źdźbło", m.Title);
                using var pig = UglyToad.PdfPig.PdfDocument.Open(pdf);
                Assert.Equal("Tytuł źdźbło", pig.Information.Title);
                Assert.Equal("Autor Ćma", pig.Information.Author);
            }),
    };

    public static IEnumerable<object[]> Names() => All.Select(o => new object[] { o.Name });

    public static Operation Get(string name) => All.Single(o => o.Name == name);

    public static IEnumerable<BookmarkNode> Flatten(IEnumerable<BookmarkNode> nodes) => nodes.SelectMany(n => new[] { n }.Concat(Flatten(n.Children)));

    public static int CountOf(string text, string what)
    {
        int n = 0, i = 0;
        while ((i = text.IndexOf(what, i, StringComparison.Ordinal)) >= 0) { n++; i += what.Length; }
        return n;
    }
}
