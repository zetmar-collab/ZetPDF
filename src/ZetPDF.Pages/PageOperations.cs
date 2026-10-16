using ZetPDF.Core.Contracts;

namespace ZetPDF.Pages;

/// <summary>ZAŚLEPKA szkieletu – do zastąpienia implementacją przez właściciela modułu.</summary>
public sealed class PageOperations : IPageOperations
{
    // ---- IPageOperations
    public Task<byte[]> InsertFromFileAsync(byte[] pdf, string sourcePath, string? sourcePassword, IReadOnlyList<int>? sourcePages, int insertAt, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> InsertFromBytesAsync(byte[] pdf, byte[] sourcePdf, IReadOnlyList<int>? sourcePages, int insertAt, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> InsertBlankAsync(byte[] pdf, int insertAt, int count, PdfSize size, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> DeleteAsync(byte[] pdf, IReadOnlyList<int> pages, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> ExtractAsync(byte[] pdf, IReadOnlyList<int> pages, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<string>> ExtractToFilesAsync(byte[] pdf, IReadOnlyList<int> pages, string outputDirectory, string fileNamePattern, string baseName, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> ReplaceAsync(byte[] pdf, IReadOnlyList<int> targetPages, string sourcePath, string? sourcePassword, IReadOnlyList<int> sourcePages, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> DuplicateAsync(byte[] pdf, IReadOnlyList<int> pages, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> MoveAsync(byte[] pdf, IReadOnlyList<int> pages, int targetIndex, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> SwapAsync(byte[] pdf, int pageA, int pageB, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> ReverseAsync(byte[] pdf, IReadOnlyList<int>? pages, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> RotateAsync(byte[] pdf, IReadOnlyList<int> pages, int degrees, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> CropAsync(byte[] pdf, IReadOnlyList<int> pages, CropOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> ResizeAsync(byte[] pdf, IReadOnlyList<int> pages, ResizeOptions options, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> InterleaveAsync(byte[] pdf, string secondPath, string? secondPassword, bool reverseSecond, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> FlattenAsync(byte[] pdf, FlattenOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> SetPageLabelsAsync(byte[] pdf, IReadOnlyList<PageLabelRange> ranges, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<PageLabelRange>> GetPageLabelsAsync(byte[] pdf, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<string>> SplitAsync(byte[] pdf, SplitOptions options, string outputDirectory, string baseName, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> MergeAsync(IReadOnlyList<MergeSource> sources, MergeOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> CreateBlankDocumentAsync(PdfSize size, int count = 1, CancellationToken ct = default) => throw new NotImplementedException();
}
