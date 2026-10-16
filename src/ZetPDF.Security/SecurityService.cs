using ZetPDF.Core.Contracts;

namespace ZetPDF.Security;

/// <summary>ZAŚLEPKA szkieletu – do zastąpienia implementacją przez właściciela modułu.</summary>
public sealed class SecurityService : ISecurityService
{
    // ---- ISecurityService
    public Task<PdfSecurityInfo> AnalyzeAsync(byte[] originalPdf, string? password, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> EncryptAsync(byte[] pdf, PdfEncryptionSettings settings, CancellationToken ct = default) => throw new NotImplementedException();
    public PdfEncryptionSettings? CreateResaveSettings(PdfSecurityInfo info) => throw new NotImplementedException();
}
