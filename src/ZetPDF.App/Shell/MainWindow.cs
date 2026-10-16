using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Fluent;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.App.Services;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Localization;
using ZetPDF.Core.Theming;
using Button = Fluent.Button;
using ToggleButton = Fluent.ToggleButton;

namespace ZetPDF.App.Shell;

/// <summary>
/// Okno główne – SZKIELET. Wstążka budowana deklaratywnie z RibbonCatalog (+ polecenia dodatkowe modułów);
/// polecenia bez obsługi są wyszarzone. Karty dokumentów z widokami z IDocumentViewFactory.
/// ui-shell rozbudowuje: backstage z ostatnimi plikami, pasek stanu, panele, QAT, Alt+Q, motyw okna itd.
/// </summary>
public sealed class MainWindow : RibbonWindow
{
    private readonly CommandRegistry _registry;
    private readonly ILocalizationService _loc;
    private readonly IDocumentManager _documents;
    private readonly IDocumentViewFactory _views;
    private readonly System.Windows.Controls.TabControl _tabs = new();
    private readonly TextBlock _status = new() { Margin = new Thickness(8, 2, 8, 2) };
    private readonly Dictionary<IPdfDocumentSession, TabItem> _tabByDoc = new();

    public MainWindow(CommandRegistry registry, ILocalizationService loc, IDocumentManager documents, IDocumentViewFactory views, ShellService shell)
    {
        _registry = registry;
        _loc = loc;
        _documents = documents;
        _views = views;
        Width = 1280;
        Height = 800;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetBinding(TitleProperty, LocBinding("Shell.AppTitle"));
        SetResourceReference(BackgroundProperty, ThemeKeys.WindowBackground);

        var ribbon = BuildRibbon();
        var empty = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 16 };
        empty.SetBinding(TextBlock.TextProperty, LocBinding("Shell.NoDocument"));
        empty.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.TextSecondary);

        var contentHost = new Grid();
        contentHost.SetResourceReference(Panel.BackgroundProperty, ThemeKeys.ViewerBackground);
        contentHost.Children.Add(empty);
        contentHost.Children.Add(_tabs);
        _tabs.Visibility = Visibility.Collapsed;
        _tabs.SelectionChanged += (_, _) =>
        {
            if (_tabs.SelectedItem is TabItem { Tag: IPdfDocumentSession s }) _documents.ActiveDocument = s;
        };

        var statusBar = new Border { Child = _status };
        statusBar.SetResourceReference(Border.BackgroundProperty, ThemeKeys.StatusBarBackground);
        _status.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.StatusBarText);
        _status.SetBinding(TextBlock.TextProperty, LocBinding("Shell.Status.Ready"));
        shell.StatusRequested += (_, t) => _status.Text = t ?? _loc["Shell.Status.Ready"];

        var root = new DockPanel();
        DockPanel.SetDock(ribbon, Dock.Top);
        DockPanel.SetDock(statusBar, Dock.Bottom);
        root.Children.Add(ribbon);
        root.Children.Add(statusBar);
        root.Children.Add(contentHost);
        Content = root;

        _documents.DocumentOpened += (_, d) => AddTab(d);
        _documents.DocumentClosed += (_, d) => RemoveTab(d);
        _documents.ActiveDocumentChanged += (_, d) =>
        {
            if (d is not null && _tabByDoc.TryGetValue(d, out var t)) _tabs.SelectedItem = t;
        };

        foreach (var d in _registry.Descriptors) AddShortcuts(d);
        AllowDrop = true;
        Drop += async (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
                foreach (var f in files.Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)))
                {
                    try { await _documents.OpenAsync(f); }
                    catch (Exception ex) { App.Services.GetRequiredService<IDialogService>().ShowError(ex, f); }
                }
        };
    }

    private Binding LocBinding(string key) => new($"[{key}]") { Source = _loc, Mode = BindingMode.OneWay };

    private Ribbon BuildRibbon()
    {
        var ribbon = new Ribbon();
        var backstageItems = new BackstageTabControl();
        foreach (var id in RibbonCatalog.Backstage)
        {
            var d = _registry.GetDescriptor(id);
            if (d is null) continue;
            var b = new Button { Command = new RegistryCommandProxy(_registry, id) };
            b.SetBinding(Button.HeaderProperty, LocBinding(d.TextKey));
            backstageItems.Items.Add(b);
        }
        var backstage = new Backstage { Content = backstageItems };
        backstage.SetBinding(Backstage.HeaderProperty, LocBinding(RibbonCatalog.FileTabTextKey));
        ribbon.Menu = backstage;

        foreach (var id in RibbonCatalog.QuickAccess)
        {
            var d = _registry.GetDescriptor(id);
            if (d is null) continue;
            var qb = CreateButton(d, RibbonControlSize.Small);
            ribbon.QuickAccessItems.Add(new QuickAccessMenuItem { Target = qb, IsChecked = true });
        }

        foreach (var tab in RibbonCatalog.Tabs)
        {
            var tabItem = new RibbonTabItem();
            tabItem.SetBinding(RibbonTabItem.HeaderProperty, LocBinding(tab.TextKey));
            foreach (var group in tab.Groups)
            {
                var box = new RibbonGroupBox();
                box.SetBinding(RibbonGroupBox.HeaderProperty, LocBinding(group.TextKey));
                foreach (var cmdId in group.CommandIds)
                {
                    var d = _registry.GetDescriptor(cmdId);
                    if (d is not null) box.Items.Add(CreateButton(d, RibbonControlSize.Large));
                }
                foreach (var extra in _registry.Extras.Where(x => x.Tab == tab.Id && x.Group == group.Id))
                    box.Items.Add(CreateButton(extra.Descriptor, RibbonControlSize.Large));
                tabItem.Groups.Add(box);
            }
            if (tab.IsContextual)
            {
                // Karty kontekstowe pokazuje IShellService.SetContextualTabVisible (ui-shell: RibbonContextualTabGroup).
                tabItem.Visibility = Visibility.Collapsed;
            }
            ribbon.Tabs.Add(tabItem);
        }
        return ribbon;
    }

    private FrameworkElement CreateButton(CommandDescriptor d, RibbonControlSize size)
    {
        var icon = new TextBlock { Text = d.IconGlyph, FontSize = size == RibbonControlSize.Large ? 22 : 16 };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, ThemeKeys.FontIcons);
        var smallIcon = new TextBlock { Text = d.IconGlyph, FontSize = 16 };
        smallIcon.SetResourceReference(TextBlock.FontFamilyProperty, ThemeKeys.FontIcons);
        var proxy = new RegistryCommandProxy(_registry, d.Id);
        Control control;
        if (d.Kind == CommandKind.Menu)
        {
            var dd = new DropDownButton { Icon = smallIcon, LargeIcon = icon, Size = size };
            dd.SetBinding(DropDownButton.HeaderProperty, LocBinding(d.TextKey));
            if (RibbonCatalog.Menus.TryGetValue(d.Id, out var children))
            {
                foreach (var childId in children)
                {
                    var cd = _registry.GetDescriptor(childId);
                    if (cd is null) continue;
                    var mi = new Fluent.MenuItem { Command = new RegistryCommandProxy(_registry, childId) };
                    mi.SetBinding(Fluent.MenuItem.HeaderProperty, LocBinding(cd.TextKey));
                    dd.Items.Add(mi);
                }
            }
            control = dd;
        }
        else if (d.Kind == CommandKind.Toggle)
        {
            var tb = new ToggleButton { Icon = smallIcon, LargeIcon = icon, Size = size, Command = proxy };
            tb.SetBinding(ToggleButton.HeaderProperty, LocBinding(d.TextKey));
            control = tb;
        }
        else
        {
            var b = new Button { Icon = smallIcon, LargeIcon = icon, Size = size, Command = proxy };
            b.SetBinding(Button.HeaderProperty, LocBinding(d.TextKey));
            control = b;
        }
        var tip = new ScreenTip();
        tip.SetBinding(ScreenTip.TitleProperty, LocBinding(d.TextKey));
        if (d.Shortcuts.Count > 0) tip.Text = string.Join(", ", d.Shortcuts);
        control.ToolTip = tip;
        return control;
    }

    private readonly List<(Shortcut Shortcut, string CommandId)> _manualShortcuts = new();

    private void AddShortcuts(CommandDescriptor d)
    {
        foreach (var text in d.Shortcuts)
        {
            if (!Shortcut.TryParse(text, out var s)) continue;
            if (s.CanUseKeyBinding) InputBindings.Add(new KeyBinding(new RegistryCommandProxy(_registry, d.Id), s.Key, s.Modifiers));
            else _manualShortcuts.Add((s, d.Id));
        }
    }

    /// <summary>Skróty bez Ctrl/Alt (Shift+H…) i klawisze jednoliterowe – tylko poza polami tekstowymi.</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled || Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox or System.Windows.Controls.ComboBox { IsEditable: true }) return;
        foreach (var (s, id) in _manualShortcuts)
        {
            if (s.Matches(e) && _registry.TryExecute(id))
            {
                e.Handled = true;
                return;
            }
        }
    }

    private void AddTab(IPdfDocumentSession session)
    {
        var view = _views.CreateView(session);
        var header = new TextBlock { Text = session.DisplayName };
        session.StateChanged += (_, _) => header.Text = session.DisplayName + (session.IsModified ? " *" : string.Empty);
        var tab = new TabItem { Header = header, Content = view.Element, Tag = session };
        _tabByDoc[session] = tab;
        _tabs.Items.Add(tab);
        _tabs.Visibility = Visibility.Visible;
        _tabs.SelectedItem = tab;
    }

    private void RemoveTab(IPdfDocumentSession session)
    {
        if (_tabByDoc.Remove(session, out var tab)) _tabs.Items.Remove(tab);
        if (_tabs.Items.Count == 0) _tabs.Visibility = Visibility.Collapsed;
    }
}

/// <summary>
/// Pośrednik: przycisk/skrót odwołuje się do id; obsługa z rejestru w chwili wykonania
/// (moduł może podpiąć ją po zbudowaniu wstążki). Brak obsługi = wyszarzone.
/// </summary>
public sealed class RegistryCommandProxy : ICommand
{
    private readonly ICommandRegistry _registry;
    private readonly string _id;

    public RegistryCommandProxy(ICommandRegistry registry, string id)
    {
        _registry = registry;
        _id = id;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _registry.GetHandler(_id)?.CanExecute(parameter) ?? false;

    public void Execute(object? parameter) => _registry.TryExecute(_id, parameter);
}
