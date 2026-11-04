using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using ZetPDF.Core.Localization;

namespace ZetPDF.Editing.Conversion;

/// <summary>Uruchomienie procesu z limitem czasu i anulowaniem; w testach atrapa.</summary>
public interface IProcessRunner
{
    /// <summary>Czeka na koniec procesu i jego procesów potomnych.</summary>
    /// <param name="isDone">Gdy zwróci true (np. plik wynikowy gotowy), procesy dostają chwilę na zakończenie, a potem są zamykane.</param>
    Task<int> RunAsync(ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken ct, Func<bool>? isDone = null);
}

/// <summary>
/// Domyślna implementacja. msedge.exe to krótki starter: kończy się po kilkudziesięciu milisekundach, a pracę wykonuje proces potomny,
/// więc sam koniec uruchomionego procesu niczego nie znaczy. Dlatego śledzone są procesy potomne (migawka systemu, sprawdzenie
/// czasu startu na wypadek ponownego użycia numeru). Przy limicie czasu i anulowaniu kończone są wyłącznie te procesy, po numerach.
/// </summary>
public sealed class SystemProcessRunner : IProcessRunner
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(5);

    public async Task<int> RunAsync(ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken ct, Func<bool>? isDone = null)
    {
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Process did not start.");
        var started = process.StartTime;
        var tree = new Dictionary<int, Process> { [process.Id] = process };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(timeout);
        DateTime? doneAt = null;
        try
        {
            while (true)
            {
                UpdateTree(tree, started);
                if (tree.Values.All(HasExited)) return process.ExitCode;
                if (doneAt is null && isDone?.Invoke() == true) doneAt = DateTime.UtcNow;
                if (doneAt is { } at && DateTime.UtcNow - at > ExitGrace)
                {
                    KillAll(tree);
                    return process.ExitCode;
                }
                await Task.Delay(Poll, stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            UpdateTree(tree, started);
            KillAll(tree);
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException("Process timed out.").WithMessageKey("Edit.Web.Error.Timeout");
        }
        finally
        {
            foreach (var p in tree.Values.Where(p => !ReferenceEquals(p, process))) p.Dispose();
        }
    }

    private static bool HasExited(Process p)
    {
        try { return p.HasExited; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return true; }
    }

    private static void KillAll(Dictionary<int, Process> tree)
    {
        foreach (var p in tree.Values)
        {
            try { if (!p.HasExited) p.Kill(); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        }
    }

    /// <summary>Dopisuje procesy, których rodzicem jest już znany proces (także zakończony: numer rodzica zostaje w migawce).</summary>
    private static void UpdateTree(Dictionary<int, Process> tree, DateTime started)
    {
        bool added = true;
        var entries = ProcessSnapshot.Read();
        while (added)
        {
            added = false;
            foreach (var (pid, parent) in entries)
            {
                if (tree.ContainsKey(pid) || !tree.ContainsKey(parent)) continue;
                try
                {
                    var p = Process.GetProcessById(pid);
                    if (p.StartTime < started) { p.Dispose(); continue; } // numer użyty ponownie: to nie nasz proces
                    tree[pid] = p;
                    added = true;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
    }

    private static class ProcessSnapshot
    {
        private const uint SnapProcess = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Entry
        {
            public uint Size, Usage, ProcessId;
            public UIntPtr HeapId;
            public uint ModuleId, Threads, ParentId;
            public int Priority;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32FirstW(IntPtr snapshot, ref Entry entry);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32NextW(IntPtr snapshot, ref Entry entry);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

        public static List<(int Pid, int Parent)> Read()
        {
            var list = new List<(int, int)>();
            var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
            if (snapshot == new IntPtr(-1)) return list;
            try
            {
                var e = new Entry { Size = (uint)Marshal.SizeOf<Entry>() };
                for (bool ok = Process32FirstW(snapshot, ref e); ok; ok = Process32NextW(snapshot, ref e)) list.Add(((int)e.ProcessId, (int)e.ParentId));
            }
            finally { CloseHandle(snapshot); }
            return list;
        }
    }
}

/// <summary>Strona lub lokalny plik HTML → PDF.</summary>
public interface IWebPdfPrinter
{
    bool IsAvailable { get; }
    Task<byte[]> PrintAsync(WebSource source, CancellationToken ct);
}

/// <summary>
/// PDF przez zainstalowaną Microsoft Edge w trybie headless. Program nie dołącza przeglądarki. Osobny, tymczasowy profil jest
/// usuwany po zakończeniu. Argumenty idą przez <see cref="ProcessStartInfo.ArgumentList"/>, a adres za znacznikiem „--”, po którym
/// Chromium nie czyta już przełączników. Wyłączone są rozszerzenia, synchronizacja i ruch w tle przeglądarki.
/// </summary>
public sealed class EdgePdfPrinter : IWebPdfPrinter
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private readonly IProcessRunner _runner;
    private readonly Func<string?> _locate;
    private readonly string _tempRoot;
    private readonly TimeSpan _timeout;

    public EdgePdfPrinter(string tempRoot) : this(new SystemProcessRunner(), FindEdge, tempRoot, Timeout) { }

    public EdgePdfPrinter(IProcessRunner runner, Func<string?> locate, string tempRoot, TimeSpan timeout)
    {
        _runner = runner;
        _locate = locate;
        _tempRoot = tempRoot;
        _timeout = timeout;
    }

    public bool IsAvailable => _locate() is not null;

    public static IReadOnlyList<string> BuildArguments(string url, string outputPdf, string profileDirectory)
    {
        // Ostatnia linia obrony: walidator odrzuca takie wartości wcześniej.
        if (url.StartsWith('-')) throw new ArgumentException("Address must not start with a dash.", nameof(url));
        return new[]
        {
            "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check", "--disable-extensions", "--disable-sync",
            "--disable-background-networking", "--disable-component-update", "--no-pdf-header-footer",
            "--user-data-dir=" + profileDirectory, "--print-to-pdf=" + outputPdf, "--", url,
        };
    }

    public async Task<byte[]> PrintAsync(WebSource source, CancellationToken ct)
    {
        var edge = _locate() ?? throw new InvalidOperationException("Microsoft Edge not found.").WithMessageKey("Edit.Web.NoEdge");
        var work = Path.Combine(_tempRoot, "web-" + Guid.NewGuid().ToString("N")[..12]);
        var profile = Path.Combine(work, "profile");
        var output = Path.Combine(work, "wynik.pdf");
        Directory.CreateDirectory(profile);
        try
        {
            var start = new ProcessStartInfo(edge) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = work };
            foreach (var a in BuildArguments(source.Argument, output, profile)) start.ArgumentList.Add(a);
            int code = await _runner.RunAsync(start, _timeout, ct, () => IsCompletePdf(output)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(output)) throw new InvalidOperationException("Edge exit code " + code).WithMessageKey("Edit.Web.Error.NoOutput");
            var pdf = await File.ReadAllBytesAsync(output, ct).ConfigureAwait(false);
            if (pdf.Length < 5 || pdf[0] != '%' || pdf[1] != 'P' || pdf[2] != 'D' || pdf[3] != 'F') throw new InvalidOperationException("Not a PDF.").WithMessageKey("Edit.Web.Error.NoOutput");
            return pdf;
        }
        finally
        {
            await RemoveDirectoryAsync(work).ConfigureAwait(false);
        }
    }

    /// <summary>Plik zapisany do końca: nagłówek PDF i znacznik końca pliku.</summary>
    internal static bool IsCompletePdf(string path)
    {
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (f.Length < 16) return false;
            var head = new byte[4];
            f.ReadExactly(head);
            var tail = new byte[Math.Min(32, (int)f.Length)];
            f.Seek(-tail.Length, SeekOrigin.End);
            f.ReadExactly(tail);
            return head[0] == '%' && head[1] == 'P' && head[2] == 'D' && head[3] == 'F' && System.Text.Encoding.ASCII.GetString(tail).Contains("%%EOF", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Procesy potomne przeglądarki mogą jeszcze chwilę trzymać pliki profilu.</summary>
    private static async Task RemoveDirectoryAsync(string path)
    {
        for (int attempt = 0; attempt < 15 && Directory.Exists(path); attempt++)
        {
            try { Directory.Delete(path, recursive: true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await Task.Delay(200).ConfigureAwait(false); }
        }
    }

    public static string? FindEdge()
    {
        const string appPaths = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe";
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = root.OpenSubKey(appPaths);
            if (key?.GetValue(null) is string value && Usable(value.Trim('"'))) return value.Trim('"');
        }
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.LocalApplicationData })
        {
            var candidate = Path.Combine(Environment.GetFolderPath(folder), "Microsoft", "Edge", "Application", "msedge.exe");
            if (Usable(candidate)) return candidate;
        }
        return null;

        static bool Usable(string path) => Path.GetFileName(path).Equals("msedge.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path);
    }
}
