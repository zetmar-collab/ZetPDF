using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using PdfSharp.Pdf;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.IntegrationTests.Infrastructure;
using ZetPDF.TestData;

namespace ZetPDF.IntegrationTests;

/// <summary>Przypadki brzegowe: redakcja pól/adnotacji/XMP, dzielenie i wyodrębnianie do polskich nazw, stan „zmieniony”, migawka.</summary>
public class EdgeCaseTests
{
    private const string Secret = ExtraSamples.SecretPhrase;

    [Fact]
    public async Task Redaction_removes_comment_and_form_field_inside_area_and_phrase_from_xmp()
    {
        using var host = new TestHost();
        var red = host.Get<IRedactionService>();
        var src = ExtraSamples.WithLiteralText(new[] { "Jawne", $"Numer {Secret}" }, title: "Tytuł jawny");
        // XMP z frazą
        src = PdfSharpEdit.Modify(src, d =>
        {
            var meta = new PdfDictionary(d);
            meta.Elements["/Type"] = new PdfName("/Metadata");
            meta.Elements["/Subtype"] = new PdfName("/XML");
            meta.CreateStream(System.Text.Encoding.UTF8.GetBytes($"<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:description>{Secret}</dc:description></rdf:Description></rdf:RDF></x:xmpmeta>"));
            d.Internals.AddObject(meta);
            d.Internals.Catalog.Elements["/Metadata"] = meta.Reference!;
        });
        var hit = Assert.Single(await red.FindAsync(src, new RedactionSearch { Phrases = new[] { Secret } }));
        var box = hit.Quads[0].Bounds;
        // notatka z frazą w treści i pole tekstowe z frazą w wartości – oba w obszarze redakcji
        src = (await host.Get<IAnnotationService>().AddAsync(src, new AnnotationSpec { Kind = AnnotationKind.Note, PageIndex = 0, Rect = new PdfRect(box.Left, box.Bottom, box.Left + 16, box.Bottom + 16), Contents = $"Uwaga {Secret}" })).Pdf;
        src = await host.Get<IFormService>().CreateFieldAsync(src, new FormFieldSpec { Kind = FormFieldKind.Text, Name = "pole", PageIndex = 0, Rect = box.Inflate(2), DefaultValue = Secret });
        src = await host.Get<IFormService>().SetValuesAsync(src, new Dictionary<string, string> { ["pole"] = Secret });
        Assert.True(Inspect.ContainsAnywhere(src, Secret));

        var result = await red.ApplyAsync(await red.MarkAsync(src, new[] { new RedactionMark(0, hit.Quads) }), new RedactionApplyOptions());
        Assert.False(Inspect.ContainsAnywhere(result, Secret), "fraza została w adnotacji, polu, XMP lub treści");
        Assert.DoesNotContain(Inspect.Annotations(result), a => a.Subtype is "/Text" or "/Widget");
        Assert.Equal("Tytuł jawny", (await host.Get<IMetadataService>().GetAsync(result)).Title);
    }

    [Fact]
    public async Task Split_by_bookmarks_and_extract_to_files_with_polish_names()
    {
        using var host = new TestHost();
        var pages = host.Get<IPageOperations>();
        var pdf = await host.Get<IBookmarkService>().SetBookmarksAsync(SamplePdfFactory.Text(5), new[]
        {
            new BookmarkNode("Wstęp – źdźbło", 0, BookmarkNode.NoChildren),
            new BookmarkNode("Rozdział łąka", 2, BookmarkNode.NoChildren),
        });
        var dir = Path.Combine(TestPaths.NewDirectory(), "podział ąę");
        Directory.CreateDirectory(dir);
        var parts = await pages.SplitAsync(pdf, new SplitOptions { Mode = SplitMode.TopLevelBookmarks, FileNamePattern = "{name} {bookmark}" }, dir, "dokument ść");
        Assert.Equal(2, parts.Count);
        Assert.Contains(parts, p => Path.GetFileName(p).Contains("łąka") && Inspect.PageCount(File.ReadAllBytes(p)) == 3);
        Assert.Contains(parts, p => Path.GetFileName(p).Contains("Wstęp") && Inspect.PageCount(File.ReadAllBytes(p)) == 2);
        Assert.All(parts, p => Assert.StartsWith("dokument ść", Path.GetFileName(p)));

        var every2 = await pages.SplitAsync(pdf, new SplitOptions { Mode = SplitMode.EveryNPages, PagesPerFile = 2 }, dir, "co dwie żółw");
        Assert.Equal(new[] { 2, 2, 1 }, every2.Select(p => Inspect.PageCount(File.ReadAllBytes(p))).ToArray());

        var extracted = await pages.ExtractToFilesAsync(pdf, new[] { 1, 3 }, dir, "{name}_{page}", "wyciąg ęą");
        Assert.Equal(2, extracted.Count);
        Assert.Contains("Strona 2", Inspect.PigText(File.ReadAllBytes(extracted[0])));
        Assert.Contains("Strona 4", Inspect.PigText(File.ReadAllBytes(extracted[1])));
    }

    [Fact]
    public async Task Merge_files_from_polish_paths_adds_bookmark_per_file_name()
    {
        using var host = new TestHost();
        var a = TestPaths.WriteSample(SamplePdfFactory.Text(1), "umowa źdźbło.pdf");
        var b = TestPaths.WriteSample(SamplePdfFactory.WithBookmarks(1), "aneks łąka.pdf");
        var merged = await host.Get<IPageOperations>().MergeAsync(new[] { new MergeSource(a), new MergeSource(b) }, new MergeOptions { BookmarkPerFile = true, KeepBookmarks = true });
        Assert.Equal(3, Inspect.PageCount(merged));
        using var doc = await Inspect.Renderer.OpenAsync(merged);
        var tree = await doc.GetBookmarksAsync();
        Assert.Contains(tree, n => n.Title.Contains("umowa źdźbło") && n.PageIndex == 0);
        var second = tree.Single(n => n.Title.Contains("aneks łąka"));
        Assert.Equal(1, second.PageIndex);
        Assert.Contains(Operations.Flatten(second.Children), n => n.Title == "Rozdział 1"); // zakładki pliku zachowane
    }

    [Fact]
    public async Task Modified_flag_follows_saved_version_through_undo_and_redo()
    {
        using var host = new TestHost();
        var path = TestPaths.WriteSample(SamplePdfFactory.Text(2), "stan zmian ą.pdf");
        var s = (await host.Documents.OpenAsync(path))!;
        var rotate = Operations.Get("Pages.Rotate");
        IDocumentCommand Cmd() => new DelegateDocumentCommand("Test", (pdf, _, _) => rotate.Run(host, pdf), DocumentChangeKind.PageContent, rotate.Permission);
        await s.ExecuteAsync(Cmd());
        await host.Documents.SaveAsync(s);
        Assert.False(s.IsModified);
        await s.History.UndoAsync();
        Assert.True(s.IsModified); // przed zapisaną wersją
        await s.History.RedoAsync();
        Assert.False(s.IsModified); // z powrotem w zapisanej wersji
        Assert.Equal(File.ReadAllBytes(path), s.Current.GetBytes());
    }

    [Fact]
    public void Snapshot_tool_copies_selected_area_to_clipboard_at_high_resolution() => Sta.Run(async () =>
    {
        IDataObject? saved = null;
        try { saved = Clipboard.GetDataObject(); } catch (Exception) { }
        using var host = new TestHost(initializeModules: true);
        var session = await host.Documents.CreateNewAsync(SamplePdfFactory.Text(1), "migawka.pdf");
        host.Documents.ActiveDocument = session;
        var view = host.Get<IDocumentViewFactory>().CreateView(session);
        try
        {
            Sta.Layout(view.Element);
            await Sta.WaitFor(() => view.GetPageTransform(0) is not null, 20000, "strona");
            Clipboard.Clear();
            Assert.True(host.Get<ICommandRegistry>().TryExecute(CommandIds.Tool.Snapshot));
            var tool = host.Get<IToolService>().ActiveTool;
            Assert.Equal(CommandIds.Tool.Snapshot, tool.Id);
            var t = view.GetPageTransform(0)!;
            PagePointerEventArgs Args(PdfPoint p, MouseButton? b) => new(0, p, t.ToView(p), b, ModifierKeys.None, 1);
            var a = new PdfPoint(50, 680);
            var b = new PdfPoint(350, 740); // linia z pangramem (72 pt × 300 / 72 = 1250 px szerokości)
            tool.OnPointerPressed(view, Args(a, MouseButton.Left));
            tool.OnPointerMoved(view, Args(b, null));
            tool.OnPointerReleased(view, Args(b, MouseButton.Left));
            await Sta.WaitFor(() => Clipboard.ContainsImage(), 20000, "obraz w schowku");
            var img = Clipboard.GetImage();
            Assert.InRange(img.PixelWidth, 1200, 1300);
            Assert.InRange(img.PixelHeight, 230, 270);
            var conv = new FormatConvertedBitmap(img, System.Windows.Media.PixelFormats.Gray8, null, 0);
            var px = new byte[conv.PixelWidth * conv.PixelHeight];
            conv.CopyPixels(px, conv.PixelWidth, 0);
            Assert.True(px.Count(p => p < 100) > px.Length / 200, "migawka bez tekstu");
        }
        finally
        {
            view.Dispose();
            try { if (saved is not null) Clipboard.SetDataObject(saved, true); } catch (Exception) { }
        }
    });

    [Fact]
    public void Copy_selected_text_with_polish_characters_to_clipboard() => Sta.Run(async () =>
    {
        IDataObject? saved = null;
        try { saved = Clipboard.GetDataObject(); } catch (Exception) { }
        using var host = new TestHost(initializeModules: true);
        var session = await host.Documents.CreateNewAsync(SamplePdfFactory.Text(1), "kopiuj.pdf");
        host.Documents.ActiveDocument = session;
        var view = host.Get<IDocumentViewFactory>().CreateView(session);
        try
        {
            Sta.Layout(view.Element);
            await Sta.WaitFor(() => view.GetPageTransform(0) is not null, 20000, "strona");
            var r = host.Get<ICommandRegistry>();
            Assert.True(r.TryExecute(CommandIds.Edit.SelectAll));
            await Sta.WaitFor(() => view.TextSelection is not null, 20000, "zaznaczenie");
            Clipboard.Clear();
            Assert.True(r.TryExecute(CommandIds.Edit.Copy));
            await Sta.WaitFor(() => Clipboard.ContainsText(), 10000, "tekst w schowku");
            Assert.Contains(SamplePdfFactory.PolishPangram, Clipboard.GetText());
        }
        finally
        {
            view.Dispose();
            try { if (saved is not null) Clipboard.SetDataObject(saved, true); } catch (Exception) { }
        }
    });
}
