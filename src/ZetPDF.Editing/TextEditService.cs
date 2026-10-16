using ZetPDF.Core.Contracts;

namespace ZetPDF.Editing;

/// <summary>ZAŚLEPKA szkieletu – do zastąpienia implementacją przez właściciela modułu.</summary>
public sealed class TextEditService : ITextEditService
{
    // ---- ITextEditService
    public Task<IReadOnlyList<PdfTextBlock>> GetTextBlocksAsync(byte[] pdf, int pageIndex, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> ReplaceTextBlockAsync(byte[] pdf, int pageIndex, string blockId, string newText, TextStyle style, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> AddTextAsync(byte[] pdf, int pageIndex, PdfPoint position, string text, TextStyle style, double? maxWidth = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<FindReplaceResult> FindReplaceAsync(byte[] pdf, string find, string replace, TextSearchOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) => throw new NotImplementedException();
}
