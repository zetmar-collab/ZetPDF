using System.Text;
using System.Windows;
using System.Windows.Controls;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;
using ZetPDF.Security.Actions;

namespace ZetPDF.Security.UI;

/// <summary>
/// Kreator działań: lista działań (wbudowane + własne), edytor kroków i parametrów, uruchamianie na wielu plikach
/// z folderem wyjściowym, postępem, anulowaniem i raportem błędów per plik.
/// </summary>
internal sealed class ActionWizardWindow : Window
{
    private sealed class StepVm
    {
        public StepVm(string type, object options) { Type = type; Options = options; }
        public string Type { get; }
        public object Options { get; }
    }

    private sealed class ActionVm
    {
        public string Id = string.Empty;
        public string Name = string.Empty;
        public string Description = string.Empty;
        public bool IsBuiltIn;
        public List<StepVm> Steps = new();
        public bool Dirty;
    }

    private readonly ILocalizationService _loc;
    private readonly IActionWizard _wizard;
    private readonly IDialogService _dialogs;
    private readonly ListBox _actions = new();
    private readonly TextBox _name = new(), _description = new() { TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, Height = 54, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly ListBox _steps = new() { Height = 170 };
    private readonly ComboBox _stepType = new() { MinWidth = 200 };
    private readonly ContentControl _params = new();
    private readonly ListBox _files = new() { Height = 120, SelectionMode = SelectionMode.Extended };
    private readonly TextBox _output = new(), _suffix = new() { Text = "_zetpdf", Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly CheckBox _overwrite;
    private readonly TextBlock _builtInNote;
    private readonly Button _save, _delete, _addStep, _removeStep, _up, _down;
    private List<ActionVm> _all = new();
    private bool _loading;

    public ActionWizardWindow(ILocalizationService loc, IActionWizard wizard, IDialogService dialogs, IReadOnlyList<string> initialFiles)
    {
        _loc = loc;
        _wizard = wizard;
        _dialogs = dialogs;
        this.Loc(TitleProperty, loc, "Security.Wizard.Title");
        Width = 1100;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        this.Themed(BackgroundProperty, ThemeKeys.SurfaceRaised).Themed(ForegroundProperty, ThemeKeys.TextPrimary);
        SetResourceReference(FontFamilyProperty, ThemeKeys.FontUi);

        _overwrite = Ui.Check(loc, "Security.Wizard.Overwrite");
        _builtInNote = Ui.Plain(loc["Security.Wizard.BuiltInNote"], secondary: true);
        foreach (var t in wizard.StepTypes) _stepType.Items.Add(new ComboBoxItem { Tag = t.Key }.Loc(ContentControl.ContentProperty, loc, t.Value));
        _stepType.SelectedIndex = 0;
        System.Windows.Automation.AutomationProperties.SetName(_actions, loc["Security.Wizard.Actions"]);
        System.Windows.Automation.AutomationProperties.SetName(_steps, loc["Security.Wizard.Steps"]);
        System.Windows.Automation.AutomationProperties.SetName(_files, loc["Security.Wizard.Files"]);
        System.Windows.Automation.AutomationProperties.SetName(_stepType, loc["Security.Wizard.StepType"]);

        // lewa kolumna – działania
        var actionButtons = Ui.Row(
            Ui.Button(loc, "Security.Wizard.New", (_, _) => NewAction()),
            Ui.Button(loc, "Security.Wizard.Duplicate", (_, _) => Duplicate()));
        _delete = Ui.Button(loc, "Security.Wizard.Delete", async (_, _) => await DeleteAsync());
        actionButtons.Children.Add(_delete);
        var left = Section("Security.Wizard.Actions", _actions, actionButtons);
        _actions.Height = 420;
        _actions.SelectionChanged += (_, _) => ShowAction();

        // środek – definicja
        _save = Ui.Button(loc, "Core.Button.Save", async (_, _) => await SaveAsync());
        _addStep = Ui.Button(loc, "Core.Button.Add", (_, _) => AddStep());
        _removeStep = Ui.Button(loc, "Core.Button.Remove", (_, _) => RemoveStep());
        _up = Ui.Button(loc, "Security.Wizard.Up", (_, _) => MoveStep(-1));
        _down = Ui.Button(loc, "Security.Wizard.Down", (_, _) => MoveStep(+1));
        _name.TextChanged += (_, _) => { if (!_loading && Current is { IsBuiltIn: false } a) { a.Name = _name.Text; a.Dirty = true; RefreshActionTitles(); } };
        _description.TextChanged += (_, _) => { if (!_loading && Current is { IsBuiltIn: false } a) { a.Description = _description.Text; a.Dirty = true; } };
        _steps.SelectionChanged += (_, _) => ShowParams();
        var middle = new StackPanel();
        middle.Children.Add(Ui.Text(loc, "Security.Wizard.Name"));
        middle.Children.Add(_name);
        middle.Children.Add(Ui.Text(loc, "Security.Wizard.Description"));
        middle.Children.Add(_description);
        middle.Children.Add(_builtInNote);
        middle.Children.Add(Ui.Text(loc, "Security.Wizard.Steps"));
        middle.Children.Add(_steps);
        middle.Children.Add(Ui.Row(_stepType, _addStep));
        middle.Children.Add(Ui.Row(_removeStep, _up, _down, _save));

        // prawa – parametry
        var paramScroll = new ScrollViewer { Content = _params, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 400 };
        var right = Section("Security.Wizard.Parameters", paramScroll, null);

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        middle.Margin = new Thickness(12, 0, 12, 0);
        Grid.SetColumn(middle, 1);
        Grid.SetColumn(right, 2);
        top.Children.Add(left);
        top.Children.Add(middle);
        top.Children.Add(right);

        // dół – uruchamianie
        foreach (var f in initialFiles) _files.Items.Add(f);
        if (initialFiles.Count > 0) _output.Text = Path.GetDirectoryName(initialFiles[0]) ?? string.Empty;
        var fileButtons = Ui.Row(
            Ui.Button(loc, "Security.Wizard.AddFiles", (_, _) => AddFiles()),
            Ui.Button(loc, "Security.Wizard.AddFolder", (_, _) => AddFolder()),
            Ui.Button(loc, "Core.Button.Remove", (_, _) => { foreach (var s in _files.SelectedItems.Cast<object>().ToList()) _files.Items.Remove(s); }),
            Ui.Button(loc, "Security.Wizard.Clear", (_, _) => _files.Items.Clear()));
        var outRow = new DockPanel();
        var browse = Ui.Button(loc, "Core.Button.Browse", (_, _) =>
        {
            var d = _dialogs.SelectFolder(_loc["Security.Wizard.Output"], _output.Text.Length > 0 ? _output.Text : null);
            if (d is not null) _output.Text = d;
        });
        DockPanel.SetDock(browse, Dock.Right);
        outRow.Children.Add(browse);
        outRow.Children.Add(_output);
        var run = Ui.Button(loc, "Security.Wizard.Run", async (_, _) => await RunAsync(), isDefault: false);
        var close = Ui.Button(loc, "Core.Button.Close", (_, _) => Close(), isCancel: true);
        var runPanel = new StackPanel();
        runPanel.Children.Add(_files);
        runPanel.Children.Add(fileButtons);
        var outForm = new Grid();
        outForm.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        outForm.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        outForm.RowDefinitions.Add(new RowDefinition());
        outForm.RowDefinitions.Add(new RowDefinition());
        var lo = Ui.Text(loc, "Security.Wizard.Output"); lo.Margin = new Thickness(0, 3, 10, 3);
        var ls = Ui.Text(loc, "Security.Wizard.Suffix"); ls.Margin = new Thickness(0, 3, 10, 3);
        Grid.SetRow(ls, 1); Grid.SetColumn(outRow, 1); Grid.SetRow(_suffix, 1); Grid.SetColumn(_suffix, 1);
        outRow.Margin = _suffix.Margin = new Thickness(0, 3, 0, 3);
        outForm.Children.Add(lo); outForm.Children.Add(outRow); outForm.Children.Add(ls); outForm.Children.Add(_suffix);
        runPanel.Children.Add(outForm);
        runPanel.Children.Add(_overwrite);
        var bottom = Ui.Group(loc, "Security.Wizard.RunGroup", runPanel);
        var buttons = Ui.Row(run, close);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;

        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(bottom);
        root.Children.Add(top);
        Content = root;
        Loaded += async (_, _) => await LoadAsync();
        Closing += (_, e) =>
        {
            if (_all.Any(a => a.Dirty) && !_dialogs.Confirm(_loc["Security.Wizard.UnsavedClose"], _loc["Security.Wizard.Title"])) e.Cancel = true;
        };
    }

    private FrameworkElement Section(string titleKey, FrameworkElement content, FrameworkElement? footer)
    {
        var p = new StackPanel();
        p.Children.Add(Ui.Text(_loc, titleKey));
        p.Children.Add(content);
        if (footer is not null) p.Children.Add(footer);
        return p;
    }

    private ActionVm? Current => (_actions.SelectedItem as ListBoxItem)?.Tag as ActionVm;

    private string DisplayName(ActionVm a) => a.IsBuiltIn ? _loc[a.Name] : (a.Name.Length > 0 ? a.Name : _loc["Security.Wizard.Untitled"]);

    private async Task LoadAsync(string? select = null)
    {
        try
        {
            var defs = await _wizard.GetActionsAsync();
            _all = defs.Select(d => new ActionVm
            {
                Id = d.Id, Name = d.Name, Description = d.Description, IsBuiltIn = d.IsBuiltIn,
                Steps = d.Steps.Select(s => new StepVm(s.StepType, ActionWizard.ReadOptions(s) ?? new object())).Where(s => ActionStepTypes.OptionTypes.ContainsKey(s.Type)).ToList(),
            }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _dialogs.ShowError(ex); }
        _actions.Items.Clear();
        foreach (var a in _all) _actions.Items.Add(new ListBoxItem { Content = DisplayName(a), Tag = a });
        _actions.SelectedItem = _actions.Items.Cast<ListBoxItem>().FirstOrDefault(i => ((ActionVm)i.Tag).Id == select) ?? (_actions.Items.Count > 0 ? _actions.Items[0] : null);
    }

    private void RefreshActionTitles()
    {
        foreach (ListBoxItem i in _actions.Items) i.Content = DisplayName((ActionVm)i.Tag) + (((ActionVm)i.Tag).Dirty ? " *" : string.Empty);
    }

    private void ShowAction()
    {
        _loading = true;
        var a = Current;
        _name.Text = a is null ? string.Empty : DisplayName(a);
        _description.Text = a is null ? string.Empty : a.IsBuiltIn ? _loc[a.Description] : a.Description;
        bool editable = a is { IsBuiltIn: false };
        _name.IsReadOnly = _description.IsReadOnly = !editable;
        _builtInNote.Visibility = a is { IsBuiltIn: true } ? Visibility.Visible : Visibility.Collapsed;
        foreach (var b in new[] { _save, _delete, _addStep, _removeStep, _up, _down }) b.IsEnabled = editable;
        _stepType.IsEnabled = editable;
        RefreshSteps();
        _loading = false;
    }

    private void RefreshSteps(int select = 0)
    {
        _steps.Items.Clear();
        var a = Current;
        if (a is null) { _params.Content = null; return; }
        int n = 1;
        foreach (var s in a.Steps)
        {
            var item = new ListBoxItem { Tag = s };
            item.Content = n++ + ". " + _loc[_wizard.StepTypes.TryGetValue(s.Type, out var key) ? key : s.Type];
            _steps.Items.Add(item);
        }
        if (_steps.Items.Count > 0) _steps.SelectedIndex = Math.Clamp(select, 0, _steps.Items.Count - 1);
        else _params.Content = null;
    }

    private void ShowParams()
    {
        if ((_steps.SelectedItem as ListBoxItem)?.Tag is not StepVm s || Current is not { } a) { _params.Content = null; return; }
        var editor = OptionsEditor.Build(s.Options, _loc, () => { a.Dirty = true; RefreshActionTitles(); });
        editor.IsEnabled = !a.IsBuiltIn;
        _params.Content = editor;
    }

    private void NewAction()
    {
        var a = new ActionVm { Id = ActionWizard.NewId(), Name = _loc["Security.Wizard.Untitled"], Dirty = true };
        _all.Add(a);
        var item = new ListBoxItem { Content = DisplayName(a) + " *", Tag = a };
        _actions.Items.Add(item);
        _actions.SelectedItem = item;
        _name.Focus();
        _name.SelectAll();
    }

    private void Duplicate()
    {
        if (Current is not { } src) return;
        var copy = new ActionVm
        {
            Id = ActionWizard.NewId(),
            Name = _loc.Format("Security.Wizard.CopyOf", DisplayName(src)),
            Description = src.IsBuiltIn ? _loc[src.Description] : src.Description,
            Steps = src.Steps.Select(s => new StepVm(s.Type, ActionWizard.ReadOptions(ActionWizard.Step(s.Type, s.Options))!)).ToList(),
            Dirty = true,
        };
        _all.Add(copy);
        var item = new ListBoxItem { Content = DisplayName(copy) + " *", Tag = copy };
        _actions.Items.Add(item);
        _actions.SelectedItem = item;
    }

    private async Task DeleteAsync()
    {
        if (Current is not { IsBuiltIn: false } a) return;
        if (!_dialogs.Confirm(_loc.Format("Security.Wizard.DeleteConfirm", DisplayName(a)), _loc["Security.Wizard.Title"])) return;
        try { await _wizard.DeleteActionAsync(a.Id); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { _dialogs.ShowError(ex); return; }
        a.Dirty = false;
        await LoadAsync();
    }

    private void AddStep()
    {
        if (Current is not { IsBuiltIn: false } a || (_stepType.SelectedItem as ComboBoxItem)?.Tag is not string type) return;
        a.Steps.Add(new StepVm(type, Activator.CreateInstance(ActionStepTypes.OptionTypes[type])!));
        a.Dirty = true;
        RefreshActionTitles();
        RefreshSteps(a.Steps.Count - 1);
    }

    private void RemoveStep()
    {
        if (Current is not { IsBuiltIn: false } a || _steps.SelectedIndex < 0) return;
        int i = _steps.SelectedIndex;
        a.Steps.RemoveAt(i);
        a.Dirty = true;
        RefreshActionTitles();
        RefreshSteps(i);
    }

    private void MoveStep(int delta)
    {
        if (Current is not { IsBuiltIn: false } a || _steps.SelectedIndex < 0) return;
        int i = _steps.SelectedIndex, j = i + delta;
        if (j < 0 || j >= a.Steps.Count) return;
        (a.Steps[i], a.Steps[j]) = (a.Steps[j], a.Steps[i]);
        a.Dirty = true;
        RefreshActionTitles();
        RefreshSteps(j);
    }

    private static ActionDefinition ToDefinition(ActionVm a) => new()
    {
        Id = a.Id, Name = a.Name, Description = a.Description, IsBuiltIn = a.IsBuiltIn,
        Steps = a.Steps.Select(s => ActionWizard.Step(s.Type, s.Options)).ToArray(),
    };

    private async Task SaveAsync()
    {
        if (Current is not { IsBuiltIn: false } a) return;
        if (a.Name.Trim().Length == 0) { _dialogs.ShowMessage(_loc["Security.Wizard.Error.NoName"], _loc["Security.Wizard.Title"], MessageSeverity.Warning); return; }
        try { await _wizard.SaveActionAsync(ToDefinition(a)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { _dialogs.ShowError(ex); return; }
        a.Dirty = false;
        RefreshActionTitles();
    }

    private void AddFiles()
    {
        var files = _dialogs.OpenFiles(_loc["Security.Wizard.AddFiles"], new[] { FileFilter.Pdf(_loc) }, multiselect: true);
        if (files is null) return;
        foreach (var f in files) if (!_files.Items.Contains(f)) _files.Items.Add(f);
        if (_output.Text.Length == 0 && files.Count > 0) _output.Text = Path.GetDirectoryName(files[0]) ?? string.Empty;
    }

    private void AddFolder()
    {
        var dir = _dialogs.SelectFolder(_loc["Security.Wizard.AddFolder"]);
        if (dir is null) return;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.pdf", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase))
                if (!_files.Items.Contains(f)) _files.Items.Add(f);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _dialogs.ShowError(ex); }
        if (_output.Text.Length == 0) _output.Text = dir;
    }

    private async Task RunAsync()
    {
        if (Current is not { } a) return;
        var files = _files.Items.Cast<string>().ToList();
        if (files.Count == 0) { _dialogs.ShowMessage(_loc["Security.Wizard.Error.NoFiles"], _loc["Security.Wizard.Title"], MessageSeverity.Warning); return; }
        if (_output.Text.Trim().Length == 0) { _dialogs.ShowMessage(_loc["Security.Wizard.Error.NoOutput"], _loc["Security.Wizard.Title"], MessageSeverity.Warning); return; }
        if (a.Steps.Count == 0) { _dialogs.ShowMessage(_loc["Security.Wizard.Error.NoSteps"], _loc["Security.Wizard.Title"], MessageSeverity.Warning); return; }
        if (_overwrite.IsChecked == true && !_dialogs.Confirm(_loc["Security.Wizard.OverwriteConfirm"], _loc["Security.Wizard.Title"])) return;

        var definition = ToDefinition(a);
        if (ActionWizard.NeedsPasswords(definition))
        {
            var pwd = new PasswordProtectDialog(_loc, null);
            if (_dialogs.ShowDialog(pwd) != true || pwd.Result is not { } enc) return;
            definition = ActionWizard.WithPasswords(definition, enc.UserPassword, enc.OwnerPassword);
        }
        var options = new ActionRunOptions { OutputDirectory = _output.Text.Trim(), FileNameSuffix = _suffix.Text, OverwriteExisting = _overwrite.IsChecked == true };
        ActionRunReport? report = null;
        bool completed;
        try
        {
            completed = await _dialogs.RunWithProgressAsync(_loc.Format("Security.Wizard.Running", DisplayName(a)),
                async (progress, ct) => report = await _wizard.RunAsync(definition, files, options, progress, ct));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _dialogs.ShowError(ex);
            return;
        }
        if (!completed || report is null)
        {
            _dialogs.ShowMessage(_loc["Security.Wizard.Cancelled"], _loc["Security.Wizard.Title"]);
            return;
        }
        var sb = new StringBuilder(_loc.Format("Security.Wizard.Report", report.Succeeded, report.Failed));
        foreach (var f in report.Files.Where(f => !f.Success))
            sb.AppendLine().Append("• ").Append(Path.GetFileName(f.InputPath)).Append(": ").Append(f.Error);
        _dialogs.ShowMessage(sb.ToString(), _loc["Security.Wizard.Title"], report.Failed > 0 ? MessageSeverity.Warning : MessageSeverity.Info);
    }
}
