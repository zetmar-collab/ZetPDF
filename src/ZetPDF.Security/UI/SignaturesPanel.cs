using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;
using ZetPDF.Core.Localization;

namespace ZetPDF.Security.UI;

/// <summary>
/// Panel „Podpisy”: wyniki weryfikacji wszystkich pól podpisu aktywnego dokumentu. Dokument niezmieniony od
/// otwarcia jest weryfikowany na bajtach pliku z dysku (tak, jak go podpisano); zmieniony – na bieżącej wersji.
/// </summary>
internal sealed class SignaturesPanel : UserControl
{
    private readonly IServiceProvider _services;
    private readonly ILocalizationService _loc;
    private readonly IDocumentManager _documents;
    private readonly ISignatureService _signatures;
    private readonly StackPanel _list = new();
    private readonly TextBlock _status;
    private IPdfDocumentSession? _session;
    private CancellationTokenSource? _cts;
    private IReadOnlyList<SignatureValidationResult> _results = Array.Empty<SignatureValidationResult>();

    public SignaturesPanel(IServiceProvider services)
    {
        _services = services;
        _loc = services.GetRequiredService<ILocalizationService>();
        _documents = services.GetRequiredService<IDocumentManager>();
        _signatures = services.GetRequiredService<ISignatureService>();
        this.Themed(BackgroundProperty, ThemeKeys.Surface);

        var validate = Ui.Button(_loc, "Security.Panel.Validate", (_, _) => _ = RefreshAsync());
        validate.Margin = new Thickness(0);
        var sign = Ui.Button(_loc, "Security.Panel.Sign", (_, _) => services.GetService<ICommandRegistry>()?.TryExecute(Core.Commands.CommandIds.Sign.PlaceSignature));
        var top = Ui.Row(validate, sign);
        top.Margin = new Thickness(6);
        _status = Ui.Plain(string.Empty, secondary: true);
        _status.Margin = new Thickness(8, 0, 8, 6);
        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(_status, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(_status);
        dock.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = dock;
        System.Windows.Automation.AutomationProperties.SetName(this, _loc["Security.Panel.Title"]);

        _documents.ActiveDocumentChanged += (_, s) => Track(s);
        _loc.CultureChanged += (_, _) => Render();
        Loaded += (_, _) => Track(_documents.ActiveDocument);
    }

    private void Track(IPdfDocumentSession? session)
    {
        if (_session is not null) _session.Changed -= OnChanged;
        _session = session;
        if (_session is not null) _session.Changed += OnChanged;
        _ = RefreshAsync();
    }

    private void OnChanged(object? sender, DocumentChangedEventArgs e) => _ = RefreshAsync();

    /// <summary>Bajty do weryfikacji: plik z dysku, gdy dokument niezmieniony; inaczej bieżąca wersja (odszyfrowana).</summary>
    public static async Task<(byte[] Pdf, string? Password)> GetValidationSourceAsync(IPdfDocumentSession session, CancellationToken ct)
    {
        if (session.FilePath is { } path && !session.IsModified && File.Exists(path))
            return (await File.ReadAllBytesAsync(path, ct), session.SecurityInfo.Password);
        return (session.Current.GetBytes(), null);
    }

    public async Task RefreshAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var session = _session;
        if (session is null)
        {
            _results = Array.Empty<SignatureValidationResult>();
            Render();
            return;
        }
        _status.Text = _loc["Security.Panel.Validating"];
        try
        {
            var (pdf, pwd) = await GetValidationSourceAsync(session, cts.Token);
            var results = await _signatures.ValidateAsync(pdf, pwd, cts.Token);
            if (cts.IsCancellationRequested) return;
            _results = results;
            Render();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or PdfCorruptException or PdfPasswordRequiredException or FormatException or NotSupportedException)
        {
            if (cts.IsCancellationRequested) return;
            _results = Array.Empty<SignatureValidationResult>();
            Render();
            _status.Text = _loc.Format("Security.Panel.Error", _loc.Describe(ex));
        }
    }

    private void Render()
    {
        _list.Children.Clear();
        _status.Text = _session is null ? _loc["Security.Panel.NoDocument"]
            : _results.Count == 0 ? _loc["Security.Panel.NoSignatures"]
            : _loc.Format("Security.Panel.Count", _results.Count);
        if (_session is { IsModified: true } && _results.Count > 0) _status.Text += Environment.NewLine + _loc["Security.Panel.ModifiedNote"];
        foreach (var r in _results) _list.Children.Add(BuildItem(r));
    }

    public static string StatusKey(SignatureStatus s) => "Security.Status." + s;

    public static string StatusBrush(SignatureStatus s) => s switch
    {
        SignatureStatus.Valid => ThemeKeys.StatusSuccess,
        SignatureStatus.ValidButModifiedAfter or SignatureStatus.UnknownSigner => ThemeKeys.StatusWarning,
        _ => ThemeKeys.StatusError,
    };

    private FrameworkElement BuildItem(SignatureValidationResult r)
    {
        var culture = _loc.CurrentCulture;
        var panel = new StackPanel { Margin = new Thickness(8, 4, 8, 8) };
        var title = Ui.Plain(r.FieldName + (r.SignerName.Length > 0 ? " – " + r.SignerName : string.Empty));
        title.FontWeight = FontWeights.SemiBold;
        var status = Ui.Plain(_loc[StatusKey(r.Status)]);
        status.SetResourceReference(TextBlock.ForegroundProperty, StatusBrush(r.Status));
        panel.Children.Add(title);
        panel.Children.Add(status);
        if (r.SigningTime is { } t) panel.Children.Add(Ui.Plain(_loc.Format("Security.Panel.SignedAt", t.ToLocalTime().ToString("G", culture)), secondary: true));
        if (r.Issuer.Length > 0) panel.Children.Add(Ui.Plain(_loc.Format("Security.Panel.Issuer", r.Issuer), secondary: true));
        if (r.PageIndex >= 0) panel.Children.Add(Ui.Plain(_loc.Format("Security.Panel.Page", (r.PageIndex + 1).ToString(CultureInfo.CurrentCulture)), secondary: true));
        var details = new Expander { Margin = new Thickness(0, 2, 0, 0), Content = Ui.Plain(r.Details, secondary: true) };
        details.Loc(HeaderedContentControl.HeaderProperty, _loc, "Security.Panel.Details");
        panel.Children.Add(details);
        var border = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = panel, Cursor = Cursors.Hand };
        border.SetResourceReference(Border.BackgroundProperty, ThemeKeys.Surface);
        border.SetResourceReference(Border.BorderBrushProperty, ThemeKeys.Border);
        border.ToolTip = _loc["Security.Panel.GoTo"];
        border.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && FindParent<Expander>(d) is not null) return;
            GoTo(r);
        };
        return border;
    }

    private void GoTo(SignatureValidationResult r)
    {
        if (r.PageIndex < 0 || _session is null) return;
        var view = _services.GetService<IViewerService>()?.GetView(_session);
        if (view is null) return;
        view.GoToPage(r.PageIndex);
        if (!r.Rect.IsEmpty) view.ScrollIntoView(r.PageIndex, r.Rect);
    }

    private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        for (var p = d; p is not null; p = p is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(p) : LogicalTreeHelper.GetParent(p))
            if (p is T t) return t;
        return null;
    }
}
