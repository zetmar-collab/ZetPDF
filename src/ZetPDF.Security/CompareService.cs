using ZetPDF.Core.Contracts;

namespace ZetPDF.Security;

/// <summary>ZAŚLEPKA szkieletu – do zastąpienia implementacją przez właściciela modułu.</summary>
public sealed class CompareService : ICompareService
{
    // ---- ICompareService
    public Task<CompareResult> CompareAsync(byte[] pdfA, string nameA, byte[] pdfB, string nameB, CompareOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task ExportReportAsync(CompareResult result, string path, CompareReportFormat format, CancellationToken ct = default) => throw new NotImplementedException();
}
