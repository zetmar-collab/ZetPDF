using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ZetPDF.Annotations.Forms.Recognition;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;

namespace ZetPDF.Annotations.UI.Dialogs;

/// <summary>Podgląd propozycji pól: lista z zaznaczaniem i edycją nazw, obok strona z zaznaczonymi prostokątami.</summary>
internal sealed class RecognizeFieldsDialog : Window
{
    /// <summary>Wiersz listy.</summary>
    public sealed class Row : INotifyPropertyChanged
    {
        private bool _create = true;
        private string _name;

        public Row(FieldProposal proposal, string kindText)
        {
            Proposal = proposal;
            KindText = kindText;
            _name = proposal.Name;
        }

        public FieldProposal Proposal { get; }
        public string KindText { get; }
        public int PageNumber => Proposal.PageIndex + 1;
        public string Label => Proposal.Label;

        public bool Create
        {
            get => _create;
            set
            {
                if (_create == value) return;
                _create = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Create)));
            }
        }

        public string Name
        {
            get => _name;
            set
            {
                _name = value ?? string.Empty;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly ILocalizationService _loc;
    private readonly IRenderDocument _document;
    private readonly IReadOnlyCollection<string> _existingNames;
    private readonly List<Row> _rows;
    private readonly DataGrid _grid;
    private readonly Image _image = new() { Stretch = Stretch.None };
    private readonly Canvas _overlay = new();
    private int _shownPage = -1;
    private double _shownScale = 1;
    private CancellationTokenSource? _renderCts;

    public RecognizeFieldsDialog(ILocalizationService loc, IReadOnlyList<FieldProposal> proposals, IRenderDocument document, IReadOnlyCollection<string> existingNames)
    {
        _existingNames = existingNames;
        _loc = loc;
        _document = document;
        Ui.SetupDialog(this, loc, "Annot.Recognize.Title");
        _rows = proposals.Select(p => new Row(p, loc[p.Kind == FormFieldKind.CheckBox ? "Annot.Recognize.Kind.CheckBox" : "Annot.Recognize.Kind.Text"])).ToList();

        var root = new DockPanel { Margin = new Thickness(16), Width = 1000, Height = 640 };
        var info = new TextBlock { Text = loc.Format("Annot.Recognize.Info", _rows.Count), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(info, Dock.Top);
        root.Children.Add(info);

        var buttons = Ui.ButtonRow(
            Ui.Button(loc, "Annot.Recognize.SelectAll", (_, _) => SetAll(true)),
            Ui.Button(loc, "Annot.Recognize.SelectNone", (_, _) => SetAll(false)),
            Ui.Button(loc, "Annot.Recognize.Create", (_, _) => { _grid!.CommitEdit(DataGridEditingUnit.Row, true); DialogResult = true; }, isDefault: true),
            Ui.Button(loc, "Annot.Dialog.Cancel", (_, _) => DialogResult = false, isCancel: true));
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        _grid = new DataGrid
        {
            ItemsSource = _rows,
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            SelectionMode = DataGridSelectionMode.Single,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            Width = 520,
            Margin = new Thickness(0, 0, 12, 0),
        };
        System.Windows.Automation.AutomationProperties.SetName(_grid, loc["Annot.Recognize.Title"]);
        _grid.Columns.Add(new DataGridCheckBoxColumn { Header = loc["Annot.Recognize.Column.Create"], Binding = new Binding(nameof(Row.Create)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged } });
        _grid.Columns.Add(new DataGridTextColumn { Header = loc["Annot.Recognize.Column.Name"], Binding = new Binding(nameof(Row.Name)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = loc["Annot.Recognize.Column.Kind"], Binding = new Binding(nameof(Row.KindText)), IsReadOnly = true });
        _grid.Columns.Add(new DataGridTextColumn { Header = loc["Annot.Recognize.Column.Page"], Binding = new Binding(nameof(Row.PageNumber)), IsReadOnly = true });
        _grid.Columns.Add(new DataGridTextColumn { Header = loc["Annot.Recognize.Column.Label"], Binding = new Binding(nameof(Row.Label)), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.SelectionChanged += (_, _) => ShowSelected();
        DockPanel.SetDock(_grid, Dock.Left);
        root.Children.Add(_grid);

        var surface = new Grid();
        surface.Children.Add(_image);
        surface.Children.Add(_overlay);
        var scroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = surface };
        scroll.SetResourceReference(BackgroundProperty, ThemeKeys.ViewerBackground);
        root.Children.Add(scroll);
        Content = root;

        Loaded += (_, _) => { if (_rows.Count > 0) _grid.SelectedIndex = 0; };
        Closed += (_, _) => { _renderCts?.Cancel(); _renderCts?.Dispose(); };
    }

    private void SetAll(bool value)
    {
        foreach (var row in _rows) row.Create = value;
        DrawOverlay();
    }

    /// <summary>Wybrane pola po oczyszczeniu nazw wpisanych w oknie. Nazwy powtórzone dostają numer.</summary>
    public IReadOnlyList<FormFieldSpec> SelectedSpecs()
    {
        // Nazwy niezmienione są już unikalne względem dokumentu; zmienione nie mogą się powtórzyć ani z nimi, ani z polami dokumentu.
        var used = FieldNames.UsedFrom(_existingNames);
        foreach (var row in _rows.Where(r => r.Create && r.Name == r.Proposal.Name)) used.Add(row.Name);
        var result = new List<FormFieldSpec>();
        foreach (var row in _rows.Where(r => r.Create))
        {
            var p = row.Proposal;
            string name = row.Name == p.Name ? p.Name
                : FieldNames.Unique(row.Name, _loc[p.Kind == FormFieldKind.CheckBox ? "Annot.Recognize.GenericCheckBox" : "Annot.Recognize.GenericText"], used);
            result.Add(new FormFieldSpec
            {
                Kind = p.Kind,
                Name = name,
                PageIndex = p.PageIndex,
                Rect = p.Rect,
                Tooltip = p.Label,
                Multiline = p.Multiline,
            });
        }
        return result;
    }

    private async void ShowSelected()
    {
        if (_grid.SelectedItem is not Row row) return;
        DrawOverlay();
        int page = row.Proposal.PageIndex;
        if (page == _shownPage) return;
        _renderCts?.Cancel();
        _renderCts?.Dispose();
        var cts = _renderCts = new CancellationTokenSource();
        try
        {
            double scale = Math.Min(1.0, 900.0 / Math.Max(_document.Pages[page].DisplaySize.Width, 1));
            var bmp = await _document.RenderPageAsync(page, new RenderRequest(scale, Flags: RenderFlags.None), cts.Token);
            if (cts.IsCancellationRequested) return;
            _image.Source = BitmapSource.Create(bmp.Width, bmp.Height, 96, 96, PixelFormats.Pbgra32, null, bmp.Bgra, bmp.Stride);
            _shownPage = page;
            _shownScale = scale;
            DrawOverlay();
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private void DrawOverlay()
    {
        _overlay.Children.Clear();
        if (_shownPage < 0 || _grid.SelectedItem is not Row current) return;
        var info = _document.Pages[_shownPage];
        var transform = new PageTransform(info.Size, info.CropOrigin, info.Rotation, _shownScale, new Point(0, 0));
        foreach (var row in _rows.Where(r => r.Proposal.PageIndex == _shownPage && (r.Create || ReferenceEquals(r, current))))
        {
            var box = transform.ToView(row.Proposal.Rect);
            var shape = new Rectangle { Width = box.Width, Height = box.Height, StrokeThickness = ReferenceEquals(row, current) ? 2.5 : 1 };
            shape.SetResourceReference(Shape.StrokeProperty, ThemeKeys.ViewerOverlayOutline);
            Canvas.SetLeft(shape, box.Left);
            Canvas.SetTop(shape, box.Top);
            _overlay.Children.Add(shape);
        }
    }
}
