using System.Windows;
using System.Windows.Controls;
using ZetPDF.Annotations.Forms.Scripting;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;

namespace ZetPDF.Annotations.UI.Dialogs;

/// <summary>
/// Okno „Skrypty formularza”: lista skryptów dokumentu (tylko podgląd – nie są wykonywane) i pól (edycja Keystroke,
/// Format, Validate, Calculate), wykonywanie skryptów w tym dokumencie (wyłączone / tylko AF* / dowolny JavaScript
/// za zgodą), przeliczenie formularza. Zmiany skryptów – polecenia z Cofnij/Ponów (wołający).
/// </summary>
internal sealed class FormScriptsDialog : Window
{
    private readonly ILocalizationService _loc;
    private readonly IDialogService? _dialogs;
    private readonly Func<string, ScriptTrigger, string, Task<IReadOnlyList<FormScriptInfo>?>> _save;
    private readonly Func<ScriptPolicy, Task> _recalculate;
    private readonly ListBox _list;
    private readonly TextBox _editor;
    private readonly TextBlock _where, _note;
    private readonly ComboBox _field, _trigger;
    private readonly RadioButton _off, _af, _all;
    private readonly Button _saveButton, _deleteButton;
    private (string Field, ScriptTrigger Trigger)? _editing;
    private bool _syncPolicy;

    public FormScriptsDialog(ILocalizationService loc, IDialogService? dialogs, IReadOnlyList<FormScriptInfo> scripts, IReadOnlyList<string> fields, ScriptPolicy policy,
        Func<string, ScriptTrigger, string, Task<IReadOnlyList<FormScriptInfo>?>> save, Func<ScriptPolicy, Task> recalculate)
    {
        _loc = loc;
        _dialogs = dialogs;
        _save = save;
        _recalculate = recalculate;
        Policy = policy;
        Ui.SetupDialog(this, loc, "Annot.Script.Title");
        ResizeMode = ResizeMode.CanResizeWithGrip;
        SizeToContent = SizeToContent.Manual;
        Width = 820;
        Height = 560;

        var root = new DockPanel { Margin = new Thickness(12) };
        var policyBox = new GroupBox { Margin = new Thickness(0, 0, 0, 8) };
        policyBox.Loc(HeaderedContentControl.HeaderProperty, loc, "Annot.Script.Policy");
        var pp = new StackPanel { Margin = new Thickness(6) };
        _off = Radio(loc, "Annot.Script.Policy.Off");
        _af = Radio(loc, "Annot.Script.Policy.AfOnly");
        _all = Radio(loc, "Annot.Script.Policy.All");
        pp.Children.Add(_off);
        pp.Children.Add(_af);
        pp.Children.Add(_all);
        var warn = Ui.Label(loc, "Annot.Script.Policy.Info");
        warn.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.TextSecondary);
        pp.Children.Add(warn);
        policyBox.Content = pp;
        DockPanel.SetDock(policyBox, Dock.Top);
        root.Children.Add(policyBox);
        SetPolicyRadios(policy);
        _off.Checked += (_, _) => PolicyChanged(ScriptPolicy.Off);
        _af.Checked += (_, _) => PolicyChanged(ScriptPolicy.AfOnly);
        _all.Checked += (_, _) => PolicyChanged(ScriptPolicy.All);

        var buttons = Ui.ButtonRow(
            Ui.Button(loc, "Annot.Script.Recalculate", async (_, _) => await _recalculate(Policy)),
            Ui.Button(loc, "Annot.Dialog.Close", (_, _) => Close(), isCancel: true));
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new DockPanel { Margin = new Thickness(0, 0, 8, 0) };
        var add = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        add.Children.Add(Ui.Label(loc, "Annot.Script.NewForField"));
        _field = new ComboBox { Margin = new Thickness(0, 0, 0, 4) };
        foreach (var f in fields) _field.Items.Add(f);
        if (_field.Items.Count > 0) _field.SelectedIndex = 0;
        System.Windows.Automation.AutomationProperties.SetName(_field, loc["Annot.Script.NewForField"]);
        _trigger = new ComboBox { Margin = new Thickness(0, 0, 0, 4) };
        foreach (var t in Enum.GetValues<ScriptTrigger>()) _trigger.Items.Add(new ComboBoxItem { Content = loc["Annot.Script.Trigger." + t], Tag = t });
        _trigger.SelectedIndex = (int)ScriptTrigger.Calculate;
        System.Windows.Automation.AutomationProperties.SetName(_trigger, loc["Annot.Script.Trigger"]);
        add.Children.Add(_field);
        add.Children.Add(_trigger);
        var open = Ui.Button(loc, "Annot.Script.Edit", (_, _) => OpenForEdit());
        open.HorizontalAlignment = HorizontalAlignment.Left;
        open.Margin = new Thickness(0);
        open.IsEnabled = fields.Count > 0;
        add.Children.Add(open);
        DockPanel.SetDock(add, Dock.Bottom);
        left.Children.Add(add);
        _list = new ListBox();
        System.Windows.Automation.AutomationProperties.SetName(_list, loc["Annot.Script.Title"]);
        _list.SelectionChanged += (_, _) => ShowSelected();
        left.Children.Add(_list);
        grid.Children.Add(left);

        var right = new DockPanel();
        _where = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap };
        DockPanel.SetDock(_where, Dock.Top);
        right.Children.Add(_where);
        _note = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
        _note.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.TextSecondary);
        DockPanel.SetDock(_note, Dock.Top);
        right.Children.Add(_note);
        var editButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        _saveButton = Ui.Button(loc, "Annot.Script.Save", async (_, _) => await SaveAsync(_editor!.Text));
        _deleteButton = Ui.Button(loc, "Annot.Script.Delete", async (_, _) => await SaveAsync(string.Empty));
        editButtons.Children.Add(_deleteButton);
        editButtons.Children.Add(_saveButton);
        DockPanel.SetDock(editButtons, Dock.Bottom);
        right.Children.Add(editButtons);
        _editor = new TextBox
        {
            AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        _editor.SetResourceReference(Control.FontFamilyProperty, ThemeKeys.FontMono);
        System.Windows.Automation.AutomationProperties.SetName(_editor, loc["Annot.Script.Code"]);
        right.Children.Add(_editor);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        root.Children.Add(grid);
        Content = root;
        Fill(scripts);
    }

    public ScriptPolicy Policy { get; private set; }

    private static RadioButton Radio(ILocalizationService loc, string key)
    {
        var r = new RadioButton { Margin = new Thickness(0, 2, 0, 2), GroupName = "ScriptPolicy" };
        r.Loc(ContentControl.ContentProperty, loc, key);
        return r;
    }

    private void SetPolicyRadios(ScriptPolicy p)
    {
        _syncPolicy = true;
        _off.IsChecked = p == ScriptPolicy.Off;
        _af.IsChecked = p == ScriptPolicy.AfOnly;
        _all.IsChecked = p == ScriptPolicy.All;
        _syncPolicy = false;
    }

    private void PolicyChanged(ScriptPolicy p)
    {
        if (_syncPolicy || p == Policy) return;
        if (p == ScriptPolicy.All && _dialogs is { } d && !d.Confirm(_loc["Annot.Script.Policy.Confirm"]))
        {
            SetPolicyRadios(Policy);
            return;
        }
        Policy = p;
    }

    private string Describe(FormScriptInfo s)
    {
        if (s.FieldName is null)
        {
            var kind = s.Kind.StartsWith("Doc:", StringComparison.Ordinal) ? _loc.Format("Annot.Script.DocNamed", s.Kind[4..])
                : s.Kind == "OpenAction" ? _loc["Annot.Script.OpenAction"] : _loc.Format("Annot.Script.DocAction", s.Kind.Replace("DocAction:", string.Empty));
            return kind;
        }
        var trig = s.Trigger is { } t ? _loc["Annot.Script.Trigger." + t] : _loc["Annot.Script.ButtonAction"];
        return _loc.Format("Annot.Script.FieldTrigger", s.FieldName, trig);
    }

    private void Fill(IReadOnlyList<FormScriptInfo> scripts)
    {
        _list.Items.Clear();
        foreach (var s in scripts.OrderBy(s => s.FieldName is null ? 0 : 1).ThenBy(s => s.FieldName, StringComparer.Ordinal))
            _list.Items.Add(new ListBoxItem { Content = Describe(s), Tag = s });
        if (_list.Items.Count == 0) { _where.Text = _loc["Annot.Script.None"]; _note.Text = string.Empty; }
        UpdateButtons();
    }

    private void ShowSelected()
    {
        if ((_list.SelectedItem as ListBoxItem)?.Tag is not FormScriptInfo s) return;
        _where.Text = Describe(s);
        _editor.Text = s.Code;
        _editor.IsReadOnly = !s.IsEditable;
        _editing = s.IsEditable ? (s.FieldName!, s.Trigger!.Value) : null;
        _note.Text = s.IsEditable
            ? (FormScripts.IsAfOnly(s.Code) ? _loc["Annot.Script.Note.AfOnly"] : _loc["Annot.Script.Note.Custom"])
            : _loc["Annot.Script.Note.NotExecuted"];
        UpdateButtons();
    }

    private void OpenForEdit()
    {
        if (_field.SelectedItem is not string field || (_trigger.SelectedItem as ComboBoxItem)?.Tag is not ScriptTrigger t) return;
        var existing = _list.Items.OfType<ListBoxItem>().Select(i => i.Tag).OfType<FormScriptInfo>().FirstOrDefault(s => s.FieldName == field && s.Trigger == t);
        if (existing is not null)
        {
            _list.SelectedItem = _list.Items.OfType<ListBoxItem>().First(i => ReferenceEquals(i.Tag, existing));
            return;
        }
        _list.SelectedItem = null;
        _editing = (field, t);
        _where.Text = _loc.Format("Annot.Script.FieldTrigger", field, _loc["Annot.Script.Trigger." + t]);
        _note.Text = _loc["Annot.Script.Note.New"];
        _editor.IsReadOnly = false;
        _editor.Text = t switch
        {
            ScriptTrigger.Calculate => "AFSimple_Calculate(\"SUM\", new Array (\"\"));",
            ScriptTrigger.Format => "AFNumber_Format(2, 2, 0, 0, \"\", false);",
            ScriptTrigger.Validate => "AFRange_Validate(true, 0, true, 100);",
            _ => "AFNumber_Keystroke(2, 2, 0, 0, \"\", false);",
        };
        _editor.Focus();
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        _saveButton.IsEnabled = _editing is not null;
        _deleteButton.IsEnabled = _editing is not null && _list.SelectedItem is not null;
    }

    private async Task SaveAsync(string code)
    {
        if (_editing is not { } e) return;
        var updated = await _save(e.Field, e.Trigger, code);
        if (updated is null) return;
        Fill(updated);
        var again = _list.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is FormScriptInfo s && s.FieldName == e.Field && s.Trigger == e.Trigger);
        if (again is not null) _list.SelectedItem = again;
        else { _editing = null; _editor.Text = string.Empty; _where.Text = string.Empty; _note.Text = string.Empty; UpdateButtons(); }
    }
}
