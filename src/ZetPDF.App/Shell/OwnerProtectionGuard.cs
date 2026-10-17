using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;

namespace ZetPDF.App.Shell;

/// <summary>Wybór użytkownika przed zapisem pliku z samym hasłem uprawnień (DECYZJE D-23).</summary>
public enum OwnerProtectionChoice
{
    Cancel,
    /// <summary>Podaj hasło właściciela – ochrona zostaje taka jak w pliku.</summary>
    EnterOwnerPassword,
    /// <summary>Ustaw nowe hasło uprawnień (te same uprawnienia).</summary>
    NewPermissionsPassword,
    /// <summary>Zapisz bez ochrony.</summary>
    NoProtection,
}

/// <summary>Okna potrzebne strażnikowi (implementuje <see cref="Services.DialogService"/>; testy – atrapa).</summary>
public interface IOwnerProtectionPrompts
{
    OwnerProtectionChoice AskOwnerProtectionChoice(string documentName, bool canEnterOwnerPassword);

    /// <summary>Hasło właściciela pliku źródłowego (null = Anuluj).</summary>
    string? AskOwnerPassword(string documentName, bool isRetry);

    /// <summary>Nowe hasło uprawnień wpisane dwa razy (okno pilnuje zgodności i niepustości; null = Anuluj).</summary>
    string? AskNewPermissionsPassword(string documentName);
}

/// <summary>
/// DECYZJE D-23: plik z samym hasłem uprawnień (puste hasło otwarcia) otwarty bez hasła właściciela nie jest po cichu
/// zapisywany z nowym losowym hasłem właściciela. Przed pierwszym zapisem użytkownik wybiera: podaje hasło właściciela
/// (sprawdzane na pliku źródłowym), ustawia nowe hasło uprawnień albo zapisuje bez ochrony. Wybór obowiązuje dla
/// sesji do zmiany jej ustawień szyfrowania (<see cref="DocumentManager.SetSaveEncryptionOverride"/>).
/// </summary>
public sealed class OwnerProtectionGuard
{
    private readonly IDocumentManager _documents;
    private readonly IOwnerProtectionPrompts _prompts;

    public OwnerProtectionGuard(IDocumentManager documents, IOwnerProtectionPrompts prompts)
    {
        _documents = documents;
        _prompts = prompts;
    }

    /// <summary>Zwraca false, gdy użytkownik anulował zapis.</summary>
    public async Task<bool> EnsureAsync(IPdfDocumentSession session)
    {
        if (_documents is not DocumentManager dm || !dm.RequiresOwnerPasswordDecision(session)) return true;
        var generated = session.Current.Encryption!;
        bool sourceAvailable = session.FilePath is { } p && File.Exists(p);
        while (true)
        {
            switch (_prompts.AskOwnerProtectionChoice(session.DisplayName, sourceAvailable))
            {
                case OwnerProtectionChoice.NoProtection:
                    dm.SetSaveEncryptionOverride(session, null);
                    return true;

                case OwnerProtectionChoice.EnterOwnerPassword when sourceAvailable:
                    var source = await File.ReadAllBytesAsync(session.FilePath!);
                    bool retry = false;
                    while (_prompts.AskOwnerPassword(session.DisplayName, retry) is { } owner)
                    {
                        if (owner.Length > 0 && await Task.Run(() => PdfSecurityHelper.IsOwnerPassword(source, owner)))
                        {
                            dm.SetSaveEncryptionOverride(session, new PdfEncryptionSettings(null, owner, generated.Method, generated.Permissions));
                            return true;
                        }
                        retry = true;
                    }
                    break; // Anuluj w oknie hasła → z powrotem do wyboru

                case OwnerProtectionChoice.NewPermissionsPassword:
                    if (_prompts.AskNewPermissionsPassword(session.DisplayName) is { Length: > 0 } fresh)
                    {
                        // AES-256: hasła w UTF-8 (polskie znaki); AES-128 dopuszcza tylko Latin-1.
                        dm.SetSaveEncryptionOverride(session, new PdfEncryptionSettings(null, fresh, PdfEncryptionMethod.Aes256, generated.Permissions));
                        return true;
                    }
                    break;

                default:
                    return false;
            }
        }
    }
}
