using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Fluent;
using Microsoft.Extensions.Logging;
using ZetPDF.App.Services;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;
using ContextMenu = System.Windows.Controls.ContextMenu;
using ToggleButton = System.Windows.Controls.Primitives.ToggleButton;
using Separator = System.Windows.Controls.Separator;
using ComboBox = System.Windows.Controls.ComboBox;
using ZetPDF.Core.Localization;

namespace ZetPDF.App.Shell;

/// <summary>
/// Okno główne ZetPDF: wstążka z katalogu (RibbonBuilder), pasek szybkiego dostępu (z zapamiętywaniem poleceń
/// dodanych przez użytkownika), pole wyszukiwania poleceń (Alt+Q), karty dokumentów, panel boczny (F4),
/// pasek stanu, tryb odczytu i pełny ekran (IShellService), skróty klawiszowe (ShortcutMap), przeciągnij i upuść,
/// zapamiętywanie położenia okna.
/// </summary>
public partial class MainWindow : RibbonWindow
{
    private static readonly Dictionary<string, string> PanelGlyphs = new(StringComparer.Ordinal)
    {
        [PanelIds.Thumbnails] = "",
        [PanelIds.Bookmarks] = "",
        [PanelIds.Comments] = "",
        [PanelIds.Attachments] = "",
        [PanelIds.Layers] = "",
        [PanelIds.Signatures] = "",
    };

    private readonly CommandRegistry _registry;
    private readonly ILocalizationService _loc;
    private readonly IDocumentManager _documents;
    private readonly IDocumentViewFactory _views;
    private readonly ShellService _shell;
    private readonly IPanelRegistry _panels;
    private readonly ISettingsService _settings;
    private readonly IServiceProvider _services;
    private readonly DocumentActions _actions;
    private readonly ILogger<MainWindow> _log;
    private readonly RibbonBuilder _ribbon;
    private readonly CommandSearchBox _search;
    private readonly RecentFilesView _backstageRecent;
    private readonly RecentFilesView _startRecent;
    private readonly ObservableCollection<DocumentTab> _tabs = new();
    private readonly Dictionary<string, PanelEntry> _panelEntries = new(StringComparer.Ordinal);
    private readonly List<ShortcutMap.Binding> _manualShortcuts = new();
    private readonly List<ShortcutMap.Binding> _singleKeyShortcuts = new();
    private string? _activePanelId;
    private bool _closingConfirmed;
    private bool _qatRestored;
    private bool _suppressQatSave;
    private FullScreenState? _fullScreen;

    private sealed class PanelEntry
    {
        public required PanelDescriptor Descriptor { get; init; }
        public required ToggleButton Button { get; init; }
        public FrameworkElement? Content { get; set; }
    }

    private sealed record FullScreenState(WindowState State, double TitleBarHeight);

    public MainWindow(CommandRegistry registry, ILocalizationService loc, IDocumentManager documents, IDocumentViewFactory views,
        ShellService shell, IPanelRegistry panels, ISettingsService settings, IRecentFilesService recent, DocumentActions actions,
        IViewerService viewer, IServiceProvider services, ILogger<MainWindow> log)
    {
        _registry = registry;
        _loc = loc;
        _documents = documents;
        _views = views;
        _shell = shell;
        _panels = panels;
        _settings = settings;
        _services = services;
        _actions = actions;
        _log = log;

        InitializeComponent();
        System.Windows.Automation.AutomationProperties.SetAutomationId(this, "Shell.MainWindow");

        _search = new CommandSearchBox(registry, loc, shell);
        _backstageRecent = new RecentFilesView(recent, loc, OpenRecentAsync, BrowseFromBackstageAsync, compact: false);
        _startRecent = new RecentFilesView(recent, loc, OpenRecentAsync, null, compact: true, maxItems: 6);
        StartRecentHost.Content = _startRecent;

        _ribbon = new RibbonBuilder(registry, loc);
        var ribbon = _ribbon.Build(_backstageRecent, _search);
        RibbonHost.Content = ribbon;
        _ribbon.Backstage.IsOpenChanged += (_, _) =>
        {
            if (_ribbon.Backstage.IsOpen)
            {
                _backstageRecent.Refresh();
                _ribbon.BackstageTabs.SelectedItem = _ribbon.RecentTab;
            }
        };
        ribbon.IsMinimized = settings.Current.RibbonMinimized;
        _shell.IsRibbonMinimized = ribbon.IsMinimized;
        ribbon.IsMinimizedChanged += (_, _) =>
        {
            _shell.IsRibbonMinimized = ribbon.IsMinimized;
            if (_settings.Current.RibbonMinimized != ribbon.IsMinimized) _settings.Update(s => s.RibbonMinimized = ribbon.IsMinimized);
        };
        ribbon.Loaded += (_, _) => RestoreQuickAccess();

        StatusBarHost.Child = new StatusBarView(viewer, loc, registry, shell);

        DocumentStrip.ItemsSource = _tabs;
        DocumentStrip.SelectionChanged += (_, _) =>
        {
            if (DocumentStrip.SelectedItem is DocumentTab t && !ReferenceEquals(_documents.ActiveDocument, t.Session))
                _documents.ActiveDocument = t.Session;
        };
        DocumentStrip.PreviewMouseUp += OnStripMouseUp;
        DocumentStrip.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (ItemsControl.ContainerFromElement(DocumentStrip, (DependencyObject)e.OriginalSource) is ListBoxItem item) item.IsSelected = true;
        };
        DocumentStrip.ContextMenu = BuildTabContextMenu();

        _documents.DocumentOpened += (_, d) => AddTab(d);
        _documents.DocumentClosed += (_, d) => RemoveTab(d);
        _documents.ActiveDocumentChanged += (_, d) => ActivateTab(d);

        StartOpenButton.Click += async (_, _) => await _actions.OpenWithDialogAsync();
        StartSettingsButton.Click += (_, _) => _registry.TryExecute(CommandIds.File.Settings);
        PanelCloseButton.Click += (_, _) => _shell.IsSidePanelVisible = false;

        var shellSettings = settings.GetModuleSettings<ShellSettings>(ShellSettings.Key);
        PanelColumn.Width = new GridLength(Math.Clamp(shellSettings.SidePanelWidth, 160, 700));
        _shell.IsSidePanelVisible = settings.Current.SidePanelVisible;
        BuildPanels();
        _panels.Changed += (_, _) => Dispatcher.BeginInvoke(BuildPanels);

        _shell.ShellStateChanged += (_, _) => ApplyShellState();
        _shell.PanelRequested += (_, id) => ShowPanel(id);
        _shell.ContextualTabRequested += (_, r) => SetContextualTab(r.TabId, r.Visible);

        InstallShortcuts();
        _registry.Changed += (_, _) => Dispatcher.BeginInvoke(InstallShortcuts);

        _loc.CultureChanged += (_, _) =>
        {
            RibbonLocalization.Current.Culture = _loc.CurrentCulture;
            UpdateTitle();
            UpdatePanelTitle();
        };
        RibbonLocalization.Current.Culture = _loc.CurrentCulture;

        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragLeave += (_, _) => DropOverlay.Visibility = Visibility.Collapsed;
        Drop += OnDrop;

        RestorePlacement();
        UpdatePanelTitle();
        UpdateTitle();
        ApplyShellState();
        UpdateEmptyState();
    }

    // ------------------------------------------------------------------ polecenia powłoki (wołane z ShellCommands)

    /// <summary>Alt+Q – pole wyszukiwania poleceń.</summary>
    public void ActivateCommandSearch()
    {
        if (_shell.IsFullScreen) _shell.IsFullScreen = false;
        if (_shell.IsReadingMode) _shell.IsReadingMode = false;
        _search.Activate();
    }

    /// <summary>Ctrl+Tab / Ctrl+Shift+Tab.</summary>
    public void CycleDocument(int delta)
    {
        if (_tabs.Count < 2) return;
        var i = DocumentStrip.SelectedIndex;
        DocumentStrip.SelectedIndex = ((i < 0 ? 0 : i) + delta + _tabs.Count) % _tabs.Count;
    }

    public bool HasPanel(string panelId) => _panelEntries.ContainsKey(panelId);

    public Ribbon Ribbon => _ribbon.Ribbon;

    // ------------------------------------------------------------------ karty dokumentów

    private void AddTab(IPdfDocumentSession session)
    {
        IDocumentView view;
        try
        {
            view = _views.CreateView(session);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "CreateView failed for {Name}", session.DisplayName);
            ((IDialogService)_services.GetService(typeof(IDialogService))!).ShowError(ex, session.DisplayName);
            return;
        }
        var tab = new DocumentTab(session, view, t => _actions.CloseAsync(t.Session));
        session.StateChanged += (_, _) =>
        {
            tab.Refresh();
            if (ReferenceEquals(_documents.ActiveDocument, session)) UpdateTitle();
        };
        session.History.HistoryChanged += (_, _) => CommandManager.InvalidateRequerySuggested();
        view.Element.Visibility = Visibility.Collapsed;
        ViewHost.Children.Add(view.Element);
        _tabs.Add(tab);
        UpdateEmptyState();
        if (ReferenceEquals(_documents.ActiveDocument, session)) ActivateTab(session);
        else _documents.ActiveDocument = session;
    }

    private void RemoveTab(IPdfDocumentSession session)
    {
        var tab = _tabs.FirstOrDefault(t => ReferenceEquals(t.Session, session));
        if (tab is null) return;
        ViewHost.Children.Remove(tab.Element);
        var index = _tabs.IndexOf(tab);
        _tabs.Remove(tab);
        UpdateEmptyState();
        if (_tabs.Count == 0)
        {
            _documents.ActiveDocument = null;
            UpdateTitle();
        }
        else if (_documents.ActiveDocument is null || ReferenceEquals(_documents.ActiveDocument, session))
        {
            _documents.ActiveDocument = _tabs[Math.Clamp(index, 0, _tabs.Count - 1)].Session;
        }
        CommandManager.InvalidateRequerySuggested();
    }

    private void ActivateTab(IPdfDocumentSession? session)
    {
        var tab = session is null ? null : _tabs.FirstOrDefault(t => ReferenceEquals(t.Session, session));
        foreach (var t in _tabs) t.Element.Visibility = ReferenceEquals(t, tab) ? Visibility.Visible : Visibility.Collapsed;
        if (!ReferenceEquals(DocumentStrip.SelectedItem, tab)) DocumentStrip.SelectedItem = tab;
        if (tab is not null)
        {
            DocumentStrip.ScrollIntoView(tab);
            var element = tab.Element;
            Dispatcher.BeginInvoke(() =>
            {
                if (element.Focusable && !_search.IsKeyboardFocusWithin) element.Focus();
            }, System.Windows.Threading.DispatcherPriority.Input);
        }
        UpdateTitle();
        CommandManager.InvalidateRequerySuggested();
    }

    private void UpdateEmptyState()
    {
        var any = _tabs.Count > 0;
        EmptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        ViewHost.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        ApplyShellState();
    }

    private void UpdateTitle()
    {
        var d = _documents.ActiveDocument;
        Title = d is null
            ? _loc["Shell.AppTitle"]
            : _loc.Format("Shell.Title.WithDocument", d.DisplayName + (d.IsModified ? " *" : string.Empty), _loc["Shell.AppTitle"]);
    }

    private void OnStripMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        if (ItemsControl.ContainerFromElement(DocumentStrip, (DependencyObject)e.OriginalSource) is ListBoxItem { DataContext: DocumentTab t })
        {
            e.Handled = true;
            _ = _actions.CloseAsync(t.Session);
        }
    }

    private ContextMenu BuildTabContextMenu()
    {
        var menu = new ContextMenu();
        System.Windows.Controls.MenuItem Item(string key, Func<DocumentTab, Task> action, Func<DocumentTab, bool>? enabled = null)
        {
            var mi = new System.Windows.Controls.MenuItem();
            mi.SetBinding(HeaderedItemsControl.HeaderProperty, Loc.Bind(_loc, key));
            mi.Click += async (_, _) =>
            {
                if (DocumentStrip.SelectedItem is DocumentTab t) await action(t);
            };
            mi.Tag = enabled;
            return mi;
        }
        menu.Items.Add(Item("Cmd.File.Close", t => _actions.CloseAsync(t.Session)));
        menu.Items.Add(Item("Shell.Tab.CloseOthers", async t =>
        {
            foreach (var other in _tabs.Where(x => !ReferenceEquals(x, t)).ToArray())
                if (!await _actions.CloseAsync(other.Session)) return;
        }, _ => _tabs.Count > 1));
        menu.Items.Add(Item("Cmd.File.CloseAll", _ => _actions.CloseAllAsync()));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Shell.Tab.CopyPath", t =>
        {
            try { Clipboard.SetText(t.Session.FilePath ?? t.Session.DisplayName); }
            catch (Exception) { /* schowek zajęty */ }
            return Task.CompletedTask;
        }, t => t.Session.FilePath is not null));
        menu.Items.Add(Item("Shell.Recent.ShowInFolder", t =>
        {
            if (t.Session.FilePath is { } p) RecentFilesView.ShowInFolder(p);
            return Task.CompletedTask;
        }, t => t.Session.FilePath is not null));
        menu.Opened += (_, _) =>
        {
            foreach (var mi in menu.Items.OfType<System.Windows.Controls.MenuItem>())
                mi.IsEnabled = DocumentStrip.SelectedItem is DocumentTab t && (mi.Tag is not Func<DocumentTab, bool> f || f(t));
        };
        return menu;
    }

    private async Task OpenRecentAsync(string path)
    {
        _ribbon.Backstage.IsOpen = false;
        await _actions.OpenAsync(new[] { path });
    }

    private async Task BrowseFromBackstageAsync()
    {
        _ribbon.Backstage.IsOpen = false;
        await _actions.OpenWithDialogAsync();
    }

    // ------------------------------------------------------------------ panele boczne

    private void BuildPanels()
    {
        RailButtons.Children.Clear();
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in _panels.Panels.OrderBy(p => p.Order).ThenBy(p => p.Id, StringComparer.Ordinal))
        {
            if (!known.Add(p.Id)) continue;
            if (!_panelEntries.TryGetValue(p.Id, out var entry))
            {
                var glyph = !string.IsNullOrEmpty(p.IconGlyph) ? p.IconGlyph : PanelGlyphs.GetValueOrDefault(p.Id, "");
                var icon = new TextBlock { Text = glyph, FontSize = 16 };
                icon.SetResourceReference(TextBlock.FontFamilyProperty, ThemeKeys.FontIcons);
                var b = new ToggleButton { Content = icon };
                b.SetResourceReference(StyleProperty, "Shell.RailButton");
                b.SetBinding(ToolTipProperty, Loc.Bind(_loc, p.TitleKey));
                b.SetBinding(System.Windows.Automation.AutomationProperties.NameProperty, Loc.Bind(_loc, p.TitleKey));
                System.Windows.Automation.AutomationProperties.SetAutomationId(b, "Shell.Rail." + p.Id);
                var id = p.Id;
                b.Click += (_, _) =>
                {
                    if (_activePanelId == id && _shell.IsSidePanelVisible) _shell.IsSidePanelVisible = false;
                    else ShowPanel(id);
                };
                entry = new PanelEntry { Descriptor = p, Button = b };
                _panelEntries[p.Id] = entry;
            }
            RailButtons.Children.Add(entry.Button);
        }
        if (_activePanelId is null || !_panelEntries.ContainsKey(_activePanelId))
        {
            var last = _settings.Current.LastSidePanel;
            var first = last is not null && _panelEntries.ContainsKey(last) ? last : _panels.Panels.OrderBy(p => p.Order).FirstOrDefault()?.Id;
            if (first is not null) SelectPanel(first);
        }
        ApplyShellState();
        CommandManager.InvalidateRequerySuggested();
    }

    private void ShowPanel(string panelId)
    {
        if (!_panelEntries.ContainsKey(panelId)) return;
        if (_shell.IsReadingMode) _shell.IsReadingMode = false;
        SelectPanel(panelId);
        _shell.IsSidePanelVisible = true;
        if (_settings.Current.LastSidePanel != panelId) _settings.Update(s => s.LastSidePanel = panelId);
    }

    private void SelectPanel(string panelId)
    {
        if (!_panelEntries.TryGetValue(panelId, out var entry)) return;
        _activePanelId = panelId;
        if (entry.Content is null)
        {
            try
            {
                entry.Content = entry.Descriptor.Factory(_services);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Panel factory failed: {Panel}", panelId);
                var err = new TextBlock { Margin = new Thickness(16), TextWrapping = TextWrapping.Wrap, Text = _loc.Format("Core.Error.Generic", _loc.Describe(ex)) };
                err.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.StatusError);
                entry.Content = err;
            }
        }
        PanelContent.Content = entry.Content;
        foreach (var e in _panelEntries.Values) e.Button.IsChecked = ReferenceEquals(e, entry) && _shell.IsSidePanelVisible;
        UpdatePanelTitle();
    }

    private void UpdatePanelTitle()
    {
        if (_activePanelId is not null && _panelEntries.TryGetValue(_activePanelId, out var e))
            PanelTitle.SetBinding(TextBlock.TextProperty, Loc.Bind(_loc, e.Descriptor.TitleKey));
        else PanelTitle.SetBinding(TextBlock.TextProperty, Loc.Bind(_loc, "Cmd.View.TogglePanel"));
    }

    // ------------------------------------------------------------------ stan powłoki (panel, wstążka, tryb odczytu, pełny ekran)

    private void ApplyShellState()
    {
        if (RibbonHost is null) return;
        var immersive = _shell.IsReadingMode || _shell.IsFullScreen;
        var hasPanels = _panelEntries.Count > 0;
        var panelVisible = _shell.IsSidePanelVisible && !immersive && hasPanels;

        RibbonHost.Visibility = immersive ? Visibility.Collapsed : Visibility.Visible;
        if (_ribbon.Ribbon.IsMinimized != _shell.IsRibbonMinimized) _ribbon.Ribbon.IsMinimized = _shell.IsRibbonMinimized;
        StatusBarHost.Visibility = _shell.IsFullScreen ? Visibility.Collapsed : Visibility.Visible;
        DocumentStrip.Visibility = _tabs.Count > 0 && !immersive ? Visibility.Visible : Visibility.Collapsed;
        Rail.Visibility = hasPanels && !immersive ? Visibility.Visible : Visibility.Collapsed;

        if (panelVisible)
        {
            if (PanelArea.Visibility != Visibility.Visible)
            {
                var w = _settings.GetModuleSettings<ShellSettings>(ShellSettings.Key).SidePanelWidth;
                PanelColumn.Width = new GridLength(Math.Clamp(w, 160, 700));
            }
            PanelArea.Visibility = Visibility.Visible;
            PanelSplitter.Visibility = Visibility.Visible;
        }
        else
        {
            if (PanelArea.Visibility == Visibility.Visible) SavePanelWidth();
            PanelArea.Visibility = Visibility.Collapsed;
            PanelSplitter.Visibility = Visibility.Collapsed;
            PanelColumn.Width = new GridLength(0);
        }
        PanelEmpty.Visibility = hasPanels ? Visibility.Collapsed : Visibility.Visible;
        foreach (var e in _panelEntries.Values) e.Button.IsChecked = panelVisible && e.Descriptor.Id == _activePanelId;
        if (_settings.Current.SidePanelVisible != _shell.IsSidePanelVisible) _settings.Update(s => s.SidePanelVisible = _shell.IsSidePanelVisible);

        if (_shell.IsFullScreen && _fullScreen is null) EnterFullScreen();
        else if (!_shell.IsFullScreen && _fullScreen is not null) ExitFullScreen();
        CommandManager.InvalidateRequerySuggested();
    }

    private void SavePanelWidth()
    {
        var width = PanelColumn.ActualWidth;
        if (width < 100) return;
        var s = _settings.GetModuleSettings<ShellSettings>(ShellSettings.Key);
        if (Math.Abs(s.SidePanelWidth - width) < 1) return;
        s.SidePanelWidth = width;
        _settings.SetModuleSettings(ShellSettings.Key, s);
    }

    private void EnterFullScreen()
    {
        _fullScreen = new FullScreenState(WindowState, TitleBarHeight);
        IgnoreTaskbarOnMaximize = true;
        TitleBarHeight = 0;
        if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal; // wymusza ponowne wyliczenie obszaru
        WindowState = WindowState.Maximized;
    }

    private void ExitFullScreen()
    {
        var saved = _fullScreen!;
        _fullScreen = null;
        IgnoreTaskbarOnMaximize = false;
        TitleBarHeight = saved.TitleBarHeight;
        WindowState = WindowState.Normal;
        if (saved.State == WindowState.Maximized) WindowState = WindowState.Maximized;
    }

    private void SetContextualTab(string tabId, bool visible)
    {
        if (!_ribbon.ContextualGroups.TryGetValue(tabId, out var group)) return;
        group.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (_ribbon.TabsById.TryGetValue(tabId, out var tab))
        {
            if (visible) _ribbon.Ribbon.SelectedTabItem = tab;
            else if (ReferenceEquals(_ribbon.Ribbon.SelectedTabItem, tab) && _ribbon.TabsById.TryGetValue("Home", out var home)) _ribbon.Ribbon.SelectedTabItem = home;
        }
    }

    // ------------------------------------------------------------------ pasek szybkiego dostępu

    private void RestoreQuickAccess()
    {
        if (_qatRestored) return;
        var qat = _ribbon.Ribbon.QuickAccessToolBar;
        if (qat is null) return;
        _qatRestored = true;
        _suppressQatSave = true;
        try
        {
            foreach (var id in _settings.Current.QuickAccessExtra.ToArray())
            {
                if (_ribbon.ElementsById.TryGetValue(id, out var element) && !_ribbon.Ribbon.IsInQuickAccessToolBar(element))
                {
                    try { _ribbon.Ribbon.AddToQuickAccessToolBar(element); }
                    catch (Exception ex) { _log.LogWarning(ex, "QAT restore failed for {Id}", id); }
                }
            }
        }
        finally
        {
            _suppressQatSave = false;
        }
        qat.ItemsChanged += (_, _) => Dispatcher.BeginInvoke(SaveQuickAccess);
    }

    private void SaveQuickAccess()
    {
        if (_suppressQatSave) return;
        var ids = _ribbon.Ribbon.GetQuickAccessElements().Keys
            .Select(e => _ribbon.IdsByElement.TryGetValue(e, out var id) ? id : null)
            .OfType<string>()
            .Where(id => !RibbonCatalog.QuickAccess.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (!ids.SequenceEqual(_settings.Current.QuickAccessExtra, StringComparer.Ordinal))
            _settings.Update(s => s.QuickAccessExtra = ids);
    }

    // ------------------------------------------------------------------ skróty klawiszowe

    private void InstallShortcuts()
    {
        var map = ShortcutMap.Build(_registry.Descriptors);
        foreach (var c in map.Conflicts)
            _log.LogWarning("Shortcut conflict {Shortcut}: kept {Kept}, ignored {Ignored}", c.Shortcut.ToDisplayString(), c.KeptCommandId, c.IgnoredCommandId);
        foreach (var i in map.Invalid) _log.LogWarning("Invalid shortcut {Shortcut}", i);

        foreach (var old in InputBindings.OfType<KeyBinding>().Where(k => k.Command is RegistryCommandProxy).ToArray()) InputBindings.Remove(old);
        _manualShortcuts.Clear();
        _manualShortcuts.AddRange(map.Manual);
        foreach (var b in map.KeyBindings)
        {
            try { InputBindings.Add(new KeyBinding(new RegistryCommandProxy(_registry, b.CommandId), b.Shortcut.Key, b.Shortcut.Modifiers)); }
            catch (NotSupportedException) { _manualShortcuts.Add(b); }
        }
        _singleKeyShortcuts.Clear();
        _singleKeyShortcuts.AddRange(map.SingleKeys);
    }

    private static bool IsTextInputFocused() =>
        Keyboard.FocusedElement is TextBoxBase or PasswordBox or ComboBox { IsEditable: true }
        || (Keyboard.FocusedElement is DependencyObject d && FindAncestor<TextBoxBase>(d) is not null);

    private static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        for (var cur = d; cur is not null; cur = cur is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(cur) : LogicalTreeHelper.GetParent(cur))
            if (cur is T t) return t;
        return null;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled) return;

        if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None && (_shell.IsFullScreen || _shell.IsReadingMode)
            && !_ribbon.Backstage.IsOpen && !IsTextInputFocused())
        {
            _shell.IsFullScreen = false;
            _shell.IsReadingMode = false;
            e.Handled = true;
            return;
        }

        if (IsTextInputFocused() || _ribbon.Backstage.IsOpen) return;
        foreach (var b in _manualShortcuts)
        {
            if (b.Shortcut.Matches(e) && _registry.TryExecute(b.CommandId))
            {
                e.Handled = true;
                return;
            }
        }
        if (_settings.Current.SingleKeyShortcuts && Keyboard.Modifiers == ModifierKeys.None)
        {
            foreach (var b in _singleKeyShortcuts)
            {
                if (b.Shortcut.Matches(e) && _registry.TryExecute(b.CommandId))
                {
                    e.Handled = true;
                    return;
                }
            }
        }
    }

    // ------------------------------------------------------------------ przeciągnij i upuść

    private static string[] DroppedPdfs(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] files
            ? files.Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(f)).ToArray()
            : Array.Empty<string>();

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var ok = e.Data.GetDataPresent(DataFormats.FileDrop) && DroppedPdfs(e).Length > 0;
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        var pdfs = DroppedPdfs(e);
        e.Handled = true;
        if (pdfs.Length == 0)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                ((IDialogService)_services.GetService(typeof(IDialogService))!).ShowMessage(_loc["Shell.Drop.NotPdf"], null, MessageSeverity.Warning);
            return;
        }
        Activate();
        await _actions.OpenAsync(pdfs);
    }

    // ------------------------------------------------------------------ położenie okna i zamykanie

    private void RestorePlacement()
    {
        var s = _settings.Current;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (s.WindowWidth >= MinWidth && s.WindowHeight >= MinHeight)
        {
            Width = Math.Min(s.WindowWidth, SystemParameters.VirtualScreenWidth);
            Height = Math.Min(s.WindowHeight, SystemParameters.VirtualScreenHeight);
        }
        if (!double.IsNaN(s.WindowLeft) && !double.IsNaN(s.WindowTop))
        {
            var visible = s.WindowLeft + 100 > SystemParameters.VirtualScreenLeft
                          && s.WindowTop + 50 > SystemParameters.VirtualScreenTop
                          && s.WindowLeft + 100 < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth
                          && s.WindowTop + 50 < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
            if (visible)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = s.WindowLeft;
                Top = s.WindowTop;
            }
        }
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void SavePlacement()
    {
        if (_fullScreen is not null) ExitFullScreen();
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        var maximized = WindowState == WindowState.Maximized;
        if (PanelArea.Visibility == Visibility.Visible) SavePanelWidth();
        if (bounds.IsEmpty || bounds.Width < 1) return;
        _settings.Update(s =>
        {
            s.WindowLeft = bounds.Left;
            s.WindowTop = bounds.Top;
            s.WindowWidth = bounds.Width;
            s.WindowHeight = bounds.Height;
            s.WindowMaximized = maximized;
        });
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (!_closingConfirmed && _documents.Documents.Any(d => d.IsModified))
        {
            e.Cancel = true;
            base.OnClosing(e);
            if (await _actions.ConfirmExitAsync())
            {
                _closingConfirmed = true;
                await Dispatcher.InvokeAsync(Close);
            }
            return;
        }
        try { SavePlacement(); }
        catch (Exception ex) { _log.LogWarning(ex, "Saving window placement failed"); }
        try { (_services.GetService(typeof(SessionRestore)) as SessionRestore)?.Capture(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _log.LogWarning(ex, "Saving session failed"); }
        base.OnClosing(e);
    }
}
