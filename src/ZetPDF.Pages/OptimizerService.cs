using ZetPDF.Core.Contracts;

namespace ZetPDF.Pages;

/// <summary>ZAŚLEPKA szkieletu – do zastąpienia implementacją przez właściciela modułu.</summary>
public sealed class OptimizerService : IOptimizerService
{
    // ---- IOptimizerService
    public Task<OptimizeResult> OptimizeAsync(byte[] pdf, OptimizeOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> ConvertToGrayscaleAsync(byte[] pdf, bool rasterize, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) => throw new NotImplementedException();
}
