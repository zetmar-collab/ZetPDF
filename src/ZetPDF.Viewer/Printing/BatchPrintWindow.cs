using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;

namespace ZetPDF.Viewer.Printing;

/// <summary>Okno druku wsadowego: lista plików z kolejnością, ustawienia z okna druku. Wynik: <see cref="Files"/> i <see cref="Options"/>.</summary>
internal sealed class BatchPrintWindow : Window
{
    /// <summary>Limit plików dodawanych z folderu (pojedyncze wybrane pliki dodaje użytkownik świadomie).</summary>
    public const int MaxFolderFiles = 10_000;

    private readonly ILocalizationService _loc;
    private readonly IDialogService? _dialogs;
    private readonly ObservableCollection<string> _files = new();
    private readonly ListBox _list = new() { Height = 300 };
    private readonly PrintSettingsPanel _settings;
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

    public BatchPrintWindow(ILocalizationService loc, IDialogService? dialogs)
    {
        _loc = loc;
        _dialogs = dialogs;
        this.Loc(TitleProperty, loc, "Viewer.Batch.Title");
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        this.Themed(BackgroundProperty, ThemeKeys.SurfaceRaised).Themed(ForegroundProperty, ThemeKeys.TextPrimary);
        _error.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.StatusError);

        var (printers, def) = PrintService.GetPrinters();
        _settings = new PrintSettingsPanel(loc, printers, def, "batch") { Width = 330, Margin = new Thickness(16, 0, 0, 0) };
        _list.ItemsSource = _files;
        System.Windows.Automation.AutomationProperties.SetName(_list, loc["Viewer.Batch.Files"]);

        var side = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
        foreach (var (key, action) in new (string, Action)[]
        {
            ("Viewer.Batch.AddFiles", AddFiles), ("Viewer.Batch.AddFolder", AddFolder), ("Viewer.Batch.Up", () => Move(-1)), ("Viewer.Batch.Down", () => Move(1)), ("Viewer.Batch.Remove", Remove),
        })
        {
            var b = new Button { MinWidth = 110, Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(8, 3, 8, 3) }.Loc(ContentControl.ContentProperty, loc, key);
            b.Click += (_, _) => action();
            side.Children.Add(b);
        }
        var listRow = new DockPanel { Width = 480 };
        DockPanel.SetDock(side, Dock.Right);
        listRow.Children.Add(side);
        listRow.Children.Add(_list);

        var left = new StackPanel();
        left.Children.Add(_settings.Section("Viewer.Batch.Files", listRow));
        left.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 480, Margin = new Thickness(0, 6, 0, 0) }.Loc(TextBlock.TextProperty, loc, "Viewer.Batch.Info"));
        left.Children.Add(_error);

        var ok = new Button { MinWidth = 90, IsDefault = true, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 3, 10, 3) }.Loc(ContentControl.ContentProperty, loc, "Viewer.Print.Ok");
        var cancel = new Button { MinWidth = 90, IsCancel = true, Padding = new Thickness(10, 3, 10, 3) }.Loc(ContentControl.ContentProperty, loc, "Viewer.Dialog.Cancel");
        ok.Click += (_, _) => Accept();
        if (!_settings.HasPrinters)
        {
            ok.IsEnabled = false;
            _error.Text = loc["Viewer.Print.NoPrinters"];
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var body = new StackPanel { Orientation = Orientation.Horizontal };
        body.Children.Add(left);
        body.Children.Add(_settings);
        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(body);
        root.Children.Add(buttons);
        Content = root;
    }

    public IReadOnlyList<string> Files => _files.ToList();
    public PrintOptions? Options { get; private set; }

    internal void AddPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
            if (!_files.Contains(path, StringComparer.OrdinalIgnoreCase)) _files.Add(path);
    }

    private void AddFiles()
    {
        var picked = _dialogs?.OpenFiles(_loc["Viewer.Batch.AddFiles"], new[] { new FileFilter(_loc["Viewer.Batch.PdfFilter"], "*.pdf") }, multiselect: true);
        if (picked is not null) AddPaths(picked);
    }

    private void AddFolder()
    {
        var folder = _dialogs?.SelectFolder(_loc["Viewer.Batch.AddFolder"]);
        if (folder is null) return;
        try
        {
            AddPaths(Directory.EnumerateFiles(folder, "*.pdf").Take(MaxFolderFiles).OrderBy(p => p, StringComparer.CurrentCultureIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _error.Text = ex.Message;
        }
    }

    internal void Move(int delta)
    {
        int i = _list.SelectedIndex, j = i + delta;
        if (i < 0 || j < 0 || j >= _files.Count) return;
        _files.Move(i, j);
        _list.SelectedIndex = j;
    }

    private void Remove()
    {
        int i = _list.SelectedIndex;
        if (i < 0) return;
        _files.RemoveAt(i);
        _list.SelectedIndex = Math.Min(i, _files.Count - 1);
    }

    private void Accept()
    {
        if (_files.Count == 0)
        {
            _error.Text = _loc["Viewer.Batch.NoFiles"];
            return;
        }
        if (!_settings.TryBuild(string.Empty, out var options, out var error))
        {
            _error.Text = error ?? string.Empty;
            return;
        }
        Options = options;
        DialogResult = true;
    }
}

/// <summary>Raport druku wsadowego: plik i wynik.</summary>
internal sealed class BatchPrintReportWindow : Window
{
    public BatchPrintReportWindow(ILocalizationService loc, IReadOnlyList<BatchPrintEntry> entries)
    {
        this.Loc(TitleProperty, loc, "Viewer.Batch.Report.Title");
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        this.Themed(BackgroundProperty, ThemeKeys.SurfaceRaised).Themed(ForegroundProperty, ThemeKeys.TextPrimary);
        var root = new StackPanel { Margin = new Thickness(16), Width = 620 };
        int printed = entries.Count(e => e.Status == BatchPrintStatus.Printed);
        root.Children.Add(new TextBlock { Text = loc.Format("Viewer.Batch.Report.Summary", printed, entries.Count), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        var list = new ListBox { Height = 320 };
        foreach (var line in entries.Select(e => Describe(loc, e))) list.Items.Add(line);
        System.Windows.Automation.AutomationProperties.SetName(list, loc["Viewer.Batch.Report.Title"]);
        root.Children.Add(list);
        var ok = new Button { MinWidth = 90, IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(10, 3, 10, 3) }.Loc(ContentControl.ContentProperty, loc, "Viewer.Dialog.Ok");
        root.Children.Add(ok);
        Content = root;
    }

    /// <summary>Jeden wiersz raportu. Szczegół techniczny tylko przy niepowodzeniu; nie zawiera hasła.</summary>
    public static string Describe(ILocalizationService loc, BatchPrintEntry e)
    {
        string status = loc["Viewer.Batch.Status." + e.Status];
        return e.Status is BatchPrintStatus.Failed or BatchPrintStatus.SkippedUnreadable && !string.IsNullOrEmpty(e.Detail)
            ? $"{Path.GetFileName(e.Path)}: {status} ({e.Detail})"
            : $"{Path.GetFileName(e.Path)}: {status}";
    }
}
