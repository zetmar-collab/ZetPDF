using ZetPDF.Core.Contracts;

namespace ZetPDF.Editing;

/// <summary>ZAŚLEPKA szkieletu – do zastąpienia implementacją przez właściciela modułu.</summary>
public sealed class LinkService : ILinkService
{
    // ---- ILinkService
    public Task<IReadOnlyList<PdfLinkInfo>> GetLinksAsync(byte[] pdf, int? pageIndex = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> AddLinkAsync(byte[] pdf, int pageIndex, PdfRect rect, LinkTarget target, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> UpdateLinkAsync(byte[] pdf, int pageIndex, int linkIndex, PdfRect rect, LinkTarget target, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> DeleteLinkAsync(byte[] pdf, int pageIndex, int linkIndex, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<(byte[] Pdf, int Created)> CreateLinksFromUrlsAsync(byte[] pdf, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> RemoveAllLinksAsync(byte[] pdf, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<BookmarkNode>> GenerateBookmarksFromHeadingsAsync(byte[] pdf, HeadingDetectionOptions options, CancellationToken ct = default) => throw new NotImplementedException();
}
