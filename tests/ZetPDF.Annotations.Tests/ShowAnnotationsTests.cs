using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.TestData;

namespace ZetPDF.Annotations.Tests;

/// <summary>
/// Ukrycie komentarzy w widoku = render bez <see cref="RenderFlags.Annotations"/> (FPDF_ANNOT), ale z
/// <see cref="RenderFlags.FormFields"/> (FPDF_FFLDraw rysuje tylko widżety pól formularza).
/// </summary>
public class ShowAnnotationsTests
{
    private static readonly PdfRect TextField = new(58, 602, 274, 620);
    private static readonly PdfRect Comment = new(300, 300, 400, 400);

    private static async Task<byte[]> FormWithComment()
    {
        var filled = await new FormService().SetValuesAsync(SamplePdfFactory.WithForm(), new Dictionary<string, string> { ["imie"] = "Jan Kowalski" });
        var (pdf, _) = await new AnnotationService().AddAsync(filled, new AnnotationSpec
        {
            Kind = AnnotationKind.Square,
            PageIndex = 0,
            Rect = Comment,
            Color = PdfColor.Red,
            FillColor = PdfColor.Red,
            LineWidth = 3,
        });
        return pdf;
    }

    [Fact]
    public async Task Render_without_annotations_flag_hides_comments_but_keeps_form_widgets()
    {
        var pdf = await FormWithComment();
        var empty = await RenderCheck.NonWhitePixels(SamplePdfFactory.WithForm(), 0, TextField);

        Assert.True(await RenderCheck.NonWhitePixels(pdf, 0, Comment) > 500, "comment visible by default");
        Assert.True(await RenderCheck.NonWhitePixels(pdf, 0, TextField) > empty + 30, "field visible by default");

        Assert.True(await RenderCheck.NonWhitePixels(pdf, 0, Comment, RenderFlags.FormFields) < 5, "comment hidden without FPDF_ANNOT");
        Assert.True(await RenderCheck.NonWhitePixels(pdf, 0, TextField, RenderFlags.FormFields) > empty + 30, "field still visible (FFLDraw)");
    }

    [Fact]
    public void HideAll_and_ShowAll_toggle_the_view_without_modifying_the_document()
    {
        ViewerIntegrationTests.RunSta(async () =>
        {
            using var sp = ViewerIntegrationTests.BuildServices();
            var docs = sp.GetRequiredService<IDocumentManager>();
            var pdf = await FormWithComment();
            var session = await docs.CreateNewAsync(pdf, "t.pdf");
            docs.ActiveDocument = session;
            var view = sp.GetRequiredService<IDocumentViewFactory>().CreateView(session);
            ViewerIntegrationTests.Layout(view.Element);
            await ViewerIntegrationTests.WaitFor(() => view.GetPageTransform(0) is not null, "page placed");
            ViewerIntegrationTests.Layout(view.Element);
            var before = session.Current.GetBytes();
            bool modifiedBefore = session.IsModified;

            int Red(PdfRect area)
            {
                ViewerIntegrationTests.Layout(view.Element);
                var r = view.GetPageTransform(0)!.ToView(area);
                var rtb = new RenderTargetBitmap(900, 700, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(view.Element);
                var px = new byte[900 * 700 * 4];
                rtb.CopyPixels(px, 900 * 4, 0);
                int n = 0;
                for (int y = Math.Max(0, (int)r.Top + 3); y < Math.Min(700, (int)r.Bottom - 3); y++)
                    for (int x = Math.Max(0, (int)r.Left + 3); x < Math.Min(900, (int)r.Right - 3); x++)
                    {
                        int o = (y * 900 + x) * 4;
                        if (px[o + 2] > 180 && px[o + 1] < 90 && px[o] < 90) n++;
                    }
                return n;
            }

            await ViewerIntegrationTests.WaitFor(() => Red(Comment) > 200, "comment rendered");
            var registry = sp.GetRequiredService<ICommandRegistry>();
            Assert.True(registry.TryExecute(CommandIds.Comment.HideAll));
            Assert.False(view.ShowAnnotations);
            await ViewerIntegrationTests.WaitFor(() => Red(Comment) == 0, "comment hidden in view");

            Assert.True(registry.TryExecute(CommandIds.Comment.ShowAll));
            Assert.True(view.ShowAnnotations);
            await ViewerIntegrationTests.WaitFor(() => Red(Comment) > 200, "comment shown again");

            // plik bez zmian: te same bajty, brak wpisu w historii, stan „zmodyfikowany” bez zmian
            Assert.Equal(before, session.Current.GetBytes());
            Assert.False(session.History.CanUndo);
            Assert.Equal(modifiedBefore, session.IsModified);
            view.Dispose();
        });
    }
}
