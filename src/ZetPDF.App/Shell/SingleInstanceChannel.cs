using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using ZetPDF.Core.Documents;

namespace ZetPDF.App.Shell;

/// <summary>Pliki (i opcjonalnie hasło -pwd) przekazane z drugiego uruchomienia programu do działającego okna.</summary>
internal sealed record ForwardedOpen(IReadOnlyList<string> Files, string? Password);

/// <summary>
/// Jedna instancja programu: nazwany potok ograniczony do bieżącego użytkownika (PipeOptions.CurrentUserOnly) i sesji Windows.
/// Bez sieci. Pierwsza kopia tworzy potok (limit jednej instancji serwera daje rezerwację nazwy bez dodatkowego muteksu), kolejna
/// przekazuje ścieżki i się kończy. Dane z potoku są niezaufane: limit rozmiaru, czasu i liczby plików, tylko pełne ścieżki do
/// istniejących plików. Zawartości wiadomości (hasła) nie zapisujemy w dzienniku.
/// </summary>
internal sealed class SingleInstanceChannel : IDisposable
{
    public const int MaxMessageBytes = 256 * 1024;
    public const int MaxFiles = 256;
    public const int MaxPasswordLength = 1024;
    private const byte AckOk = 1, AckRejected = 0;

    private static readonly TimeSpan DefaultClientTimeout = TimeSpan.FromSeconds(5);

    private readonly NamedPipeServerStream _server;
    private readonly CancellationTokenSource _stop = new();
    private readonly TimeSpan _clientTimeout;
    private Task? _loop;

    private SingleInstanceChannel(NamedPipeServerStream server, TimeSpan clientTimeout)
    {
        _server = server;
        _clientTimeout = clientTimeout;
    }

    // ------------------------------------------------------------------ kiedy i pod jaką nazwą

    /// <summary>
    /// Tryby diagnostyczne i ZETPDF_HOME działają osobno – nie mogą połączyć się z kopią użytkownika. ZETPDF_TEST_PIPE (nazwa potoku
    /// wyłącznie testowa, inna niż kopii użytkownika) włącza kanał w izolowanym ZETPDF_HOME na potrzeby testu całego programu.
    /// </summary>
    public static bool ShouldUse(IEnumerable<string> args, string? home, string? testPipe)
    {
        if (args.Any(a => a.StartsWith("--diag-", StringComparison.OrdinalIgnoreCase))) return false;
        if (!string.IsNullOrWhiteSpace(testPipe)) return true;
        return string.IsNullOrWhiteSpace(home);
    }

    public static string PipeName(string? testPipe)
    {
        if (!string.IsNullOrWhiteSpace(testPipe)) return testPipe;
        using var identity = WindowsIdentity.GetCurrent();
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return $"ZetPDF-{identity.User!.Value}-{process.SessionId}";
    }

    // ------------------------------------------------------------------ format wiadomości

    private sealed record WireMessage(int V, string[]? Files, string? Password);

    public static byte[] Encode(ForwardedOpen message) =>
        JsonSerializer.SerializeToUtf8Bytes(new WireMessage(1, message.Files.ToArray(), message.Password));

    /// <summary>Dekoduje niezaufaną wiadomość. Null = niepoprawna. Ścieżki niespełniające wymagań są pomijane.</summary>
    public static ForwardedOpen? Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxMessageBytes) return null;
        WireMessage? wire;
        try { wire = JsonSerializer.Deserialize<WireMessage>(payload); }
        catch (JsonException) { return null; }
        if (wire is not { V: 1 }) return null;
        if (wire.Password is { Length: > MaxPasswordLength }) return null;
        var files = new List<string>();
        foreach (var file in (wire.Files ?? Array.Empty<string>()).Take(MaxFiles))
        {
            if (TryValidatePath(file, out var full)) files.Add(full);
        }
        return new ForwardedOpen(files, wire.Password);
    }

    private static bool TryValidatePath(string? path, out string full)
    {
        full = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !RecoveryStore.IsAcceptablePath(path)) return false;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        return File.Exists(full);
    }

    // ------------------------------------------------------------------ serwer (pierwsza kopia)

    /// <summary>Tworzy potok. Null = nazwa jest zajęta (działa inna kopia lub ktoś inny trzyma tę nazwę).</summary>
    public static SingleInstanceChannel? TryCreateServer(string pipeName, TimeSpan? clientTimeout = null)
    {
        try
        {
            var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            return new SingleInstanceChannel(server, clientTimeout ?? DefaultClientTimeout);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Nasłuch w tle. <paramref name="onOpen"/> jest wołane poza wątkiem UI; błędy pojedynczego klienta nie kończą nasłuchu.</summary>
    public void Start(Action<ForwardedOpen> onOpen, Action<Exception>? onError = null) =>
        _loop = Task.Run(() => ServeAsync(onOpen, onError));

    private async Task ServeAsync(Action<ForwardedOpen> onOpen, Action<Exception>? onError)
    {
        var ct = _stop.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _server.WaitForConnectionAsync(ct);
                ForwardedOpen? message = null;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(_clientTimeout);
                    var payload = await ReadMessageAsync(_server, timeout.Token);
                    message = payload is null ? null : Decode(payload);
                    _server.WriteByte(message is null ? AckRejected : AckOk);
                    await _server.FlushAsync(timeout.Token);
                }
                if (message is not null) onOpen(message);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (ObjectDisposedException) { return; }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException)
            {
                onError?.Invoke(ex); // klient zerwał połączenie lub nie zdążył w limicie czasu
            }
            try { if (_server.IsConnected) _server.Disconnect(); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { onError?.Invoke(ex); }
        }
    }

    /// <summary>Ramka: 4 bajty długości (little endian) + treść. Null, gdy długość przekracza limit lub jest niepoprawna.</summary>
    private static async Task<byte[]?> ReadMessageAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        var length = BitConverter.ToInt32(header);
        if (length <= 0 || length > MaxMessageBytes) return null;
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct);
        return payload;
    }

    // ------------------------------------------------------------------ klient (kolejna kopia)

    /// <summary>Pełne ścieżki (względne wobec folderu tej kopii) i hasło z argumentów drugiego uruchomienia.</summary>
    public static ForwardedOpen PrepareMessage(string[] args)
    {
        var (files, password) = App.ParseArgs(args);
        var full = new List<string>();
        foreach (var file in files)
        {
            try { full.Add(Path.GetFullPath(file.Trim().Trim('"'))); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { /* nieprawidłowa ścieżka */ }
        }
        return new ForwardedOpen(full, password);
    }

    /// <summary>Przekazuje wiadomość działającej kopii. False = nie przyjęto (program uruchomi się wtedy osobno, żeby pliki się otworzyły).</summary>
    public static bool TrySend(string pipeName, ForwardedOpen message, int timeoutMs = 5000)
    {
        var payload = Encode(message);
        if (payload.Length > MaxMessageBytes) return false;
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            client.Connect(timeoutMs);
            // Bez tej zgody Windows nie pozwoliłby działającej kopii wyjść na wierzch.
            if (GetNamedPipeServerProcessId(client.SafePipeHandle, out var serverPid)) AllowSetForegroundWindow(serverPid);
            client.Write(BitConverter.GetBytes(payload.Length));
            client.Write(payload);
            client.Flush();
            return client.ReadByte() == AckOk;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    public void Dispose()
    {
        _stop.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(1)); }
        catch (AggregateException) { /* nasłuch i tak się kończy */ }
        _server.Dispose();
        _stop.Dispose();
    }
}
