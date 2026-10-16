using System.Windows.Data;
using System.Windows.Input;
using ZetPDF.Core.Contracts;

namespace ZetPDF.App.Shell;

/// <summary>
/// Pośrednik: przycisk/skrót odwołuje się do id; obsługa z rejestru w chwili wykonania
/// (moduł może podpiąć ją po zbudowaniu wstążki). Brak obsługi = wyszarzone.
/// </summary>
public sealed class RegistryCommandProxy : ICommand
{
    private readonly ICommandRegistry _registry;

    public RegistryCommandProxy(ICommandRegistry registry, string id)
    {
        _registry = registry;
        CommandId = id;
    }

    public string CommandId { get; }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _registry.GetHandler(CommandId)?.CanExecute(parameter) ?? false;

    public void Execute(object? parameter) => _registry.TryExecute(CommandId, parameter);
}

/// <summary>Wiązania do tekstów zlokalizowanych (odświeżane na żywo po zmianie języka).</summary>
public static class Loc
{
    public static Binding Bind(ILocalizationService loc, string key, string? format = null)
    {
        var b = new Binding($"[{key}]") { Source = loc, Mode = BindingMode.OneWay };
        if (format is not null) b.StringFormat = format;
        return b;
    }
}
