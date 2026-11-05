using System.Diagnostics;
using System.Text;
using ZetPDF.Editing.Conversion;
using ZetPDF.TestData;

namespace ZetPDF.Editing.Tests;

/// <summary>Pomija test, gdy w systemie nie ma Microsoft Edge, podając przyczynę.</summary>
public sealed class EdgeFactAttribute : FactAttribute
{
    public EdgeFactAttribute()
    {
        if (EdgePdfPrinter.FindEdge() is null) Skip = "W systemie nie ma Microsoft Edge, więc nie da się sprawdzić prawdziwej konwersji.";
    }
}

public class WebToPdfTests
{
    private sealed class FakeRunner : IProcessRunner
    {
        public ProcessStartInfo? Started;
        public Func<ProcessStartInfo, CancellationToken, Task<int>> Behavior = (_, _) => Task.FromResult(0);
        public string? ProfileSeen;

        public Task<int> RunAsync(ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken ct, Func<bool>? isDone = null)
        {
            Started = startInfo;
            ProfileSeen = startInfo.ArgumentList.Single(a => a.StartsWith("--user-data-dir=", StringComparison.Ordinal))["--user-data-dir=".Length..];
            return Behavior(startInfo, ct);
        }

        public static string OutputOf(ProcessStartInfo s) => s.ArgumentList.Single(a => a.StartsWith("--print-to-pdf=", StringComparison.Ordinal))["--print-to-pdf=".Length..];
    }

    private static readonly WebSource Remote = new("https://example.com/", true, "example.com.pdf");

    private static EdgePdfPrinter Printer(FakeRunner runner, string temp) => new(runner, () => @"C:\Edge\msedge.exe", temp, TimeSpan.FromSeconds(5));

    [Fact]
    public async Task Arguments_are_passed_as_a_list_with_required_switches_and_the_address_after_the_terminator()
    {
        var temp = TestPaths.NewDirectory();
        var runner = new FakeRunner { Behavior = (s, _) => { File.WriteAllBytes(FakeRunner.OutputOf(s), Encoding.ASCII.GetBytes("%PDF-1.7")); return Task.FromResult(0); } };
        await Printer(runner, temp).PrintAsync(Remote, default);
        var s = runner.Started!;
        Assert.Equal(string.Empty, s.Arguments); // bez sklejania linii poleceń
        Assert.False(s.UseShellExecute);
        Assert.Equal(@"C:\Edge\msedge.exe", s.FileName);
        var args = s.ArgumentList.ToList();
        foreach (var required in new[] { "--headless=new", "--disable-gpu", "--no-first-run" }) Assert.Contains(required, args);
        Assert.Equal("--", args[^2]);
        Assert.Equal("https://example.com/", args[^1]);
    }

    [Fact]
    public async Task Temporary_profile_is_separate_and_removed_after_success_failure_and_cancel()
    {
        var temp = TestPaths.NewDirectory();
        var ok = new FakeRunner
        {
            Behavior = (s, _) =>
            {
                Assert.True(Directory.Exists(s.ArgumentList.Single(a => a.StartsWith("--user-data-dir=", StringComparison.Ordinal))["--user-data-dir=".Length..]));
                File.WriteAllBytes(FakeRunner.OutputOf(s), Encoding.ASCII.GetBytes("%PDF-1.7 x"));
                return Task.FromResult(0);
            },
        };
        var pdf = await Printer(ok, temp).PrintAsync(Remote, default);
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(pdf));
        Assert.StartsWith(temp, ok.ProfileSeen!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFileSystemEntries(temp));

        var noOutput = new FakeRunner { Behavior = (_, _) => Task.FromResult(1) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Printer(noOutput, temp).PrintAsync(Remote, default));
        Assert.Empty(Directory.GetFileSystemEntries(temp));

        using var cts = new CancellationTokenSource();
        var slow = new FakeRunner { Behavior = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return 0; } };
        var run = Printer(slow, temp).PrintAsync(Remote, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Empty(Directory.GetFileSystemEntries(temp));
    }

    [Fact]
    public async Task Output_that_is_not_a_pdf_is_rejected()
    {
        var runner = new FakeRunner { Behavior = (s, _) => { File.WriteAllText(FakeRunner.OutputOf(s), "<html>"); return Task.FromResult(0); } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Printer(runner, TestPaths.NewDirectory()).PrintAsync(Remote, default));
    }

    [Fact]
    public async Task Missing_edge_gives_clear_message()
    {
        var printer = new EdgePdfPrinter(new FakeRunner(), () => null, TestPaths.NewDirectory(), TimeSpan.FromSeconds(1));
        Assert.False(printer.IsAvailable);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => printer.PrintAsync(Remote, default));
        Assert.Contains("Edge", ex.Message);
    }

    [Fact]
    public async Task Runner_waits_for_child_processes_and_kills_them_on_timeout()
    {
        // cmd kończy się od razu, a uruchomiony przez niego ping działa dalej: tak zachowuje się starter msedge.exe
        var marker = Path.Combine(Path.GetTempPath(), "zetpdf-" + Guid.NewGuid().ToString("N") + ".txt"); // bez spacji: cmd nie cytuje ścieżki
        var child = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "/c", "start", "/b", "cmd", "/c", "ping -n 3 127.0.0.1 >nul & echo ok>" + marker }) child.ArgumentList.Add(a);
        var runner = new SystemProcessRunner();
        await runner.RunAsync(child, TimeSpan.FromSeconds(30), default);
        Assert.True(File.Exists(marker), "powrót przed końcem procesu potomnego");
        File.Delete(marker);

        var hung = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "/c", "start", "/b", "ping", "-n", "60", "127.0.0.1" }) hung.ArgumentList.Add(a);
        hung.RedirectStandardOutput = false;
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => runner.RunAsync(hung, TimeSpan.FromSeconds(2), default));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void Complete_pdf_check_needs_header_and_end_marker()
    {
        var p = Path.Combine(TestPaths.NewDirectory(), "a.pdf");
        Assert.False(EdgePdfPrinter.IsCompletePdf(p));
        File.WriteAllText(p, "%PDF-1.7 tylko poczatek pliku bez konca");
        Assert.False(EdgePdfPrinter.IsCompletePdf(p));
        File.WriteAllText(p, "%PDF-1.7 tresc pliku\n%%EOF\n");
        Assert.True(EdgePdfPrinter.IsCompletePdf(p));
    }

    [Fact]
    public async Task Real_runner_kills_process_on_timeout_and_cancel()
    {
        var sleep = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-NoProfile", "-Command", "Start-Sleep -Seconds 60" }) sleep.ArgumentList.Add(a);
        var runner = new SystemProcessRunner();
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => runner.RunAsync(sleep, TimeSpan.FromMilliseconds(500), default));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(sleep, TimeSpan.FromMinutes(1), cts.Token));
    }

    [Theory]
    [InlineData("--user-data-dir=C:\\zly", WebSourceError.LeadingDash)]
    [InlineData("-x", WebSourceError.LeadingDash)]
    [InlineData("  --headless", WebSourceError.LeadingDash)]
    [InlineData("", WebSourceError.Empty)]
    [InlineData("   ", WebSourceError.Empty)]
    [InlineData("javascript:alert(1)", WebSourceError.UnsupportedScheme)]
    [InlineData("data:text/html,<script>1</script>", WebSourceError.UnsupportedScheme)]
    [InlineData("edge://settings", WebSourceError.UnsupportedScheme)]
    [InlineData("ftp://example.com/a", WebSourceError.UnsupportedScheme)]
    [InlineData("about:blank", WebSourceError.UnsupportedScheme)]
    [InlineData(@"\\serwer\udzial\a.html", WebSourceError.NetworkPath)]
    [InlineData("//serwer/a.html", WebSourceError.NetworkPath)]
    [InlineData("file://serwer/udzial/a.html", WebSourceError.NetworkPath)]
    [InlineData(@"C:\Windows\notepad.exe", WebSourceError.NotHtml)]
    [InlineData(@"C:\nie\ma\takiego.html", WebSourceError.FileNotFound)]
    [InlineData("http://example.com/a b", WebSourceError.InvalidAddress)]
    [InlineData("https://example.com/\n--x", WebSourceError.InvalidAddress)]
    [InlineData("https://user:pass@example.com/", WebSourceError.InvalidAddress)]
    [InlineData("slowo", WebSourceError.InvalidAddress)]
    public void Bad_sources_are_rejected(string input, WebSourceError expected)
    {
        Assert.Equal(expected, WebSourceValidator.Validate(input, out var source));
        Assert.Null(source);
    }

    [Theory]
    [InlineData("https://example.com/a?b=1", "https://example.com/a?b=1", true)]
    [InlineData("HTTP://Example.com", "http://example.com/", true)]
    [InlineData("example.com/strona", "https://example.com/strona", true)]
    [InlineData("example.com:8080/a", "https://example.com:8080/a", true)]
    [InlineData("http://localhost:5000/", "http://localhost:5000/", true)]
    public void Good_addresses_are_normalized(string input, string expected, bool remote)
    {
        Assert.Equal(WebSourceError.None, WebSourceValidator.Validate(input, out var source));
        Assert.Equal(expected, source!.Argument);
        Assert.Equal(remote, source.IsRemote);
    }

    [Fact]
    public void Local_html_with_polish_path_becomes_file_uri()
    {
        var dir = Path.Combine(TestPaths.NewDirectory(), "Próbny folder");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "zażółć gęślą.html");
        File.WriteAllText(file, "<p>x</p>");
        Assert.Equal(WebSourceError.None, WebSourceValidator.Validate(file, out var source));
        Assert.False(source!.IsRemote);
        Assert.StartsWith("file:///", source.Argument);
        Assert.Equal("zażółć gęślą.pdf", source.SuggestedName);
        Assert.Equal(WebSourceError.None, WebSourceValidator.Validate(new Uri(file).AbsoluteUri, out _));
    }

    [EdgeFact]
    public async Task Local_html_with_polish_characters_becomes_pdf_through_real_edge()
    {
        var dir = Path.Combine(TestPaths.NewDirectory(), "Próbny folder");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "zażółć.html");
        await File.WriteAllTextAsync(file, "<!doctype html><html><head><meta charset=\"utf-8\"></head><body><p>Zażółć gęślą jaźń ZAŻÓŁĆ</p></body></html>", new UTF8Encoding(false));
        Assert.Equal(WebSourceError.None, WebSourceValidator.Validate(file, out var source));
        var temp = TestPaths.NewDirectory();
        var pdf = await new EdgePdfPrinter(temp).PrintAsync(source!, default);
        Assert.Contains(TestEnv.Squash("Zażółć gęślą jaźń ZAŻÓŁĆ"), TestEnv.Squash(await TestEnv.PdfiumText(pdf)));
        // Procesy Edge mogą jeszcze chwilę trzymać pliki profilu, a program ponawia usuwanie w tle wątku wywołania.
        for (int i = 0; i < 150 && Directory.GetFileSystemEntries(temp).Length > 0; i++) await Task.Delay(200);
        Assert.Empty(Directory.GetFileSystemEntries(temp));
    }
}
