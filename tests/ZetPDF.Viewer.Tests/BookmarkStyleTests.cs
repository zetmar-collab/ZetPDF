using ZetPDF.Core.Contracts;
using ZetPDF.TestData;
using ZetPDF.Viewer.Panels;

namespace ZetPDF.Viewer.Tests;

/// <summary>Kolor (/C) i styl (/F) zakładek: zapis, odczyt, usuwanie koloru.</summary>
public class BookmarkStyleTests
{
    private readonly BookmarkService _service = new();

    [Fact]
    public async Task Color_bold_and_italic_round_trip_and_default_color_removes_entry()
    {
        var red = BookmarkColors.Palette.Single(p => p.TextKey.EndsWith(".Red")).Color!.Value;
        var tree = new[]
        {
            new BookmarkNode("Czerwona ąę", 0, BookmarkNode.NoChildren, Bold: true, Color: red),
            new BookmarkNode("Kursywa", 1, BookmarkNode.NoChildren, Italic: true),
            new BookmarkNode("Czarna", 2, BookmarkNode.NoChildren, Color: PdfColor.Black),
        };
        var saved = await _service.SetBookmarksAsync(SamplePdfFactory.Text(3), tree);
        var read = await _service.GetBookmarksAsync(saved);

        Assert.Equal(red, read[0].Color);
        Assert.True(read[0].Bold);
        Assert.False(read[0].Italic);
        Assert.True(read[1].Italic);
        Assert.Null(read[1].Color);
        Assert.Null(read[2].Color); // czarny = domyślny, bez wpisu /C

        var cleared = await _service.SetBookmarksAsync(saved, read.Select(n => n with { Color = null }).ToArray());
        Assert.All(await _service.GetBookmarksAsync(cleared), n => Assert.Null(n.Color));
    }

    [Fact]
    public void Palette_has_unique_texts_and_one_default()
    {
        Assert.Equal(BookmarkColors.Palette.Count, BookmarkColors.Palette.Select(p => p.TextKey).Distinct().Count());
        Assert.Single(BookmarkColors.Palette, p => p.Color is null);
    }
}
