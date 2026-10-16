using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PDFiumCore;
using ZetPDF.Core.Contracts;
using static ZetPDF.Rendering.PdfiumNative;
using RenderFlags = ZetPDF.Core.Contracts.RenderFlags;

namespace ZetPDF.Rendering;

/// <summary>
/// Otwarty dokument PDFium (<see cref="IRenderDocument"/>).
/// <list type="bullet">
/// <item>Bajty są kopiowane i przypięte (<see cref="GCHandle"/>) na czas życia uchwytu – wołający może dalej używać swojej tablicy.</item>
/// <item>Każda metoda wykonuje pracę na wątku <see cref="PdfiumThread"/>; uchwyty stron, stron tekstu, bitmap
/// i wyszukiwania są tworzone i zwalniane w obrębie jednego zadania (try/finally).</item>
/// <item><see cref="Dispose"/> kolejkuje zamknięcie (formularz → dokument → przypięcie) za zadaniami już zleconymi,
/// więc trwające rendery kończą się poprawnie; nowe wywołania po Dispose rzucają <see cref="ObjectDisposedException"/>.</item>
/// </list>
/// </summary>
internal sealed class PdfiumDocument : IRenderDocument
{
    /// <summary>Limit pikseli jednej bitmapy (ochrona przed brakiem pamięci przy ogromnym powiększeniu – użyj Clip).</summary>
    public const long MaxBitmapPixels = 120_000_000;

    private readonly PdfiumThread _thread;
    private FpdfDocumentT? _doc;
    private GCHandle _pin;
    private FpdfFormHandleT? _form;
    private FPDF_FORMFILLINFO? _formInfo;
    private volatile bool _disposed;
    private bool _closed;

    private PdfiumDocument(PdfiumThread thread, FpdfDocumentT doc, GCHandle pin, IReadOnlyList<PageInfo> pages)
    {
        _thread = thread;
        _doc = doc;
        _pin = pin;
        Pages = pages;
    }

    /// <summary>Otwiera dokument. Wołać na wątku PDFium.</summary>
    internal static PdfiumDocument OpenOnThread(PdfiumThread thread, byte[] pdf, string? password, CancellationToken ct)
    {
        var copy = (byte[])pdf.Clone();
        var doc = LoadDocument(copy, password, out var pin);
        try
        {
            int count = fpdfview.FPDF_GetPageCount(doc);
            if (count <= 0) throw new PdfCorruptException(Text("Render.Error.NoPages"));
            var pages = new PageInfo[count];
            for (int i = 0; i < count; i++)
            {
                if ((i & 31) == 0) ct.ThrowIfCancellationRequested();
                pages[i] = ReadPageInfo(doc, i);
            }
            var result = new PdfiumDocument(thread, doc, pin, pages);
            result.InitForms();
            return result;
        }
        catch
        {
            CloseDocument(doc, ref pin);
            throw;
        }
    }

    private static PageInfo ReadPageInfo(FpdfDocumentT doc, int index)
    {
        var page = LoadPage(doc, index);
        try
        {
            int rot = fpdf_edit.FPDFPageGetRotation(page);
            int rotation = rot is >= 0 and <= 3 ? rot * 90 : 0;
            using var box = new FS_RECTF_();
            double left, bottom, width, height;
            if (fpdfview.FPDF_GetPageBoundingBox(page, box) != 0)
            {
                left = Math.Min(box.Left, box.Right);
                bottom = Math.Min(box.Bottom, box.Top);
                width = Math.Abs(box.Right - box.Left);
                height = Math.Abs(box.Top - box.Bottom);
            }
            else
            {
                left = bottom = 0;
                double w = fpdfview.FPDF_GetPageWidthF(page), h = fpdfview.FPDF_GetPageHeightF(page);
                (width, height) = rotation % 180 == 0 ? (w, h) : (h, w);
            }
            if (width <= 0 || height <= 0) (width, height) = (PdfSize.A4.Width, PdfSize.A4.Height);
            return new PageInfo(index, new PdfSize(width, height), rotation, new PdfPoint(left, bottom));
        }
        finally { ClosePage(page); }
    }

    private void InitForms()
    {
        if (fpdf_formfill.FPDF_GetFormType(_doc!) == 0) return; // FORMTYPE_NONE
        var info = new FPDF_FORMFILLINFO { Version = 1 };
        var form = fpdf_formfill.FPDFDOC_InitFormFillEnvironment(_doc!, info);
        if (IsNull(form))
        {
            info.Dispose();
            return;
        }
        FormCreated();
        _form = form;
        _formInfo = info;
    }

    public int PageCount => Pages.Count;

    public IReadOnlyList<PageInfo> Pages { get; }

    /// <summary>Czy uchwyty natywne zostały już zamknięte (testy).</summary>
    internal bool IsClosed => Volatile.Read(ref _closed);

    private FpdfDocumentT Doc
    {
        get
        {
            if (_closed || _doc is null) throw new ObjectDisposedException(nameof(PdfiumDocument));
            return _doc;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PdfiumDocument));
    }

    private void CheckPage(int pageIndex)
    {
        if ((uint)pageIndex >= (uint)Pages.Count) throw new ArgumentOutOfRangeException(nameof(pageIndex));
    }

    // ------------------------------------------------------------------ render

    public Task<RenderedBitmap> RenderPageAsync(int pageIndex, RenderRequest request, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        CheckPage(pageIndex);
        ArgumentNullException.ThrowIfNull(request);
        if (!(request.Scale > 0) || double.IsInfinity(request.Scale)) throw new ArgumentOutOfRangeException(nameof(request));
        return _thread.InvokeAsync(() => RenderOnThread(pageIndex, request, ct), ct);
    }

    /// <summary>Rozmiar bitmapy całej strony (piksele) dla danego żądania – ta sama formuła co w renderze.</summary>
    internal static (int Width, int Height) FullPixelSize(PageInfo info, RenderRequest request)
    {
        int total = ((info.Rotation + request.ViewRotation) % 360 + 360) % 360;
        double w = info.Size.Width, h = info.Size.Height;
        if (total % 180 != 0) (w, h) = (h, w);
        return (Math.Max(1, (int)Math.Round(w * request.Scale)), Math.Max(1, (int)Math.Round(h * request.Scale)));
    }

    private RenderedBitmap RenderOnThread(int pageIndex, RenderRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var doc = Doc;
        var info = Pages[pageIndex];
        var (fullW, fullH) = FullPixelSize(info, request);
        var clip = request.Clip ?? new PixelRect(0, 0, fullW, fullH);
        int cx = Math.Clamp(clip.X, 0, fullW), cy = Math.Clamp(clip.Y, 0, fullH);
        int cw = Math.Clamp(clip.Width, 1, Math.Max(1, fullW - cx)), ch = Math.Clamp(clip.Height, 1, Math.Max(1, fullH - cy));
        if ((long)cw * ch > MaxBitmapPixels) throw new ArgumentOutOfRangeException(nameof(request), Text("Render.Error.TooLarge"));

        int rotate = ((request.ViewRotation % 360 + 360) % 360) / 90;
        int flags = 0;
        if (request.Flags.HasFlag(RenderFlags.Annotations)) flags |= FlagAnnot;
        if (request.Flags.HasFlag(RenderFlags.Grayscale)) flags |= FlagGrayscale;
        if (request.Flags.HasFlag(RenderFlags.Printing)) flags |= FlagPrinting;
        if (request.Flags.HasFlag(RenderFlags.NoSmoothText)) flags |= FlagNoSmoothText;

        var page = LoadPage(doc, pageIndex);
        try
        {
            var bmp = CreateBitmap(cw, ch);
            try
            {
                fpdfview.FPDFBitmapFillRect(bmp, 0, 0, cw, ch, request.BackgroundBgra);
                fpdfview.FPDF_RenderPageBitmap(bmp, page, -cx, -cy, fullW, fullH, rotate, flags);
                if (request.Flags.HasFlag(RenderFlags.FormFields) && !IsNull(_form))
                {
                    fpdf_formfill.FORM_OnAfterLoadPage(page, _form!);
                    try { fpdf_formfill.FPDF_FFLDraw(_form!, bmp, page, -cx, -cy, fullW, fullH, rotate, flags); }
                    finally { fpdf_formfill.FORM_OnBeforeClosePage(page, _form!); }
                }
                int stride = fpdfview.FPDFBitmapGetStride(bmp);
                var buffer = new byte[stride * ch];
                Marshal.Copy(fpdfview.FPDFBitmapGetBuffer(bmp), buffer, 0, buffer.Length);
                if ((request.BackgroundBgra >> 24) != 0xFF) Premultiply(buffer);
                return new RenderedBitmap(cw, ch, stride, buffer, request.Scale);
            }
            finally { DestroyBitmap(bmp); }
        }
        finally { ClosePage(page); }
    }

    private static void Premultiply(byte[] bgra)
    {
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            int a = bgra[i + 3];
            if (a == 255) continue;
            bgra[i] = (byte)(bgra[i] * a / 255);
            bgra[i + 1] = (byte)(bgra[i + 1] * a / 255);
            bgra[i + 2] = (byte)(bgra[i + 2] * a / 255);
        }
    }

    // ------------------------------------------------------------------ tekst

    public Task<PageText> GetPageTextAsync(int pageIndex, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        CheckPage(pageIndex);
        return _thread.InvokeAsync(() => WithTextPage(pageIndex, tp => ReadText(pageIndex, tp)), ct);
    }

    private T WithTextPage<T>(int pageIndex, Func<FpdfTextpageT, T> work)
    {
        var page = LoadPage(Doc, pageIndex);
        try
        {
            var tp = LoadTextPage(page);
            try { return work(tp); }
            finally { CloseTextPage(tp); }
        }
        finally { ClosePage(page); }
    }

    private static string ReadRawText(FpdfTextpageT tp, int count)
    {
        var chars = new char[count];
        for (int i = 0; i < count; i++)
        {
            uint u = fpdf_text.FPDFTextGetUnicode(tp, i);
            chars[i] = u <= 0xFFFF ? (char)u : '�';
        }
        return new string(chars);
    }

    private static PageText ReadText(int pageIndex, FpdfTextpageT tp)
    {
        int n = fpdf_text.FPDFTextCountChars(tp);
        if (n <= 0) return PageText.Empty(pageIndex);
        var text = ReadRawText(tp, n);
        var list = new TextChar[n];
        for (int i = 0; i < n; i++)
        {
            double l = 0, r = 0, b = 0, t = 0;
            bool generated = fpdf_text.FPDFTextIsGenerated(tp, i) == 1;
            PdfRect box = default;
            if (!generated && fpdf_text.FPDFTextGetCharBox(tp, i, ref l, ref r, ref b, ref t) != 0)
                box = new PdfRect(Math.Min(l, r), Math.Min(b, t), Math.Max(l, r), Math.Max(b, t));
            double size = generated ? 0 : fpdf_text.FPDFTextGetFontSize(tp, i);
            list[i] = new TextChar(text[i], box, size, generated);
        }
        return new PageText(pageIndex, text, list);
    }

    // ------------------------------------------------------------------ wyszukiwanie

    public async IAsyncEnumerable<TextSearchHit> SearchAsync(string query, TextSearchOptions options, IProgress<OperationProgress>? progress = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(query)) yield break;
        ulong flags = 0;
        if (options.HasFlag(TextSearchOptions.MatchCase)) flags |= MatchCase;
        if (options.HasFlag(TextSearchOptions.WholeWord)) flags |= MatchWholeWord;
        int total = Pages.Count;
        for (int p = 0; p < total; p++)
        {
            ct.ThrowIfCancellationRequested();
            int pageIndex = p;
            var hits = await _thread.InvokeAsync(() => WithTextPage(pageIndex, tp => SearchPage(pageIndex, tp, query, flags)), ct).ConfigureAwait(false);
            progress?.Report(OperationProgress.Of(p + 1, total));
            foreach (var h in hits) yield return h;
        }
    }

    /// <summary>Wyszukiwanie na jednej stronie (wątek PDFium). Do testów i wyszukiwania w widoku.</summary>
    internal Task<IReadOnlyList<TextSearchHit>> SearchPageAsync(int pageIndex, string query, TextSearchOptions options, CancellationToken ct = default)
    {
        ulong flags = (options.HasFlag(TextSearchOptions.MatchCase) ? MatchCase : 0) | (options.HasFlag(TextSearchOptions.WholeWord) ? MatchWholeWord : 0);
        return _thread.InvokeAsync(() => WithTextPage(pageIndex, tp => SearchPage(pageIndex, tp, query, flags)), ct);
    }

    /// <summary>
    /// Wyszukiwanie na stronie. Dopasowanie robimy w kodzie zarządzanym, bo FPDFText_FindStart w PDFium 157
    /// nie składa wielkości liter dla znaków spoza ASCII („GĘŚLĄ” ≠ „gęślą”) i traktuje litery diakrytyczne
    /// jako granice słów („jaź” byłoby „całym słowem” w „jaźń”). PDFium dostarcza tekst i prostokąty (FPDFText_GetRect).
    /// Białe znaki (w tym znaki końca wiersza generowane przez PDFium) są zwijane do jednej spacji,
    /// więc fraza może przechodzić przez koniec wiersza.
    /// </summary>
    private static IReadOnlyList<TextSearchHit> SearchPage(int pageIndex, FpdfTextpageT tp, string query, ulong flags)
    {
        int n = fpdf_text.FPDFTextCountChars(tp);
        if (n <= 0) return Array.Empty<TextSearchHit>();
        var text = ReadRawText(tp, n);
        var matches = TextMatcher.FindAll(text, query, (flags & MatchCase) != 0, (flags & MatchWholeWord) != 0);
        if (matches.Count == 0) return Array.Empty<TextSearchHit>();
        var result = new List<TextSearchHit>(matches.Count);
        foreach (var (idx, cnt) in matches)
        {
            var rects = new List<PdfRect>();
            int nr = fpdf_text.FPDFTextCountRects(tp, idx, cnt);
            for (int r = 0; r < nr; r++)
            {
                double l = 0, t = 0, rr = 0, b = 0;
                if (fpdf_text.FPDFTextGetRect(tp, r, ref l, ref t, ref rr, ref b) != 0)
                    rects.Add(new PdfRect(Math.Min(l, rr), Math.Min(b, t), Math.Max(l, rr), Math.Max(b, t)));
            }
            result.Add(new TextSearchHit(pageIndex, idx, cnt, rects, Context(text, idx, cnt)));
        }
        return result;
    }

    private static string Context(string text, int idx, int cnt)
    {
        const int Around = 30;
        int start = Math.Max(0, idx - Around);
        int end = Math.Min(text.Length, idx + cnt + Around);
        var s = text[start..end].Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        return (start > 0 ? "…" : string.Empty) + s.Trim() + (end < text.Length ? "…" : string.Empty);
    }

    /// <summary>
    /// FPDF_PageToDevice – punkt strony → piksel urządzenia (testy zgodności z <see cref="PageTransform"/>).
    /// rotate = obrót widoku 0..3 (ćwierćobroty), PDFium sam uwzględnia /Rotate strony.
    /// </summary>
    internal Task<(int X, int Y)> PageToDeviceAsync(int pageIndex, int sizeX, int sizeY, int rotate, double x, double y)
    {
        ThrowIfDisposed();
        CheckPage(pageIndex);
        return _thread.InvokeAsync(() =>
        {
            var page = LoadPage(Doc, pageIndex);
            try
            {
                int dx = 0, dy = 0;
                fpdfview.FPDF_PageToDevice(page, 0, 0, sizeX, sizeY, rotate, x, y, ref dx, ref dy);
                return (dx, dy);
            }
            finally { ClosePage(page); }
        });
    }

    // ------------------------------------------------------------------ łącza

    public Task<IReadOnlyList<PdfLinkInfo>> GetLinksAsync(int pageIndex, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        CheckPage(pageIndex);
        return _thread.InvokeAsync(() => ReadLinks(pageIndex), ct);
    }

    private IReadOnlyList<PdfLinkInfo> ReadLinks(int pageIndex)
    {
        var doc = Doc;
        var page = LoadPage(doc, pageIndex);
        var result = new List<PdfLinkInfo>();
        try
        {
            int count = fpdf_annot.FPDFPageGetAnnotCount(page);
            for (int i = 0; i < count; i++)
            {
                var annot = fpdf_annot.FPDFPageGetAnnot(page, i);
                if (IsNull(annot)) continue;
                try
                {
                    if (fpdf_annot.FPDFAnnotGetSubtype(annot) != AnnotLink) continue;
                    using var r = new FS_RECTF_();
                    if (fpdf_annot.FPDFAnnotGetRect(annot, r) == 0) continue;
                    var rect = new PdfRect(Math.Min(r.Left, r.Right), Math.Min(r.Bottom, r.Top), Math.Max(r.Left, r.Right), Math.Max(r.Bottom, r.Top));
                    var link = fpdf_annot.FPDFAnnotGetLink(annot);
                    if (IsNull(link)) continue;
                    var dest = fpdf_doc.FPDFLinkGetDest(doc, link);
                    if (IsNull(dest))
                    {
                        var action = fpdf_doc.FPDFLinkGetAction(link);
                        if (!IsNull(action))
                        {
                            var type = fpdf_doc.FPDFActionGetType(action);
                            if (type == ActionUri)
                            {
                                var uri = ReadAscii((buf, len) => fpdf_doc.FPDFActionGetURIPath(doc, action, buf, len));
                                result.Add(new PdfLinkInfo(pageIndex, rect, PdfLinkKind.Uri, uri, null, null));
                                continue;
                            }
                            if (type == ActionGoTo) dest = fpdf_doc.FPDFActionGetDest(doc, action);
                        }
                    }
                    if (!IsNull(dest))
                    {
                        var (target, point) = ReadDest(doc, dest!);
                        result.Add(new PdfLinkInfo(pageIndex, rect, target >= 0 ? PdfLinkKind.GoToPage : PdfLinkKind.Other, null, target >= 0 ? target : null, point));
                    }
                    else
                    {
                        result.Add(new PdfLinkInfo(pageIndex, rect, PdfLinkKind.Other, null, null, null));
                    }
                }
                finally { fpdf_annot.FPDFPageCloseAnnot(annot); }
            }
        }
        finally { ClosePage(page); }
        return result;
    }

    private static (int Page, PdfPoint? Point) ReadDest(FpdfDocumentT doc, FpdfDestT dest)
    {
        int target = fpdf_doc.FPDFDestGetDestPageIndex(doc, dest);
        int hasX = 0, hasY = 0, hasZoom = 0;
        float x = 0, y = 0, zoom = 0;
        PdfPoint? point = null;
        if (fpdf_doc.FPDFDestGetLocationInPage(dest, ref hasX, ref hasY, ref hasZoom, ref x, ref y, ref zoom) != 0 && (hasX != 0 || hasY != 0))
            point = new PdfPoint(hasX != 0 ? x : 0, hasY != 0 ? y : 0);
        return (target, point);
    }

    // ------------------------------------------------------------------ zakładki

    public Task<IReadOnlyList<BookmarkNode>> GetBookmarksAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return _thread.InvokeAsync(() => ReadBookmarks(Doc, null, 0), ct);
    }

    private static IReadOnlyList<BookmarkNode> ReadBookmarks(FpdfDocumentT doc, FpdfBookmarkT? parent, int depth)
    {
        if (depth > 64) return BookmarkNode.NoChildren; // ochrona przed cyklami w uszkodzonych plikach
        var list = new List<BookmarkNode>();
        var seen = new HashSet<IntPtr>();
        var bm = fpdf_doc.FPDFBookmarkGetFirstChild(doc, parent!);
        while (!IsNull(bm) && seen.Add(bm.__Instance) && list.Count < 10_000)
        {
            var current = bm;
            string title = ReadUtf16((buf, len) => fpdf_doc.FPDFBookmarkGetTitle(current, buf, len));
            int pageIndex = -1;
            PdfPoint? target = null;
            var dest = fpdf_doc.FPDFBookmarkGetDest(doc, current);
            if (IsNull(dest))
            {
                var action = fpdf_doc.FPDFBookmarkGetAction(current);
                if (!IsNull(action) && fpdf_doc.FPDFActionGetType(action) == ActionGoTo) dest = fpdf_doc.FPDFActionGetDest(doc, action);
            }
            if (!IsNull(dest)) (pageIndex, target) = ReadDest(doc, dest!);
            int style = fpdf_doc.FPDFBookmarkGetStyle(current);
            bool open = fpdf_doc.FPDFBookmarkGetCount(current) > 0;
            var children = ReadBookmarks(doc, current, depth + 1);
            list.Add(new BookmarkNode(title, pageIndex, children, target, open, (style & 2) != 0, (style & 1) != 0));
            bm = fpdf_doc.FPDFBookmarkGetNextSibling(doc, current);
        }
        return list;
    }

    // ------------------------------------------------------------------ zapis bez zabezpieczeń

    /// <summary>FPDF_SaveAsCopy(FPDF_REMOVE_SECURITY) – wątek PDFium.</summary>
    internal static byte[] SaveWithoutSecurity(FpdfDocumentT doc)
    {
        using var output = new MemoryStream();
        Exception? writeError = null;
        PDFiumCore.Delegates.Func_int___IntPtr___IntPtr_ulong writer = (_, data, size) =>
        {
            try
            {
                if (size == 0) return 1;
                var buf = new byte[size];
                Marshal.Copy(data, buf, 0, (int)size);
                output.Write(buf, 0, buf.Length);
                return 1;
            }
            catch (Exception ex)
            {
                writeError = ex;
                return 0;
            }
        };
        using var fileWrite = new FPDF_FILEWRITE_ { Version = 1, WriteBlock = writer };
        int ok = fpdf_save.FPDF_SaveAsCopy(doc, fileWrite, SaveRemoveSecurity);
        GC.KeepAlive(writer);
        if (ok == 0 || writeError is not null || output.Length == 0)
            throw new PdfCorruptException(Text("Render.Error.Save"), writeError);
        return output.ToArray();
    }

    // ------------------------------------------------------------------ zwalnianie

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);
        _thread.Post(CloseOnThread);
    }

    ~PdfiumDocument()
    {
        // Zabezpieczenie na wypadek braku Dispose – zamknięcie i tak musi przejść przez wątek PDFium.
        if (!_disposed) _thread.Post(CloseOnThread);
    }

    private void CloseOnThread()
    {
        if (_closed) return;
        _closed = true;
        if (!IsNull(_form))
        {
            fpdf_formfill.FPDFDOC_ExitFormFillEnvironment(_form!);
            FormClosed();
            _form = null;
        }
        _formInfo?.Dispose();
        _formInfo = null;
        if (_doc is not null)
        {
            CloseDocument(_doc, ref _pin);
            _doc = null;
        }
    }
}
