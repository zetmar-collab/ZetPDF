using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Fluent;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;
using Button = Fluent.Button;
using MenuItem = Fluent.MenuItem;
using ToggleButton = Fluent.ToggleButton;

namespace ZetPDF.App.Shell;

/// <summary>
/// Buduje wstążkę Fluent.Ribbon z deklaratywnego katalogu (<see cref="RibbonCatalog"/>) i poleceń dodatkowych
/// modułów. Polecenia bez obsługi są wyszarzone (z podpowiedzią "jeszcze niedostępne"), przełączniki śledzą
/// <see cref="IToggleCommand.IsChecked"/>, menu są wypełniane przy otwarciu (stałe dzieci + menu dynamiczne).
/// Teksty – wiązania do <see cref="ILocalizationService"/> (zmiana języka na żywo).
/// </summary>
internal sealed class RibbonBuilder
{
    private readonly CommandRegistry _registry;
    private readonly ILocalizationService _loc;
    private readonly List<(ScreenTip Tip, CommandDescriptor Descriptor)> _tips = new();
    private readonly List<(DropDownButton Button, CommandDescriptor Descriptor)> _menus = new();

    public RibbonBuilder(CommandRegistry registry, ILocalizationService loc)
    {
        _registry = registry;
        _loc = loc;
        _loc.CultureChanged += (_, _) => { RefreshTips(); AssignKeyTips(); };
        _registry.Changed += (_, _) => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            RefreshTips();
            RefreshMenuAvailability();
        });
    }

    public Ribbon Ribbon { get; } = new();
    public Backstage Backstage { get; } = new();
    public BackstageTabControl BackstageTabs { get; } = new();
    public BackstageTabItem RecentTab { get; } = new();

    /// <summary>Pierwszy element wstążki dla danego polecenia (do dodawania do paska szybkiego dostępu).</summary>
    public Dictionary<string, FrameworkElement> ElementsById { get; } = new(StringComparer.Ordinal);

    /// <summary>Element wstążki → id polecenia (także kopie w pasku szybkiego dostępu tworzone przez nas).</summary>
    public Dictionary<UIElement, string> IdsByElement { get; } = new();

    public Dictionary<string, RibbonTabItem> TabsById { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, RibbonContextualTabGroup> ContextualGroups { get; } = new(StringComparer.Ordinal);

    public Ribbon Build(FrameworkElement recentContent, UIElement? toolBarItem)
    {
        var ribbon = Ribbon;
        ribbon.AutomaticStateManagement = false;
        ribbon.CanCustomizeRibbon = false;
        ribbon.CanCustomizeQuickAccessToolBar = false;
        ribbon.CanUseSimplified = false;
        ribbon.IsQuickAccessToolBarVisible = true;

        BuildBackstage(recentContent);
        ribbon.Menu = Backstage;

        foreach (var id in RibbonCatalog.QuickAccess)
        {
            var d = _registry.GetDescriptor(id);
            if (d is null) continue;
            var qb = CreateControl(d, RibbonControlSize.Small);
            IdsByElement[qb] = id;
            ribbon.QuickAccessItems.Add(new QuickAccessMenuItem { Target = qb, IsChecked = true });
        }

        foreach (var tab in RibbonCatalog.Tabs)
        {
            var tabItem = new RibbonTabItem();
            tabItem.SetBinding(RibbonTabItem.HeaderProperty, Loc.Bind(_loc, tab.TextKey));
            TabsById[tab.Id] = tabItem;
            var groupNames = new List<string>();
            foreach (var group in tab.Groups)
            {
                var box = new RibbonGroupBox { Name = "g" + groupNames.Count };
                groupNames.Add(box.Name);
                box.SetBinding(HeaderedItemsControl.HeaderProperty, Loc.Bind(_loc, group.TextKey));
                var descriptors = group.CommandIds.Select(id => _registry.GetDescriptor(id)).OfType<CommandDescriptor>()
                    .Concat(_registry.Extras.Where(x => x.Tab == tab.Id && x.Group == group.Id).Select(x => x.Descriptor))
                    .ToList();
                if (descriptors.Count > 0) box.Icon = Glyph(descriptors[0].IconGlyph, 16);
                for (int i = 0; i < descriptors.Count; i++)
                {
                    var large = descriptors.Count <= 3 || i < 2;
                    var c = CreateControl(descriptors[i], large ? RibbonControlSize.Large : RibbonControlSize.Middle);
                    SetSizeDefinition(c, large ? "Large, Middle, Small" : "Middle, Small, Small");
                    box.Items.Add(c);
                    ElementsById.TryAdd(descriptors[i].Id, c);
                    IdsByElement[c] = descriptors[i].Id;
                }
                tabItem.Groups.Add(box);
            }
            // Przy wąskim oknie (lub dłuższych polskich etykietach) grupy najpierw zmniejszają przyciski
            // (Large → Middle → Small), od ostatniej grupy, a dopiero potem zwijają się do jednego przycisku –
            // zamiast przewijania wstążki strzałką, które ukrywało ostatnie grupy (np. Rozpoznawanie, Podpis).
            var reverse = Enumerable.Reverse(groupNames).ToList();
            tabItem.ReduceOrder = string.Join(",", reverse.Select(n => "(" + n + ")")
                .Concat(reverse.Select(n => "(" + n + ")"))
                .Concat(reverse));
            if (tab.IsContextual)
            {
                var ctx = new RibbonContextualTabGroup { Visibility = Visibility.Collapsed };
                ctx.SetBinding(RibbonContextualTabGroup.HeaderProperty, Loc.Bind(_loc, tab.TextKey));
                ribbon.ContextualGroups.Add(ctx);
                tabItem.Group = ctx;
                ContextualGroups[tab.Id] = ctx;
            }
            ribbon.Tabs.Add(tabItem);
        }

        if (toolBarItem is not null) ribbon.ToolBarItems.Add(toolBarItem);
        RefreshTips();
        AssignKeyTips();
        RefreshMenuAvailability();
        return ribbon;
    }

    // Alt+Q to wyszukiwanie poleceń – litera Q nie trafia do kart, żeby nie myliła się ze skrótem.
    private static readonly string[] ReservedTabKeys = { "Q" };

    /// <summary>
    /// KeyTips (Alt, potem litery): karta Plik, karty wstążki i przyciski każdej karty. Litery z etykiet w bieżącym języku.
    /// </summary>
    private void AssignKeyTips()
    {
        var tabs = RibbonCatalog.Tabs.Where(t => TabsById.ContainsKey(t.Id)).ToList();
        var labels = new List<string> { _loc[RibbonCatalog.FileTabTextKey] };
        labels.AddRange(tabs.Select(t => _loc[t.TextKey]));
        var keys = KeyTipAssigner.Assign(labels, ReservedTabKeys);
        KeyTip.SetKeys(Backstage, keys[0]);
        for (int i = 0; i < tabs.Count; i++)
        {
            var tabItem = TabsById[tabs[i].Id];
            KeyTip.SetKeys(tabItem, keys[i + 1]);
            var controls = tabItem.Groups.SelectMany(g => g.Items.OfType<Control>()).Where(c => IdsByElement.ContainsKey(c)).ToList();
            var controlKeys = KeyTipAssigner.Assign(controls.Select(c => _registry.GetDescriptor(IdsByElement[c]) is { } d ? _loc[d.TextKey] : string.Empty).ToList());
            for (int k = 0; k < controls.Count; k++) KeyTip.SetKeys(controls[k], controlKeys[k]);
        }
    }

    private void BuildBackstage(FrameworkElement recentContent)
    {
        Backstage.SetBinding(Backstage.HeaderProperty, Loc.Bind(_loc, RibbonCatalog.FileTabTextKey));
        RecentTab.SetBinding(BackstageTabItem.HeaderProperty, Loc.Bind(_loc, "Shell.Backstage.Recent"));
        RecentTab.Content = recentContent;
        BackstageTabs.Items.Add(RecentTab);
        foreach (var id in RibbonCatalog.Backstage)
        {
            var d = _registry.GetDescriptor(id);
            if (d is null) continue;
            if (id is CommandIds.File.Print or CommandIds.File.CreateFromFile or CommandIds.File.Settings)
                BackstageTabs.Items.Add(new SeparatorTabItem());
            var b = new Button { Command = new RegistryCommandProxy(_registry, id), Icon = Glyph(d.IconGlyph, 16) };
            b.SetBinding(Button.HeaderProperty, Loc.Bind(_loc, d.TextKey));
            var tip = CreateTip(d);
            b.ToolTip = tip;
            BackstageTabs.Items.Add(b);
        }
        BackstageTabs.SelectedItem = RecentTab;
        Backstage.Content = BackstageTabs;
    }

    private static void SetSizeDefinition(Control c, string definition)
    {
        var def = new RibbonControlSizeDefinition(definition);
        switch (c)
        {
            case Button b: b.SizeDefinition = def; break;
            case ToggleButton t: t.SizeDefinition = def; break;
            case DropDownButton dd: dd.SizeDefinition = def; break;
        }
    }

    /// <summary>Glif z czcionki ikon (Segoe Fluent Icons / Segoe MDL2 Assets) – kolor dziedziczony z kontrolki.</summary>
    public static TextBlock Glyph(string glyph, double size)
    {
        var t = new TextBlock { Text = glyph, FontSize = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.FontFamilyProperty, ThemeKeys.FontIcons);
        return t;
    }

    public Control CreateControl(CommandDescriptor d, RibbonControlSize size)
    {
        var proxy = new RegistryCommandProxy(_registry, d.Id);
        Control control;
        if (d.Kind == CommandKind.Menu)
        {
            // Obsługa części głównej → SplitButton; same pozycje menu → DropDownButton.
            DropDownButton dd = _registry.GetHandler(d.Id) is not null
                ? new SplitButton { Command = proxy }
                : new DropDownButton();
            dd.Icon = Glyph(d.IconGlyph, 16);
            dd.LargeIcon = Glyph(d.IconGlyph, 26);
            dd.Size = size;
            dd.SetBinding(DropDownButton.HeaderProperty, Loc.Bind(_loc, d.TextKey));
            dd.DropDownOpened += (_, _) => FillMenu(dd, d);
            FillMenu(dd, d);
            _menus.Add((dd, d));
            control = dd;
        }
        else if (d.Kind == CommandKind.Toggle)
        {
            var tb = new ToggleButton { Icon = Glyph(d.IconGlyph, 16), LargeIcon = Glyph(d.IconGlyph, 26), Size = size, Command = proxy };
            tb.SetBinding(ToggleButton.HeaderProperty, Loc.Bind(_loc, d.TextKey));
            AttachToggleState(tb, d.Id);
            control = tb;
        }
        else
        {
            var b = new Button { Icon = Glyph(d.IconGlyph, 16), LargeIcon = Glyph(d.IconGlyph, 26), Size = size, Command = proxy };
            b.SetBinding(Button.HeaderProperty, Loc.Bind(_loc, d.TextKey));
            control = b;
        }
        control.ToolTip = CreateTip(d);
        AutomationIdHelper.Set(control, d.Id);
        return control;
    }

    /// <summary>Stan przełącznika = IToggleCommand.IsChecked (kliknięcie nie zmienia go samo z siebie).</summary>
    private void AttachToggleState(ToggleButton tb, string id)
    {
        void Sync()
        {
            var checkedNow = _registry.GetHandler(id) is IToggleCommand t && t.IsChecked;
            if (tb.IsChecked != checkedNow) tb.IsChecked = checkedNow;
        }

        PropertyChangedEventHandler onChanged = (_, e) =>
        {
            if (e.PropertyName is null or "" or nameof(IToggleCommand.IsChecked)) tb.Dispatcher.BeginInvoke(Sync);
        };
        IToggleCommand? subscribed = null;
        void Subscribe()
        {
            var h = _registry.GetHandler(id) as IToggleCommand;
            if (ReferenceEquals(h, subscribed)) return;
            if (subscribed is not null) subscribed.PropertyChanged -= onChanged;
            subscribed = h;
            if (h is not null) h.PropertyChanged += onChanged;
        }

        Subscribe();
        _registry.Changed += (_, _) => tb.Dispatcher.BeginInvoke(() =>
        {
            Subscribe();
            Sync();
        });
        tb.Click += (_, _) => tb.Dispatcher.BeginInvoke(Sync);
        tb.Loaded += (_, _) => Sync();
    }

    private void FillMenu(DropDownButton dd, CommandDescriptor d)
    {
        dd.Items.Clear();
        if (RibbonCatalog.Menus.TryGetValue(d.Id, out var children))
        {
            foreach (var childId in children)
            {
                var cd = _registry.GetDescriptor(childId);
                if (cd is null) continue;
                var mi = new MenuItem { Command = new RegistryCommandProxy(_registry, childId), Icon = Glyph(cd.IconGlyph, 14) };
                mi.SetBinding(MenuItem.HeaderProperty, Loc.Bind(_loc, cd.TextKey));
                dd.Items.Add(mi);
            }
        }
        foreach (var item in _registry.GetMenuItems(d.Id))
        {
            var mi = new MenuItem { Command = item.Command, CommandParameter = item.Parameter };
            if (!string.IsNullOrEmpty(item.IconGlyph)) mi.Icon = Glyph(item.IconGlyph, 14);
            if (item.TextKey is not null) mi.SetBinding(MenuItem.HeaderProperty, Loc.Bind(_loc, item.TextKey));
            else mi.Header = item.Text;
            dd.Items.Add(mi);
        }
    }

    private void RefreshMenuAvailability()
    {
        foreach (var (dd, d) in _menus)
        {
            if (dd is SplitButton) continue;
            var hasChildren = (RibbonCatalog.Menus.TryGetValue(d.Id, out var children) && children.Any(c => _registry.GetHandler(c) is not null))
                              || _registry.GetMenuItems(d.Id).Count > 0;
            dd.IsEnabled = hasChildren;
        }
    }

    public ScreenTip CreateTip(CommandDescriptor d)
    {
        var tip = new ScreenTip { IsRibbonAligned = true };
        tip.SetBinding(ScreenTip.TitleProperty, Loc.Bind(_loc, d.TextKey));
        _tips.Add((tip, d));
        UpdateTip(tip, d);
        return tip;
    }

    private void RefreshTips()
    {
        foreach (var (tip, d) in _tips) UpdateTip(tip, d);
    }

    private void UpdateTip(ScreenTip tip, CommandDescriptor d)
    {
        var lines = new List<string>();
        if (_loc.HasKey(d.TooltipKey)) lines.Add(_loc[d.TooltipKey]);
        var keys = ShortcutMap.Describe(d, includeSingleKey: false);
        if (keys.Length > 0) lines.Add(_loc.Format("Shell.Tip.Shortcut", keys));
        if (!string.IsNullOrWhiteSpace(d.SingleKey)) lines.Add(_loc.Format("Shell.Tip.SingleKey", d.SingleKey.Trim().ToUpperInvariant()));
        tip.Text = lines.Count > 0 ? string.Join(Environment.NewLine, lines) : null;
        var hasHandler = _registry.GetHandler(d.Id) is not null
                         || (d.Kind == CommandKind.Menu && (_registry.GetMenuItems(d.Id).Count > 0 || (RibbonCatalog.Menus.TryGetValue(d.Id, out var ch) && ch.Any(c => _registry.GetHandler(c) is not null))));
        tip.DisableReason = hasHandler ? _loc["Shell.Tip.Unavailable"] : _loc["Shell.Tip.NotImplemented"];
    }
}

/// <summary>Identyfikator automatyzacji (UI Automation) = id polecenia – ułatwia testy i czytniki ekranu.</summary>
internal static class AutomationIdHelper
{
    public static void Set(DependencyObject element, string id) =>
        System.Windows.Automation.AutomationProperties.SetAutomationId(element, id);
}
