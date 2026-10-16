using System.Windows.Input;
using Microsoft.Extensions.Logging;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Core.Commands;

/// <summary>Implementacja <see cref="ICommandRegistry"/> (singleton).</summary>
public sealed class CommandRegistry : ICommandRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ICommand> _handlers = new(StringComparer.Ordinal);
    private readonly List<CommandDescriptor> _descriptors = new(RibbonCatalog.Commands);
    private readonly Dictionary<string, CommandDescriptor> _byId = RibbonCatalog.Commands.ToDictionary(c => c.Id, StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<MenuItemDescriptor>> _menus = new(StringComparer.Ordinal);
    private readonly List<(CommandDescriptor Descriptor, string Tab, string Group)> _extras = new();
    private readonly ILogger<CommandRegistry>? _log;

    public CommandRegistry(ILogger<CommandRegistry>? log = null) => _log = log;

    public IReadOnlyList<CommandDescriptor> Descriptors
    {
        get { lock (_gate) return _descriptors.ToArray(); }
    }

    /// <summary>Dodatkowe polecenia modułów z miejscem na wstążce (dla powłoki).</summary>
    public IReadOnlyList<(CommandDescriptor Descriptor, string Tab, string Group)> Extras
    {
        get { lock (_gate) return _extras.ToArray(); }
    }

    public event EventHandler? Changed;

    public void Register(string commandId, ICommand handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            if (!_byId.ContainsKey(commandId))
                throw new ArgumentException($"Unknown command id '{commandId}'. Use RegisterExtra for commands outside the catalog.", nameof(commandId));
            if (_handlers.ContainsKey(commandId))
                throw new InvalidOperationException($"Command '{commandId}' already has a handler.");
            _handlers[commandId] = handler;
        }
        _log?.LogDebug("Registered handler for {CommandId}", commandId);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RegisterExtra(CommandDescriptor descriptor, string tabId, string groupId, ICommand handler)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            if (_byId.ContainsKey(descriptor.Id))
                throw new InvalidOperationException($"Command id '{descriptor.Id}' already exists.");
            _byId[descriptor.Id] = descriptor;
            _descriptors.Add(descriptor);
            _extras.Add((descriptor, tabId, groupId));
            _handlers[descriptor.Id] = handler;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetMenuItems(string menuCommandId, IReadOnlyList<MenuItemDescriptor> items)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(menuCommandId, out var d) || d.Kind != CommandKind.Menu)
                throw new ArgumentException($"'{menuCommandId}' is not a menu command.", nameof(menuCommandId));
            _menus[menuCommandId] = items.ToArray();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<MenuItemDescriptor> GetMenuItems(string menuCommandId)
    {
        lock (_gate) return _menus.TryGetValue(menuCommandId, out var items) ? items : Array.Empty<MenuItemDescriptor>();
    }

    public ICommand? GetHandler(string commandId)
    {
        lock (_gate) return _handlers.TryGetValue(commandId, out var h) ? h : null;
    }

    public CommandDescriptor? GetDescriptor(string commandId)
    {
        lock (_gate) return _byId.TryGetValue(commandId, out var d) ? d : null;
    }

    public bool TryExecute(string commandId, object? parameter = null)
    {
        var h = GetHandler(commandId);
        if (h is null || !h.CanExecute(parameter)) return false;
        h.Execute(parameter);
        return true;
    }

    public void InvalidateAll() => CommandManager.InvalidateRequerySuggested();
}
