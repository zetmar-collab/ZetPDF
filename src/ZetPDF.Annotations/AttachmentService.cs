using ZetPDF.Core.Contracts;

namespace ZetPDF.Annotations;

/// <summary>ZAŚLEPKA szkieletu – do zastąpienia implementacją przez właściciela modułu.</summary>
public sealed class AttachmentService : IAttachmentService
{
    // ---- IAttachmentService
    public Task<IReadOnlyList<AttachmentInfo>> GetAttachmentsAsync(byte[] pdf, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> ExtractAsync(byte[] pdf, string name, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> AddAsync(byte[] pdf, string filePath, string description, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> RemoveAsync(byte[] pdf, string name, CancellationToken ct = default) => throw new NotImplementedException();
}
