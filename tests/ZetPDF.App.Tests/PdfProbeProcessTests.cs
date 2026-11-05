using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Xunit.Abstractions;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.TestData;

namespace ZetPDF.App.Tests;

/// <summary>Prawdziwa sonda: ZetPDF.exe --probe-pdf jako proces potomny. Awaria sondy nie może dotknąć tego procesu.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProbeSwapCollection
{
    public const string Name = "ProbeSwap";
}

[Collection(ProbeSwapCollection.Name)]
public class PdfProbeProcessTests
{
    private readonly ITestOutputHelper _output;

    public PdfProbeProcessTests(ITestOutputHelper output) => _output = output;

    private static string Exe()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "ZetPDF.exe");
        Assert.True(File.Exists(local), "brak " + local);
        return local;
    }

    private static byte[] Latin(string s) => Encoding.Latin1.GetBytes(s);
    private static string Arrays(int d) => new string('[', d) + "1" + new string(']', d);

    /// <summary>Plik AES-256 ze strumieniami obiektów, zapisany przez PyMuPDF (hasło użytkownika „u”, właściciela „o”).</summary>
    private static byte[] EncryptedWithObjectStreams() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestFiles", "enc_objstm.pdf"));

    private static byte[] InObjectStream(string nested, string filter = "/Filter/FlateDecode", Func<byte[], byte[]>? encode = null)
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Latin(s));
        var off = new long[7];
        W("%PDF-1.5\n");
        off[1] = ms.Position; W("1 0 obj\n<</Type/Catalog/Pages 2 0 R>>\nendobj\n");
        off[2] = ms.Position; W("2 0 obj\n<</Type/Pages/Kids[3 0 R]/Count 1>>\nendobj\n");
        off[3] = ms.Position; W("3 0 obj\n<</Type/Page/Parent 2 0 R/MediaBox[0 0 100 100]/X 4 0 R>>\nendobj\n");
        var z = new MemoryStream();
        using (var zl = new ZLibStream(z, CompressionLevel.Optimal, true)) zl.Write(Latin("4 0 " + nested + "\n"));
        var data = encode is null ? z.ToArray() : encode(z.ToArray());
        off[5] = ms.Position;
        W("5 0 obj\n<</Type/ObjStm/N 1/First 4" + filter + "/Length " + data.Length + ">>\nstream\n");
        ms.Write(data);
        W("\nendstream\nendobj\n");
        long x = ms.Position;
        var xr = new MemoryStream();
        void E(int t, long a, int b)
        {
            xr.WriteByte((byte)t);
            xr.WriteByte((byte)(a >> 16));
            xr.WriteByte((byte)(a >> 8));
            xr.WriteByte((byte)a);
            xr.WriteByte((byte)b);
        }
        E(0, 0, 0); E(1, off[1], 0); E(1, off[2], 0); E(1, off[3], 0); E(2, 5, 0); E(1, off[5], 0); E(1, x, 0);
        W("6 0 obj\n<</Type/XRef/Size 7/W[1 3 1]/Root 1 0 R/Length " + xr.Length + ">>\nstream\n");
        ms.Write(xr.ToArray());
        W("\nendstream\nendobj\nstartxref\n" + x + "\n%%EOF\n");
        return ms.ToArray();
    }

    [Theory]
    [InlineData("zwykły")]
    [InlineData("formularz")]
    [InlineData("adnotacje")]
    [InlineData("uszkodzony")]
    [InlineData("nie-pdf")]
    public async Task Normal_and_merely_damaged_files_pass(string kind)
    {
        var pdf = kind switch
        {
            "zwykły" => SamplePdfFactory.Text(3),
            "formularz" => SamplePdfFactory.WithForm(),
            "adnotacje" => SamplePdfFactory.WithAnnotations(),
            "uszkodzony" => SamplePdfFactory.Corrupted(),
            _ => SamplePdfFactory.NotAPdf(),
        };
        Assert.True(await new ProcessPdfProbe(Exe()).IsSafeAsync(pdf, null, default));
    }

    [Theory]
    [InlineData(10_000)]
    [InlineData(1_000_000)]
    public async Task Deep_nesting_in_object_stream_crashes_only_the_probe(int depth)
    {
        var sw = Stopwatch.StartNew();
        Assert.False(await new ProcessPdfProbe(Exe()).IsSafeAsync(InObjectStream(Arrays(depth)), null, default));
        _output.WriteLine($"{depth}: {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Deep_nesting_behind_escaped_filter_and_chain_is_found_because_the_real_parser_runs()
    {
        var probe = new ProcessPdfProbe(Exe());
        Assert.False(await probe.IsSafeAsync(InObjectStream(Arrays(100_000), "/Filter/Fl#61teDecode"), null, default));
        Assert.False(await probe.IsSafeAsync(InObjectStream(Arrays(100_000), "/Filter[/ASCIIHexDecode/FlateDecode]", raw => Encoding.ASCII.GetBytes(Convert.ToHexString(raw) + ">")), null, default));
    }

    [Fact]
    public async Task Encrypted_file_with_object_streams_passes_with_and_without_password()
    {
        var probe = new ProcessPdfProbe(Exe());
        var pdf = EncryptedWithObjectStreams();
        Assert.True(await probe.IsSafeAsync(pdf, "u", default));
        Assert.True(await probe.IsSafeAsync(pdf, null, default));
        Assert.True(await probe.IsSafeAsync(pdf, "złe hasło", default));
        Assert.True(await probe.IsSafeAsync(SamplePdfFactory.EncryptedAes256(), SamplePdfFactory.UserPassword, default));
    }

    [Fact]
    public async Task Process_timeout_is_incomplete_not_ok_and_cancel_is_cancel()
    {
        var pdf = SamplePdfFactory.Text(1);
        Assert.Equal(PdfProbeVerdict.Incomplete, await new ProcessPdfProbe(Exe(), TimeSpan.FromMilliseconds(1)).CheckAsync(pdf, null, default));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProcessPdfProbe(Exe()).IsSafeAsync(pdf, null, cts.Token));
    }

    [Fact]
    public async Task Wrong_exit_for_garbage_protocol()
    {
        // uruchomienie bez danych na wejściu kończy się kodem odrzucenia
        var start = new ProcessStartInfo(Exe(), PdfProbeProtocol.Argument) { RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(start)!;
        p.StandardInput.Close();
        await p.WaitForExitAsync();
        Assert.NotEqual(PdfProbeProtocol.ExitOk, p.ExitCode);
        Assert.NotEqual(PdfProbeProtocol.ExitManagedError, p.ExitCode);
    }

    [Fact]
    public async Task Five_hundred_pages_pass_within_the_budget()
    {
        var sw = Stopwatch.StartNew();
        Assert.Equal(PdfProbeVerdict.Safe, await new ProcessPdfProbe(Exe()).CheckAsync(SamplePdfFactory.Large(500), null, default));
        _output.WriteLine("500 stron: " + sw.ElapsedMilliseconds + " ms");
    }

    private sealed class IncompleteProbe : IPdfFileProbe
    {
        public int Calls { get; private set; }

        public Task<PdfProbeVerdict> CheckAsync(byte[] pdf, string? password, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(PdfProbeVerdict.Incomplete);
        }
    }

    public class Answer : System.Reflection.DispatchProxy
    {
        public bool Reply { get; set; }
        public List<string> Asked { get; } = new();

        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IDialogService.ConfirmDanger))
            {
                Asked.Add((string)args![0]!);
                return Reply;
            }
            return method.ReturnType.IsValueType && method.ReturnType != typeof(void) ? Activator.CreateInstance(method.ReturnType) : null;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Incomplete_check_asks_with_default_no_in_interactive_mode_and_skips_in_batch(bool accept)
    {
        var fake = new IncompleteProbe();
        var previous = PdfFileProbes.Current;
        PdfFileProbes.Current = fake;
        try
        {
            var pdf = SamplePdfFactory.Text(1);
            await Assert.ThrowsAsync<PdfProbeIncompleteException>(() => PdfFileProbes.EnsureSafeAsync(pdf));

            var dialogs = System.Reflection.DispatchProxy.Create<IDialogService, Answer>();
            var answer = (Answer)(object)dialogs;
            answer.Reply = accept;
            var loc = new ZetPDF.Core.Localization.LocalizationService(initial: new System.Globalization.CultureInfo("pl"));
            Assert.Equal(accept, await PdfFileProbes.EnsureSafeInteractiveAsync(pdf, null, dialogs, loc));
            Assert.Single(answer.Asked);
            if (accept)
            {
                // zgoda dotyczy tych bajtów: kolejne wywołania wsadowe już nie pytają
                int calls = fake.Calls;
                await PdfFileProbes.EnsureSafeAsync(pdf);
                Assert.Equal(calls, fake.Calls);
            }
            // bez okna dialogowego nie ma zgody
            await Assert.ThrowsAsync<PdfProbeIncompleteException>(() => PdfFileProbes.EnsureSafeInteractiveAsync(SamplePdfFactory.Text(2), null, null, null));
        }
        finally
        {
            PdfFileProbes.Current = previous;
        }
    }

    [Fact]
    public void Budget_scales_with_size_and_pages_and_has_an_upper_limit()
    {
        var small = ZetPDF.App.PdfProbeRunnerAccess.Budget(1024, 1);
        var large = ZetPDF.App.PdfProbeRunnerAccess.Budget(50L * 1024 * 1024, 500);
        var huge = ZetPDF.App.PdfProbeRunnerAccess.Budget(long.MaxValue / 2, 100_000);
        Assert.True(small < large);
        Assert.Equal(TimeSpan.FromSeconds(240), huge);
    }

    [Fact]
    public async Task Startup_time_is_reported()
    {
        var probe = new ProcessPdfProbe(Exe());
        var pdf = SamplePdfFactory.Text(1);
        await probe.IsSafeAsync(pdf, null, default);
        var times = new List<long>();
        for (int i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            Assert.True(await probe.IsSafeAsync(pdf, null, default));
            times.Add(sw.ElapsedMilliseconds);
        }
        _output.WriteLine("sonda, plik 1 strona, ms: " + string.Join(", ", times));
    }

    [Fact]
    public async Task Opening_through_document_manager_shows_corrupt_message_and_process_lives()
    {
        var path = Path.Combine(TestPaths.NewDirectory(), "głęboki plik.pdf");
        await File.WriteAllBytesAsync(path, InObjectStream(Arrays(100_000)));
        var okPath = Path.Combine(TestPaths.NewDirectory(), "zwykły plik.pdf");
        await File.WriteAllBytesAsync(okPath, SamplePdfFactory.Text(2));
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        ZetPDF.Core.Services.CoreServiceCollectionExtensions.AddZetPdfCore(services, new ZetPDF.Core.Services.AppPaths(TestPaths.NewDirectory()));
        new ZetPDF.Rendering.RenderingModule().ConfigureServices(services);
        using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        var documents = (IDocumentManager)provider.GetService(typeof(IDocumentManager))!;
        var previous = PdfFileProbes.Current;
        PdfFileProbes.Current = new ProcessPdfProbe(Exe());
        try
        {
            var ex = await Assert.ThrowsAsync<PdfCorruptException>(() => documents.OpenAsync(path));
            Assert.Contains("complex", ex.Message);
            Assert.NotNull(await documents.OpenAsync(okPath));
        }
        finally
        {
            PdfFileProbes.Current = previous;
        }
    }

    [Fact]
    public async Task Unconfigured_probe_refuses_files_from_disk()
    {
        var path = Path.Combine(TestPaths.NewDirectory(), "plik.pdf");
        await File.WriteAllBytesAsync(path, SamplePdfFactory.Text(1));
        var previous = PdfFileProbes.Current;
        PdfFileProbes.Current = new UnconfiguredPdfProbe();
        try
        {
            await Assert.ThrowsAsync<PdfCorruptException>(() => PdfFileProbes.ReadVerifiedAsync(path));
        }
        finally
        {
            PdfFileProbes.Current = previous;
        }
    }
}
