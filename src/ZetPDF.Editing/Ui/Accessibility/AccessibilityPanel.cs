using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;
using ZetPDF.Editing.Accessibility;

namespace ZetPDF.Editing.Ui.Accessibility;

/// <summary>
/// Panel boczny „Sprawdzanie ułatwień dostępu”: lista reguł ze statusem Zaliczone/Niezaliczone/Ręczne,
/// przyciski Sprawdź, Napraw, Napraw wszystko, Wyjaśnij, Raport. Śledzi aktywny dokument.
/// </summary>
internal sealed class AccessibilityPanel : UserControl
{
    private readonly EditingContext _ctx;
    private readonly AccessibilityController _controller;
    private readonly ListBox _list;
    private readonly TextBlock _summary;
    private readonly Button _fix;
    private readonly Button _fixAll;
    private readonly Button _explain;
    private readonly Button _report;
    private readonly Button _check;
    private bool _busy;

    public AccessibilityPanel(EditingContext ctx, AccessibilityController controller)
    {
        _ctx = ctx;
        _controller = controller;
        var loc = ctx.Loc;
        this.Themed(BackgroundProperty, ThemeKeys.Surface);

        _check = Small(loc, "Edit.Access.Panel.Check", async s => await _controller.CheckAsync(s));
        _fixAll = Small(loc, "Edit.Access.Panel.FixAll", _controller.FixAllAsync);
        _report = Small(loc, "Edit.Access.Panel.Report", _controller.ReportAsync);
        _fix = Small(loc, "Edit.Access.Panel.Fix", s => Selected is { } r ? _controller.FixAsync(s, new[] { r.Rule }) : Task.CompletedTask);
        _explain = Small(loc, "Edit.Access.Panel.Explain", _ => { if (Selected is { } r) _controller.Explain(r); return Task.CompletedTask; });

        var top = new WrapPanel { Margin = new Thickness(6, 6, 6, 2) };
        top.Children.Add(_check);
        top.Children.Add(_fixAll);
        top.Children.Add(_report);
        _summary = new TextBlock { Margin = new Thickness(8, 4, 8, 4), TextWrapping = TextWrapping.Wrap };
        _summary.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.TextSecondary);
        _list = new ListBox { Margin = new Thickness(6, 2, 6, 2), BorderThickness = new Thickness(0) };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        System.Windows.Automation.AutomationProperties.SetName(_list, loc["Edit.Access.Panel.Title"]);
        _list.SelectionChanged += (_, _) => UpdateButtons();
        _list.MouseDoubleClick += (_, _) => { if (Selected is { } r) _controller.Explain(r); };
        _list.KeyDown += (_, e) => { if (e.Key == Key.Enter && Selected is { } r) { _controller.Explain(r); e.Handled = true; } };
        var bottom = new WrapPanel { Margin = new Thickness(6, 2, 6, 6) };
        bottom.Children.Add(_fix);
        bottom.Children.Add(_explain);

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(_summary, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(_summary);
        root.Children.Add(bottom);
        root.Children.Add(_list);
        Content = root;

        ctx.Documents.ActiveDocumentChanged += (_, _) => Refresh();
        controller.ReportChanged += (_, s) => { if (ReferenceEquals(s, ctx.Documents.ActiveDocument)) Refresh(); };
        loc.CultureChanged += (_, _) => Refresh();
        Refresh();
    }

    private AccessCheckResult? Selected => (_list.SelectedItem as ListBoxItem)?.Tag as AccessCheckResult;

    private Button Small(ILocalizationService loc, string key, Func<IPdfDocumentSession, Task> run)
    {
        var b = Ui.Button(loc, key, async (_, _) =>
        {
            if (_busy || _ctx.Documents.ActiveDocument is not { } s) return;
            _busy = true;
            UpdateButtons();
            try { await run(s); }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is not OutOfMemoryException) { _ctx.Dialogs?.ShowError(ex); }
            finally { _busy = false; UpdateButtons(); }
        });
        b.MinWidth = 0;
        b.Margin = new Thickness(0, 0, 6, 4);
        b.Padding = new Thickness(8, 2, 8, 2);
        return b;
    }

    private void Refresh()
    {
        var keep = Selected?.Rule;
        _list.Items.Clear();
        var report = _controller.GetReport(_ctx.Documents.ActiveDocument);
        var loc = _ctx.Loc;
        if (report is null)
        {
            _summary.Text = loc[_ctx.Documents.ActiveDocument is null ? "Edit.Access.Panel.NoDocument" : "Edit.Access.Panel.Hint"];
        }
        else
        {
            _summary.Text = loc.Format("Edit.Access.Summary", report.Passed, report.Failed, report.Manual);
            foreach (var r in report.Results)
            {
                var item = new ListBoxItem { Tag = r, Content = Row(r), HorizontalContentAlignment = HorizontalAlignment.Stretch };
                System.Windows.Automation.AutomationProperties.SetName(item, loc[r.NameKey] + ": " + loc["Edit.Access.Status." + r.Status]);
                _list.Items.Add(item);
                if (r.Rule == keep) _list.SelectedItem = item;
            }
        }
        UpdateButtons();
    }

    private FrameworkElement Row(AccessCheckResult r)
    {
        var loc = _ctx.Loc;
        var icon = new TextBlock
        {
            Text = r.Status switch { AccessCheckStatus.Passed => "", AccessCheckStatus.Failed => "", _ => "" },
            Width = 20,
            Margin = new Thickness(0, 2, 6, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, ThemeKeys.FontIcons);
        icon.SetResourceReference(TextBlock.ForegroundProperty, r.Status switch
        {
            AccessCheckStatus.Passed => ThemeKeys.StatusSuccess,
            AccessCheckStatus.Failed => ThemeKeys.StatusError,
            _ => ThemeKeys.StatusWarning,
        });
        var name = Ui.Text(loc[r.NameKey] + " – " + loc["Edit.Access.Status." + r.Status]);
        name.FontWeight = FontWeights.SemiBold;
        name.Margin = new Thickness(0);
        var detail = Ui.Text(_controller.Detail(r), secondary: true);
        detail.Margin = new Thickness(0, 0, 0, 2);
        var text = new StackPanel();
        text.Children.Add(name);
        text.Children.Add(detail);
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(text);
        return row;
    }

    private void UpdateButtons()
    {
        var session = _ctx.Documents.ActiveDocument;
        var report = _controller.GetReport(session);
        bool canModify = session?.Can(DocumentPermissions.ModifyContent) == true;
        _check.IsEnabled = !_busy && session is not null;
        _report.IsEnabled = !_busy && session is not null;
        _fixAll.IsEnabled = !_busy && canModify && report?.Results.Any(r => r.CanFix) == true;
        _fix.IsEnabled = !_busy && canModify && Selected is { CanFix: true };
        _explain.IsEnabled = Selected is not null;
    }
}
