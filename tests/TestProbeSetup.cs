using System.Runtime.CompilerServices;
using ZetPDF.Core.Documents;

namespace ZetPDF.Tests.Shared;

/// <summary>
/// W procesie testów nie ma ZetPDF.exe, a domyślna sonda odrzuca wszystko. Pliki testów są generowane. Sondę prawdziwą sprawdzają testy
/// uruchamiające exe (ZetPDF.App.Tests), a ten test ustawia ją jawnie.
/// </summary>
internal static class TestProbeSetup
{
    private sealed class Accept : IPdfFileProbe
    {
        public Task<PdfProbeVerdict> CheckAsync(byte[] pdf, string? password, CancellationToken ct) => Task.FromResult(PdfProbeVerdict.Safe);
    }

#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Init() => PdfFileProbes.Current = new Accept();
}

internal static class ProbeTestExtensions
{
    public static async Task<bool> IsSafeAsync(this IPdfFileProbe probe, byte[] pdf, string? password, CancellationToken ct) =>
        await probe.CheckAsync(pdf, password, ct) == PdfProbeVerdict.Safe;
}
