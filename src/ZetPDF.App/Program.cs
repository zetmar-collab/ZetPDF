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

/// <summary>
/// Sonda pliku w procesie potomnym (docs/DECYZJE.md D-44): robi to, co program zrobi z plikiem z dysku, ale na stosie mniejszym niż w programie.
/// Awaria tutaj to dowód, że pliku nie wolno otworzyć. Zwykły wyjątek parsera to wynik „bez awarii”, bo program główny też go obsłuży.
/// </summary>
internal static class PdfProbeRunner
{
    /// <summary>Mniej niż najmniejszy stos w programie (1 MB wątek okna, 1,5 MB pula), więc plik, który przejdzie, przejdzie też tam.</summary>
    public const int StackBytes = 512 * 1024;

    private static readonly TimeSpan PagesBudget = TimeSpan.FromSeconds(20);

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
        bool managedError = false;
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
        // bajty bez szyfrowania, tak jak sesja po otwarciu
        byte[] plain = pdf;
        Step(() =>
        {
            var renderer = new PdfiumRenderer();
            var probe = renderer.ProbeAsync(pdf, password).GetAwaiter().GetResult();
            if (probe.Status == PdfProbeStatus.Ok && probe.IsEncrypted) plain = renderer.RemoveSecurityAsync(pdf, password ?? string.Empty).GetAwaiter().GetResult();
        });
        if (!ReferenceEquals(plain, pdf)) Step(() => OpenAndSave(plain, null));
        Step(() => ReadWithPdfPig(plain));
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

    private static void ReadWithPdfPig(byte[] pdf)
    {
        using var pig = UglyToad.PdfPig.PdfDocument.Open(pdf);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 1; i <= pig.NumberOfPages && clock.Elapsed < PagesBudget; i++)
            _ = pig.GetPage(i).Letters.Count;
    }

    [DllImport("kernel32")]
    private static extern uint SetErrorMode(uint mode);
}
