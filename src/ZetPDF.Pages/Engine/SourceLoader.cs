using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;

namespace ZetPDF.Pages.Engine;

/// <summary>
/// Wczytuje plik źródłowy (Wstaw z pliku, Zastąp, Przeplatanie, Scal) i zwraca bajty BEZ szyfrowania.
/// Plik zaszyfrowany: PDFium (IPdfRenderer.RemoveSecurityAsync) – działa z hasłem użytkownika lub właściciela;
/// bez renderera rezerwa PDFsharp (tylko hasło właściciela).
/// </summary>
internal sealed class SourceLoader
{
    private readonly IPdfRenderer? _renderer;

    public SourceLoader(IPdfRenderer? renderer) => _renderer = renderer;

    public async Task<byte[]> LoadFileAsync(string path, string? password, CancellationToken ct)
    {
        var bytes = await PdfFileProbes.ReadVerifiedAsync(path, password, ct).ConfigureAwait(false);
        return await DecryptAsync(bytes, password, ct).ConfigureAwait(false);
    }

    public async Task<byte[]> DecryptAsync(byte[] bytes, string? password, CancellationToken ct)
    {
        bool encrypted;
        try { encrypted = PdfSecurityHelper.IsEncrypted(bytes); }
        catch (Exception ex) when (ex is not OperationCanceledException) { encrypted = false; }
        if (!encrypted) return bytes;

        if (_renderer is not null)
        {
            var probe = await _renderer.ProbeAsync(bytes, password, ct).ConfigureAwait(false);
            switch (probe.Status)
            {
                case PdfProbeStatus.PasswordRequired: throw new PdfPasswordRequiredException();
                case PdfProbeStatus.InvalidPassword: throw new PdfInvalidPasswordException();
                case PdfProbeStatus.Corrupt: throw new PdfCorruptException(probe.ErrorMessage ?? "Corrupt PDF.");
            }
            if (!probe.IsEncrypted) return bytes;
            return await _renderer.RemoveSecurityAsync(bytes, password ?? string.Empty, ct).ConfigureAwait(false);
        }

        if (string.IsNullOrEmpty(password)) throw new PdfPasswordRequiredException();
        try { return PdfSecurityHelper.RemoveSecurity(bytes, password); }
        catch (Exception ex) when (ex is not OperationCanceledException and not PdfPasswordRequiredException)
        {
            throw new PdfInvalidPasswordException(ex.Message);
        }
    }
}
