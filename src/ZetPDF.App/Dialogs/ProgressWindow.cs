using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;

namespace ZetPDF.App.Dialogs;

/// <summary>
/// Okno postępu operacji w tle (IDialogService.RunWithProgressAsync). Pasek określony/nieokreślony, opis kroku,
/// przycisk Anuluj (zamknięcie krzyżykiem = anulowanie). Zamykane przez kod po zakończeniu pracy.
/// </summary>
public sealed class ProgressWindow : Window
{
    private readonly ProgressBar _bar = new() { Height = 6, IsIndeterminate = true, Margin = new Thickness(0, 10, 0, 6) };
    private readonly TextBlock _step = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 18 };
    private readonly TextBlock _percent = new() { HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Button? _cancel;
    private readonly Action? _onCancel;
    private bool _allowClose;

    public ProgressWindow(string title, string workingText, string? cancelText, Action? onCancel)
    {
        Title = title;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        _onCancel = onCancel;
        _step.Text = workingText;
        _percent.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.TextSecondary);

        var stack = new StackPanel { Margin = new Thickness(20, 18, 20, 12) };
        var heading = new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        stack.Children.Add(heading);
        stack.Children.Add(_bar);
        var line = new DockPanel();
        DockPanel.SetDock(_percent, Dock.Right);
        line.Children.Add(_percent);
        line.Children.Add(_step);
        stack.Children.Add(line);

        var root = new DockPanel();
        if (cancelText is not null && onCancel is not null)
        {
            _cancel = new Button { Content = cancelText, IsCancel = true, MinWidth = 88, HorizontalAlignment = HorizontalAlignment.Right };
            _cancel.Click += (_, _) => RequestCancel();
            var footer = new Border { Padding = new Thickness(20, 10, 20, 12), Child = _cancel };
            footer.SetResourceReference(Border.BackgroundProperty, ThemeKeys.SurfaceAlt);
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);
        }
        root.Children.Add(stack);
        Content = root;
    }

    public void Report(OperationProgress p)
    {
        if (p.Fraction is double f)
        {
            _bar.IsIndeterminate = false;
            _bar.Value = Math.Clamp(f, 0, 1) * 100;
            _percent.Text = $"{Math.Clamp(f, 0, 1) * 100:0}%";
        }
        else
        {
            _bar.IsIndeterminate = true;
            _percent.Text = string.Empty;
        }
        if (!string.IsNullOrEmpty(p.Text)) _step.Text = p.Text;
    }

    private bool _isShowing;

    /// <summary>Zamyka okno po zakończeniu pracy (bezpieczne także przed pokazaniem okna).</summary>
    public void CloseFromCode()
    {
        _allowClose = true;
        if (_isShowing) Close();
    }

    /// <summary>Czy praca zakończyła się, zanim okno zdążyło się pokazać (wtedy nie pokazujemy go wcale).</summary>
    public bool IsCompleted => _allowClose;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _isShowing = true;
        if (_allowClose) Dispatcher.BeginInvoke(Close);
    }

    private void RequestCancel()
    {
        if (_cancel is not null) _cancel.IsEnabled = false;
        // Po zakończeniu pracy źródło anulowania jest już zwolnione – nie ma czego anulować.
        try { _onCancel?.Invoke(); }
        catch (ObjectDisposedException) { }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            RequestCancel();
        }
        base.OnClosing(e);
    }
}
