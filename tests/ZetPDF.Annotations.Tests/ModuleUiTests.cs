using System.Globalization;
using System.Resources;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Windows.Ink;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Annotations.UI;
using ZetPDF.Annotations.UI.Dialogs;
using ZetPDF.Annotations.UI.Signatures;
using ZetPDF.Annotations.UI.Tools;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Services;
using ZetPDF.TestData;

namespace ZetPDF.Annotations.Tests;

public class ModuleUiTests
{
    private static void Sta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() => { try { body(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static ServiceProvider Services()
    {
        var s = new ServiceCollection();
        s.AddZetPdfCore(new AppPaths(TestPaths.NewDirectory()));
        new AnnotationsModule().ConfigureServices(s);
        return s.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    [Fact]
    public void All_annotation_commands_have_handlers_after_initialize()
    {
        Sta(() =>
        {
            using var sp = Services();
            new AnnotationsModule().Initialize(sp);
            var registry = sp.GetRequiredService<ICommandRegistry>();
            var missing = RibbonCatalog.Commands.Where(c => c.Module == FeatureModuleId.Annotations && registry.GetHandler(c.Id) is null).Select(c => c.Id).ToList();
            Assert.Empty(missing);
            Assert.NotEmpty(registry.GetMenuItems(CommandIds.Comment.Stamp));
            Assert.NotEmpty(registry.GetMenuItems(CommandIds.FillSign.Signature));
            var panels = sp.GetRequiredService<IPanelRegistry>().Panels.Select(p => p.Id).ToList();
            Assert.Contains(PanelIds.Comments, panels);
            Assert.Contains(PanelIds.Attachments, panels);
        });
    }

    [Fact]
    public void Panels_and_dialogs_can_be_created()
    {
        Sta(() =>
        {
            using var sp = Services();
            new AnnotationsModule().Initialize(sp);
            foreach (var p in sp.GetRequiredService<IPanelRegistry>().Panels) Assert.NotNull(p.Factory(sp));
            var loc = sp.GetRequiredService<ILocalizationService>();
            _ = new StyleDialog(loc, "Annot.Style.Title", StyleParts.Color | StyleParts.Fill | StyleParts.Opacity | StyleParts.LineWidth | StyleParts.Font | StyleParts.Contents, new ToolStyle(), "x");
            _ = new FieldPropertiesDialog(loc, new FormFieldSpec { Kind = FormFieldKind.PushButton, Name = "b", PageIndex = 0, Rect = new(0, 0, 10, 10) }, _ => false);
            _ = new TabOrderDialog(loc, 0, new[] { new FormFieldInfo { FullName = "a", Kind = FormFieldKind.Text, PageIndex = 0, Rect = new(0, 0, 1, 1) } });
            _ = new SignatureDialog(loc, null, "Jan");
        });
    }

    [Fact]
    public void Drawn_and_typed_signatures_render_to_transparent_png_and_can_be_stamped()
    {
        byte[] drawn = Array.Empty<byte>(), typed = Array.Empty<byte>();
        Sta(() =>
        {
            var strokes = new StrokeCollection { new Stroke(new StylusPointCollection(new[] { new StylusPoint(0, 0), new StylusPoint(40, 20), new StylusPoint(80, 5) })) };
            drawn = SignatureDialog.RenderStrokes(strokes);
            typed = SignatureDialog.RenderText("Jan Kowalski", "Segoe UI", System.Windows.Media.Colors.Black);
        });
        Assert.True(drawn.Length > 100);
        Assert.True(typed.Length > 100);
        var store = new SignatureStore(Path.Combine(TestPaths.NewDirectory(), "signatures"));
        var path = store.Save(drawn);
        Assert.Single(store.List());
        var (pdf, _) = new AnnotationService().AddAsync(SamplePdfFactory.Text(1),
            new AnnotationSpec { Kind = AnnotationKind.Stamp, PageIndex = 0, Rect = new(100, 300, 250, 340), ImageData = typed }).GetAwaiter().GetResult();
        Assert.True(RenderCheck.NonWhitePixels(pdf, 0, new PdfRect(100, 300, 250, 340)).GetAwaiter().GetResult() > 100);
        store.Delete(path);
        Assert.Empty(store.List());
    }

    [Fact]
    public void Markup_tool_builds_line_quads_from_char_boxes()
    {
        var chars = new List<TextChar>();
        string text = "ab\ncd";
        double x = 10;
        foreach (char c in text)
        {
            if (c == '\n') { chars.Add(new TextChar(c, default, 0, true)); x = 10; continue; }
            double top = chars.Count < 2 ? 700 : 680;
            chars.Add(new TextChar(c, new PdfRect(x, top - 10, x + 6, top), 10, false));
            x += 6;
        }
        var pt = new PageText(0, text, chars);
        var quads = MarkupTool.LineQuads(pt, 0, 4);
        Assert.Equal(2, quads.Count);
        Assert.Equal(new PdfRect(10, 690, 22, 700), quads[0].Bounds);
        Assert.Equal(1, MarkupTool.HitChar(pt, new PdfPoint(17, 695)));
    }

    [Fact]
    public void Ink_simplification_keeps_endpoints()
    {
        var pts = Enumerable.Range(0, 50).Select(i => new PdfPoint(i, i % 2 == 0 ? 0 : 0.01)).ToList();
        var s = InkTool.Simplify(pts, 0.4);
        Assert.Equal(2, s.Count);
        Assert.Equal(pts[0], s[0]);
        Assert.Equal(pts[^1], s[^1]);
    }

    [Fact]
    public void Resources_have_same_keys_in_english_and_polish_and_cover_enums()
    {
        var rm = AnnotationsModule.Strings;
        var en = rm.GetResourceSet(CultureInfo.InvariantCulture, true, true)!.Cast<System.Collections.DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
        var pl = rm.GetResourceSet(new CultureInfo("pl"), true, false)!.Cast<System.Collections.DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
        Assert.Equal(en.Keys.OrderBy(k => k), pl.Keys.OrderBy(k => k));
        Assert.All(en.Keys, k => Assert.StartsWith("Annot.", k));
        foreach (var k in Enum.GetNames<AnnotationKind>()) Assert.Contains("Annot.Kind." + k, en.Keys);
        foreach (var k in Enum.GetNames<AnnotationReviewState>()) Assert.Contains("Annot.State." + k, en.Keys);
        foreach (var k in Enum.GetNames<FormFieldKind>()) Assert.Contains("Annot.Field.Kind." + k, en.Keys);
        foreach (var k in Enum.GetNames<FormButtonAction>()) Assert.Contains("Annot.Field.Action." + k, en.Keys);

        // klucze użyte w kodzie źródłowym modułu
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZetPDF.sln"))) dir = Path.GetDirectoryName(dir);
        if (dir is null) return;
        var src = Path.Combine(dir, "src", "ZetPDF.Annotations");
        var used = Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), "\"(Annot\\.[A-Za-z0-9.]+)\"").Select(m => m.Groups[1].Value))
            .Where(k => !k.EndsWith('.') && k != "Annot.ToolStyle")
            .Distinct().ToList();
        var missing = used.Where(k => !en.ContainsKey(k) && !k.StartsWith("Annot.Stamp.File") && !k.StartsWith("Annot.Sign.File")).ToList();
        Assert.Empty(missing);
    }
}
