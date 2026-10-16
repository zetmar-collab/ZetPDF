using ZetPDF.Core.Contracts;

namespace ZetPDF.Viewer;

/// <summary>ZAŚLEPKA szkieletu – do zastąpienia implementacją przez właściciela modułu.</summary>
public sealed class PrintService : IPrintService
{
    // ---- IPrintService
    public Task ShowPrintDialogAsync(IPdfDocumentSession session, int currentPageIndex) => throw new NotImplementedException();
    public Task PrintAsync(IPdfDocumentSession session, PrintOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) => throw new NotImplementedException();
}
