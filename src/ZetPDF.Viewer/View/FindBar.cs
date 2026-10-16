using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;

namespace ZetPDF.Viewer.View;

/// <summary>
/// Pasek wyszukiwania (Ctrl+F) i stan wyszukiwania widoku: wyszukiwanie w tle z anulowaniem
/// (IRenderDocument.SearchAsync), licznik wyników, podświetlenie wszystkich trafień, przewijanie do bieżącego (F3 / Shift+F3).
/// </summary>
internal sealed class FindBar : Border
{
    private readonly DocumentView _view;
    private readonly ILocalizationService _loc;
    private readonly TextBox _box;
    private readonly TextBlock _count;
    private readonly ToggleButton _matchCase;
    private readonly ToggleButton _wholeWord;
    private readonly DispatcherTimer _debounce;
    private readonly List<TextSearchHit> _hits = new();
    private readonly Dictionary<int, List<int>> _hitsByPage = new();
    private CancellationTokenSource? _cts;
    private bool _searching;
    private string _lastQuery = string.Empty;
    private TextSearchOptions _lastOptions;

    public FindBar(DocumentView view, ILocalizationService loc)
    {
        _view = view;
        _loc = loc;
        Visibility = Visibility.Collapsed;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(0, 6, 24, 0);
        Padding = new Thickness(6, 4, 6, 4);
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(4);
        this.Themed(BackgroundProperty, ThemeKeys.SurfaceRaised).Themed(BorderBrushProperty, ThemeKeys.BorderStrong);

        _box = new TextBox { Width = 220, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
        _box.Tip(loc, "Viewer.Find.Placeholder");
        System.Windows.Automation.AutomationProperties.SetName(_box, loc["Viewer.Find.Placeholder"]);
        _box.TextChanged += (_, _) => Restart(TimeSpan.FromMilliseconds(350));
        _box.PreviewKeyDown += OnBoxKeyDown;

        _count = new TextBlock { MinWidth = 70, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) };
        _count.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.TextSecondary);

        _matchCase = Ui.IconToggle("", loc, "Viewer.Find.MatchCase");
        _matchCase.Content = "Aa";
        _matchCase.ClearValue(Control.FontFamilyProperty);
        _matchCase.Click += (_, _) => Restart(TimeSpan.Zero);
        _wholeWord = Ui.IconToggle("", loc, "Viewer.Find.WholeWord");
        _wholeWord.Content = "\"ab\"";
        _wholeWord.ClearValue(Control.FontFamilyProperty);
        _wholeWord.Click += (_, _) => Restart(TimeSpan.Zero);

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(_box);
        panel.Children.Add(_count);
        panel.Children.Add(Ui.IconButton("", loc, "Viewer.Find.Previous", (_, _) => Move(-1)));
        panel.Children.Add(Ui.IconButton("", loc, "Viewer.Find.Next", (_, _) => Move(+1)));
        panel.Children.Add(_matchCase);
        panel.Children.Add(_wholeWord);
        panel.Children.Add(Ui.IconButton("", loc, "Viewer.Find.Close", (_, _) => Close()));
        Child = panel;

        _debounce = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(350) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); StartSearch(); };
        loc.CultureChanged += (_, _) => UpdateCount();
    }

    public IReadOnlyList<TextSearchHit> Hits => _hits;
    public int CurrentIndex { get; private set; } = -1;
    public bool IsOpen => Visibility == Visibility.Visible;
    public bool IsSearching => _searching;
    public string Query => _box.Text;

    public TextSearchHit? Current => CurrentIndex >= 0 && CurrentIndex < _hits.Count ? _hits[CurrentIndex] : null;

    /// <summary>Indeksy trafień na stronie (do rysowania podświetleń).</summary>
    public IReadOnlyList<int> HitsOnPage(int page) => _hitsByPage.TryGetValue(page, out var l) ? l : (IReadOnlyList<int>)Array.Empty<int>();

    public void Open(string? initialText = null)
    {
        Visibility = Visibility.Visible;
        if (!string.IsNullOrWhiteSpace(initialText) && initialText.Length < 200) _box.Text = initialText.Trim();
        _box.Focus();
        _box.SelectAll();
    }

    public void Close()
    {
        Cancel();
        Visibility = Visibility.Collapsed;
        ClearResults();
        _lastQuery = string.Empty;
        _view.InvalidateOverlays();
        _view.FocusPages();
    }

    /// <summary>Uruchom wyszukiwanie od nowa (np. po przeładowaniu dokumentu).</summary>
    public void Refresh()
    {
        if (!IsOpen || string.IsNullOrEmpty(_box.Text)) { ClearResults(); return; }
        _lastQuery = string.Empty;
        StartSearch();
    }

    /// <summary>Następne/poprzednie trafienie (F3 / Shift+F3). Gdy pasek zamknięty – otwiera go.</summary>
    public void Move(int direction)
    {
        if (!IsOpen || string.IsNullOrEmpty(_box.Text)) { Open(_view.TextSelection?.Text); return; }
        if (QueryChanged()) { StartSearch(); return; }
        if (_hits.Count == 0) return;
        if (CurrentIndex < 0) CurrentIndex = FirstHitFromPage(_view.CurrentPageIndex, direction);
        else CurrentIndex = (CurrentIndex + direction + _hits.Count) % _hits.Count;
        ShowCurrent();
    }

    private int FirstHitFromPage(int page, int direction)
    {
        if (direction > 0)
        {
            int i = _hits.FindIndex(h => h.PageIndex >= page);
            return i < 0 ? 0 : i;
        }
        int j = _hits.FindLastIndex(h => h.PageIndex <= page);
        return j < 0 ? _hits.Count - 1 : j;
    }

    private void ShowCurrent()
    {
        UpdateCount();
        if (Current is { } h)
        {
            var bounds = h.Rects.Count > 0 ? h.Rects.Aggregate((a, b) => a.Union(b)) : default;
            _view.ScrollIntoView(h.PageIndex, bounds);
        }
        _view.InvalidateOverlays();
    }

    private void OnBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Move(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : +1);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private TextSearchOptions Options =>
        (_matchCase.IsChecked == true ? TextSearchOptions.MatchCase : 0) | (_wholeWord.IsChecked == true ? TextSearchOptions.WholeWord : 0);

    private bool QueryChanged() => _box.Text != _lastQuery || Options != _lastOptions;

    private void Restart(TimeSpan delay)
    {
        _debounce.Stop();
        if (delay == TimeSpan.Zero) StartSearch();
        else _debounce.Start();
    }

    private void Cancel()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _searching = false;
    }

    private void ClearResults()
    {
        _hits.Clear();
        _hitsByPage.Clear();
        CurrentIndex = -1;
        UpdateCount();
    }

    private async void StartSearch()
    {
        _debounce.Stop();
        Cancel();
        ClearResults();
        var query = _box.Text;
        _lastQuery = query;
        _lastOptions = Options;
        _view.InvalidateOverlays();
        var doc = _view.RenderDocument;
        if (string.IsNullOrWhiteSpace(query) || doc is null) { UpdateCount(); return; }

        var cts = new CancellationTokenSource();
        _cts = cts;
        _searching = true;
        UpdateCount();
        int startPage = _view.CurrentPageIndex;
        try
        {
            int sinceRefresh = 0;
            await foreach (var hit in doc.SearchAsync(query, _lastOptions, null, cts.Token).WithCancellation(cts.Token))
            {
                // wracamy na wątek UI (kontekst synchronizacji Dispatchera zachowany przez await)
                if (cts.IsCancellationRequested) return;
                AddHit(hit);
                if (CurrentIndex < 0 && hit.PageIndex >= startPage)
                {
                    CurrentIndex = _hits.Count - 1;
                    ShowCurrent();
                }
                if (++sinceRefresh >= 25) { sinceRefresh = 0; UpdateCount(); _view.InvalidateOverlays(); }
            }
            if (CurrentIndex < 0 && _hits.Count > 0)
            {
                CurrentIndex = 0;
                ShowCurrent();
            }
        }
        catch (OperationCanceledException) { return; }
        catch (ObjectDisposedException) { return; }
        finally
        {
            if (ReferenceEquals(_cts, cts)) _searching = false;
        }
        UpdateCount();
        _view.InvalidateOverlays();
    }

    private void AddHit(TextSearchHit hit)
    {
        _hits.Add(hit);
        if (!_hitsByPage.TryGetValue(hit.PageIndex, out var list)) _hitsByPage[hit.PageIndex] = list = new List<int>();
        list.Add(_hits.Count - 1);
    }

    private void UpdateCount()
    {
        if (string.IsNullOrEmpty(_box.Text)) _count.Text = string.Empty;
        else if (_hits.Count == 0) _count.Text = _searching ? _loc["Viewer.Find.Searching"] : _loc["Viewer.Find.NoResults"];
        else _count.Text = _loc.Format("Viewer.Find.Count", CurrentIndex + 1, _hits.Count) + (_searching ? "…" : string.Empty);
    }
}
