using ZetPDF.Core.Contracts;

namespace ZetPDF.Ocr;

/// <summary>ZAŚLEPKA szkieletu – do zastąpienia implementacją przez właściciela modułu.</summary>
public sealed class TesseractOcrService : IOcrService
{
    // ---- IOcrService
    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<OcrResult> RecognizeAsync(byte[] pdf, OcrOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> ApplyTextLayerAsync(byte[] pdf, OcrResult result, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> OcrDocumentAsync(byte[] pdf, OcrOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<OcrWord>> RecognizeBitmapAsync(RenderedBitmap bitmap, string languages, CancellationToken ct = default) => throw new NotImplementedException();
}
