using ZetPDF.Core.Contracts;

namespace ZetPDF.Pages;

/// <summary>ZAŚLEPKA szkieletu – do zastąpienia implementacją przez właściciela modułu.</summary>
public sealed class StampingService : IStampingService
{
    // ---- IStampingService
    public Task<byte[]> AddWatermarkAsync(byte[] pdf, WatermarkOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> RemoveWatermarksAsync(byte[] pdf, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> AddBackgroundAsync(byte[] pdf, BackgroundOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> RemoveBackgroundsAsync(byte[] pdf, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> AddHeaderFooterAsync(byte[] pdf, HeaderFooterOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> RemoveHeaderFooterAsync(byte[] pdf, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> AddPageNumbersAsync(byte[] pdf, PageNumberOptions options, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<BatesResult> AddBatesAsync(byte[] pdf, BatesOptions options, long startNumber, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> RemoveBatesAsync(byte[] pdf, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<PageMarkKind> DetectMarksAsync(byte[] pdf, CancellationToken ct = default) => throw new NotImplementedException();
}
