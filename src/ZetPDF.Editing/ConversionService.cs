using ZetPDF.Core.Contracts;

namespace ZetPDF.Editing;

/// <summary>ZAŚLEPKA szkieletu – do zastąpienia implementacją przez właściciela modułu.</summary>
public sealed class ConversionService : IConversionService
{
    // ---- IConversionService
    public Task<byte[]> ImagesToPdfAsync(IReadOnlyList<string> imagePaths, ImageToPdfOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<string>> PdfToImagesAsync(byte[] pdf, ImageExportOptions options, string outputDirectory, string baseName, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<string> PdfToTextAsync(byte[] pdf, TextExportOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task PdfToDocxAsync(byte[] pdf, string outputPath, DocxExportOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> CombineFilesAsync(IReadOnlyList<string> paths, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> TextToPdfAsync(string text, TextStyle? style = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> ImageDataToPdfAsync(byte[] imageData, ImageToPdfOptions options, CancellationToken ct = default) => throw new NotImplementedException();
}
