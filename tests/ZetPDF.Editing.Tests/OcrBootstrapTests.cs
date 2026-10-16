using ZetPDF.Core.Services;
using ZetPDF.Ocr;
using ZetPDF.TestData;

namespace ZetPDF.Editing.Tests;

public class OcrBootstrapTests
{
    /// <summary>Natywne DLL Tesseract (x64) i tessdata pol+eng rozpakowują się z zasobów i silnik startuje.</summary>
    [Fact]
    public void Tesseract_engine_loads_from_embedded_resources()
    {
        var paths = new AppPaths(TestPaths.NewDirectory());
        var boot = new TesseractBootstrap(paths);
        using var engine = boot.CreateEngine("pol+eng");
        Assert.True(File.Exists(Path.Combine(boot.TessdataDirectory, "pol.traineddata")));
        Assert.True(File.Exists(Path.Combine(boot.EnsureReady(), "x64", "tesseract50.dll")));
        Assert.False(string.IsNullOrEmpty(engine.Version));
    }
}
