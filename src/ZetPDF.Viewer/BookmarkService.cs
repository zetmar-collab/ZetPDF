using ZetPDF.Core.Contracts;

namespace ZetPDF.Viewer;

/// <summary>ZAŚLEPKA szkieletu – do zastąpienia implementacją przez właściciela modułu.</summary>
public sealed class BookmarkService : IBookmarkService
{
    // ---- IBookmarkService
    public Task<IReadOnlyList<BookmarkNode>> GetBookmarksAsync(byte[] pdf, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> SetBookmarksAsync(byte[] pdf, IReadOnlyList<BookmarkNode> bookmarks, CancellationToken ct = default) => throw new NotImplementedException();
}
