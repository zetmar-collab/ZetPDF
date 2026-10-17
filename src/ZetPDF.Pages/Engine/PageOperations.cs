using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Core.Localization;

namespace ZetPDF.Pages.Engine;

/// <summary>
/// Operacje na stronach (karta Organizuj, menu miniatur, Kreator działań). Wszystkie metody: bajty bez szyfrowania →
/// nowe bajty; wejście nie jest modyfikowane. Indeksy 0-based.
/// </summary>
public sealed partial class PageOperations : IPageOperations
{
    private readonly IPdfRenderer? _renderer;
    private readonly SourceLoader _sources;

    public PageOperations() : this(null) { }

    /// <summary>Renderer (opcjonalny) – białe marginesy przy kadrowaniu i odszyfrowanie plików źródłowych hasłem użytkownika.</summary>
    public PageOperations(IPdfRenderer? renderer)
    {
        _renderer = renderer;
        _sources = new SourceLoader(renderer);
    }

    // ------------------------------------------------------------------ wstawianie

    public async Task<byte[]> InsertFromFileAsync(byte[] pdf, string sourcePath, string? sourcePassword, IReadOnlyList<int>? sourcePages, int insertAt, CancellationToken ct = default)
    {
        var source = await _sources.LoadFileAsync(sourcePath, sourcePassword, ct).ConfigureAwait(false);
        return await InsertFromBytesAsync(pdf, source, sourcePages, insertAt, ct).ConfigureAwait(false);
    }

    public Task<byte[]> InsertFromBytesAsync(byte[] pdf, byte[] sourcePdf, IReadOnlyList<int>? sourcePages, int insertAt, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            using var doc = PdfIo.OpenModify(pdf);
            using var src = PdfIo.OpenImport(sourcePdf);
            var pages = sourcePages ?? Enumerable.Range(0, src.PageCount).ToArray();
            Validate(pages, src.PageCount, nameof(sourcePages));
            int at = Math.Clamp(insertAt, 0, doc.PageCount);
            foreach (var p in pages)
            {
                ct.ThrowIfCancellationRequested();
                doc.Pages.Insert(at++, src.Pages[p]);
            }
            return PdfIo.Save(doc);
        }, ct);

    public Task<byte[]> InsertBlankAsync(byte[] pdf, int insertAt, int count, PdfSize size, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
            CheckSize(size);
            using var doc = PdfIo.OpenModify(pdf);
            int at = Math.Clamp(insertAt, 0, doc.PageCount);
            for (int i = 0; i < count; i++) AddBlank(doc, at + i, size);
            return PdfIo.Save(doc);
        }, ct);

    public Task<byte[]> CreateBlankDocumentAsync(PdfSize size, int count = 1, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
            CheckSize(size);
            using var doc = PdfIo.NewDocument();
            for (int i = 0; i < count; i++) AddBlank(doc, i, size);
            return PdfIo.Save(doc);
        }, ct);

    private static void AddBlank(PdfDocument doc, int index, PdfSize size)
    {
        var page = doc.Pages.Insert(index);
        page.MediaBox = PdfIo.ToPdfRectangle(new PdfRect(0, 0, size.Width, size.Height));
        page.Elements.Remove("/CropBox");
        // pusty strumień treści – niektóre czytniki wymagają /Contents
        var c = page.Contents.AppendContent();
        c.CreateStream(Array.Empty<byte>());
    }

    private static void CheckSize(PdfSize size)
    {
        if (size.Width < 3 || size.Height < 3 || size.Width > 14400 || size.Height > 14400)
            throw new ArgumentOutOfRangeException(nameof(size), "Page size must be between 3 and 14400 points.");
    }

    // ------------------------------------------------------------------ usuwanie, wyodrębnianie, zastępowanie

    public Task<byte[]> DeleteAsync(byte[] pdf, IReadOnlyList<int> pages, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            using var doc = PdfIo.OpenModify(pdf);
            var set = Distinct(pages, doc.PageCount);
            if (set.Count == 0) return pdf;
            if (set.Count >= doc.PageCount) throw new InvalidOperationException(PagesModule.Text("Pages.Error.CannotDeleteAll")).WithMessageKey("Pages.Error.CannotDeleteAll");
            RemovePages(doc, set);
            return PdfIo.Save(doc);
        }, ct);

    public Task<byte[]> ExtractAsync(byte[] pdf, IReadOnlyList<int> pages, CancellationToken ct = default) =>
        Task.Run(() => ExtractCore(pdf, pages, ct), ct);

    internal static byte[] ExtractCore(byte[] pdf, IReadOnlyList<int> pages, CancellationToken ct)
    {
        using var src = PdfIo.OpenImport(pdf);
        Validate(pages, src.PageCount, nameof(pages));
        if (pages.Count == 0) throw new ArgumentException("No pages to extract.", nameof(pages)).WithMessageKey("Pages.Error.NoPagesToExtract");
        using var doc = PdfIo.NewDocument();
        CopyInfo(src, doc);
        foreach (var p in pages)
        {
            ct.ThrowIfCancellationRequested();
            doc.AddPage(src.Pages[p]);
        }
        return PdfIo.Save(doc);
    }

    public Task<IReadOnlyList<string>> ExtractToFilesAsync(byte[] pdf, IReadOnlyList<int> pages, string outputDirectory, string fileNamePattern, string baseName, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            using var src = PdfIo.OpenImport(pdf);
            Validate(pages, src.PageCount, nameof(pages));
            Directory.CreateDirectory(outputDirectory);
            var result = new List<string>();
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int n = 0;
            foreach (var p in pages)
            {
                ct.ThrowIfCancellationRequested();
                n++;
                using var doc = PdfIo.NewDocument();
                CopyInfo(src, doc);
                doc.AddPage(src.Pages[p]);
                var name = FileNames.Expand(string.IsNullOrWhiteSpace(fileNamePattern) ? "{name}_{page}" : fileNamePattern, baseName, n, p + 1, string.Empty);
                var path = PdfIo.UniquePath(Path.Combine(outputDirectory, name), taken);
                PdfIo.WriteFileSafe(path, PdfIo.Save(doc));
                result.Add(path);
            }
            return result;
        }, ct);

    public async Task<byte[]> ReplaceAsync(byte[] pdf, IReadOnlyList<int> targetPages, string sourcePath, string? sourcePassword, IReadOnlyList<int> sourcePages, CancellationToken ct = default)
    {
        var source = await _sources.LoadFileAsync(sourcePath, sourcePassword, ct).ConfigureAwait(false);
        return await Task.Run(() => ReplaceCore(pdf, targetPages, source, sourcePages, ct), ct).ConfigureAwait(false);
    }

    internal static byte[] ReplaceCore(byte[] pdf, IReadOnlyList<int> targetPages, byte[] source, IReadOnlyList<int> sourcePages, CancellationToken ct)
    {
        if (targetPages.Count != sourcePages.Count)
            throw new ArgumentException(PagesModule.Text("Pages.Error.ReplaceCountMismatch")).WithMessageKey("Pages.Error.ReplaceCountMismatch");
        using var doc = PdfIo.OpenModify(pdf);
        using var src = PdfIo.OpenImport(source);
        Validate(targetPages, doc.PageCount, nameof(targetPages));
        Validate(sourcePages, src.PageCount, nameof(sourcePages));
        if (targetPages.Distinct().Count() != targetPages.Count) throw new ArgumentException("Duplicate target pages.", nameof(targetPages)).WithMessageKey("Pages.Error.DuplicateTargets");
        var old = targetPages.Select(i => doc.Pages[i]).ToArray();
        // wstaw nowe strony przed zastępowanymi, potem usuń stare (zachowuje pozycje)
        for (int k = 0; k < targetPages.Count; k++)
        {
            ct.ThrowIfCancellationRequested();
            int at = IndexOf(doc, old[k]);
            var inserted = doc.Pages.Insert(at, src.Pages[sourcePages[k]]);
            RedirectPage(doc, old[k], inserted);
        }
        RemovePages(doc, old.Select(p => IndexOf(doc, p)).ToHashSet());
        return PdfIo.Save(doc);
    }

    // ------------------------------------------------------------------ kolejność

    public Task<byte[]> DuplicateAsync(byte[] pdf, IReadOnlyList<int> pages, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            using var doc = PdfIo.OpenModify(pdf);
            using var copy = PdfIo.OpenImport(pdf);
            var set = Distinct(pages, doc.PageCount).OrderByDescending(i => i).ToArray();
            foreach (var p in set)
            {
                ct.ThrowIfCancellationRequested();
                doc.Pages.Insert(p + 1, copy.Pages[p]);
            }
            return PdfIo.Save(doc);
        }, ct);

    public Task<byte[]> MoveAsync(byte[] pdf, IReadOnlyList<int> pages, int targetIndex, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            using var doc = PdfIo.OpenModify(pdf);
            int n = doc.PageCount;
            var moving = Distinct(pages, n).OrderBy(i => i).ToArray();
            if (moving.Length == 0) return pdf;
            int target = Math.Clamp(targetIndex, 0, n);
            var rest = Enumerable.Range(0, n).Where(i => Array.BinarySearch(moving, i) < 0).ToList();
            int insertPos = target - moving.Count(i => i < target);
            var order = new List<int>(rest);
            order.InsertRange(Math.Clamp(insertPos, 0, order.Count), moving);
            Reorder(doc, order);
            return PdfIo.Save(doc);
        }, ct);

    public Task<byte[]> SwapAsync(byte[] pdf, int pageA, int pageB, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            using var doc = PdfIo.OpenModify(pdf);
            Validate(new[] { pageA, pageB }, doc.PageCount, nameof(pageA));
            if (pageA == pageB) return pdf;
            var order = Enumerable.Range(0, doc.PageCount).ToArray();
            (order[pageA], order[pageB]) = (order[pageB], order[pageA]);
            Reorder(doc, order);
            return PdfIo.Save(doc);
        }, ct);

    public Task<byte[]> ReverseAsync(byte[] pdf, IReadOnlyList<int>? pages, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            using var doc = PdfIo.OpenModify(pdf);
            int n = doc.PageCount;
            var positions = pages is null ? Enumerable.Range(0, n).ToArray() : Distinct(pages, n).OrderBy(i => i).ToArray();
            if (positions.Length < 2) return pdf;
            var order = Enumerable.Range(0, n).ToArray();
            for (int k = 0; k < positions.Length; k++) order[positions[k]] = positions[positions.Length - 1 - k];
            Reorder(doc, order);
            return PdfIo.Save(doc);
        }, ct);

    public async Task<byte[]> InterleaveAsync(byte[] pdf, string secondPath, string? secondPassword, bool reverseSecond, CancellationToken ct = default)
    {
        var second = await _sources.LoadFileAsync(secondPath, secondPassword, ct).ConfigureAwait(false);
        return await Task.Run(() => InterleaveCore(pdf, second, reverseSecond, ct), ct).ConfigureAwait(false);
    }

    internal static byte[] InterleaveCore(byte[] pdf, byte[] second, bool reverseSecond, CancellationToken ct)
    {
        using var doc = PdfIo.OpenModify(pdf);
        using var src = PdfIo.OpenImport(second);
        int a = doc.PageCount, b = src.PageCount;
        for (int i = 0; i < b; i++)
        {
            ct.ThrowIfCancellationRequested();
            doc.AddPage(src.Pages[reverseSecond ? b - 1 - i : i]);
        }
        var order = new List<int>(a + b);
        for (int i = 0; i < Math.Max(a, b); i++)
        {
            if (i < a) order.Add(i);
            if (i < b) order.Add(a + i);
        }
        Reorder(doc, order);
        return PdfIo.Save(doc);
    }

    public Task<byte[]> RotateAsync(byte[] pdf, IReadOnlyList<int> pages, int degrees, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            if (degrees % 90 != 0) throw new ArgumentException("Rotation must be a multiple of 90 degrees.", nameof(degrees)).WithMessageKey("Pages.Error.Rotation90");
            using var doc = PdfIo.OpenModify(pdf);
            foreach (var p in Distinct(pages, doc.PageCount))
            {
                var page = doc.Pages[p];
                int r = PdfIo.NormalizeRotation(PdfIo.GetRotation(page) + degrees);
                if (r == 0) page.Elements.Remove("/Rotate");
                else page.Elements.SetInteger("/Rotate", r);
            }
            return PdfIo.Save(doc);
        }, ct);

    // ------------------------------------------------------------------ pomocniki

    internal static void Validate(IReadOnlyList<int> pages, int count, string param)
    {
        foreach (var p in pages)
            if (p < 0 || p >= count) throw new ArgumentOutOfRangeException(param, $"Page index {p} is outside 0–{count - 1}.");
    }

    internal static HashSet<int> Distinct(IReadOnlyList<int> pages, int count)
    {
        Validate(pages, count, nameof(pages));
        return pages.ToHashSet();
    }

    internal static int IndexOf(PdfDocument doc, PdfPage page)
    {
        for (int i = 0; i < doc.PageCount; i++)
            if (ReferenceEquals(doc.Pages[i], page) || (page.Reference is not null && doc.Pages[i].Reference?.ObjectID == page.Reference.ObjectID)) return i;
        return -1;
    }

    /// <summary>Ustawia kolejność stron: order[i] = dawny indeks strony, która ma stanąć na pozycji i.</summary>
    internal static void Reorder(PdfDocument doc, IReadOnlyList<int> order)
    {
        int n = doc.PageCount;
        if (order.Count != n || order.Distinct().Count() != n || order.Any(i => i < 0 || i >= n))
            throw new ArgumentException("Invalid page order.", nameof(order)).WithMessageKey("Pages.Error.InvalidOrder");
        var original = Enumerable.Range(0, n).Select(i => doc.Pages[i]).ToArray();
        for (int i = 0; i < n; i++)
        {
            var page = original[order[i]];
            int cur = IndexOf(doc, page);
            if (cur != i) doc.Pages.MovePage(cur, i);
        }
    }

    /// <summary>Usuwa strony i porządkuje odwołania do nich (zakładki, łącza, nazwane cele, pola formularza).</summary>
    internal static void RemovePages(PdfDocument doc, ISet<int> indices)
    {
        var valid = indices.Where(i => i >= 0 && i < doc.PageCount).ToHashSet();
        var removed = valid.Select(i => doc.Pages[i]).ToList();
        var removedIds = removed.Where(p => p.Reference is not null).Select(p => p.Reference!.ObjectID).ToHashSet();
        var widgetIds = new HashSet<PdfObjectID>();
        foreach (var p in removed)
            if (PdfIo.GetArray(p, "/Annots") is { } annots)
                foreach (var a in annots.Elements.OfType<PdfReference>()) widgetIds.Add(a.ObjectID);
        var map = References.ReplacementMap(doc, valid);
        foreach (var i in valid.OrderByDescending(i => i)) doc.Pages.RemoveAt(i);
        if (doc.PageCount == 0) return;
        References.Redirect(doc, map);
        References.RemoveFields(doc, widgetIds, removedIds);
    }

    /// <summary>Przekierowuje odwołania ze starej strony na nową (Zastąp).</summary>
    private static void RedirectPage(PdfDocument doc, PdfPage from, PdfPage to)
    {
        if (from.Reference is null || to.Reference is null) return;
        References.RedirectTo(doc, from.Reference.ObjectID, to.Reference);
    }

    internal static void CopyInfo(PdfDocument src, PdfDocument dst)
    {
        try
        {
            if (!string.IsNullOrEmpty(src.Info.Title)) dst.Info.Title = src.Info.Title;
            if (!string.IsNullOrEmpty(src.Info.Author)) dst.Info.Author = src.Info.Author;
            if (!string.IsNullOrEmpty(src.Info.Subject)) dst.Info.Subject = src.Info.Subject;
            if (!string.IsNullOrEmpty(src.Info.Keywords)) dst.Info.Keywords = src.Info.Keywords;
        }
        catch (Exception) { /* metadane nieczytelne – pomijamy */ }
    }
}
