using System.IO.Pipes;
using System.Text;
using ZetPDF.App.Shell;
using ZetPDF.TestData;

namespace ZetPDF.App.Tests;

/// <summary>Jedna instancja: parsowanie niezaufanych danych z potoku i przekazywanie przez prawdziwy potok (nazwy testowe, nigdy kopii użytkownika).</summary>
public class SingleInstanceTests
{
    private static string TestPipe() => "ZetPDF-test-" + Guid.NewGuid().ToString("N");

    private static string ExistingFile()
    {
        var path = TestPaths.WriteSample(SamplePdfFactory.Text(1));
        Assert.True(File.Exists(path));
        return path;
    }

    private static ForwardedOpen? Roundtrip(ForwardedOpen m) => SingleInstanceChannel.Decode(SingleInstanceChannel.Encode(m));

    [Fact]
    public void Diagnostics_and_home_override_do_not_use_the_channel()
    {
        Assert.True(SingleInstanceChannel.ShouldUse(new[] { "a.pdf" }, null, null));
        Assert.True(SingleInstanceChannel.ShouldUse(Array.Empty<string>(), "  ", null));
        Assert.False(SingleInstanceChannel.ShouldUse(new[] { "--diag-smoke", "x" }, null, null));
        Assert.False(SingleInstanceChannel.ShouldUse(new[] { "--DIAG-screenshots", "x" }, null, "pipe"));
        Assert.False(SingleInstanceChannel.ShouldUse(new[] { "a.pdf" }, @"C:\dom", null));
        Assert.True(SingleInstanceChannel.ShouldUse(new[] { "a.pdf" }, @"C:\dom", "pipe"));
    }

    [Fact]
    public void Pipe_name_is_per_user_and_session_and_test_name_is_separate()
    {
        var name = SingleInstanceChannel.PipeName(null);
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        Assert.Contains(identity.User!.Value, name);
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        Assert.EndsWith("-" + process.SessionId, name);
        Assert.Equal("abc", SingleInstanceChannel.PipeName("abc"));
    }

    [Fact]
    public void Decode_keeps_existing_files_with_polish_names_and_password()
    {
        var file = ExistingFile();
        var m = Roundtrip(new ForwardedOpen(new[] { file }, "hasło ąę"));
        Assert.NotNull(m);
        Assert.Equal(new[] { Path.GetFullPath(file) }, m!.Files);
        Assert.Equal("hasło ąę", m.Password);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(@"wzgledna\sciezka.pdf")]
    [InlineData(@"C:\nie\ma\takiego\pliku.pdf")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"\\?\C:\Windows\win.ini")]
    [InlineData(@"C:\Windows\win.ini:strumien")]
    [InlineData("C:\\zly\0znak.pdf")]
    public void Decode_drops_paths_that_are_not_existing_full_paths(string path)
    {
        var m = Roundtrip(new ForwardedOpen(new[] { path }, null));
        Assert.NotNull(m);
        Assert.Empty(m!.Files);
    }

    [Fact]
    public void Decode_normalizes_dot_dot_segments_and_rejects_directories()
    {
        var file = ExistingFile();
        var dir = Path.GetDirectoryName(file)!;
        var withDots = Path.Combine(dir, "..", Path.GetFileName(dir), Path.GetFileName(file));
        Assert.Equal(new[] { Path.GetFullPath(file) }, Roundtrip(new ForwardedOpen(new[] { withDots, dir }, null))!.Files);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nie json")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"V\":2,\"Files\":[]}")]
    [InlineData("{\"V\":1,\"Files\":\"tekst\"}")]
    [InlineData("{\"V\":1,\"Files\":[1]}")]
    public void Decode_rejects_malformed_messages(string json) => Assert.Null(SingleInstanceChannel.Decode(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void Decode_enforces_limits()
    {
        var file = ExistingFile();
        Assert.Null(SingleInstanceChannel.Decode(new byte[SingleInstanceChannel.MaxMessageBytes + 1]));
        Assert.Null(Roundtrip(new ForwardedOpen(new[] { file }, new string('x', SingleInstanceChannel.MaxPasswordLength + 1))));
        var many = Roundtrip(new ForwardedOpen(Enumerable.Repeat(file, SingleInstanceChannel.MaxFiles + 10).ToArray(), null));
        Assert.Equal(SingleInstanceChannel.MaxFiles, many!.Files.Count);
    }

    [Fact]
    public void Prepare_makes_relative_paths_absolute_and_separates_password()
    {
        var m = SingleInstanceChannel.PrepareMessage(new[] { "a b.pdf", "-pwdtajne", "\"C:\\x\\c.pdf\"" });
        Assert.Equal(new[] { Path.GetFullPath("a b.pdf"), @"C:\x\c.pdf" }, m.Files);
        Assert.Equal("tajne", m.Password);
    }

    private static async Task<ForwardedOpen> Receive(TaskCompletionSource<ForwardedOpen> tcs) =>
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));

    [Fact]
    public async Task Client_passes_files_and_password_to_server()
    {
        var pipe = TestPipe();
        var file = ExistingFile();
        var received = new TaskCompletionSource<ForwardedOpen>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = SingleInstanceChannel.TryCreateServer(pipe);
        Assert.NotNull(server);
        server!.Start(received.SetResult);

        Assert.True(await Task.Run(() => SingleInstanceChannel.TrySend(pipe, new ForwardedOpen(new[] { file }, "h"))));

        var m = await Receive(received);
        Assert.Equal(new[] { Path.GetFullPath(file) }, m.Files);
        Assert.Equal("h", m.Password);
    }

    [Fact]
    public async Task Launch_without_files_only_signals_the_window()
    {
        var pipe = TestPipe();
        var received = new TaskCompletionSource<ForwardedOpen>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = SingleInstanceChannel.TryCreateServer(pipe)!;
        server.Start(received.SetResult);
        Assert.True(await Task.Run(() => SingleInstanceChannel.TrySend(pipe, new ForwardedOpen(Array.Empty<string>(), null))));
        Assert.Empty((await Receive(received)).Files);
    }

    [Fact]
    public void Second_server_with_the_same_name_is_refused_and_name_is_free_after_dispose()
    {
        var pipe = TestPipe();
        var first = SingleInstanceChannel.TryCreateServer(pipe);
        Assert.NotNull(first);
        Assert.Null(SingleInstanceChannel.TryCreateServer(pipe));
        first!.Dispose();
        using var again = SingleInstanceChannel.TryCreateServer(pipe);
        Assert.NotNull(again);
    }

    [Fact]
    public void Client_without_server_reports_failure() =>
        Assert.False(SingleInstanceChannel.TrySend(TestPipe(), new ForwardedOpen(Array.Empty<string>(), null), timeoutMs: 100));

    private static async Task SendRaw(string pipe, byte[] bytes)
    {
        using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        await client.WriteAsync(bytes);
        await client.FlushAsync();
        // Czekamy na odpowiedź albo zerwanie: serwer sam decyduje, co zrobić z wiadomością.
        var ack = new byte[1];
        try { _ = await client.ReadAsync(ack).AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (IOException) { }
    }

    [Fact]
    public async Task Server_survives_hostile_clients_and_still_accepts_valid_message()
    {
        var pipe = TestPipe();
        var file = ExistingFile();
        var received = new TaskCompletionSource<ForwardedOpen>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = SingleInstanceChannel.TryCreateServer(pipe, TimeSpan.FromMilliseconds(500))!;
        server.Start(received.SetResult);

        await SendRaw(pipe, BitConverter.GetBytes(int.MaxValue));                                   // długość ponad limit
        await SendRaw(pipe, BitConverter.GetBytes(-1));                                             // długość ujemna
        await SendRaw(pipe, BitConverter.GetBytes(3).Concat(new byte[] { 1, 2, 3 }).ToArray());     // śmieci
        await SendRaw(pipe, BitConverter.GetBytes(100));                                            // obiecuje dane i milczy (limit czasu)
        Assert.False(received.Task.IsCompleted);

        Assert.True(await Task.Run(() => SingleInstanceChannel.TrySend(pipe, new ForwardedOpen(new[] { file }, null), 10000)));
        Assert.Equal(new[] { Path.GetFullPath(file) }, (await Receive(received)).Files);
    }

    [Fact]
    public void Client_sends_nothing_when_message_is_over_limit()
    {
        var huge = Enumerable.Repeat(new string('a', 30000), 20).ToArray();
        Assert.False(SingleInstanceChannel.TrySend(TestPipe(), new ForwardedOpen(huge, null), timeoutMs: 100));
    }
}
