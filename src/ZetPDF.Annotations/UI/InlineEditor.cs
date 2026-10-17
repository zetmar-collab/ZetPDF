using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;

namespace ZetPDF.Annotations.UI;

/// <summary>Jak zakończono edycję w miejscu.</summary>
internal enum EditorExit { Commit, Cancel, TabNext, TabPrevious }

/// <summary>
/// Edytor w miejscu nad widokiem (WPF Popup względem <see cref="IDocumentView.Element"/>) – tekst komentarza,
/// wartość pola formularza, lista wyboru. Enter (lub Ctrl+Enter w wielowierszowym) zatwierdza, Esc anuluje,
/// Tab/Shift+Tab zatwierdza i przechodzi do następnego/poprzedniego pola.
/// </summary>
internal sealed class InlineEditor
{
    private static InlineEditor? _current;
    private readonly Popup _popup;
    private readonly Action<EditorExit, string> _done;
    private readonly Func<string> _value;
    private bool _finished;

    private InlineEditor(IDocumentView view, Rect viewRect, FrameworkElement content, Func<string> value, Action<EditorExit, string> done)
    {
        _done = done;
        _value = value;
        var border = new Border { Child = content, BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BorderBrushProperty, ThemeKeys.Focus);
        border.SetResourceReference(Border.BackgroundProperty, ThemeKeys.InputBackground);
        _popup = new Popup
        {
            PlacementTarget = view.Element,
            Placement = PlacementMode.Relative,
            HorizontalOffset = viewRect.X,
            VerticalOffset = viewRect.Y,
            StaysOpen = true,
            AllowsTransparency = true,
            Child = border,
        };
        content.PreviewKeyDown += OnKey;
        content.LostKeyboardFocus += (_, e) =>
        {
            if (e.NewFocus is DependencyObject d && IsInside(d, border)) return;
            Finish(EditorExit.Commit);
        };
        view.Element.Unloaded += (_, _) => Finish(EditorExit.Commit);
    }

    private static bool IsInside(DependencyObject d, DependencyObject root)
    {
        for (var c = d; c is not null; c = System.Windows.Media.VisualTreeHelper.GetParent(c) ?? LogicalTreeHelper.GetParent(c))
            if (ReferenceEquals(c, root)) return true;
        return false;
    }

    public static bool IsOpen => _current is { _finished: false };

    public static void CloseCurrent() => _current?.Finish(EditorExit.Commit);

    private void OnKey(object sender, KeyEventArgs e)
    {
        bool multiline = sender is TextBox { AcceptsReturn: true };
        switch (e.Key)
        {
            case Key.Escape: e.Handled = true; Finish(EditorExit.Cancel); break;
            case Key.Enter when !multiline || Keyboard.Modifiers.HasFlag(ModifierKeys.Control): e.Handled = true; Finish(EditorExit.Commit); break;
            case Key.Tab: e.Handled = true; Finish(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? EditorExit.TabPrevious : EditorExit.TabNext); break;
        }
    }

    private void Finish(EditorExit exit)
    {
        if (_finished) return;
        _finished = true;
        var v = _value();
        _popup.IsOpen = false;
        if (ReferenceEquals(_current, this)) _current = null;
        _done(exit, v);
    }

    private void Open(FrameworkElement focus)
    {
        CloseCurrent();
        _current = this;
        _popup.IsOpen = true;
        focus.Dispatcher.BeginInvoke(() =>
        {
            focus.Focus();
            Keyboard.Focus(focus);
            if (focus is TextBox tb) tb.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>Pole tekstowe w miejscu (prostokąt w układzie widoku).</summary>
    public static void EditText(IDocumentView view, Rect viewRect, string initial, bool multiline, double fontSizeDip, Action<EditorExit, string> done)
    {
        var tb = new TextBox
        {
            Text = initial,
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MinWidth = Math.Max(60, viewRect.Width),
            Width = Math.Max(60, viewRect.Width),
            MinHeight = Math.Max(20, viewRect.Height),
            MaxHeight = Math.Max(200, viewRect.Height),
            FontSize = Math.Clamp(fontSizeDip, 8, 72),
            VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            BorderThickness = new Thickness(0),
        };
        var ed = new InlineEditor(view, viewRect, tb, () => tb.Text, done);
        ed.Open(tb);
    }

    /// <summary>Lista wyboru w miejscu (pole kombi/lista). editable = można wpisać własną wartość.</summary>
    public static void EditChoice(IDocumentView view, Rect viewRect, IReadOnlyList<string> options, string current, bool editable, Action<EditorExit, string> done)
    {
        FrameworkElement control;
        Func<string> value;
        if (editable)
        {
            var cb = new ComboBox { IsEditable = true, Text = current, MinWidth = Math.Max(80, viewRect.Width), IsDropDownOpen = false };
            foreach (var o in options) cb.Items.Add(o);
            control = cb;
            value = () => cb.Text;
        }
        else
        {
            var lb = new ListBox { MinWidth = Math.Max(80, viewRect.Width), MaxHeight = 240 };
            foreach (var o in options) lb.Items.Add(o);
            lb.SelectedItem = options.Contains(current) ? current : null;
            control = lb;
            value = () => lb.SelectedItem as string ?? current;
            InlineEditor? self = null;
            lb.MouseUp += (_, _) => { if (lb.SelectedItem is not null) self?.Finish(EditorExit.Commit); };
            var ed0 = new InlineEditor(view, viewRect, lb, value, done);
            self = ed0;
            ed0.Open(lb);
            return;
        }
        var ed = new InlineEditor(view, viewRect, control, value, done);
        ed.Open(control);
    }

    /// <summary>Kalendarz (pole daty) – wybór daty zatwierdza.</summary>
    public static void EditDate(IDocumentView view, Rect viewRect, DateTime? current, string format, Action<EditorExit, string> done)
    {
        var cal = new Calendar { SelectedDate = current, DisplayDate = current ?? DateTime.Today };
        var tb = new TextBox { Text = current?.ToString(format, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty, Margin = new Thickness(2) };
        var panel = new StackPanel();
        panel.Children.Add(tb);
        panel.Children.Add(cal);
        InlineEditor? self = null;
        cal.SelectedDatesChanged += (_, _) =>
        {
            if (cal.SelectedDate is { } d) tb.Text = d.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
            self?.Finish(EditorExit.Commit);
        };
        var ed = new InlineEditor(view, viewRect, panel, () => tb.Text, done);
        tb.PreviewKeyDown += ed.OnKey;
        self = ed;
        ed.Open(tb);
    }
}
