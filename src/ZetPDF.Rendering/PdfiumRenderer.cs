using PDFiumCore;
using ZetPDF.Core.Contracts;
using static ZetPDF.Rendering.PdfiumNative;

namespace ZetPDF.Rendering;

/// <summary>
/// Implementacja <see cref="IPdfRenderer"/> na PDFium (PDFiumCore 157, Apache-2.0; PDFium BSD-3).
/// Wszystkie wywołania natywne idą przez jeden dedykowany wątek (<see cref="PdfiumThread"/>).
/// Błędy: brak hasła → <see cref="PdfPasswordRequiredException"/>, złe hasło → <see cref="PdfInvalidPasswordException"/>
/// (PDFium zwraca w obu przypadkach FPDF_ERR_PASSWORD – rozróżniamy po tym, czy podano hasło),
/// uszkodzony/nie-PDF → <see cref="PdfCorruptException"/> z przetłumaczonym opisem.
/// </summary>
public sealed class PdfiumRenderer : IPdfRenderer
{
    private readonly PdfiumThread _thread = PdfiumThread.Shared;

    public Task<IRenderDocument> OpenAsync(byte[] pdf, string? password = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        return _thread.InvokeAsync<IRenderDocument>(() => PdfiumDocument.OpenOnThread(_thread, pdf, password, ct), ct);
    }

    public Task<PdfProbeResult> ProbeAsync(byte[] pdf, string? password = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        return _thread.InvokeAsync(() => ProbeOnThread(pdf, password), ct);
    }

    private static PdfProbeResult ProbeOnThread(byte[] pdf, string? password)
    {
        FpdfDocumentT doc;
        System.Runtime.InteropServices.GCHandle pin;
        try
        {
            doc = LoadDocument(pdf, password, out pin);
        }
        catch (PdfInvalidPasswordException ex)
        {
            return new PdfProbeResult(PdfProbeStatus.InvalidPassword, 0, true, -1, DocumentPermissions.None, ex.Message);
        }
        catch (PdfPasswordRequiredException ex)
        {
            return new PdfProbeResult(PdfProbeStatus.PasswordRequired, 0, true, -1, DocumentPermissions.None, ex.Message);
        }
        catch (PdfCorruptException ex)
        {
            return new PdfProbeResult(PdfProbeStatus.Corrupt, 0, false, -1, DocumentPermissions.None, ex.Message);
        }
        try
        {
            int pages = fpdfview.FPDF_GetPageCount(doc);
            if (pages <= 0)
                return new PdfProbeResult(PdfProbeStatus.Corrupt, 0, false, -1, DocumentPermissions.None, Text("Render.Error.NoPages"));
            int revision = fpdfview.FPDF_GetSecurityHandlerRevision(doc);
            bool encrypted = revision != -1;
            var perms = encrypted ? MapPermissions((uint)fpdfview.FPDF_GetDocPermissions(doc), revision) : DocumentPermissions.All;
            return new PdfProbeResult(PdfProbeStatus.Ok, pages, encrypted, revision, perms);
        }
        finally { CloseDocument(doc, ref pin); }
    }

    public Task<byte[]> RemoveSecurityAsync(byte[] pdf, string password, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        return _thread.InvokeAsync(() =>
        {
            var doc = LoadDocument(pdf, password, out var pin);
            try { return PdfiumDocument.SaveWithoutSecurity(doc); }
            finally { CloseDocument(doc, ref pin); }
        }, ct);
    }

    /// <summary>Diagnostyka (testy): liczba otwartych uchwytów natywnych.</summary>
    internal static (int Documents, int Pages, int TextPages, int Bitmaps, int Forms) OpenHandleCounts =>
        (OpenDocuments, OpenPages, OpenTextPages, OpenBitmaps, OpenForms);

    /// <summary>Czeka, aż wątek PDFium wykona wszystko, co zlecono przed tym wywołaniem (testy zwalniania).</summary>
    internal static Task FlushAsync() => PdfiumThread.Shared.InvokeAsync(() => { });
}
