using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;

namespace ZetPDF.Core.Commands;

/// <summary>
/// Polecenie dokumentu z funkcji "bajty → bajty" (wzorzec dla wszystkich modułów).
/// Przykład: new DelegateDocumentCommand("Pages.Undo.Rotate", (pdf, p, ct) => pages.RotateAsync(pdf, idx, 90, ct), DocumentChangeKind.PageContent, DocumentPermissions.AssembleDocument).
/// Wynik identyczny bajtowo z wejściem = brak zmiany (nie trafia do historii).
/// </summary>
public sealed class DelegateDocumentCommand : IDocumentCommand
{
    private readonly Func<byte[], IProgress<OperationProgress>?, CancellationToken, Task<byte[]>> _transform;

    public DelegateDocumentCommand(
        string descriptionKey,
        Func<byte[], IProgress<OperationProgress>?, CancellationToken, Task<byte[]>> transform,
        DocumentChangeKind kind = DocumentChangeKind.All,
        DocumentPermissions requiredPermission = DocumentPermissions.ModifyContent,
        IReadOnlyList<int>? affectedPages = null)
    {
        DescriptionKey = descriptionKey;
        _transform = transform;
        Kind = kind;
        RequiredPermission = requiredPermission;
        AffectedPages = affectedPages;
    }

    public string DescriptionKey { get; }
    public DocumentPermissions RequiredPermission { get; }
    public DocumentChangeKind Kind { get; }
    public IReadOnlyList<int>? AffectedPages { get; }

    public async Task<DocumentEditResult?> ExecuteAsync(DocumentEditContext context, CancellationToken ct)
    {
        var input = context.Input.GetSharedBytes();
        var output = await _transform(input, context.Progress, ct).ConfigureAwait(false);
        if (output is null || output.AsSpan().SequenceEqual(input)) return null;
        return new DocumentEditResult(context.Input.WithData(output), Kind, AffectedPages);
    }
}

/// <summary>Polecenie zmieniające tylko ustawienia szyfrowania zapisu (Ochrona hasłem / Usuń zabezpieczenia).</summary>
public sealed class SetEncryptionCommand : IDocumentCommand
{
    private readonly PdfEncryptionSettings? _settings;

    public SetEncryptionCommand(string descriptionKey, PdfEncryptionSettings? settings)
    {
        DescriptionKey = descriptionKey;
        _settings = settings;
    }

    public string DescriptionKey { get; }

    /// <summary>Zmiana zabezpieczeń wymaga pełnych uprawnień (hasło właściciela).</summary>
    public DocumentPermissions RequiredPermission => DocumentPermissions.All;

    public Task<DocumentEditResult?> ExecuteAsync(DocumentEditContext context, CancellationToken ct) =>
        Task.FromResult<DocumentEditResult?>(Equals(context.Input.Encryption, _settings)
            ? null
            : new DocumentEditResult(context.Input.WithEncryption(_settings), DocumentChangeKind.Security));
}

/// <summary>
/// Polecenie wstążki działające na aktywnym dokumencie (pomocnik dla modułów).
/// CanExecute: jest aktywny dokument i ma wymagane uprawnienie (oraz opcjonalny dodatkowy warunek).
/// Execute: wywołuje akcję asynchronicznie; wyjątki trafiają do IDialogService.ShowError.
/// </summary>
public sealed class ActiveDocumentCommand : ICommand
{
    private readonly IDocumentManager _documents;
    private readonly IDialogService? _dialogs;
    private readonly Func<IPdfDocumentSession, Task> _execute;
    private readonly Func<IPdfDocumentSession, bool>? _canExecute;
    private readonly DocumentPermissions _permission;
    private bool _running;

    public ActiveDocumentCommand(IDocumentManager documents, IDialogService? dialogs, Func<IPdfDocumentSession, Task> execute,
        DocumentPermissions permission = DocumentPermissions.None, Func<IPdfDocumentSession, bool>? canExecute = null)
    {
        _documents = documents;
        _dialogs = dialogs;
        _execute = execute;
        _permission = permission;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter)
    {
        var doc = _documents.ActiveDocument;
        return !_running && doc is not null && doc.Can(_permission) && (_canExecute?.Invoke(doc) ?? true);
    }

    public async void Execute(object? parameter)
    {
        var doc = _documents.ActiveDocument;
        if (doc is null || _running) return;
        _running = true;
        CommandManager.InvalidateRequerySuggested();
        try { await _execute(doc); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _dialogs?.ShowError(ex); }
        finally
        {
            _running = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}

/// <summary>Prosta implementacja przełącznika (np. narzędzie: IsChecked = IToolService.IsActive(id)).</summary>
public sealed class ToggleCommand : IToggleCommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private readonly Func<bool> _isChecked;

    public ToggleCommand(Action<object?> execute, Func<bool> isChecked, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _isChecked = isChecked;
        _canExecute = canExecute;
    }

    public bool IsChecked => _isChecked();

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter)
    {
        _execute(parameter);
        RaiseIsCheckedChanged();
    }

    /// <summary>Wołaj, gdy stan zaznaczenia mógł się zmienić z zewnątrz (np. ActiveToolChanged).</summary>
    public void RaiseIsCheckedChanged([CallerMemberName] string? _ = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
}
