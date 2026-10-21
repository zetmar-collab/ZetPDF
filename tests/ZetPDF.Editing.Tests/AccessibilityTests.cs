using Microsoft.Extensions.DependencyInjection;
using PdfSharp.Pdf;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Core.Services;
using ZetPDF.Editing.Accessibility;
using ZetPDF.TestData;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace ZetPDF.Editing.Tests;

public class AccessibilityTests
{
    private static readonly AccessibilityService Service = new();

    private static byte[] Untitled(int pages = 2) => PdfSharpEdit.Modify(SamplePdfFactory.Text(pages), d => d.Info.Elements.Remove("/Title"));

    /// <summary>Dokument otagowany: /MarkInfo, /StructTreeRoot → Document → P + 2× Figure (jedna z /Alt), RoleMap Obraz → Figure.</summary>
    private static byte[] Tagged() => PdfSharpEdit.Modify(SamplePdfFactory.Text(2), doc =>
    {
        var cat = doc.Internals.Catalog;
        var mark = new PdfDictionary(doc);
        mark.Elements.SetBoolean("/Marked", true);
        cat.Elements["/MarkInfo"] = mark;
        var root = new PdfDictionary(doc);
        root.Elements.SetName("/Type", "/StructTreeRoot");
        doc.Internals.AddObject(root);
        var roleMap = new PdfDictionary(doc);
        roleMap.Elements.SetName("/Obraz", "/Figure");
        root.Elements["/RoleMap"] = roleMap;
        PdfDictionary Elem(string s, PdfDictionary parent, int? page)
        {
            var e = new PdfDictionary(doc);
            e.Elements.SetName("/Type", "/StructElem");
            e.Elements.SetName("/S", s);
            e.Elements["/P"] = parent.Reference!;
            if (page is int p) e.Elements["/Pg"] = doc.Pages[p].Reference!;
            doc.Internals.AddObject(e);
            return e;
        }
        var document = Elem("/Document", root, 0);
        var p1 = Elem("/P", document, 0);
        var fig1 = Elem("/Figure", document, 0);
        fig1.Elements["/Alt"] = new PdfString("Logo firmy");
        var fig2 = Elem("/Obraz", document, 1);
        document.Elements["/K"] = new PdfArray(doc, p1.Reference!, fig1.Reference!, fig2.Reference!);
        root.Elements["/K"] = document.Reference!;
        cat.Elements["/StructTreeRoot"] = root.Reference!;
    });

    [Fact]
    public async Task Full_check_without_title_fails_and_fix_sets_title_language_and_display_title()
    {
        var pdf = Untitled();
        var report = await Service.CheckAsync(pdf);
        Assert.Equal(AccessCheckStatus.Failed, report[AccessRule.DocTitle].Status);
        Assert.True(report[AccessRule.DocTitle].CanFix);
        Assert.Equal(AccessCheckStatus.Failed, report[AccessRule.Language].Status);
        Assert.Equal(AccessCheckStatus.Failed, report[AccessRule.DisplayDocTitle].Status);
        Assert.Equal(AccessCheckStatus.Failed, report[AccessRule.Tagged].Status);
        Assert.False(report[AccessRule.Tagged].CanFix);
        Assert.Equal(AccessCheckStatus.Manual, report[AccessRule.ReadingOrder].Status);
        Assert.Equal(AccessCheckStatus.Passed, report[AccessRule.TextPresence].Status);
        Assert.Equal(AccessCheckStatus.Passed, report[AccessRule.Bookmarks].Status);
        Assert.Equal(Enum.GetValues<AccessRule>().Length, report.Results.Count);

        var fixedPdf = await Service.FixAsync(pdf, report.Results.Where(r => r.CanFix).Select(r => r.Rule).ToList(),
            new AccessFixOptions { Title = "Sprawozdanie – zażółć gęślą", Language = "pl-PL" });
        var after = await Service.CheckAsync(fixedPdf);
        Assert.Equal(AccessCheckStatus.Passed, after[AccessRule.DocTitle].Status);
        Assert.Equal(AccessCheckStatus.Passed, after[AccessRule.Language].Status);
        Assert.Equal(AccessCheckStatus.Passed, after[AccessRule.DisplayDocTitle].Status);
        using var pig = PigDocument.Open(fixedPdf);
        Assert.Equal("Sprawozdanie – zażółć gęślą", pig.Information.Title);
        Assert.Equal("pl-PL", PdfSharpEdit.Read(fixedPdf, d => d.Internals.Catalog.Elements.GetString("/Lang")));
    }

    [Fact]
    public async Task Fix_through_session_command_is_undoable()
    {
        var services = new ServiceCollection();
        services.AddZetPdfCore(new AppPaths(TestPaths.NewDirectory()));
        new Rendering.RenderingModule().ConfigureServices(services);
        using var sp = services.BuildServiceProvider();
        var session = await sp.GetRequiredService<IDocumentManager>().CreateNewAsync(Untitled(), "bez tytułu.pdf");
        var cmd = new Core.Commands.DelegateDocumentCommand("Edit.Undo.AccessFix",
            (b, _, ct) => Service.FixAsync(b, new[] { AccessRule.DocTitle }, new AccessFixOptions { Title = "Tytuł" }, ct),
            DocumentChangeKind.Metadata, DocumentPermissions.ModifyContent);
        Assert.True(await session.ExecuteAsync(cmd));
        Assert.Equal(AccessCheckStatus.Passed, (await Service.CheckAsync(session.Current.GetBytes()))[AccessRule.DocTitle].Status);
        await session.History.UndoAsync();
        Assert.Equal(AccessCheckStatus.Failed, (await Service.CheckAsync(session.Current.GetBytes()))[AccessRule.DocTitle].Status);
    }

    [Fact]
    public async Task Form_fields_get_descriptions_and_pages_with_annotations_get_structure_tab_order()
    {
        var pdf = SamplePdfFactory.WithForm();
        var report = await Service.CheckAsync(pdf);
        Assert.Equal(AccessCheckStatus.Failed, report[AccessRule.FieldDescriptions].Status);
        Assert.Equal(AccessCheckStatus.Failed, report[AccessRule.TabOrder].Status);
        Assert.Contains(0, report[AccessRule.TabOrder].Pages);
        var fixedPdf = await Service.FixAsync(pdf, new[] { AccessRule.FieldDescriptions, AccessRule.TabOrder }, new AccessFixOptions());
        var after = await Service.CheckAsync(fixedPdf);
        Assert.Equal(AccessCheckStatus.Passed, after[AccessRule.FieldDescriptions].Status);
        Assert.Equal(AccessCheckStatus.Passed, after[AccessRule.TabOrder].Status);
        Assert.Equal("/S", PdfSharpEdit.Read(fixedPdf, d => d.Pages[0].Elements.GetName("/Tabs")));
        Assert.Equal("data urodzenia", AccessibilityService.Readable("data_urodzenia"));
    }

    [Fact]
    public async Task Scanned_pages_and_long_documents_without_bookmarks_fail()
    {
        var scanned = await Service.CheckAsync(SamplePdfFactory.Scanned());
        Assert.Equal(AccessCheckStatus.Failed, scanned[AccessRule.TextPresence].Status);
        Assert.Equal(new[] { 0 }, scanned[AccessRule.TextPresence].Pages);
        var longDoc = await Service.CheckAsync(SamplePdfFactory.Text(21));
        Assert.Equal(AccessCheckStatus.Failed, longDoc[AccessRule.Bookmarks].Status);
        var withBookmarks = await Service.CheckAsync(PdfSharpEdit.Modify(SamplePdfFactory.Text(21), d => d.Outlines.Add("Start", d.Pages[0])));
        Assert.Equal(AccessCheckStatus.Passed, withBookmarks[AccessRule.Bookmarks].Status);
        Assert.Equal("1, 3–5, 9", AccessibilityService.PageList(new[] { 0, 2, 3, 4, 8 }));
    }

    [Fact]
    public async Task Tagged_document_figures_are_found_through_role_map_and_alt_text_can_be_set()
    {
        var pdf = Tagged();
        var report = await Service.CheckAsync(pdf);
        Assert.True(report.IsTagged);
        Assert.Equal(AccessCheckStatus.Passed, report[AccessRule.Tagged].Status);
        Assert.Equal(AccessCheckStatus.Passed, report[AccessRule.StructureTree].Status);
        Assert.Equal(AccessCheckStatus.Failed, report[AccessRule.FigureAltText].Status);
        Assert.Equal(new[] { 1 }, report[AccessRule.FigureAltText].Pages);
        var figures = await Service.GetFiguresAsync(pdf);
        Assert.Equal(2, figures.Count);
        Assert.Equal("Logo firmy", figures[0].Alt);
        Assert.Equal(1, figures[1].PageIndex);

        var updated = await Service.SetPropertiesAsync(pdf, new AccessProperties
        {
            Title = "Raport roczny", Language = "pl-PL", DisplayDocTitle = true,
            AltTexts = new Dictionary<int, string> { [1] = "Wykres sprzedaży – wzrost o 12%" },
        });
        var after = await Service.CheckAsync(updated);
        Assert.Equal(AccessCheckStatus.Passed, after[AccessRule.FigureAltText].Status);
        Assert.Equal(AccessCheckStatus.Passed, after[AccessRule.DocTitle].Status);
        Assert.Equal(AccessCheckStatus.Passed, after[AccessRule.Language].Status);
        Assert.Equal("Wykres sprzedaży – wzrost o 12%", (await Service.GetFiguresAsync(updated))[1].Alt);
        // brak drzewa struktury: tekst alternatywny do sprawdzenia ręcznie
        Assert.Equal(AccessCheckStatus.Manual, (await Service.CheckAsync(Untitled()))[AccessRule.FigureAltText].Status);
    }

    [Fact]
    public async Task Report_is_generated_as_html_and_text_in_interface_language()
    {
        var loc = new Core.Localization.LocalizationService();
        loc.RegisterResources(EditingModule.Strings);
        loc.SetCulture(System.Globalization.CultureInfo.GetCultureInfo("pl"));
        var report = await Service.CheckAsync(Untitled());
        var html = AccessibilityService.BuildReport(report, loc, "<zażółć>.pdf", html: true);
        Assert.Contains("<html lang=\"pl\">", html);
        Assert.Contains("Tytuł dokumentu", html);
        Assert.Contains("&lt;zażółć&gt;.pdf", html);
        Assert.Contains("Niezaliczone", html);
        var txt = AccessibilityService.BuildReport(report, loc, "a.pdf", html: false);
        Assert.Contains("[Niezaliczone] Tytuł dokumentu", txt);
        Assert.Contains("[Sprawdź ręcznie] Logiczna kolejność odczytu", txt);
        Assert.DoesNotContain("[Edit.", txt);
    }
}
