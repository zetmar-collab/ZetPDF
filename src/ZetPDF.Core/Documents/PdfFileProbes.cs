using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ZetPDF.Core.Localization;

namespace ZetPDF.Core.Documents;

/// <summary>Wynik sondy. Incomplete: bez awarii, ale nie sprawdzono do końca w wyznaczonym czasie.</summary>
public enum PdfProbeVerdict { Safe, Incomplete, Rejected }

/// <summary>Sprawdzenie pliku spoza zaufanych źródeł. Safe tylko po udanym przejściu.</summary>
public interface IPdfFileProbe
{
    Task<PdfProbeVerdict> CheckAsync(byte[] pdf, string? password, CancellationToken ct);
}

/// <summary>Plik nie został sprawdzony do końca: operacja z użytkownikiem pyta, operacja wsadowa pomija plik.</summary>
public sealed class PdfProbeIncompleteException(string message) : ZetPDF.Core.Contracts.PdfCorruptException(message);

/// <summary>Stan domyślny: sonda nieskonfigurowana odrzuca wszystko. Program ustawia <see cref="ProcessPdfProbe"/> na starcie.</summary>
public sealed class UnconfiguredPdfProbe : IPdfFileProbe
{
    public Task<PdfProbeVerdict> CheckAsync(byte[] pdf, string? password, CancellationToken ct) => Task.FromResult(PdfProbeVerdict.Rejected);
}

/// <summary>
/// Wspólne wejście dla plików z dysku (otwieranie, Scal, Wstaw z pliku, Kreator, indeks, porównanie). Skan bajtów tylko odrzuca oczywiste przypadki,
/// a o bezpieczeństwie decyduje sonda: wyniki własnych operacji programu przez nią nie przechodzą.
/// </summary>
public static class PdfFileProbes
{
    private const int MaxRemembered = 128;
    private static readonly HashSet<string> Passed = new(StringComparer.Ordinal);
    private static IPdfFileProbe _current = new UnconfiguredPdfProbe();

    public static IPdfFileProbe Current
    {
        get => _current;
        set
        {
            _current = value;
            lock (Passed) Passed.Clear();
        }
    }

    private static string Key(byte[] pdf, string? password) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pdf)) + ":" + (password is null ? "-" : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(password))));

    private static void Remember(string key)
    {
        lock (Passed)
        {
            if (Passed.Count >= MaxRemembered) Passed.Clear();
            Passed.Add(key);
        }
    }

    /// <summary>
    /// Tryb bez pytania (Kreator, indeks, operacje wsadowe). Wyjątek pliku uszkodzonego lub zbyt złożonego albo <see cref="PdfProbeIncompleteException"/>.
    /// Te same bajty i hasło sprawdzone wcześniej lub zaakceptowane przez użytkownika nie idą do sondy ponownie.
    /// </summary>
    public static async Task EnsureSafeAsync(byte[] pdf, string? password = null, CancellationToken ct = default)
    {
        var key = Key(pdf, password);
        lock (Passed)
            if (Passed.Contains(key)) return;
        await Task.Run(() => PdfStructureGuard.RejectObviousDepth(pdf, ct), ct).ConfigureAwait(false);
        switch (await Current.CheckAsync(pdf, password, ct).ConfigureAwait(false))
        {
            case PdfProbeVerdict.Safe:
                Remember(key);
                return;
            case PdfProbeVerdict.Incomplete:
                throw new PdfProbeIncompleteException("The file could not be fully checked in the allotted time.").WithMessageKey("Core.Error.ProbeIncomplete");
            default:
                throw PdfStructureGuard.TooComplex();
        }
    }

    /// <summary>Tryb z użytkownikiem: przy niepełnym sprawdzeniu pytanie z domyślnym Nie. False, gdy użytkownik odmówił.</summary>
    public static async Task<bool> EnsureSafeInteractiveAsync(byte[] pdf, string? password, ZetPDF.Core.Contracts.IDialogService? dialogs, ZetPDF.Core.Contracts.ILocalizationService? loc, CancellationToken ct = default)
    {
        try
        {
            await EnsureSafeAsync(pdf, password, ct);
            return true;
        }
        catch (PdfProbeIncompleteException) when (dialogs is not null && loc is not null)
        {
            if (!dialogs.ConfirmDanger(loc["Core.Probe.Incomplete"], loc["Core.Probe.Incomplete.Title"])) return false;
            Remember(Key(pdf, password));
            return true;
        }
    }

    /// <summary>Wczytuje plik z dysku i sprawdza go sondą.</summary>
    public static async Task<byte[]> ReadVerifiedAsync(string path, string? password = null, CancellationToken ct = default)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        await EnsureSafeAsync(bytes, password, ct).ConfigureAwait(false);
        return bytes;
    }
}

/// <summary>Protokół sondy: na standardowe wejście długość hasła i hasło w UTF-8, długość i bajty pliku. Hasło nigdy w linii poleceń.</summary>
public static class PdfProbeProtocol
{
    public const string Argument = "--probe-pdf";
    public const int ExitOk = 0;

    /// <summary>Parser zgłosił zwykły błąd (uszkodzony plik, złe hasło): bez awarii, więc program główny zachowa się tak samo bezpiecznie.</summary>
    public const int ExitManagedError = 3;

    /// <summary>Bez awarii, ale budżet czasu nie pozwolił sprawdzić wszystkiego. Tak samo liczy się limit czasu procesu.</summary>
    public const int ExitIncomplete = 4;

    public const long MaxPassword = 64 * 1024;

    public static async Task WriteAsync(Stream output, byte[] pdf, string? password, CancellationToken ct)
    {
        var pw = password is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(password);
        var header = new byte[13];
        header[0] = password is null ? (byte)0 : (byte)1;
        BitConverter.TryWriteBytes(header.AsSpan(1), pw.Length);
        BitConverter.TryWriteBytes(header.AsSpan(5), (long)pdf.Length);
        await output.WriteAsync(header, ct).ConfigureAwait(false);
        await output.WriteAsync(pw, ct).ConfigureAwait(false);
        await output.WriteAsync(pdf, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    public static (byte[] Pdf, string? Password) Read(Stream input)
    {
        var header = ReadExact(input, 13);
        int pwLength = BitConverter.ToInt32(header, 1);
        long pdfLength = BitConverter.ToInt64(header, 5);
        if (pwLength < 0 || pwLength > MaxPassword || pdfLength < 0 || pdfLength > int.MaxValue) throw new InvalidDataException("Bad probe header.");
        var pw = ReadExact(input, pwLength);
        var pdf = ReadExact(input, (int)pdfLength);
        return (pdf, header[0] == 0 ? null : Encoding.UTF8.GetString(pw));
    }

    private static byte[] ReadExact(Stream input, int count)
    {
        var buffer = new byte[count];
        int done = 0;
        while (done < count)
        {
            int read = input.Read(buffer, done, count - done);
            if (read <= 0) throw new EndOfStreamException();
            done += read;
        }
        return buffer;
    }
}

/// <summary>
/// Sonda w osobnym procesie: awaria, przepełnienie stosu, limit czasu lub pamięci kończą tylko proces potomny. Job Object: limit pamięci, bez
/// procesów potomnych, koniec razem z obiektem, bez okna błędu. Dane idą przez potok dopiero po przypisaniu do obiektu, więc sonda niczego
/// wcześniej nie czyta. Każdy błąd uruchomienia to odrzucenie.
/// </summary>
public sealed class ProcessPdfProbe : IPdfFileProbe
{
    private readonly string _executable;
    private readonly TimeSpan _baseTimeout;

    public ProcessPdfProbe(string executable, TimeSpan? baseTimeout = null)
    {
        _executable = executable;
        _baseTimeout = baseTimeout ?? TimeSpan.FromSeconds(360);
    }

    /// <summary>Limit pamięci sondy: stała część i kilkukrotność pliku.</summary>
    internal static long MemoryLimit(long fileBytes) => Math.Min(1536L * 1024 * 1024 + fileBytes * 8, 8L * 1024 * 1024 * 1024);

    public async Task<PdfProbeVerdict> CheckAsync(byte[] pdf, string? password, CancellationToken ct)
    {
        using var job = JobObject.TryCreate(MemoryLimit(pdf.Length));
        if (job is null) return PdfProbeVerdict.Rejected;
        Process? process = null;
        try
        {
            var start = new ProcessStartInfo(_executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add(PdfProbeProtocol.Argument);
            process = Process.Start(start);
            if (process is null) return PdfProbeVerdict.Rejected;
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!job.Assign(process)) return PdfProbeVerdict.Rejected;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_baseTimeout + TimeSpan.FromSeconds(pdf.Length / (5 * 1024 * 1024)));
            try
            {
                await PdfProbeProtocol.WriteAsync(process.StandardInput.BaseStream, pdf, password, timeout.Token).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // sonda padła w trakcie czytania danych; wynik i tak daje kod wyjścia
            }
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return process.ExitCode switch
            {
                PdfProbeProtocol.ExitOk or PdfProbeProtocol.ExitManagedError => PdfProbeVerdict.Safe,
                PdfProbeProtocol.ExitIncomplete => PdfProbeVerdict.Incomplete,
                _ => PdfProbeVerdict.Rejected,
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return PdfProbeVerdict.Incomplete; // limit czasu procesu: bez awarii, ale bez wyniku
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return PdfProbeVerdict.Rejected; // każdy błąd uruchomienia lub komunikacji
        }
        finally
        {
            if (process is not null)
            {
                try { if (!process.HasExited) process.Kill(); }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
                process.Dispose();
            }
        }
    }

    private sealed class JobObject : IDisposable
    {
        private const int ExtendedLimitInformation = 9;
        private const uint KillOnClose = 0x2000, ActiveProcess = 0x8, ProcessMemory = 0x100, DieOnUnhandled = 0x400;
        private readonly IntPtr _handle;

        private JobObject(IntPtr handle) => _handle = handle;

        public static JobObject? TryCreate(long memoryLimit)
        {
            var handle = CreateJobObjectW(IntPtr.Zero, null);
            if (handle == IntPtr.Zero) return null;
            var job = new JobObject(handle);
            var info = new ExtendedLimits
            {
                Basic = new BasicLimits { LimitFlags = KillOnClose | ActiveProcess | ProcessMemory | DieOnUnhandled, ActiveProcessLimit = 1 },
                ProcessMemoryLimit = (nuint)memoryLimit,
            };
            if (!SetInformationJobObject(handle, ExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<ExtendedLimits>()))
            {
                job.Dispose();
                return null;
            }
            return job;
        }

        public bool Assign(Process process) => AssignProcessToJobObject(_handle, process.Handle);

        public void Dispose() => CloseHandle(_handle);

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimits
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public nuint Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimits
        {
            public BasicLimits Basic;
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
            public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

        [DllImport("kernel32", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimits info, uint length);

        [DllImport("kernel32", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
