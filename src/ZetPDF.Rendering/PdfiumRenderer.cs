using ZetPDF.Core.Contracts;

namespace ZetPDF.Rendering;

/// <summary>ZAŚLEPKA szkieletu – do zastąpienia implementacją przez właściciela modułu.</summary>
public sealed class PdfiumRenderer : IPdfRenderer
{
    // ---- IPdfRenderer
    public Task<IRenderDocument> OpenAsync(byte[] pdf, string? password = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<PdfProbeResult> ProbeAsync(byte[] pdf, string? password = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> RemoveSecurityAsync(byte[] pdf, string password, CancellationToken ct = default) => throw new NotImplementedException();
}
