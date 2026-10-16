using ZetPDF.Core.Contracts;

namespace ZetPDF.Annotations;

/// <summary>ZAŚLEPKA szkieletu – do zastąpienia implementacją przez właściciela modułu.</summary>
public sealed class AnnotationService : IAnnotationService
{
    // ---- IAnnotationService
    public Task<IReadOnlyList<AnnotationInfo>> GetAnnotationsAsync(byte[] pdf, int? pageIndex = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<(byte[] Pdf, AnnotationId Id)> AddAsync(byte[] pdf, AnnotationSpec spec, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> UpdateAsync(byte[] pdf, AnnotationId id, AnnotationUpdate update, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> DeleteAsync(byte[] pdf, IReadOnlyList<AnnotationId> ids, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> DeleteAllAsync(byte[] pdf, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<(byte[] Pdf, AnnotationId Id)> AddReplyAsync(byte[] pdf, AnnotationId parent, string text, string author, CancellationToken ct = default) => throw new NotImplementedException();
    public Task ExportXfdfAsync(byte[] pdf, Stream output, string? sourceFileName, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> ImportCommentsAsync(byte[] pdf, Stream input, CancellationToken ct = default) => throw new NotImplementedException();
}
