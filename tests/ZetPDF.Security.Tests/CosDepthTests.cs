using System.Text;
using ZetPDF.Security.Pdf;

namespace ZetPDF.Security.Tests;

/// <summary>Własny parser COS (analiza zabezpieczeń, podpisy): zagnieżdżenie i czytanie spekulacyjne na danych spreparowanych.</summary>
public class CosDepthTests
{
    [Theory]
    [InlineData(300)]
    [InlineData(100_000)]
    [InlineData(1_000_000)]
    public void Deep_nesting_is_format_error_not_stack_overflow(int depth)
    {
        var data = Encoding.Latin1.GetBytes(new string('[', depth) + new string(']', depth));
        Assert.Throws<FormatException>(() => new CosParser(data).ReadObject());
        data = Encoding.Latin1.GetBytes(string.Concat(Enumerable.Repeat("<</A ", depth)));
        Assert.ThrowsAny<FormatException>(() => new CosParser(data).ReadObject());
    }

    [Fact]
    public void Integers_before_arrays_do_not_cause_exponential_reread()
    {
        var data = Encoding.Latin1.GetBytes(string.Concat(Enumerable.Repeat("1 [", 200)) + new string(']', 200));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.NotNull(new CosParser(data).ReadObject());
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), sw.Elapsed.ToString());
        Assert.IsType<CosReference>(new CosParser(Encoding.ASCII.GetBytes("12 0 R")).ReadObject());
        Assert.IsType<CosNumber>(new CosParser(Encoding.ASCII.GetBytes("12 0 obj")).ReadObject());
    }
}
