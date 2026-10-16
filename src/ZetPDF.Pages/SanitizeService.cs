using ZetPDF.Core.Contracts;

namespace ZetPDF.Pages;

/// <summary>ZAŚLEPKA szkieletu – do zastąpienia implementacją przez właściciela modułu.</summary>
public sealed class SanitizeService : ISanitizeService
{
    // ---- ISanitizeService
    public Task<SanitizeResult> SanitizeAsync(byte[] pdf, SanitizeOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) => throw new NotImplementedException();
}
