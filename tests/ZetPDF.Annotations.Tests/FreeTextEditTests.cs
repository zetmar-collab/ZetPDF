using ZetPDF.Annotations.UI;
using ZetPDF.Core.Contracts;
using ZetPDF.TestData;

namespace ZetPDF.Annotations.Tests;

/// <summary>Edycja tekstu pola tekstowego w miejscu: ramka rośnie, gdy tekst się nie mieści; które komentarze się kwalifikują.</summary>
public class FreeTextEditTests
{
    private readonly AnnotationService _svc = new();

    private async Task<(byte[] Pdf, AnnotationId Id)> AddAsync(AnnotationKind kind, string text)
    {
        var spec = new AnnotationSpec
        {
            Kind = kind, PageIndex = 0, Rect = new PdfRect(100, 700, 100, 700), Contents = text, LineWidth = kind == AnnotationKind.Typewriter ? 0 : 1,
            TextStyle = new TextStyle { FontFamily = "Arial", FontSize = 12, Color = PdfColor.Black },
        };
        return await _svc.AddAsync(SamplePdfFactory.Text(1), spec);
    }

    [Theory]
    [InlineData(AnnotationKind.FreeText)]
    [InlineData(AnnotationKind.Typewriter)]
    public async Task Longer_text_grows_box_downwards_keeping_top_left_and_width(AnnotationKind kind)
    {
        var (pdf, id) = await AddAsync(kind, "Krótki");
        var before = (await _svc.GetAnnotationsAsync(pdf)).Single().Rect;

        var updated = await _svc.UpdateAsync(pdf, id, new AnnotationUpdate { Contents = "Pierwszy wiersz\nDrugi wiersz\nTrzeci wiersz zażółć" });
        var after = (await _svc.GetAnnotationsAsync(updated)).Single();

        Assert.Equal("Pierwszy wiersz\nDrugi wiersz\nTrzeci wiersz zażółć", after.Contents);
        Assert.Equal(before.Left, after.Rect.Left, 1);
        Assert.Equal(before.Top, after.Rect.Top, 1);
        Assert.Equal(before.Width, after.Rect.Width, 1);
        Assert.True(after.Rect.Height > before.Height * 2, $"{before.Height} -> {after.Rect.Height}");
    }

    [Fact]
    public async Task Shorter_text_keeps_box_size()
    {
        var (pdf, id) = await AddAsync(AnnotationKind.FreeText, "Jeden\nDwa\nTrzy");
        var before = (await _svc.GetAnnotationsAsync(pdf)).Single().Rect;
        var updated = await _svc.UpdateAsync(pdf, id, new AnnotationUpdate { Contents = "Jeden" });
        Assert.Equal(before, (await _svc.GetAnnotationsAsync(updated)).Single().Rect);
    }

    [Fact]
    public async Task Only_free_text_and_typewriter_are_edited_in_place()
    {
        var (ft, _) = await AddAsync(AnnotationKind.FreeText, "Tekst");
        var (callout, _) = await AddAsync(AnnotationKind.Callout, "Tekst");
        Assert.True(AnnotationActions.IsInlineEditable((await _svc.GetAnnotationsAsync(ft)).Single()));
        Assert.False(AnnotationActions.IsInlineEditable((await _svc.GetAnnotationsAsync(callout)).Single()));
        var locked = (await _svc.GetAnnotationsAsync(ft)).Single() with { IsLocked = true };
        Assert.False(AnnotationActions.IsInlineEditable(locked));
    }
}
