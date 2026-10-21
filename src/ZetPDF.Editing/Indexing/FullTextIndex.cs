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
    public FolderIndex(string root, bool recursive, DateTime builtUtc, IReadOnlyList<IndexedFile> files)
    {
        Root = root;
        Recursive = recursive;
        BuiltUtc = builtUtc;
        Files = files;
    }

    public string Root { get; }
    public bool Recursive { get; }
    public DateTime BuiltUtc { get; }
    public IReadOnlyList<IndexedFile> Files { get; }
    public string FullPath(IndexedFile f) => Path.Combine(Root, f.RelativePath);
}

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
/// Indeks pełnotekstowy folderu: tekst stron PDF (PdfPig) w lokalnym pliku (GZip) w %LOCALAPPDATA%\ZetPDF\index,
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
        using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
        using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
        using (var w = new BinaryWriter(gz, Encoding.UTF8))
        {
            w.Write(Magic);
            w.Write(FormatVersion);
            w.Write(index.Root);
            w.Write(index.Recursive);
            w.Write(index.BuiltUtc.Ticks);
            w.Write(index.Files.Count);
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
        File.Move(temp, target, overwrite: true);
    }

    /// <summary>Wczytuje indeks folderu (null, gdy brak lub plik nieczytelny/innej wersji).</summary>
    public FolderIndex? Load(string folder)
    {
        var path = IndexFileFor(folder);
        return File.Exists(path) ? Read(path, headerOnly: false) : null;
    }

    /// <summary>Foldery z indeksem (nagłówki plików indeksu).</summary>
    public IReadOnlyList<FolderIndex> ListIndexes()
    {
        if (!Directory.Exists(IndexDirectory)) return Array.Empty<FolderIndex>();
        return Directory.EnumerateFiles(IndexDirectory, "*.zidx").Select(p => Read(p, headerOnly: true)).OfType<FolderIndex>()
            .OrderBy(i => i.Root, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public void Delete(string folder)
    {
        var path = IndexFileFor(folder);
        if (File.Exists(path)) File.Delete(path);
    }

    private static FolderIndex? Read(string path, bool headerOnly)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var r = new BinaryReader(gz, Encoding.UTF8);
            if (r.ReadString() != Magic || r.ReadInt32() != FormatVersion) return null;
            var root = r.ReadString();
            bool recursive = r.ReadBoolean();
            var built = new DateTime(r.ReadInt64(), DateTimeKind.Utc);
            int count = r.ReadInt32();
            if (count < 0) return null;
            if (headerOnly) return new FolderIndex(root, recursive, built, Array.Empty<IndexedFile>());
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
            return new FolderIndex(root, recursive, built, files);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException or UnauthorizedAccessException or FormatException
                                   or ArgumentException or OverflowException or DecoderFallbackException)
        {
            return null;
        }
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
