using System.Runtime.CompilerServices;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Security.Pdf;

namespace ZetPDF.Security;

/// <summary>
/// Analiza i szyfrowanie. Analiza: własny handler Standard (dokładne rozróżnienie hasła właściciela i użytkownika,
/// odzyskanie hasła użytkownika z hasła właściciela dla R2–R4, wykrycie pustego hasła użytkownika).
/// Szyfrowanie: PDFsharp 6.2.4 (AES-256 = V5/R6, AES-128 = V4 AESV2), z pełnym mapowaniem uprawnień.
/// Samo stosowanie szyfrowania odbywa się przy zapisie (DocumentManager → EncryptAsync), zmiany ustawień –
/// przez SetEncryptionCommand (Cofnij/Ponów).
/// </summary>
public sealed class SecurityService : ISecurityService
{
    private sealed record Extra(bool UserPasswordEmpty, string? RecoveredUserPassword);

    private readonly ConditionalWeakTable<PdfSecurityInfo, Extra> _extra = new();

    public Task<PdfSecurityInfo> AnalyzeAsync(byte[] originalPdf, string? password, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(originalPdf);
        return Task.Run(() => Analyze(originalPdf, password), ct);
    }

    internal PdfSecurityInfo Analyze(byte[] original, string? password)
    {
        CosDocument doc;
        try
        {
            doc = CosDocument.Open(original, password);
        }
        catch (Exception ex) when (ex is PdfCorruptException or NotSupportedException or FormatException)
        {
            return PdfSecurityHelper.Analyze(original, password); // zapasowo PDFsharp
        }
        if (!doc.IsEncrypted || doc.Security is null) return PdfSecurityInfo.None;
        var h = doc.Security;
        var info = new PdfSecurityInfo(true, h.Method, h.R, h.Permissions, h.IsOwner, password);
        _extra.AddOrUpdate(info, new Extra(h.UserPasswordEmpty, h.RecoveredUserPassword));
        return info;
    }

    /// <summary>Czy plik ma puste hasło użytkownika (znane po <see cref="AnalyzeAsync"/>).</summary>
    public bool? IsUserPasswordEmpty(PdfSecurityInfo info) => _extra.TryGetValue(info, out var e) ? e.UserPasswordEmpty : null;

    /// <summary>
    /// Czy przy zapisie hasło właściciela zastąpi nieznane hasło otwierania (DECYZJE D-08: AES-256 nie pozwala
    /// odzyskać hasła użytkownika z hasła właściciela). Dla R2–R4 hasło użytkownika jest odzyskiwane.
    /// </summary>
    public bool IsResaveOwnerOnly(PdfSecurityInfo info) =>
        info.IsEncrypted && info.OpenedWithOwnerPassword && _extra.TryGetValue(info, out var e) && !e.UserPasswordEmpty && e.RecoveredUserPassword is null;

    public Task<byte[]> EncryptAsync(byte[] pdf, PdfEncryptionSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentNullException.ThrowIfNull(settings);
        return Task.Run(() => Encrypt(pdf, settings), ct);
    }

    internal static byte[] Encrypt(byte[] plain, PdfEncryptionSettings settings)
    {
        if (string.IsNullOrEmpty(settings.OwnerPassword) && string.IsNullOrEmpty(settings.UserPassword))
            throw new ArgumentException("An owner or user password is required.", nameof(settings));
        return PdfSharpEdit.Modify(plain, doc =>
        {
            var h = doc.SecurityHandler;
            switch (settings.Method)
            {
                case PdfEncryptionMethod.Aes256: h.SetEncryptionToV5(); break;
                case PdfEncryptionMethod.Aes128: h.SetEncryptionToV4UsingAES(); break;
                case PdfEncryptionMethod.Rc4_128: h.SetEncryptionToV2With128Bits(); break;
                case PdfEncryptionMethod.Rc4_40: h.SetEncryptionToV1(); break;
                default: throw new NotSupportedException($"Encryption method {settings.Method} is not supported.");
            }
            var s = doc.SecuritySettings;
            s.UserPassword = settings.UserPassword ?? string.Empty;
            s.OwnerPassword = string.IsNullOrEmpty(settings.OwnerPassword) ? settings.UserPassword! : settings.OwnerPassword;
            var p = settings.Permissions;
            s.PermitPrint = p.HasFlag(DocumentPermissions.Print);
            s.PermitFullQualityPrint = p.HasFlag(DocumentPermissions.PrintHighQuality);
            s.PermitModifyDocument = p.HasFlag(DocumentPermissions.ModifyContent);
            s.PermitExtractContent = p.HasFlag(DocumentPermissions.CopyContent);
            s.PermitAnnotations = p.HasFlag(DocumentPermissions.Annotate);
            s.PermitFormsFill = p.HasFlag(DocumentPermissions.FillForms);
            s.PermitAssembleDocument = p.HasFlag(DocumentPermissions.AssembleDocument);
            // PDFsharp 6.2.4 nie ma osobnej właściwości dla bitu 10 (dostępność) – w PDF 2.0 jest on i tak traktowany jako ustawiony.
        });
    }

    public PdfEncryptionSettings? CreateResaveSettings(PdfSecurityInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (!info.IsEncrypted) return null;
        var method = info.Method is PdfEncryptionMethod.Aes128 or PdfEncryptionMethod.Aes256 ? info.Method : PdfEncryptionMethod.Aes256;
        if (!_extra.TryGetValue(info, out var extra))
            return PdfSecurityHelper.CreateResaveSettings(info, userPasswordEmpty: info.Password is null);
        if (info.OpenedWithOwnerPassword)
        {
            string? user = extra.UserPasswordEmpty ? null : extra.RecoveredUserPassword ?? info.Password;
            if (string.IsNullOrEmpty(user)) user = null;
            return new PdfEncryptionSettings(user, info.Password ?? string.Empty, method, info.FilePermissions);
        }
        return new PdfEncryptionSettings(info.Password, PdfSecurityHelper.GenerateRandomPassword(), method, info.FilePermissions) { GeneratedOwnerPassword = true };
    }
}
