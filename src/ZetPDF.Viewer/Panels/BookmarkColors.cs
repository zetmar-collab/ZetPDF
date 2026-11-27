using System.Windows.Media;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Viewer.Panels;

/// <summary>
/// Kolory zakładek do wyboru w panelu. To kolory treści dokumentu (zapisywane w pliku jako /C), a nie kolory motywu programu –
/// inne czytniki pokazują je tak samo. „Domyślny” usuwa kolor.
/// </summary>
internal static class BookmarkColors
{
    public static readonly IReadOnlyList<(string TextKey, PdfColor? Color)> Palette = new (string, PdfColor?)[]
    {
        ("Viewer.Bookmarks.Color.Default", null),
        ("Viewer.Bookmarks.Color.Red", new PdfColor(255, 200, 30, 30)),
        ("Viewer.Bookmarks.Color.Orange", new PdfColor(255, 220, 120, 0)),
        ("Viewer.Bookmarks.Color.Green", new PdfColor(255, 20, 140, 50)),
        ("Viewer.Bookmarks.Color.Blue", new PdfColor(255, 30, 90, 210)),
        ("Viewer.Bookmarks.Color.Purple", new PdfColor(255, 130, 50, 180)),
        ("Viewer.Bookmarks.Color.Gray", new PdfColor(255, 120, 120, 120)),
    };

    public static Brush Brush(PdfColor c)
    {
        var b = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
        b.Freeze();
        return b;
    }
}
