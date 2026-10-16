using ZetPDF.Core.Contracts;

namespace ZetPDF.Editing;

/// <summary>ZAŚLEPKA szkieletu – do zastąpienia implementacją przez właściciela modułu.</summary>
public sealed class ObjectEditService : IObjectEditService
{
    // ---- IObjectEditService
    public Task<IReadOnlyList<PageObjectInfo>> GetObjectsAsync(byte[] pdf, int pageIndex, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> TransformObjectAsync(byte[] pdf, int pageIndex, string objectId, PdfMatrix transform, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> DeleteObjectAsync(byte[] pdf, int pageIndex, string objectId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> ReplaceImageAsync(byte[] pdf, int pageIndex, string objectId, byte[] imageData, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> AddImageAsync(byte[] pdf, int pageIndex, byte[] imageData, PdfRect rect, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> AddShapeAsync(byte[] pdf, int pageIndex, ShapeSpec shape, CancellationToken ct = default) => throw new NotImplementedException();
}
