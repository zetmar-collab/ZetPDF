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

/// <summary>
/// Schowek systemowy (migawka, kopiowanie tekstu). Schowek jest wspólny dla całego procesu i systemu, a przegląd
/// wszystkich poleceń wstążki (RibbonCommandTests) wykonuje też Kopiuj i Ze schowka – dlatego te testy są
/// w kolekcji sekwencyjnej (bez równoległych klas), a asercje pozostają bez zmian.
/// </summary>
[Collection(SequentialCollection.Name)]
public class ClipboardTests
{
    [ClipboardFact]
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
            ClipboardProbe.Retry(Clipboard.Clear);
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

    [ClipboardFact]
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
            ClipboardProbe.Retry(Clipboard.Clear);
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
