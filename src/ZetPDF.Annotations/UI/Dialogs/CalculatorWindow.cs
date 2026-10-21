using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ZetPDF.Annotations.Measure;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;

namespace ZetPDF.Annotations.UI.Dialogs;

/// <summary>
/// Kalkulator księgowy (okno modalne przez IDialogService; taśma zachowywana między otwarciami): liczby wpisywane
/// i z zaznaczonego tekstu dokumentu (przy otwarciu i przyciskiem), operacje
/// + − × ÷, cofnięcie wpisu, historia wyników, kopiowanie wyniku, wstawienie wyniku na stronę (polecenie z Cofnij/Ponów).
/// </summary>
internal sealed class CalculatorWindow : Window
{
    private readonly ILocalizationService _loc;
    private readonly Func<string?> _selectionText;
    private readonly Action<string> _insert;
    private readonly TextBox _input;
    private readonly ListBox _tape, _history;
    private readonly TextBlock _total, _status;

    public CalculatorWindow(ILocalizationService loc, Func<string?> selectionText, Action<string> insert, CalculatorTape? tape = null)
    {
        _loc = loc;
        _selectionText = selectionText;
        _insert = insert;
        Ui.SetupDialog(this, loc, "Annot.Calc.Title");
        ResizeMode = ResizeMode.CanResizeWithGrip;
        SizeToContent = SizeToContent.Manual;
        Width = 360;
        Height = 520;
        MinWidth = 300;
        MinHeight = 380;
        ShowInTaskbar = false;
        Tape = tape ?? new CalculatorTape();
        EventHandler onChanged = (_, _) => Refresh();
        Tape.Changed += onChanged;
        Closed += (_, _) => Tape.Changed -= onChanged;

        var root = new DockPanel { Margin = new Thickness(12) };

        var top = new StackPanel();
        _input = new TextBox { FontSize = 16, Margin = new Thickness(0, 0, 0, 6) };
        System.Windows.Automation.AutomationProperties.SetName(_input, loc["Annot.Calc.Input"]);
        _input.Tip(loc, "Annot.Calc.Input");
        _input.PreviewKeyDown += OnInputKey;
        top.Children.Add(_input);
        var ops = new UniformGrid4();
        foreach (var op in Enum.GetValues<TapeOp>())
        {
            var o = op;
            var b = new Button { Content = CalculatorTape.Symbol(op), FontSize = 16, Margin = new Thickness(0, 0, 4, 4), MinHeight = 30 };
            b.Tip(loc, "Annot.Calc.Op." + op);
            b.Click += (_, _) => Apply(o);
            ops.Add(b);
        }
        top.Children.Add(ops.Panel);
        var row = new WrapPanel { Margin = new Thickness(0, 2, 0, 6) };
        var fromSel = Ui.Button(loc, "Annot.Calc.FromSelection", (_, _) => AddSelection());
        fromSel.Margin = new Thickness(0, 0, 4, 4);
        var undo = Ui.Button(loc, "Annot.Calc.RemoveLast", (_, _) => Tape.RemoveLast());
        undo.Margin = new Thickness(0, 0, 4, 4);
        var clear = Ui.Button(loc, "Annot.Calc.Clear", (_, _) => Tape.Clear());
        clear.Margin = new Thickness(0, 0, 4, 4);
        row.Children.Add(fromSel);
        row.Children.Add(undo);
        row.Children.Add(clear);
        top.Children.Add(row);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var bottom = new StackPanel();
        _total = new TextBlock { FontSize = 22, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 2) };
        _total.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.TextPrimary);
        System.Windows.Automation.AutomationProperties.SetName(_total, loc["Annot.Calc.Total"]);
        bottom.Children.Add(_total);
        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
        _status.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.TextSecondary);
        bottom.Children.Add(_status);
        var historyExpander = new Expander { Margin = new Thickness(0, 4, 0, 4) };
        historyExpander.Loc(HeaderedContentControl.HeaderProperty, loc, "Annot.Calc.History");
        _history = new ListBox { Height = 80 };
        System.Windows.Automation.AutomationProperties.SetName(_history, loc["Annot.Calc.History"]);
        _history.MouseDoubleClick += (_, _) => { if (_history.SelectedItem is ListBoxItem { Tag: decimal v }) Tape.Add(TapeOp.Add, v); };
        historyExpander.Content = _history;
        bottom.Children.Add(historyExpander);
        bottom.Children.Add(Ui.ButtonRow(
            Ui.Button(loc, "Annot.Calc.Copy", (_, _) => CopyResult()),
            Ui.Button(loc, "Annot.Calc.Insert", (_, _) => _insert(CalculatorTape.Format(Tape.Total, _loc.CurrentCulture))),
            Ui.Button(loc, "Annot.Dialog.Close", (_, _) => Close(), isCancel: true)));
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        _tape = new ListBox { FontFamily = new System.Windows.Media.FontFamily("Consolas, Courier New") };
        _tape.SetResourceReference(Control.FontFamilyProperty, ThemeKeys.FontMono);
        System.Windows.Automation.AutomationProperties.SetName(_tape, loc["Annot.Calc.Tape"]);
        root.Children.Add(_tape);
        Content = root;
        Loaded += (_, _) => _input.Focus();
        Refresh();
    }

    /// <summary>Taśma (współdzielona między kolejnymi otwarciami okna w sesji programu).</summary>
    public CalculatorTape Tape { get; }

    private void OnInputKey(object sender, KeyEventArgs e)
    {
        TapeOp? op = e.Key switch
        {
            Key.Enter or Key.Add or Key.OemPlus => TapeOp.Add,
            Key.Subtract or Key.OemMinus when _input.Text.Trim().Length > 0 && _input.CaretIndex > 0 => TapeOp.Subtract,
            Key.Multiply => TapeOp.Multiply,
            Key.Divide or Key.Oem2 => TapeOp.Divide,
            _ => null,
        };
        if (op is null) return;
        e.Handled = true;
        Apply(op.Value);
    }

    /// <summary>Operacja z liczbą z pola edycji (puste pole – bez zmian).</summary>
    public void Apply(TapeOp op)
    {
        var text = _input.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        var v = NumberParser.ParseSingle(text, _loc.CurrentCulture);
        if (v is null) { _status.Text = _loc["Annot.Calc.NotANumber"]; return; }
        if (!Tape.Add(op, v.Value)) { _status.Text = _loc["Annot.Calc.Invalid"]; return; }
        _input.Clear();
        _status.Text = string.Empty;
    }

    /// <summary>Dodaje (sumuje) liczby z zaznaczonego tekstu dokumentu.</summary>
    public int AddSelection()
    {
        var text = _selectionText();
        if (string.IsNullOrWhiteSpace(text)) { _status.Text = _loc["Annot.Calc.NoSelection"]; return 0; }
        var numbers = NumberParser.Parse(text, _loc.CurrentCulture);
        foreach (var n in numbers) Tape.Add(TapeOp.Add, n, fromDocument: true);
        _status.Text = numbers.Count == 0 ? _loc["Annot.Calc.NoNumbers"] : _loc.Format("Annot.Calc.Added", numbers.Count);
        return numbers.Count;
    }

    private void CopyResult()
    {
        try
        {
            Clipboard.SetText(CalculatorTape.Format(Tape.Total, _loc.CurrentCulture));
            _status.Text = _loc["Annot.Calc.Copied"];
        }
        catch (System.Runtime.InteropServices.ExternalException) { _status.Text = _loc["Annot.Calc.ClipboardBusy"]; }
    }

    private void Refresh()
    {
        var c = _loc.CurrentCulture;
        _tape.Items.Clear();
        foreach (var e in Tape.Entries)
        {
            var line = $"{CalculatorTape.Symbol(e.Op)} {CalculatorTape.Format(e.Value, c),18}";
            var item = new ListBoxItem { Content = e.FromDocument ? line + "  ·" : line };
            if (e.FromDocument) item.Tip(_loc, "Annot.Calc.FromDocument");
            _tape.Items.Add(item);
        }
        if (_tape.Items.Count > 0) _tape.ScrollIntoView(_tape.Items[^1]);
        _total.Text = "= " + CalculatorTape.Format(Tape.Total, c);
        _history.Items.Clear();
        foreach (var h in Tape.History) _history.Items.Add(new ListBoxItem { Content = CalculatorTape.Format(h, c), Tag = h });
    }

    /// <summary>Siatka 4 przycisków operacji w jednym wierszu.</summary>
    private sealed class UniformGrid4
    {
        public System.Windows.Controls.Primitives.UniformGrid Panel { get; } = new() { Columns = 4 };
        public void Add(UIElement e) => Panel.Children.Add(e);
    }
}
