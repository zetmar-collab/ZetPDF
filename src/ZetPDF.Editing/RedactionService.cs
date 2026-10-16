using ZetPDF.Core.Contracts;

namespace ZetPDF.Editing;

/// <summary>ZAŚLEPKA szkieletu – do zastąpienia implementacją przez właściciela modułu.</summary>
public sealed class RedactionService : IRedactionService
{
    // ---- IRedactionService
    public Task<IReadOnlyList<RedactionCandidate>> FindAsync(byte[] pdf, RedactionSearch search, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> MarkAsync(byte[] pdf, IReadOnlyList<RedactionMark> marks, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> ApplyAsync(byte[] pdf, RedactionApplyOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) => throw new NotImplementedException();
    public bool Validate(SensitiveDataPattern pattern, string value) => throw new NotImplementedException();
}
