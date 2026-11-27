using ZetPDF.Core.Contracts;
using ZetPDF.Viewer.View;

namespace ZetPDF.Viewer.Tests;

/// <summary>Tryb nocny i sepia: przeliczanie pikseli strony i przełączanie trybu.</summary>
public class PageColorTests
{
    private static RenderedBitmap Pixels(params (byte R, byte G, byte B)[] px)
    {
        var data = new byte[px.Length * 4];
        for (int i = 0; i < px.Length; i++) { data[i * 4] = px[i].B; data[i * 4 + 1] = px[i].G; data[i * 4 + 2] = px[i].R; data[i * 4 + 3] = 255; }
        return new RenderedBitmap(px.Length, 1, px.Length * 4, data, 1);
    }

    private static (byte R, byte G, byte B) At(RenderedBitmap b, int i) => (b.Bgra[i * 4 + 2], b.Bgra[i * 4 + 1], b.Bgra[i * 4]);

    [Fact]
    public void Night_mode_turns_white_paper_dark_and_black_text_light_and_keeps_input()
    {
        var input = Pixels((255, 255, 255), (0, 0, 0), (255, 255, 0));
        var copy = (byte[])input.Bgra.Clone();
        var night = PageColorFilter.Apply(input, PageColorMode.Night);

        Assert.Equal(copy, input.Bgra);
        var paper = PageColorFilter.PaperColor(PageColorMode.Night);
        Assert.Equal((paper.R, paper.G, paper.B), At(night, 0));
        Assert.True(At(night, 1).R > 200, "black text becomes light");
        var (r, g, b) = At(night, 2);
        Assert.True(r > b && g > b, "yellow highlight keeps its hue");
        Assert.Equal(255, night.Bgra[3]);
    }

    [Fact]
    public void Sepia_maps_white_to_paper_and_normal_is_unchanged()
    {
        var input = Pixels((255, 255, 255), (0, 0, 0));
        var sepia = PageColorFilter.Apply(input, PageColorMode.Sepia);
        var paper = PageColorFilter.PaperColor(PageColorMode.Sepia);
        Assert.Equal((paper.R, paper.G, paper.B), At(sepia, 0));
        Assert.True(At(sepia, 1).R < 100);
        Assert.Same(input, PageColorFilter.Apply(input, PageColorMode.Normal));
    }

    [Fact]
    public void Toggle_switches_between_modes_and_back_to_normal()
    {
        var s = new PageColorService();
        int changed = 0;
        s.Changed += (_, _) => changed++;
        s.Toggle(PageColorMode.Night);
        Assert.Equal(PageColorMode.Night, s.Mode);
        s.Toggle(PageColorMode.Sepia);
        Assert.Equal(PageColorMode.Sepia, s.Mode);
        s.Toggle(PageColorMode.Sepia);
        Assert.Equal(PageColorMode.Normal, s.Mode);
        s.Mode = (PageColorMode)42;
        Assert.Equal(PageColorMode.Normal, s.Mode);
        Assert.Equal(3, changed);
    }
}
