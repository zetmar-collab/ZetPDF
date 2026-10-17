using PdfSharp.Pdf;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Pages.Engine;

public sealed partial class PageOperations
{
    // ------------------------------------------------------------------ dzielenie

    public Task<IReadOnlyList<string>> SplitAsync(byte[] pdf, SplitOptions options, string outputDirectory, string baseName, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            using var src = PdfIo.OpenImport(pdf);
            int n = src.PageCount;
            var parts = PlanSplit(src, options, ct);
            Directory.CreateDirectory(outputDirectory);
            var result = new List<string>();
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var outlines = Outlines.Read(src);
            for (int k = 0; k < parts.Count; k++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(OperationProgress.Of(k, parts.Count));
                var (first, last, title) = parts[k];
                var bytes = BuildPart(src, first, last, outlines);
                var name = FileNames.Expand(string.IsNullOrWhiteSpace(options.FileNamePattern) ? "{name}_{n}" : options.FileNamePattern, baseName, k + 1, first + 1, title);
                var path = PdfIo.UniquePath(Path.Combine(outputDirectory, name), taken);
                PdfIo.WriteFileSafe(path, bytes);
                result.Add(path);
            }
            progress?.Report(OperationProgress.Of(parts.Count, parts.Count));
            return result;
        }, ct);

    /// <summary>Plan podziału: zakresy (pierwsza, ostatnia strona 0-based, tytuł zakładki).</summary>
    internal static List<(int First, int Last, string Title)> PlanSplit(PdfDocument src, SplitOptions options, CancellationToken ct)
    {
        int n = src.PageCount;
        var parts = new List<(int, int, string)>();
        switch (options.Mode)
        {
            case SplitMode.EveryNPages:
            {
                int per = Math.Max(1, options.PagesPerFile);
                for (int s = 0; s < n; s += per) parts.Add((s, Math.Min(n, s + per) - 1, string.Empty));
                break;
            }
            case SplitMode.TopLevelBookmarks:
            {
                var starts = Outlines.Read(src).Where(o => o.PageIndex >= 0)
                    .GroupBy(o => o.PageIndex).Select(g => (Page: g.Key, Title: g.First().Title)).OrderBy(x => x.Page).ToList();
                if (starts.Count == 0) throw new InvalidOperationException(PagesModule.Text("Pages.Error.NoBookmarks"));
                if (starts[0].Page > 0) parts.Add((0, starts[0].Page - 1, string.Empty));
                for (int i = 0; i < starts.Count; i++)
                {
                    int last = i + 1 < starts.Count ? starts[i + 1].Page - 1 : n - 1;
                    parts.Add((starts[i].Page, last, starts[i].Title));
                }
                break;
            }
            case SplitMode.MaxFileSize:
            {
                long max = Math.Max(16 * 1024, options.MaxFileSizeBytes);
                int s = 0;
                while (s < n)
                {
                    ct.ThrowIfCancellationRequested();
                    // wyszukiwanie wykładnicze + binarne największego e, dla którego rozmiar(s..e) <= max
                    int good = s, step = 1, bad = -1;
                    while (true)
                    {
                        int e = Math.Min(n - 1, good + step);
                        if (e == good) break;
                        if (PartSize(src, s, e) <= max) { good = e; step *= 2; if (good == n - 1) break; }
                        else { bad = e; break; }
                    }
                    if (bad > 0)
                    {
                        int lo = good, hi = bad;
                        while (hi - lo > 1)
                        {
                            ct.ThrowIfCancellationRequested();
                            int mid = (lo + hi) / 2;
                            if (PartSize(src, s, mid) <= max) lo = mid; else hi = mid;
                        }
                        good = lo;
                    }
                    parts.Add((s, good, string.Empty));
                    s = good + 1;
                }
                break;
            }
        }
        return parts;
    }

    private static long PartSize(PdfDocument src, int first, int last) => BuildPart(src, first, last, null).LongLength;

    private static byte[] BuildPart(PdfDocument src, int first, int last, IReadOnlyList<OutlineItem>? outlines)
    {
        using var doc = PdfIo.NewDocument();
        CopyInfo(src, doc);
        var map = new Dictionary<int, PdfPage>();
        for (int i = first; i <= last; i++) map[i] = doc.AddPage(src.Pages[i]);
        if (outlines is not null) Outlines.Write(doc.Outlines, outlines, i => map.TryGetValue(i, out var p) ? p : null);
        return PdfIo.Save(doc);
    }

    // ------------------------------------------------------------------ scalanie

    public async Task<byte[]> MergeAsync(IReadOnlyList<MergeSource> sources, MergeOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        if (sources.Count == 0) throw new ArgumentException("No sources.", nameof(sources));
        var loaded = new List<(MergeSource Source, byte[] Data)>();
        for (int i = 0; i < sources.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var s = sources[i];
            var data = s.Data is not null
                ? await _sources.DecryptAsync(s.Data, s.Password, ct).ConfigureAwait(false)
                : await _sources.LoadFileAsync(s.Path, s.Password, ct).ConfigureAwait(false);
            loaded.Add((s, data));
        }
        return await Task.Run(() => MergeCore(loaded, options, progress, ct), ct).ConfigureAwait(false);
    }

    internal static byte[] MergeCore(IReadOnlyList<(MergeSource Source, byte[] Data)> sources, MergeOptions options, IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        using var doc = PdfIo.NewDocument();
        for (int k = 0; k < sources.Count; k++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(OperationProgress.Of(k, sources.Count));
            var (source, data) = sources[k];
            using var src = PdfIo.OpenImport(data);
            if (k == 0) CopyInfo(src, doc);
            var pages = source.Pages ?? Enumerable.Range(0, src.PageCount).ToArray();
            Validate(pages, src.PageCount, nameof(MergeSource.Pages));
            var map = new Dictionary<int, PdfPage>();
            PdfPage? first = null;
            foreach (var p in pages)
            {
                var added = doc.AddPage(src.Pages[p]);
                first ??= added;
                map.TryAdd(p, added);
            }
            if (first is null) continue;
            var target = doc.Outlines;
            if (options.BookmarkPerFile)
            {
                var title = string.IsNullOrEmpty(source.Path) ? (k + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : Path.GetFileNameWithoutExtension(source.Path);
                target = doc.Outlines.Add(title, first, false).Outlines;
            }
            if (options.KeepBookmarks)
                Outlines.Write(target, Outlines.Read(src), i => map.TryGetValue(i, out var pg) ? pg : null);
        }
        progress?.Report(OperationProgress.Of(sources.Count, sources.Count));
        if (doc.PageCount == 0) throw new InvalidOperationException(PagesModule.Text("Pages.Error.NoPages"));
        return PdfIo.Save(doc);
    }
}
