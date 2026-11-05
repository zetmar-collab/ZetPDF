using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ZetPDF.Core.Contracts;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace ZetPDF.Editing.Indexing;

public enum IndexedFileStatus { Ok, Encrypted, Error }

/// <summary>Plik w indeksie: ścieżka względna, rozmiar i data modyfikacji (aktualizacja przyrostowa), tekst stron.</summary>
public sealed record IndexedFile(string RelativePath, long Size, long ModifiedUtcTicks, IndexedFileStatus Status, IReadOnlyList<string> Pages, string? Error = null);

/// <summary>Indeks folderu (wczytany z pliku).</summary>
public sealed class FolderIndex
{
    private readonly int? _fileCount;

    public FolderIndex(string root, bool recursive, DateTime builtUtc, IReadOnlyList<IndexedFile> files, int? fileCount = null)
    {
        Root = root;
        Recursive = recursive;
        BuiltUtc = builtUtc;
        Files = files;
        _fileCount = fileCount;
    }

    /// <summary>Liczba plików w indeksie (znana także po odczycie samego nagłówka).</summary>
    public int FileCount => _fileCount ?? Files.Count;

    public string Root { get; }
    public bool Recursive { get; }
    public DateTime BuiltUtc { get; }
    public IReadOnlyList<IndexedFile> Files { get; }
    public string FullPath(IndexedFile f) => Path.Combine(Root, f.RelativePath);
}

/// <summary>Plik indeksu na liście w oknie zarządzania. Root == null: plik nieczytelny (uszkodzony lub z innego konta) – można go tylko usunąć.</summary>
public sealed record IndexEntry(string FilePath, string? Root, bool Recursive, DateTime? BuiltUtc, int FileCount, long SizeBytes);

public sealed record IndexBuildResult(FolderIndex Index, int Indexed, int Reused, int Removed, IReadOnlyList<string> Encrypted, IReadOnlyList<string> Failed);

public sealed record TextSearchOptionsEx
{
    public bool MatchCase { get; init; }
    public bool IgnoreDiacritics { get; init; }
    public bool WholeWords { get; init; }
    public int MaxHits { get; init; } = 5000;
}

/// <summary>Trafienie: plik (lub dokument otwarty), strona, pozycja w tekście strony, kontekst przed/po.</summary>
public sealed record FullTextHit(string Source, string DisplayName, int PageIndex, int CharIndex, int Length, int OccurrenceOnPage, string Before, string Match, string After)
{
    public string Context => Before + Match + After;
}

/// <summary>
/// Indeks pełnotekstowy folderu: tekst stron PDF (PdfPig) w lokalnym pliku (GZip, chroniony DPAPI – bieżący użytkownik)
/// w %LOCALAPPDATA%\ZetPDF\index,
/// budowany w tle z postępem i anulowaniem, aktualizowany przyrostowo (rozmiar + data modyfikacji).
/// Wyszukiwanie = przegląd znormalizowanego tekstu w pamięci (frazy, fragmenty słów, bez wielkości liter,
/// opcjonalnie bez znaków diakrytycznych). Wybór zamiast SQLite FTS5: brak natywnych bibliotek (single-file),
/// polskie znaki i wyszukiwanie fragmentów bez tokenizera, wystarczająca szybkość dla tysięcy dokumentów.
/// </summary>
public sealed class FullTextIndex
{
    private const string Magic = "ZIDX";
    private const int FormatVersion = 1;
    private const int ContextChars = 40;
    private const int MaxHeaderBytes = 1024 * 1024;

    /// <summary>Format chroniony: znacznik, długość nagłówka, DPAPI(nagłówek), DPAPI(GZip listy plików). Stary format (sam GZip) zaczyna się od 1F 8B.</summary>
    private static readonly byte[] ProtectedMagic = "ZIDXDP2\n"u8.ToArray();
    private static readonly byte[] HeaderEntropy = "ZetPDF.Index.Header.2"u8.ToArray();
    private static readonly byte[] BodyEntropy = "ZetPDF.Index.Body.2"u8.ToArray();

    public FullTextIndex(string indexDirectory) => IndexDirectory = indexDirectory;

    public string IndexDirectory { get; }

    public static string NormalizeFolder(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    public string IndexFileFor(string folder)
    {
        var key = NormalizeFolder(folder).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16].ToLowerInvariant();
        return Path.Combine(IndexDirectory, hash + ".zidx");
    }

    // ------------------------------------------------------------------ budowanie

    public Task<IndexBuildResult> BuildAsync(string folder, bool recursive, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run(() => Build(folder, recursive, progress, ct), ct);

    private IndexBuildResult Build(string folder, bool recursive, IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        var root = NormalizeFolder(folder);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var previous = Load(root);
        var old = previous is not null && previous.Recursive == recursive
            ? previous.Files.ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, IndexedFile>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive, AttributesToSkip = FileAttributes.System };
        var paths = Directory.EnumerateFiles(root, "*.pdf", options).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        var files = new List<IndexedFile>(paths.Count);
        int indexed = 0, reused = 0;
        var encrypted = new List<string>();
        var failed = new List<string>();
        try
        {
            for (int i = 0; i < paths.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var path = paths[i];
                var rel = Path.GetRelativePath(root, path);
                progress?.Report(new OperationProgress(paths.Count == 0 ? 1 : (double)i / paths.Count, rel, i + 1, paths.Count));
                FileInfo info;
                try { info = new FileInfo(path); _ = info.Length; }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                long ticks = info.LastWriteTimeUtc.Ticks;
                if (old.TryGetValue(rel, out var prev) && prev.Size == info.Length && prev.ModifiedUtcTicks == ticks)
                {
                    files.Add(prev);
                    reused++;
                }
                else
                {
                    files.Add(Extract(path, rel, info.Length, ticks));
                    indexed++;
                }
                var f = files[^1];
                if (f.Status == IndexedFileStatus.Encrypted) encrypted.Add(rel);
                else if (f.Status == IndexedFileStatus.Error) failed.Add(rel);
            }
        }
        catch (OperationCanceledException)
        {
            // to, co już wyodrębniono, zostaje zapisane – kolejna aktualizacja zacznie od tego miejsca
            foreach (var kv in old) if (!files.Any(f => f.RelativePath.Equals(kv.Key, StringComparison.OrdinalIgnoreCase)) && File.Exists(Path.Combine(root, kv.Key))) files.Add(kv.Value);
            Save(new FolderIndex(root, recursive, DateTime.UtcNow, files));
            throw;
        }
        int removed = old.Keys.Count(k => !files.Any(f => f.RelativePath.Equals(k, StringComparison.OrdinalIgnoreCase)));
        progress?.Report(new OperationProgress(1, null, paths.Count, paths.Count));
        var index = new FolderIndex(root, recursive, DateTime.UtcNow, files);
        Save(index);
        return new IndexBuildResult(index, indexed, reused, removed, encrypted, failed);
    }

    /// <summary>Tekst stron pliku (PdfPig: słowa w kolejności treści). Plik zaszyfrowany hasłem → Encrypted.</summary>
    internal static IndexedFile Extract(string path, string relative, long size, long ticks)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            ZetPDF.Core.Documents.PdfFileProbes.EnsureSafeAsync(bytes).GetAwaiter().GetResult();
            return new IndexedFile(relative, size, ticks, IndexedFileStatus.Ok, ExtractPages(bytes));
        }
        catch (UglyToad.PdfPig.Exceptions.PdfDocumentEncryptedException ex)
        {
            return new IndexedFile(relative, size, ticks, IndexedFileStatus.Encrypted, Array.Empty<string>(), ex.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            return new IndexedFile(relative, size, ticks, IndexedFileStatus.Error, Array.Empty<string>(), ex.Message);
        }
    }

    /// <summary>Tekst stron PDF z bajtów (także dokumentów otwartych – sesja trzyma bajty bez szyfrowania).</summary>
    public static IReadOnlyList<string> ExtractPages(byte[] pdf)
    {
        using var doc = PigDocument.Open(pdf);
        var pages = new List<string>(doc.NumberOfPages);
        foreach (var page in doc.GetPages())
        {
            var sb = new StringBuilder();
            double? lastBottom = null;
            foreach (var w in page.GetWords())
            {
                if (sb.Length > 0) sb.Append(lastBottom is { } b && Math.Abs(b - w.BoundingBox.Bottom) > w.BoundingBox.Height * 0.6 ? '\n' : ' ');
                sb.Append(w.Text);
                lastBottom = w.BoundingBox.Bottom;
            }
            pages.Add(sb.ToString());
        }
        return pages;
    }

    // ------------------------------------------------------------------ zapis/odczyt

    private void Save(FolderIndex index)
    {
        Directory.CreateDirectory(IndexDirectory);
        var target = IndexFileFor(index.Root);
        var temp = target + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        var header = new MemoryStream();
        using (var w = new BinaryWriter(header, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(index.Root);
            w.Write(index.Recursive);
            w.Write(index.BuiltUtc.Ticks);
            w.Write(index.Files.Count);
        }
        var body = new MemoryStream();
        using (var gz = new GZipStream(body, CompressionLevel.Fastest, leaveOpen: true))
        using (var w = new BinaryWriter(gz, Encoding.UTF8))
        {
            foreach (var f in index.Files)
            {
                w.Write(f.RelativePath);
                w.Write(f.Size);
                w.Write(f.ModifiedUtcTicks);
                w.Write((byte)f.Status);
                w.Write(f.Error ?? string.Empty);
                w.Write(f.Pages.Count);
                foreach (var p in f.Pages) w.Write(p);
            }
        }
        var protectedHeader = ProtectedData.Protect(header.ToArray(), HeaderEntropy, DataProtectionScope.CurrentUser);
        var protectedBody = ProtectedData.Protect(body.ToArray(), BodyEntropy, DataProtectionScope.CurrentUser);
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            {
                fs.Write(ProtectedMagic);
                fs.Write(BitConverter.GetBytes(protectedHeader.Length));
                fs.Write(protectedHeader);
                fs.Write(protectedBody);
            }
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temp);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Wczytuje indeks folderu (null, gdy brak lub plik nieczytelny/innej wersji).</summary>
    public FolderIndex? Load(string folder)
    {
        var path = IndexFileFor(folder);
        if (!File.Exists(path)) return null;
        var index = Read(path, headerOnly: false);
        if (index is not null && !IsProtectedFile(path)) TryProtectLegacy(index);
        return index;
    }

    /// <summary>
    /// Pliki starego formatu (tekst tylko w GZip): czytelne przepisywane na format chroniony, nieczytelne usuwane (indeks i tak zbuduje
    /// się od nowa). Sprząta też stare pliki tymczasowe, które mogły zawierać jawny tekst. Wołane w tle przy starcie.
    /// </summary>
    public void ProtectLegacyFiles()
    {
        try
        {
            if (!Directory.Exists(IndexDirectory)) return;
            foreach (var path in Directory.GetFiles(IndexDirectory, "*.zidx"))
            {
                if (IsProtectedFile(path)) continue;
                var index = Read(path, headerOnly: false);
                if (index is null || !TryProtectLegacy(index)) TryDeleteFile(path);
            }
            foreach (var temp in Directory.GetFiles(IndexDirectory, "*.tmp"))
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(temp) > TimeSpan.FromDays(1)) TryDeleteFile(temp);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private bool TryProtectLegacy(FolderIndex index)
    {
        try
        {
            Save(index);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return false;
        }
    }

    private static bool IsProtectedFile(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return HasProtectedMagic(fs);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool HasProtectedMagic(Stream stream)
    {
        var magic = new byte[ProtectedMagic.Length];
        return stream.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false) == magic.Length && magic.AsSpan().SequenceEqual(ProtectedMagic);
    }

    /// <summary>Foldery z indeksem (nagłówki plików indeksu).</summary>
    public IReadOnlyList<FolderIndex> ListIndexes()
    {
        if (!Directory.Exists(IndexDirectory)) return Array.Empty<FolderIndex>();
        return Directory.EnumerateFiles(IndexDirectory, "*.zidx").Select(p => Read(p, headerOnly: true)).OfType<FolderIndex>()
            .OrderBy(i => i.Root, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Wszystkie pliki indeksu z rozmiarem i datą (także nieczytelne, żeby dało się je usunąć).</summary>
    public IReadOnlyList<IndexEntry> ListEntries()
    {
        if (!Directory.Exists(IndexDirectory)) return Array.Empty<IndexEntry>();
        var entries = new List<IndexEntry>();
        foreach (var path in Directory.EnumerateFiles(IndexDirectory, "*.zidx"))
        {
            long size;
            try { size = new FileInfo(path).Length; }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            var head = Read(path, headerOnly: true);
            entries.Add(new IndexEntry(path, head?.Root, head?.Recursive ?? false, head?.BuiltUtc, head?.FileCount ?? 0, size));
        }
        return entries.OrderBy(e => e.Root is null).ThenBy(e => e.Root, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public void Delete(string folder)
    {
        var path = IndexFileFor(folder);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>Usuwa plik indeksu z listy; wyłącznie plik w folderze indeksów.</summary>
    public void DeleteEntry(IndexEntry entry)
    {
        var full = Path.GetFullPath(entry.FilePath);
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(IndexDirectory), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Not an index file.", nameof(entry));
        if (File.Exists(full)) File.Delete(full);
    }

    /// <summary>Usuwa wszystkie indeksy i pliki tymczasowe.</summary>
    public void DeleteAll()
    {
        if (!Directory.Exists(IndexDirectory)) return;
        foreach (var path in Directory.GetFiles(IndexDirectory, "*.zidx").Concat(Directory.GetFiles(IndexDirectory, "*.tmp"))) File.Delete(path);
    }

    private static FolderIndex? Read(string path, bool headerOnly)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (HasProtectedMagic(fs)) return ReadProtected(fs, headerOnly);
            fs.Position = 0;
            return ReadLegacy(fs, headerOnly);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException or UnauthorizedAccessException or FormatException
                                   or ArgumentException or OverflowException or DecoderFallbackException or CryptographicException)
        {
            return null;
        }
    }

    private static FolderIndex? ReadProtected(FileStream fs, bool headerOnly)
    {
        var lengthBytes = new byte[sizeof(int)];
        fs.ReadExactly(lengthBytes);
        int headerLength = BitConverter.ToInt32(lengthBytes);
        if (headerLength <= 0 || headerLength > MaxHeaderBytes) return null;
        var protectedHeader = new byte[headerLength];
        fs.ReadExactly(protectedHeader);
        using var header = new BinaryReader(new MemoryStream(ProtectedData.Unprotect(protectedHeader, HeaderEntropy, DataProtectionScope.CurrentUser)), Encoding.UTF8);
        var root = header.ReadString();
        bool recursive = header.ReadBoolean();
        var built = new DateTime(header.ReadInt64(), DateTimeKind.Utc);
        int count = header.ReadInt32();
        if (count < 0) return null;
        if (headerOnly) return new FolderIndex(root, recursive, built, Array.Empty<IndexedFile>(), count);
        var protectedBody = new byte[fs.Length - fs.Position];
        fs.ReadExactly(protectedBody);
        var body = ProtectedData.Unprotect(protectedBody, BodyEntropy, DataProtectionScope.CurrentUser);
        using var gz = new GZipStream(new MemoryStream(body), CompressionMode.Decompress);
        using var r = new BinaryReader(gz, Encoding.UTF8);
        var files = ReadFiles(r, count);
        return files is null ? null : new FolderIndex(root, recursive, built, files);
    }

    /// <summary>Stary format: całość w GZip, bez ochrony.</summary>
    private static FolderIndex? ReadLegacy(FileStream fs, bool headerOnly)
    {
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var r = new BinaryReader(gz, Encoding.UTF8);
        if (r.ReadString() != Magic || r.ReadInt32() != FormatVersion) return null;
        var root = r.ReadString();
        bool recursive = r.ReadBoolean();
        var built = new DateTime(r.ReadInt64(), DateTimeKind.Utc);
        int count = r.ReadInt32();
        if (count < 0) return null;
        if (headerOnly) return new FolderIndex(root, recursive, built, Array.Empty<IndexedFile>(), count);
        var files = ReadFiles(r, count);
        return files is null ? null : new FolderIndex(root, recursive, built, files);
    }

    private static List<IndexedFile>? ReadFiles(BinaryReader r, int count)
    {
        // pojemność bez ufania licznikom z pliku (uszkodzony plik nie może wymusić ogromnej alokacji)
        var files = new List<IndexedFile>(Math.Min(count, 4096));
        for (int i = 0; i < count; i++)
        {
            var rel = r.ReadString();
            long size = r.ReadInt64();
            long ticks = r.ReadInt64();
            var status = (IndexedFileStatus)r.ReadByte();
            var error = r.ReadString();
            int pages = r.ReadInt32();
            if (pages < 0 || !Enum.IsDefined(status)) return null;
            var list = new List<string>(Math.Min(pages, 4096));
            for (int p = 0; p < pages; p++) list.Add(r.ReadString());
            files.Add(new IndexedFile(rel, size, ticks, status, list, error.Length == 0 ? null : error));
        }
        return files;
    }

    // ------------------------------------------------------------------ wyszukiwanie

    public static IReadOnlyList<FullTextHit> Search(FolderIndex index, string query, TextSearchOptionsEx options, CancellationToken ct = default)
    {
        var hits = new List<FullTextHit>();
        foreach (var f in index.Files.Where(f => f.Status == IndexedFileStatus.Ok))
        {
            ct.ThrowIfCancellationRequested();
            SearchPages(index.FullPath(f), f.RelativePath, f.Pages, query, options, hits);
            if (hits.Count >= options.MaxHits) break;
        }
        return hits;
    }

    /// <summary>Wyszukiwanie w tekście stron jednego dokumentu (dopisuje do <paramref name="hits"/>).</summary>
    public static void SearchPages(string source, string displayName, IReadOnlyList<string> pages, string query, TextSearchOptionsEx options, List<FullTextHit> hits)
    {
        var q = TextFolding.Fold(Collapse(query), !options.MatchCase, options.IgnoreDiacritics);
        if (q.Length == 0) return;
        for (int p = 0; p < pages.Count && hits.Count < options.MaxHits; p++)
        {
            var text = pages[p];
            var folded = TextFolding.Fold(text, !options.MatchCase, options.IgnoreDiacritics);
            int occurrence = 0;
            for (int i = folded.IndexOf(q, StringComparison.Ordinal); i >= 0; i = folded.IndexOf(q, i + 1, StringComparison.Ordinal))
            {
                if (options.WholeWords && !IsWholeWord(folded, i, q.Length)) continue;
                int start = Math.Max(0, i - ContextChars), end = Math.Min(text.Length, i + q.Length + ContextChars);
                hits.Add(new FullTextHit(source, displayName, p, i, q.Length, occurrence++,
                    OneLine(text[start..i], start > 0, false), OneLine(text.Substring(i, q.Length), false, false), OneLine(text[(i + q.Length)..end], false, end < text.Length)));
                if (hits.Count >= options.MaxHits) return;
            }
        }
    }

    /// <summary>Pozycje kolejnych wystąpień frazy w dowolnym tekście (np. tekście strony z PDFium – do podświetlenia).</summary>
    public static IReadOnlyList<int> FindAll(string text, string query, TextSearchOptionsEx options)
    {
        var q = TextFolding.Fold(Collapse(query), !options.MatchCase, options.IgnoreDiacritics);
        var folded = CollapseFolded(TextFolding.Fold(text, !options.MatchCase, options.IgnoreDiacritics));
        var list = new List<int>();
        if (q.Length == 0) return list;
        for (int i = folded.IndexOf(q, StringComparison.Ordinal); i >= 0; i = folded.IndexOf(q, i + 1, StringComparison.Ordinal))
            if (!options.WholeWords || IsWholeWord(folded, i, q.Length)) list.Add(i);
        return list;
    }

    // PDFium wstawia \r\n między wierszami – zamieniamy oba znaki na spacje (długość bez zmian)
    private static string CollapseFolded(string s) => s.Replace('\r', ' ').Replace('\n', ' ');

    private static string Collapse(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool IsWholeWord(string s, int i, int len) =>
        (i == 0 || !char.IsLetterOrDigit(s[i - 1])) && (i + len >= s.Length || !char.IsLetterOrDigit(s[i + len]));

    private static string OneLine(string s, bool ellipsisStart, bool ellipsisEnd)
    {
        var t = s.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        return (ellipsisStart ? "…" : string.Empty) + t + (ellipsisEnd ? "…" : string.Empty);
    }

    // ------------------------------------------------------------------ CSV

    /// <summary>
    /// Eksport wyników do CSV (UTF-8 z BOM). Każde pole w cudzysłowie; wartości zaczynające się od =, +, -, @, tabulatora
    /// lub CR dostają apostrof na początku (ochrona przed wstrzyknięciem formuł w arkuszu kalkulacyjnym).
    /// </summary>
    public static string ToCsv(IReadOnlyList<FullTextHit> hits, IReadOnlyList<string> headers, string separator)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(separator, headers.Select(CsvField)));
        foreach (var h in hits)
            sb.AppendLine(string.Join(separator, new[]
            {
                CsvField(Path.GetFileName(h.DisplayName)), CsvField(h.Source), CsvField((h.PageIndex + 1).ToString(CultureInfo.InvariantCulture)), CsvField(h.Context),
            }));
        return sb.ToString();
    }

    public static string CsvField(string value)
    {
        var v = value ?? string.Empty;
        if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@' or '\t' or '\r') v = "'" + v;
        return "\"" + v.Replace("\"", "\"\"") + "\"";
    }
}
