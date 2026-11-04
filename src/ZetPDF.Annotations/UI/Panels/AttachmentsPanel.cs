using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;

namespace ZetPDF.Annotations.UI.Panels;

/// <summary>Panel Załączniki: pliki osadzone i adnotacje z plikiem – zapisz na dysk, dodaj, usuń.</summary>
internal sealed class AttachmentsPanel : UserControl
{
    private readonly AnnotContext _ctx;
    private readonly ILocalizationService _loc;
    private readonly ListView _list;
    private readonly TextBlock _empty;
    private IPdfDocumentSession? _session;
    private int _version;

    public AttachmentsPanel(AnnotContext ctx)
    {
        _ctx = ctx;
        _loc = ctx.Loc;
        var root = new DockPanel();
        var bar = new WrapPanel { Margin = new Thickness(4) };
        bar.Children.Add(Ui.IconButton("", _loc, "Annot.Attach.Add", (_, _) => _ = AddAsync()));
        bar.Children.Add(Ui.IconButton("", _loc, "Annot.Attach.Save", (_, _) => _ = SaveAsync()));
        bar.Children.Add(Ui.IconButton("", _loc, "Annot.Attach.Remove", (_, _) => _ = RemoveAsync()));
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        _list = new ListView { BorderThickness = new Thickness(0) };
        System.Windows.Automation.AutomationProperties.SetName(_list, _loc["Annot.Attach.Title"]);
        _list.MouseDoubleClick += (_, _) => _ = SaveAsync();
        _empty = new TextBlock { Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center };
        _empty.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.TextSecondary);
        _empty.Loc(TextBlock.TextProperty, _loc, "Annot.Attach.Empty");
        var host = new Grid();
        host.Children.Add(_list);
        host.Children.Add(_empty);
        root.Children.Add(host);
        Content = root;
        this.Themed(BackgroundProperty, ThemeKeys.Surface);
        _ctx.Documents.ActiveDocumentChanged += (_, d) => Track(d);
        _loc.CultureChanged += (_, _) => _ = RefreshAsync();
        Track(_ctx.Documents.ActiveDocument);
    }

    private bool _refreshPending;

    /// <summary>Ukryty panel nie parsuje dokumentu po każdej edycji; odświeża się, gdy znów będzie widoczny.</summary>
    private void RequestRefresh()
    {
        if (IsLoaded && !IsVisible) { _refreshPending = true; return; }
        _ = RefreshAsync();
    }

    private void OnPanelVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible || !_refreshPending) return;
        _refreshPending = false;
        _ = RefreshAsync();
    }

    private void Track(IPdfDocumentSession? s)
    {
        IsVisibleChanged -= OnPanelVisibilityChanged;
        IsVisibleChanged += OnPanelVisibilityChanged;
        if (_session is not null) _session.Changed -= OnChanged;
        _session = s;
        if (s is not null) s.Changed += OnChanged;
        _ = RefreshAsync();
    }

    private void OnChanged(object? sender, DocumentChangedEventArgs e)
    {
        if ((e.Kind & (DocumentChangeKind.Attachments | DocumentChangeKind.Annotations)) != 0 || e.Source != DocumentChangeSource.Command) RequestRefresh();
    }

    private async Task RefreshAsync()
    {
        int v = ++_version;
        IReadOnlyList<AttachmentInfo> list = Array.Empty<AttachmentInfo>();
        if (_session is { } s)
        {
            try { list = await _ctx.Attachments.GetAttachmentsAsync(s.Current.GetSharedBytes()); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
        }
        if (v != _version) return;
        _list.Items.Clear();
        foreach (var a in list)
        {
            var panel = new StackPanel { Margin = new Thickness(2) };
            var name = new TextBlock { Text = a.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            name.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.TextPrimary);
            var meta = new List<string> { FormatSize(a.Size) };
            if (a.Modified is { } m) meta.Add(m.LocalDateTime.ToString("g", _loc.CurrentCulture));
            if (a.PageIndex is { } p) meta.Add(_loc.Format("Annot.Attach.OnPage", p + 1));
            var info = new TextBlock { Text = string.Join(" · ", meta) };
            info.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.TextSecondary);
            panel.Children.Add(name);
            panel.Children.Add(info);
            if (a.Description.Length > 0)
            {
                var d = new TextBlock { Text = a.Description, TextWrapping = TextWrapping.Wrap };
                d.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.TextSecondary);
                panel.Children.Add(d);
            }
            _list.Items.Add(new ListViewItem { Content = panel, Tag = a });
        }
        _empty.Visibility = _list.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private string FormatSize(long size) => size switch
    {
        < 1024 => _loc.Format("Annot.Attach.Bytes", size),
        < 1024 * 1024 => _loc.Format("Annot.Attach.KB", (size / 1024.0).ToString("0.#", _loc.CurrentCulture)),
        _ => _loc.Format("Annot.Attach.MB", (size / 1048576.0).ToString("0.#", _loc.CurrentCulture)),
    };

    private AttachmentInfo? Selected => (_list.SelectedItem as ListViewItem)?.Tag as AttachmentInfo;

    internal int AttachmentCount => _list.Items.Count;

    internal void SelectAttachment(int index) => _list.SelectedIndex = index;

    public async Task AddAsync()
    {
        if (_session is not { } s || _ctx.Dialogs is not { } dialogs) return;
        var files = dialogs.OpenFiles(_loc["Annot.Attach.Add"], new[] { FileFilter.All(_loc) }, multiselect: true);
        if (files is not { Count: > 0 }) return;
        await _ctx.RunAsync(s, "Annot.Undo.AttachAdd", async (pdf, ct) =>
        {
            foreach (var f in files) pdf = await _ctx.Attachments.AddAsync(pdf, f, string.Empty, ct);
            return pdf;
        }, DocumentChangeKind.Attachments, DocumentPermissions.ModifyContent);
    }

    internal async Task SaveAsync()
    {
        if (_session is not { } s || Selected is not { } a || _ctx.Dialogs is not { } dialogs) return;
        if (_ctx.Services.GetService(typeof(IFileService)) is not IFileService files) return;
        var target = AttachmentSaveGuard.ChooseTarget(a.Name, dialogs, files, _loc);
        if (target is null) return;
        try
        {
            var data = await _ctx.Attachments.ExtractAsync(s.Current.GetSharedBytes(), a.Name);
            await files.WriteAllBytesSafeAsync(target, data);
            _ctx.Shell?.SetStatus(_loc.Format("Annot.Attach.Saved", Path.GetFileName(target)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { dialogs.ShowError(ex); }
    }

    private async Task RemoveAsync()
    {
        if (_session is not { } s || Selected is not { } a) return;
        if (_ctx.Dialogs is { } d && !d.Confirm(_loc.Format("Annot.Attach.RemoveConfirm", a.Name))) return;
        await _ctx.RunAsync(s, "Annot.Undo.AttachRemove", (pdf, ct) => _ctx.Attachments.RemoveAsync(pdf, a.Name, ct), DocumentChangeKind.Attachments | DocumentChangeKind.Annotations, DocumentPermissions.ModifyContent);
    }
}
