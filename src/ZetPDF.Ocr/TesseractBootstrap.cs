using System.Reflection;
using Tesseract;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Ocr;

/// <summary>
/// Przygotowanie Tesseract 5.2.0 tak, by działał także w publikacji single-file (zweryfikowane w scratch/LibCheck):
/// pakiet NuGet ładuje natywne DLL z "{katalog Tesseract.dll}\x64\", a w single-file Assembly.Location jest puste
/// (ArgumentNullException). Rozwiązanie: natywne DLL i tessdata są zasobami osadzonymi tego projektu; przy pierwszym
/// użyciu rozpakowujemy je do %LOCALAPPDATA%\ZetPDF\ocr\5.2.0\ (x64\*.dll, tessdata\*.traineddata) i ustawiamy
/// TesseractEnviornment.CustomSearchPath na ten katalog.
/// </summary>
public sealed class TesseractBootstrap
{
    private const string Version = "5.2.0";
    private static readonly object Gate = new();
    private readonly IAppPaths _paths;
    private string? _root;

    public TesseractBootstrap(IAppPaths paths) => _paths = paths;

    /// <summary>Katalog z tessdata (po rozpakowaniu).</summary>
    public string TessdataDirectory => Path.Combine(EnsureReady(), "tessdata");

    /// <summary>Rozpakowuje zasoby (jeśli trzeba) i konfiguruje ścieżkę wyszukiwania. Zwraca katalog bazowy.</summary>
    public string EnsureReady()
    {
        if (_root is not null) return _root;
        lock (Gate)
        {
            if (_root is not null) return _root;
            var root = Path.Combine(_paths.LocalDataDirectory, "ocr", Version);
            var asm = typeof(TesseractBootstrap).Assembly;
            Extract(asm, "ZetPDF.Ocr.native.x64.tesseract50.dll", Path.Combine(root, "x64", "tesseract50.dll"));
            Extract(asm, "ZetPDF.Ocr.native.x64.leptonica-1.82.0.dll", Path.Combine(root, "x64", "leptonica-1.82.0.dll"));
            Extract(asm, "ZetPDF.Ocr.tessdata.pol.traineddata", Path.Combine(root, "tessdata", "pol.traineddata"));
            Extract(asm, "ZetPDF.Ocr.tessdata.eng.traineddata", Path.Combine(root, "tessdata", "eng.traineddata"));
            TesseractEnviornment.CustomSearchPath = root;
            _root = root;
            return root;
        }
    }

    /// <summary>Nowy silnik (nie jest wątkowo bezpieczny – jeden na wątek/zadanie).</summary>
    public TesseractEngine CreateEngine(string languages = "pol+eng") =>
        new(TessdataDirectory, languages, EngineMode.Default);

    private static void Extract(Assembly asm, string resource, string target)
    {
        using var stream = asm.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing embedded resource {resource}.");
        if (File.Exists(target) && new FileInfo(target).Length == stream.Length) return;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var tmp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var fs = File.Create(tmp)) stream.CopyTo(fs);
        File.Move(tmp, target, overwrite: true);
    }
}
