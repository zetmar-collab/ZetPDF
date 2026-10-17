using System.Windows;
using System.Windows.Controls;
using ZetPDF.Core.Contracts;
using CompareOptions = ZetPDF.Core.Contracts.CompareOptions;

namespace ZetPDF.Security.UI;

/// <summary>Źródło dokumentu do porównania: otwarty dokument albo plik.</summary>
internal sealed class CompareSource
{
    public CompareSource(IPdfDocumentSession session) { Session = session; Name = session.DisplayName; }
    public CompareSource(string path) { Path = path; Name = System.IO.Path.GetFileName(path); }
    public IPdfDocumentSession? Session { get; }
    public string? Path { get; }
    public string Name { get; }
    public override string ToString() => Session is not null ? Name : Name + "  (" + Path + ")";
}

/// <summary>Porównaj dokumenty: wybór dokumentu starego i nowego (otwarte lub z pliku) oraz opcji.</summary>
internal sealed class CompareDialog : SecurityDialog
{
    private readonly IDialogService _dialogs;
    private readonly ComboBox _a = new(), _b = new();
    private readonly CheckBox _ignoreCase, _graphics;

    public CompareDialog(ILocalizationService loc, IDialogService dialogs, IDocumentManager documents) : base(loc, "Security.Compare.Title", 560)
    {
        _dialogs = dialogs;
        foreach (var s in documents.Documents)
        {
            _a.Items.Add(new CompareSource(s));
            _b.Items.Add(new CompareSource(s));
        }
        if (documents.ActiveDocument is { } active)
        {
            _a.SelectedItem = _a.Items.Cast<CompareSource>().First(x => x.Session == active);
            _b.SelectedItem = _b.Items.Cast<CompareSource>().FirstOrDefault(x => x.Session != active);
        }
        System.Windows.Automation.AutomationProperties.SetName(_a, loc["Security.Compare.Old"]);
        System.Windows.Automation.AutomationProperties.SetName(_b, loc["Security.Compare.New"]);
        _ignoreCase = Ui.Check(loc, "Security.Compare.IgnoreCase");
        _graphics = Ui.Check(loc, "Security.Compare.Graphics");

        FrameworkElement Picker(ComboBox box)
        {
            var dock = new DockPanel();
            var browse = Ui.Button(loc, "Core.Button.Browse", (_, _) =>
            {
                var files = _dialogs.OpenFiles(loc["Security.Compare.Title"], new[] { FileFilter.Pdf(loc) });
                if (files is not { Count: > 0 }) return;
                var src = new CompareSource(files[0]);
                box.Items.Add(src);
                box.SelectedItem = src;
            });
            DockPanel.SetDock(browse, Dock.Right);
            dock.Children.Add(browse);
            dock.Children.Add(box);
            return dock;
        }

        var body = new StackPanel();
        body.Children.Add(Form((Ui.Text(loc, "Security.Compare.Old"), Picker(_a)), (Ui.Text(loc, "Security.Compare.New"), Picker(_b))));
        body.Children.Add(_ignoreCase);
        body.Children.Add(_graphics);
        body.Children.Add(Ui.Plain(loc["Security.Compare.Note"], secondary: true));
        SetBody(body);
        AddOkCancel("Security.Compare.Run");
    }

    public CompareSource? Old => _a.SelectedItem as CompareSource;
    public CompareSource? New => _b.SelectedItem as CompareSource;
    public CompareOptions Options => new() { IgnoreCase = _ignoreCase.IsChecked == true, IgnoreWhitespace = true, CompareGraphics = _graphics.IsChecked == true };

    protected override bool Validate()
    {
        if (Old is null || New is null) { ShowError(Loc["Security.Compare.Error.Select"]); return false; }
        if (ReferenceEquals(Old, New) || (Old.Session is not null && Old.Session == New.Session)
            || (Old.Path is not null && string.Equals(Old.Path, New.Path, StringComparison.OrdinalIgnoreCase)))
        { ShowError(Loc["Security.Compare.Error.Same"]); return false; }
        return true;
    }
}
