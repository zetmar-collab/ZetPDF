using System.Runtime.InteropServices;
using PdfSharp.Pdf.IO;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Rendering;

namespace ZetPDF.App;

/// <summary>Punkt wejścia: tryb sondy pliku zaczyna się przed WPF, żeby start był krótki i bez okien.</summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == PdfProbeProtocol.Argument) return PdfProbeRunner.Run();
        App.Main();
        return 0;
    }
}

/// <summary>Budżet sondy dla testów (sama sonda jest wewnętrzna).</summary>
public static class PdfProbeRunnerAccess
{
    public static TimeSpan Budget(long fileBytes, int pages) => PdfProbeRunner.Budget(fileBytes, pages);
}

/// <summary>
/// Sonda pliku w procesie potomnym (docs/DECYZJE.md D-44): robi to, co program zrobi z plikiem z dysku, ale na stosie mniejszym niż w programie.
/// Awaria tutaj to dowód, że pliku nie wolno otworzyć. Zwykły wyjątek parsera to wynik „bez awarii”, bo program główny też go obsłuży.
/// </summary>
internal static class PdfProbeRunner
{
    /// <summary>Mniej niż najmniejszy stos w programie (1 MB wątek okna, 1,5 MB pula), więc plik, który przejdzie, przejdzie też tam.</summary>
    public const int StackBytes = 512 * 1024;

    /// <summary>Budżet czasu: od rozmiaru pliku i liczby stron, z górnym limitem. Po jego wyczerpaniu wynik to „nie sprawdzono do końca”, a nie OK.</summary>
    public static TimeSpan Budget(long fileBytes, int pages) =>
        TimeSpan.FromSeconds(Math.Min(240, 30 + pages * 0.2 + fileBytes / (1024.0 * 1024) * 0.5));

    public static int Run()
    {
        // bez okna „program przestał działać”; to samo ustawia Job Object
        SetErrorMode(0x0001 | 0x0002 | 0x8000);
        try
        {
            var (pdf, password) = PdfProbeProtocol.Read(Console.OpenStandardInput());
            int exit = 9;
            var thread = new Thread(() => exit = Check(pdf, password), StackBytes) { IsBackground = false };
            thread.Start();
            thread.Join();
            return exit;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return 9; // błąd protokołu: odrzucenie
        }
    }

    private static int Check(byte[] pdf, string? password)
    {
        bool managedError = false, incomplete = false;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        void Step(Action step)
        {
            try { step(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                managedError = true;
            }
        }

        PdfSharpEdit.EnsureInitialized();
        // surowe bajty, tak jak Wstaw z pliku, sprawdzanie hasła właściciela i zapasowe odszyfrowanie
        Step(() => OpenAndSave(pdf, password));
        // parser COS: analiza zabezpieczeń i podpisy czytają surowe bajty
        Step(() => new ZetPDF.Security.SecurityService().AnalyzeAsync(pdf, password).GetAwaiter().GetResult());
        // bajty bez szyfrowania, tak jak sesja po otwarciu
        byte[] plain = pdf;
        var renderer = new PdfiumRenderer();
        Step(() =>
        {
            var probe = renderer.ProbeAsync(pdf, password).GetAwaiter().GetResult();
            if (probe.Status == PdfProbeStatus.Ok && probe.IsEncrypted) plain = renderer.RemoveSecurityAsync(pdf, password ?? string.Empty).GetAwaiter().GetResult();
        });
        if (!ReferenceEquals(plain, pdf)) Step(() => OpenAndSave(plain, null));

        int pages = 0;
        Step(() =>
        {
            using var doc = renderer.OpenAsync(plain).GetAwaiter().GetResult();
            pages = doc.PageCount;
            var budget = Budget(pdf.Length, pages);
            for (int i = 0; i < doc.PageCount; i++)
            {
                if (clock.Elapsed > budget / 2)
                {
                    incomplete = true;
                    break;
                }
                int page = i;
                Step(() =>
                {
                    var size = doc.Pages[page].DisplaySize;
                    double scale = Math.Min(0.5, 800.0 / Math.Max(Math.Max(size.Width, size.Height), 1));
                    doc.RenderPageAsync(page, new RenderRequest(scale)).GetAwaiter().GetResult();
                    doc.GetPageTextAsync(page).GetAwaiter().GetResult();
                });
            }
        });
        Step(() =>
        {
            using var pig = UglyToad.PdfPig.PdfDocument.Open(plain);
            var budget = Budget(pdf.Length, pig.NumberOfPages);
            for (int i = 1; i <= pig.NumberOfPages; i++)
            {
                if (clock.Elapsed > budget)
                {
                    incomplete = true;
                    break;
                }
                int number = i;
                // wyjątek na jednej stronie nie może pominąć kolejnych
                Step(() => ReadPage(pig, number));
            }
        });
        if (incomplete) return PdfProbeProtocol.ExitIncomplete;
        return managedError ? PdfProbeProtocol.ExitManagedError : PdfProbeProtocol.ExitOk;
    }

    private static void OpenAndSave(byte[] pdf, string? password)
    {
        using (var import = password is null
            ? PdfReader.Open(new MemoryStream(pdf, false), PdfDocumentOpenMode.Import)
            : PdfReader.Open(new MemoryStream(pdf, false), password, PdfDocumentOpenMode.Import))
            _ = import.PageCount;
        using var modify = password is null
            ? PdfReader.Open(new MemoryStream(pdf, false), PdfDocumentOpenMode.Modify)
            : PdfReader.Open(new MemoryStream(pdf, false), password, PdfDocumentOpenMode.Modify);
        using var output = new MemoryStream();
        modify.Save(output, closeStream: false);
    }

    /// <summary>Te same wywołania PdfPig, których używa program: litery, słowa, obrazy z ich danymi.</summary>
    private static void ReadPage(UglyToad.PdfPig.PdfDocument pig, int number)
    {
        var page = pig.GetPage(number);
        _ = page.Letters.Count;
        _ = page.GetWords().Count();
        foreach (var image in page.GetImages())
        {
            _ = image.TryGetPng(out _);
            _ = image.RawBytes.Length;
        }
    }

    [DllImport("kernel32")]
    private static extern uint SetErrorMode(uint mode);
}
