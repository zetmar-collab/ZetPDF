using ZetPDF.Core.Contracts;
using ZetPDF.Core.Utilities;

namespace ZetPDF.Core.Tests;

/// <summary>Prostowanie skanów: szacowanie kąta, obrót i przeliczanie współrzędnych.</summary>
public class DeskewTests
{
    /// <summary>Białe „strona” z poziomymi pasami „tekstu” obróconymi o kąt (stopnie, dodatni = opadają w prawo).</summary>
    private static RenderedBitmap Lines(double angle, int w = 1200, int h = 1600)
    {
        var data = new byte[w * h * 4];
        Array.Fill(data, (byte)255);
        double tan = Math.Tan(angle * Math.PI / 180);
        for (int y0 = 100; y0 < h - 100; y0 += 40)
            for (int x = 100; x < w - 100; x++)
            {
                if ((x / 15) % 4 == 3) continue; // przerwy między „słowami”
                for (int t = 0; t < 6; t++)
                {
                    int y = (int)Math.Round(y0 + t + (x - w / 2.0) * tan);
                    if (y < 0 || y >= h) continue;
                    int i = (y * w + x) * 4;
                    data[i] = data[i + 1] = data[i + 2] = 0;
                }
            }
        return new RenderedBitmap(w, h, w * 4, data, 1);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.5)]
    [InlineData(-2.3)]
    [InlineData(4.0)]
    public void Estimates_skew_angle_of_text_lines(double angle)
    {
        double estimated = Deskew.EstimateAngle(Lines(angle));
        Assert.InRange(estimated, angle - 0.15, angle + 0.15);
    }

    [Fact]
    public void Rotated_image_has_straight_lines()
    {
        var straight = Deskew.Rotate(Lines(2.0), Deskew.EstimateAngle(Lines(2.0)));
        Assert.InRange(Deskew.EstimateAngle(straight), -0.3, 0.3);
    }

    [Fact]
    public void Blank_page_and_small_angle_are_left_alone()
    {
        var blank = new RenderedBitmap(200, 200, 800, Enumerable.Repeat((byte)255, 800 * 200).ToArray(), 1);
        Assert.Equal(0, Deskew.EstimateAngle(blank));
        Assert.Same(blank, Deskew.Rotate(blank, 0.1));
    }

    [Fact]
    public void Coordinates_round_trip_between_source_and_deskewed()
    {
        var (u, v) = Deskew.ToDeskewed(300, 500, 1200, 1600, 2.5);
        var (x, y) = Deskew.ToSource(u, v, 1200, 1600, 2.5);
        Assert.Equal(300, x, 6);
        Assert.Equal(500, y, 6);
        var (l, t, r, b) = Deskew.RectToSource(100, 100, 200, 120, 1200, 1600, 2.5);
        Assert.True(r - l > 100 && b - t > 20, "bounding rect grows a little after rotation");
    }
}
