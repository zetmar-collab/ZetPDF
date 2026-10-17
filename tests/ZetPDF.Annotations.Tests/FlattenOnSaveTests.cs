using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.TestData;

namespace ZetPDF.Annotations.Tests;

/// <summary>„Spłaszcz przy zapisie” (FillSign.Flatten) – hak IDocumentManager.AddSaveTransform: spłaszczony jest tylko zapisany plik.</summary>
public class FlattenOnSaveTests
{
    [Fact]
    public void Flatten_on_save_flattens_saved_file_only_and_can_be_turned_off()
    {
        ViewerIntegrationTests.RunSta(async () =>
        {
            using var sp = ViewerIntegrationTests.BuildServices();
            var docs = sp.GetRequiredService<IDocumentManager>();
            var annots = sp.GetRequiredService<IAnnotationService>();
            var forms = sp.GetRequiredService<IFormService>();
            var area = new PdfRect(300, 300, 400, 400);
            var filled = await forms.SetValuesAsync(SamplePdfFactory.WithForm(), new Dictionary<string, string> { ["imie"] = "Jan" });
            var (pdf, _) = await annots.AddAsync(filled, new AnnotationSpec { Kind = AnnotationKind.Square, PageIndex = 0, Rect = area, Color = PdfColor.Red, LineWidth = 3 });
            var session = await docs.CreateNewAsync(pdf, "t.pdf");
            docs.ActiveDocument = session;

            var registry = sp.GetRequiredService<ICommandRegistry>();
            var toggle = Assert.IsAssignableFrom<IToggleCommand>(registry.GetHandler(CommandIds.FillSign.Flatten));
            Assert.False(toggle.IsChecked);
            Assert.True(registry.TryExecute(CommandIds.FillSign.Flatten));
            Assert.True(toggle.IsChecked);
            Assert.False(session.History.CanUndo); // włączenie opcji nie zmienia dokumentu

            var dir = TestPaths.NewDirectory();
            var path = Path.Combine(dir, "spłaszczony plik.pdf");
            await docs.SaveAsync(session, path);
            var saved = await File.ReadAllBytesAsync(path);
            Assert.Empty(await annots.GetAnnotationsAsync(saved));
            Assert.Empty(await forms.GetFieldsAsync(saved));
            Assert.True(await RenderCheck.NonWhitePixels(saved, 0, area, RenderFlags.None) > 50, "comment drawn into page content");
            // sesja zachowuje edytowalne komentarze i pola
            Assert.Single(await annots.GetAnnotationsAsync(session.Current.GetBytes()), a => a.Kind != AnnotationKind.Widget);
            Assert.NotEmpty(await forms.GetFieldsAsync(session.Current.GetBytes()));
            Assert.False(session.IsModified);

            Assert.True(registry.TryExecute(CommandIds.FillSign.Flatten));
            Assert.False(toggle.IsChecked);
            var path2 = Path.Combine(dir, "zwykły.pdf");
            await docs.SaveAsync(session, path2);
            Assert.Single(await annots.GetAnnotationsAsync(await File.ReadAllBytesAsync(path2)), a => a.Kind != AnnotationKind.Widget);
        });
    }
}
