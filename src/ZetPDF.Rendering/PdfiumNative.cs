using System.Globalization;
using System.Runtime.InteropServices;
using PDFiumCore;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Rendering;

/// <summary>
/// Stałe i pomocniki PDFium (wołać WYŁĄCZNIE na wątku <see cref="PdfiumThread"/>).
/// Liczniki uchwytów służą testom deterministycznego zwalniania (wiele cykli otwórz/zamknij).
/// </summary>
internal static class PdfiumNative
{
    // FPDF_GetLastError
    public const int ErrSuccess = 0;
    public const int ErrUnknown = 1;
    public const int ErrFile = 2;
    public const int ErrFormat = 3;
    public const int ErrPassword = 4;
    public const int ErrSecurity = 5;
    public const int ErrPage = 6;

    // flagi renderu (fpdfview.h)
    public const int FlagAnnot = 0x01;
    public const int FlagGrayscale = 0x08;
    public const int FlagPrinting = 0x800;
    public const int FlagNoSmoothText = 0x1000;

    // FPDFBitmap_BGRA
    public const int BitmapBgra = 4;

    // FPDFText_FindStart
    public const ulong MatchCase = 0x1;
    public const ulong MatchWholeWord = 0x2;

    // FPDF_SaveAsCopy
    public const ulong SaveRemoveSecurity = 3;

    // FPDFAnnot_GetSubtype
    public const int AnnotLink = 2;

    // FPDFAction_GetType
    public const ulong ActionGoTo = 1;
    public const ulong ActionUri = 3;

    private static int _documents, _pages, _textPages, _bitmaps, _forms;

    /// <summary>Otwarte uchwyty dokumentów (FPDF_LoadMemDocument bez FPDF_CloseDocument).</summary>
    public static int OpenDocuments => Volatile.Read(ref _documents);
    public static int OpenPages => Volatile.Read(ref _pages);
    public static int OpenTextPages => Volatile.Read(ref _textPages);
    public static int OpenBitmaps => Volatile.Read(ref _bitmaps);
    public static int OpenForms => Volatile.Read(ref _forms);

    public static bool IsNull(FpdfDocumentT? h) => h is null || h.__Instance == IntPtr.Zero;
    public static bool IsNull(FpdfPageT? h) => h is null || h.__Instance == IntPtr.Zero;
    public static bool IsNull(FpdfTextpageT? h) => h is null || h.__Instance == IntPtr.Zero;
    public static bool IsNull(FpdfBitmapT? h) => h is null || h.__Instance == IntPtr.Zero;
    public static bool IsNull(FpdfFormHandleT? h) => h is null || h.__Instance == IntPtr.Zero;
    public static bool IsNull(FpdfSchhandleT? h) => h is null || h.__Instance == IntPtr.Zero;
    public static bool IsNull(FpdfBookmarkT? h) => h is null || h.__Instance == IntPtr.Zero;
    public static bool IsNull(FpdfDestT? h) => h is null || h.__Instance == IntPtr.Zero;
    public static bool IsNull(FpdfActionT? h) => h is null || h.__Instance == IntPtr.Zero;
    public static bool IsNull(FpdfLinkT? h) => h is null || h.__Instance == IntPtr.Zero;
    public static bool IsNull(FpdfAnnotationT? h) => h is null || h.__Instance == IntPtr.Zero;

    /// <summary>
    /// Otwiera dokument z przypiętej tablicy. Przy błędzie zwalnia przypięcie i rzuca wyjątek domenowy.
    /// </summary>
    public static FpdfDocumentT LoadDocument(byte[] pdf, string? password, out GCHandle pin)
    {
        if (pdf is null || pdf.Length == 0) throw new PdfCorruptException(Text("Render.Error.Empty"));
        pin = GCHandle.Alloc(pdf, GCHandleType.Pinned);
        FpdfDocumentT? doc;
        try
        {
            doc = fpdfview.FPDF_LoadMemDocument(pin.AddrOfPinnedObject(), pdf.Length, string.IsNullOrEmpty(password) ? null : password);
        }
        catch
        {
            pin.Free();
            throw;
        }
        if (IsNull(doc))
        {
            int err = (int)fpdfview.FPDF_GetLastError();
            pin.Free();
            throw ErrorToException(err, password);
        }
        Interlocked.Increment(ref _documents);
        return doc!;
    }

    public static void CloseDocument(FpdfDocumentT doc, ref GCHandle pin)
    {
        fpdfview.FPDF_CloseDocument(doc);
        Interlocked.Decrement(ref _documents);
        if (pin.IsAllocated) pin.Free();
    }

    public static Exception ErrorToException(int err, string? password) => err switch
    {
        ErrPassword when string.IsNullOrEmpty(password) => new PdfPasswordRequiredException(Text("Render.Error.PasswordRequired")),
        ErrPassword => new PdfInvalidPasswordException(Text("Render.Error.InvalidPassword")),
        ErrFile => new PdfCorruptException(Text("Render.Error.File")),
        ErrFormat => new PdfCorruptException(Text("Render.Error.Format")),
        ErrSecurity => new PdfCorruptException(Text("Render.Error.Security")),
        ErrPage => new PdfCorruptException(Text("Render.Error.Page")),
        _ => new PdfCorruptException(Text("Render.Error.Unknown")),
    };

    public static FpdfPageT LoadPage(FpdfDocumentT doc, int index)
    {
        var page = fpdfview.FPDF_LoadPage(doc, index);
        if (IsNull(page)) throw new PdfCorruptException(string.Format(CultureInfo.CurrentCulture, Text("Render.Error.PageLoad"), index + 1));
        Interlocked.Increment(ref _pages);
        return page;
    }

    public static void ClosePage(FpdfPageT page)
    {
        fpdfview.FPDF_ClosePage(page);
        Interlocked.Decrement(ref _pages);
    }

    public static FpdfTextpageT LoadTextPage(FpdfPageT page)
    {
        var tp = fpdf_text.FPDFTextLoadPage(page);
        if (IsNull(tp)) throw new PdfCorruptException(Text("Render.Error.Text"));
        Interlocked.Increment(ref _textPages);
        return tp;
    }

    public static void CloseTextPage(FpdfTextpageT tp)
    {
        fpdf_text.FPDFTextClosePage(tp);
        Interlocked.Decrement(ref _textPages);
    }

    public static FpdfBitmapT CreateBitmap(int width, int height)
    {
        var bmp = fpdfview.FPDFBitmapCreateEx(width, height, BitmapBgra, IntPtr.Zero, 0);
        if (IsNull(bmp)) throw new OutOfMemoryException(Text("Render.Error.Bitmap"));
        Interlocked.Increment(ref _bitmaps);
        return bmp;
    }

    public static void DestroyBitmap(FpdfBitmapT bmp)
    {
        fpdfview.FPDFBitmapDestroy(bmp);
        Interlocked.Decrement(ref _bitmaps);
    }

    public static void FormCreated() => Interlocked.Increment(ref _forms);
    public static void FormClosed() => Interlocked.Decrement(ref _forms);

    /// <summary>Mapuje bity /P (FPDF_GetDocPermissions) na uprawnienia ZetPDF.</summary>
    public static DocumentPermissions MapPermissions(uint p, int revision)
    {
        if (p == 0xFFFFFFFF) return DocumentPermissions.All;
        var r = DocumentPermissions.None;
        bool print = (p & (1u << 2)) != 0, modify = (p & (1u << 3)) != 0, copy = (p & (1u << 4)) != 0, annot = (p & (1u << 5)) != 0;
        if (print) r |= DocumentPermissions.Print;
        if (modify) r |= DocumentPermissions.ModifyContent;
        if (copy) r |= DocumentPermissions.CopyContent;
        if (annot) r |= DocumentPermissions.Annotate;
        if (revision >= 3)
        {
            if ((p & (1u << 8)) != 0) r |= DocumentPermissions.FillForms;
            if ((p & (1u << 9)) != 0) r |= DocumentPermissions.ExtractForAccessibility;
            if ((p & (1u << 10)) != 0) r |= DocumentPermissions.AssembleDocument;
            if ((p & (1u << 11)) != 0) r |= DocumentPermissions.PrintHighQuality;
        }
        else
        {
            if (annot) r |= DocumentPermissions.FillForms;
            if (copy) r |= DocumentPermissions.ExtractForAccessibility;
            if (modify) r |= DocumentPermissions.AssembleDocument;
            if (print) r |= DocumentPermissions.PrintHighQuality;
        }
        return r;
    }

    /// <summary>Tekst z zasobów modułu w bieżącej kulturze interfejsu.</summary>
    public static string Text(string key) =>
        RenderingModule.Strings.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    /// <summary>Odczyt łańcucha UTF-16LE zwracanego przez funkcje typu (buffer, buflen) → wymagana długość w bajtach.</summary>
    public static string ReadUtf16(Func<IntPtr, ulong, ulong> call)
    {
        ulong len = call(IntPtr.Zero, 0);
        if (len <= 2) return string.Empty;
        var buf = Marshal.AllocHGlobal((int)len);
        try
        {
            call(buf, len);
            return Marshal.PtrToStringUni(buf, (int)(len / 2) - 1) ?? string.Empty;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>Odczyt łańcucha 7-bit ASCII (np. FPDFAction_GetURIPath).</summary>
    public static string ReadAscii(Func<IntPtr, ulong, ulong> call)
    {
        ulong len = call(IntPtr.Zero, 0);
        if (len <= 1) return string.Empty;
        var buf = Marshal.AllocHGlobal((int)len);
        try
        {
            call(buf, len);
            return Marshal.PtrToStringAnsi(buf, (int)len - 1) ?? string.Empty;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}
