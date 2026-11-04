using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Core.Documents;

public enum RecoveryDocumentState
{
    /// <summary>Plik na dysku z niezapisanymi zmianami.</summary>
    Modified,
    /// <summary>Dokument jeszcze nigdy niezapisany (wynik konwersji, scalania, pusty).</summary>
    NeverSaved,
}

/// <summary>
/// Opis kopii odzyskiwania. NIE zawiera haseł: dla dokumentu chronionego zapamiętuje tylko, że ochrona była
/// (<see cref="IsProtected"/>), a hasła użytkownik podaje przy odzyskiwaniu (DECYZJE D-27).
/// </summary>
public sealed record RecoveryInfo
{
    public string Id { get; init; } = string.Empty;
    public int ProcessId { get; init; }
    /// <summary>Start procesu-właściciela (odróżnia go od innego procesu z tym samym numerem).</summary>
    public long ProcessStartUtcTicks { get; init; }
    public string? OriginalPath { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public DateTime SavedUtc { get; init; }
    public RecoveryDocumentState State { get; init; }
    public bool IsProtected { get; init; }
    public DocumentPermissions Permissions { get; init; } = DocumentPermissions.All;
    /// <summary>Stan pliku oryginalnego z chwili otwarcia lub ostatniego zapisu – po odzyskaniu działa ostrzeżenie o zmianie na dysku.</summary>
    public long SourceLength { get; init; }
    public long SourceModifiedUtcTicks { get; init; }
    public bool SourceSigned { get; init; }
}

/// <summary>
/// Folder odzyskiwania: para plików na dokument, oba zawsze chronione DPAPI (bieżący użytkownik):
/// <c>&lt;pid&gt;-&lt;guid&gt;.recovery</c> (bajty PDF) i <c>&lt;pid&gt;-&lt;guid&gt;.info</c> (opis JSON). Podmiana atomowa
/// (plik tymczasowy + przeniesienie). Klasa jest bezpieczna wątkowo; zapis może trwać w tle.
/// </summary>
public sealed class RecoveryStore
{
    private const string PdfExtension = ".recovery";
    private const string InfoExtension = ".info";
    private const string TempExtension = ".tmp";
    private const long MaxInfoBytes = 1024 * 1024;

    private static readonly byte[] PdfEntropy = "ZetPDF.Recovery.Pdf.1"u8.ToArray();
    private static readonly byte[] InfoEntropy = "ZetPDF.Recovery.Info.1"u8.ToArray();
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    private readonly object _gate = new();

    public RecoveryStore(string directory) => Directory_ = directory;

    private string Directory_ { get; }

    public string DirectoryPath => Directory_;

    /// <summary>Identyfikator nowej kopii bieżącego procesu.</summary>
    public static string NewId() => $"{Environment.ProcessId}-{Guid.NewGuid():N}";

    /// <summary>Znacznik startu bieżącego procesu (do <see cref="RecoveryInfo.ProcessStartUtcTicks"/>).</summary>
    public static long CurrentProcessStartUtcTicks()
    {
        using var p = Process.GetCurrentProcess();
        return p.StartTime.ToUniversalTime().Ticks;
    }

    /// <summary>
    /// Zapisuje kopię. <paramref name="stillWanted"/> jest sprawdzane tuż przed podmianą (pod blokadą wspólną z <see cref="Delete"/>):
    /// false = dokument zapisano lub zamknięto w trakcie, więc kopii nie publikujemy. Zwraca, czy kopia została opublikowana.
    /// </summary>
    public bool Write(RecoveryInfo info, byte[] pdf, Func<bool>? stillWanted = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(pdf);
        if (!IsValidId(info.Id)) throw new ArgumentException("Invalid recovery id.", nameof(info));
        Directory.CreateDirectory(Directory_);

        var unique = Guid.NewGuid().ToString("N");
        var tempPdf = PathOf(info.Id, $".{unique}.pdf{TempExtension}");
        var tempInfo = PathOf(info.Id, $".{unique}.info{TempExtension}");
        try
        {
            WriteDurable(tempPdf, ProtectedData.Protect(pdf, PdfEntropy, DataProtectionScope.CurrentUser));
            ct.ThrowIfCancellationRequested();
            var json = JsonSerializer.SerializeToUtf8Bytes(info, Json);
            WriteDurable(tempInfo, ProtectedData.Protect(json, InfoEntropy, DataProtectionScope.CurrentUser));
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (stillWanted is not null && !stillWanted()) return false;
                // Najpierw dane: awaria między podmianami zostawia nowsze dane ze starszym opisem albo dane bez opisu (sprzątane).
                File.Move(tempPdf, PathOf(info.Id, PdfExtension), overwrite: true);
                File.Move(tempInfo, PathOf(info.Id, InfoExtension), overwrite: true);
            }
            return true;
        }
        finally
        {
            TryDelete(tempPdf);
            TryDelete(tempInfo);
        }
    }

    private static void WriteDurable(string path, byte[] data)
    {
        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough);
        fs.Write(data);
        fs.Flush(flushToDisk: true);
    }

    /// <summary>Odczyt bajtów PDF kopii (bez szyfrowania). Wyjątki: <see cref="IOException"/>, <see cref="CryptographicException"/>.</summary>
    public byte[] ReadPdf(string id)
    {
        if (!IsValidId(id)) throw new ArgumentException("Invalid recovery id.", nameof(id));
        return ProtectedData.Unprotect(File.ReadAllBytes(PathOf(id, PdfExtension)), PdfEntropy, DataProtectionScope.CurrentUser);
    }

    /// <summary>Usuwa kopię i jej pliki tymczasowe (po zapisie, zamknięciu, odzyskaniu, odrzuceniu).</summary>
    public void Delete(string id)
    {
        if (!IsValidId(id)) return;
        lock (_gate)
        {
            TryDelete(PathOf(id, InfoExtension));
            TryDelete(PathOf(id, PdfExtension));
        }
    }

    /// <summary>Usuwa wszystkie kopie bieżącego procesu (normalne wyjście).</summary>
    public void DeleteOwn()
    {
        var prefix = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-";
        foreach (var file in EnumerateFiles())
        {
            if (Path.GetFileName(file).StartsWith(prefix, StringComparison.Ordinal))
                lock (_gate) TryDelete(file);
        }
    }

    /// <summary>
    /// Kopie porzucone przez procesy, które już nie działają (najnowsze pierwsze). Przy okazji sprząta to, czego nie da się
    /// odzyskać: pliki tymczasowe po awarii, dane bez opisu i opisy nieczytelne lub bez danych. Kopii działających procesów nie dotyka.
    /// </summary>
    public IReadOnlyList<RecoveryInfo> FindAbandoned()
    {
        var result = new List<RecoveryInfo>();
        foreach (var file in EnumerateFiles())
        {
            var name = Path.GetFileName(file);
            var dash = name.IndexOf('-', StringComparison.Ordinal);
            var dot = name.IndexOf('.', StringComparison.Ordinal);
            if (dash <= 0 || dot < dash || !int.TryParse(name[..dash], out var pid)) continue;
            var id = name[..dot];
            if (!IsValidId(id)) continue;

            if (name.EndsWith(TempExtension, StringComparison.Ordinal))
            {
                if (!IsProcessAlive(pid, null)) TryDelete(file);
            }
            else if (name.EndsWith(PdfExtension, StringComparison.Ordinal))
            {
                if (!File.Exists(PathOf(id, InfoExtension)) && !IsProcessAlive(pid, null)) TryDelete(file);
            }
            else if (name.EndsWith(InfoExtension, StringComparison.Ordinal))
            {
                var info = TryReadInfo(file, id);
                if (info is null || !File.Exists(PathOf(id, PdfExtension)))
                {
                    if (!IsProcessAlive(pid, null)) Delete(id);
                }
                else if (!IsProcessAlive(info.ProcessId, info.ProcessStartUtcTicks)) result.Add(info);
            }
        }
        result.Sort((a, b) => b.SavedUtc.CompareTo(a.SavedUtc));
        return result;
    }

    private static RecoveryInfo? TryReadInfo(string file, string id)
    {
        try
        {
            if (new FileInfo(file).Length > MaxInfoBytes) return null;
            var json = ProtectedData.Unprotect(File.ReadAllBytes(file), InfoEntropy, DataProtectionScope.CurrentUser);
            var info = JsonSerializer.Deserialize<RecoveryInfo>(json, Json);
            // Identyfikator z nazwy pliku musi zgadzać się z opisem; ścieżka oryginału – tylko pełna, bez strumieni i urządzeń.
            if (info is null || info.Id != id) return null;
            if (info.OriginalPath is not null && !IsAcceptablePath(info.OriginalPath)) return null;
            return info;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            return null;
        }
    }

    internal static bool IsAcceptablePath(string path)
    {
        try
        {
            return Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\?\", StringComparison.Ordinal) && !path.StartsWith(@"\\.\", StringComparison.Ordinal)
                && path.IndexOf(':', 2) < 0 && path.IndexOfAny(Path.GetInvalidPathChars()) < 0;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Czy proces działa (przy znanym starcie – dokładnie ten; numer procesu system może przydzielić ponownie).</summary>
    internal static bool IsProcessAlive(int pid, long? startUtcTicks)
    {
        if (pid == Environment.ProcessId && startUtcTicks is null) return true;
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return false;
            return startUtcTicks is null || p.StartTime.ToUniversalTime().Ticks == startUtcTicks.Value;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; } // proces innego użytkownika – nie nasz
    }

    private IEnumerable<string> EnumerateFiles()
    {
        try
        {
            return Directory.Exists(Directory_) ? Directory.GetFiles(Directory_) : Array.Empty<string>();
        }
        catch (IOException) { return Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private static bool IsValidId(string id)
    {
        var dash = id.IndexOf('-', StringComparison.Ordinal);
        return dash > 0 && int.TryParse(id.AsSpan(0, dash), out _) && id.Length == dash + 33
            && id.AsSpan(dash + 1).IndexOfAnyExcept("0123456789abcdef") < 0;
    }

    private string PathOf(string id, string suffix) => Path.Combine(Directory_, id + suffix);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
