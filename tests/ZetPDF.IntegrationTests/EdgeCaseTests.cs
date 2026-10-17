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
}
