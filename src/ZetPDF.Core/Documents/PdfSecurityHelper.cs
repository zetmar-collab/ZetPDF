using System.Security.Cryptography;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Localization;

namespace ZetPDF.Core.Documents;

/// <summary>
/// Zabezpieczenia PDF na PDFsharp 6.2.4 – implementacja referencyjna/zapasowa (DocumentManager używa jej,
/// gdy ISecurityService nie jest jeszcze zaimplementowany; security-signatures może z niej korzystać).
/// Zweryfikowane (scratch/LibCheck): SetEncryptionToV5 zapisuje AES-256 (V=5, R=6, AESV3) otwierany przez PDFium
/// z hasłem użytkownika i właściciela; PDFsharp w trybie Modify wymaga hasła WŁAŚCICIELA; po otwarciu
/// zaszyfrowanego pliku i zapisie PDFsharp NIE zachowuje szyfrowania – trzeba je ustawić ponownie.
/// </summary>
public static class PdfSecurityHelper
{
    /// <summary>Czy plik ma słownik /Encrypt (szybkie sprawdzenie bez hasła).</summary>
    public static bool IsEncrypted(byte[] pdf)
    {
        PdfSharpEdit.EnsureInitialized();
        try
        {
            using var doc = PdfReader.Open(new MemoryStream(pdf, false), PdfDocumentOpenMode.Import);
            return doc.SecurityHandler.Elements.ContainsKey("/Filter");
        }
        catch (PdfReaderException)
        {
            return true; // wymaga hasła
        }
    }

    /// <summary>
    /// Analiza zabezpieczeń. password = hasło, którym otwarto (null = bez hasła).
    /// Wyjątek <see cref="PdfInvalidPasswordException"/>, gdy hasło nie pasuje.
    /// </summary>
    public static PdfSecurityInfo Analyze(byte[] original, string? password)
    {
        PdfSharpEdit.EnsureInitialized();
        PdfDocument doc;
        try
        {
            doc = password is null
                ? PdfReader.Open(new MemoryStream(original, false), PdfDocumentOpenMode.Import)
                : PdfReader.Open(new MemoryStream(original, false), password, PdfDocumentOpenMode.Import);
        }
        catch (PdfReaderException ex)
        {
            throw new PdfInvalidPasswordException(ex.Message);
        }

        using (doc)
        {
            var handler = doc.SecurityHandler;
            bool encrypted = handler.Elements.ContainsKey("/Filter");
            if (!encrypted) return PdfSecurityInfo.None;

            int v = handler.Elements.GetInteger("/V");
            int r = handler.Elements.GetInteger("/R");
            int p = handler.Elements.GetInteger("/P");
            int length = handler.Elements.GetInteger("/Length");
            var method = DetectMethod(handler, v, r, length);
            bool owner = IsOwnerPassword(original, password);
            return new PdfSecurityInfo(true, method, r, FromPValue(p), owner, password);
        }
    }

    /// <summary>Czy hasło jest hasłem właściciela (PDFsharp otwiera w trybie Modify tylko z nim).</summary>
    public static bool IsOwnerPassword(byte[] original, string? password)
    {
        try
        {
            using var d = PdfReader.Open(new MemoryStream(original, false), password ?? string.Empty, PdfDocumentOpenMode.Modify);
            return true;
        }
        catch (PdfReaderException)
        {
            return false;
        }
    }

    /// <summary>Czy hasło użytkownika jest puste (plik otwiera się bez hasła).</summary>
    public static bool HasEmptyUserPassword(byte[] original)
    {
        try
        {
            using var d = PdfReader.Open(new MemoryStream(original, false), PdfDocumentOpenMode.Import);
            return true;
        }
        catch (PdfReaderException)
        {
            return false;
        }
    }

    /// <summary>Szyfruje bajty (wejście bez szyfrowania).</summary>
    public static byte[] Encrypt(byte[] plain, PdfEncryptionSettings settings)
    {
        return PdfSharpEdit.Modify(plain, doc =>
        {
            var h = doc.SecurityHandler;
            switch (settings.Method)
            {
                case PdfEncryptionMethod.Aes256: h.SetEncryptionToV5(); break;
                case PdfEncryptionMethod.Aes128: h.SetEncryptionToV4UsingAES(); break;
                case PdfEncryptionMethod.Rc4_128: h.SetEncryptionToV2With128Bits(); break;
                case PdfEncryptionMethod.Rc4_40: h.SetEncryptionToV1(); break;
                default: throw new NotSupportedException($"Encryption method {settings.Method} is not supported.").WithMessageKey("Core.Error.EncryptionMethod", settings.Method);
            }
            var s = doc.SecuritySettings;
            s.UserPassword = settings.UserPassword ?? string.Empty;
            s.OwnerPassword = settings.OwnerPassword;
            var perm = settings.Permissions;
            s.PermitPrint = perm.HasFlag(DocumentPermissions.Print);
            s.PermitFullQualityPrint = perm.HasFlag(DocumentPermissions.PrintHighQuality);
            s.PermitModifyDocument = perm.HasFlag(DocumentPermissions.ModifyContent);
            s.PermitExtractContent = perm.HasFlag(DocumentPermissions.CopyContent);
            s.PermitAnnotations = perm.HasFlag(DocumentPermissions.Annotate);
            s.PermitFormsFill = perm.HasFlag(DocumentPermissions.FillForms);
            s.PermitAssembleDocument = perm.HasFlag(DocumentPermissions.AssembleDocument);
        });
    }

    /// <summary>Odszyfrowanie hasłem właściciela (zapasowo; główna ścieżka: IPdfRenderer.RemoveSecurityAsync – działa też z hasłem użytkownika).</summary>
    public static byte[] RemoveSecurity(byte[] original, string? ownerPassword)
    {
        PdfSharpEdit.EnsureInitialized();
        try
        {
            using var doc = PdfReader.Open(new MemoryStream(original, false), ownerPassword ?? string.Empty, PdfDocumentOpenMode.Modify);
            doc.SecurityHandler.SetEncryptionToNoneAndResetPasswords();
            return PdfSharpEdit.Save(doc);
        }
        catch (PdfReaderException ex)
        {
            throw new PdfInvalidPasswordException(ex.Message);
        }
    }

    /// <summary>
    /// Ustawienia ponownego szyfrowania przy zapisie:
    /// – hasło właściciela + puste hasło użytkownika → (puste, właściciel, uprawnienia pliku);
    /// – hasło właściciela, niepuste nieznane hasło użytkownika → użytkownik = hasło właściciela (komunikat w UI);
    /// – tylko hasło użytkownika → nowe losowe hasło właściciela, uprawnienia z pliku.
    /// </summary>
    public static PdfEncryptionSettings? CreateResaveSettings(PdfSecurityInfo info, bool userPasswordEmpty)
    {
        if (!info.IsEncrypted) return null;
        var method = info.Method is PdfEncryptionMethod.Aes128 or PdfEncryptionMethod.Aes256 ? info.Method : PdfEncryptionMethod.Aes256;
        if (info.OpenedWithOwnerPassword)
            return new PdfEncryptionSettings(userPasswordEmpty ? null : info.Password, info.Password ?? string.Empty, method, info.FilePermissions);
        return new PdfEncryptionSettings(info.Password, GenerateRandomPassword(), method, info.FilePermissions) { GeneratedOwnerPassword = true };
    }

    public static string GenerateRandomPassword() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));

    /// <summary>Wartość /P → flagi.</summary>
    public static DocumentPermissions FromPValue(int p)
    {
        var r = DocumentPermissions.None;
        if ((p & 4) != 0) r |= DocumentPermissions.Print;
        if ((p & 8) != 0) r |= DocumentPermissions.ModifyContent;
        if ((p & 16) != 0) r |= DocumentPermissions.CopyContent;
        if ((p & 32) != 0) r |= DocumentPermissions.Annotate;
        if ((p & 256) != 0) r |= DocumentPermissions.FillForms;
        if ((p & 512) != 0) r |= DocumentPermissions.ExtractForAccessibility;
        if ((p & 1024) != 0) r |= DocumentPermissions.AssembleDocument;
        if ((p & 2048) != 0) r |= DocumentPermissions.PrintHighQuality;
        return r;
    }

    private static PdfEncryptionMethod DetectMethod(PdfSharp.Pdf.Security.PdfStandardSecurityHandler handler, int v, int r, int length)
    {
        if (v == 5 || r >= 5) return PdfEncryptionMethod.Aes256;
        if (v == 4)
        {
            var cf = handler.Elements.GetDictionary("/CF");
            var std = cf?.Elements.GetDictionary("/StdCF");
            var cfm = std?.Elements.GetName("/CFM");
            return cfm == "/AESV2" ? PdfEncryptionMethod.Aes128 : PdfEncryptionMethod.Rc4_128;
        }
        if (v == 2) return length > 40 ? PdfEncryptionMethod.Rc4_128 : PdfEncryptionMethod.Rc4_40;
        if (v == 1) return PdfEncryptionMethod.Rc4_40;
        return PdfEncryptionMethod.Unknown;
    }
}
