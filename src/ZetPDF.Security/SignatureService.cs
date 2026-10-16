using ZetPDF.Core.Contracts;
using System.Security.Cryptography.X509Certificates;

namespace ZetPDF.Security;

/// <summary>ZAŚLEPKA szkieletu – do zastąpienia implementacją przez właściciela modułu.</summary>
public sealed class SignatureService : ISignatureService
{
    // ---- ISignatureService
    public Task<byte[]> SignAsync(byte[] finalPdf, SignRequest request, string? password, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<SignatureValidationResult>> ValidateAsync(byte[] pdf, string? password, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> TimestampDocumentAsync(byte[] finalPdf, string tsaUrl, string? password, CancellationToken ct = default) => throw new NotImplementedException();
    public IReadOnlyList<X509Certificate2> GetStoreCertificates() => throw new NotImplementedException();
    public Task<string> CreateSelfSignedIdAsync(DigitalIdRequest request, CancellationToken ct = default) => throw new NotImplementedException();
}
