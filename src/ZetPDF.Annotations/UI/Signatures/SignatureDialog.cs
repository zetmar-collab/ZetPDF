using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;

namespace ZetPDF.Annotations.UI.Signatures;

/// <summary>
/// Nowy podpis odręczny: narysuj (mysz/pióro, InkCanvas), wpisz (czcionka pisana) albo z obrazu (PNG/JPG).
/// Wynik: PNG z przezroczystym tłem (<see cref="Png"/>). Kolor atramentu to kolor treści (wybór użytkownika).
/// </summary>
internal sealed class SignatureDialog : Window
{
    private static readonly string[] ScriptFonts = { "Segoe Script", "Ink Free", "Lucida Handwriting", "Segoe Print", "Brush Script MT", "Gabriola" };
    private static readonly (string Key, Color Color)[] Inks = { ("Annot.Sign.InkBlack", Color.FromRgb(0, 0, 0)), ("Annot.Sign.InkBlue", Color.FromRgb(20, 50, 160)) };

    private readonly TabControl _tabs;
    private readonly InkCanvas _ink;
    private readonly TextBox _typed;
    private readonly ComboBox _font;
    private readonly ComboBox _inkColor;
    private readonly TextBlock _typedPreview;
    private readonly Image _imagePreview;
    private readonly IDialogService? _dialogs;
    private readonly ILocalizationService _loc;
    private byte[]? _imageData;

    public SignatureDialog(ILocalizationService loc, IDialogService? dialogs, string defaultName)
    {
        _loc = loc;
        _dialogs = dialogs;
        Ui.SetupDialog(this, loc, "Annot.Sign.Title");
        var root = new StackPanel { Margin = new Thickness(16), Width = 520 };
        var inkRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        inkRow.Children.Add(Ui.Label(loc, "Annot.Sign.Ink"));
        _inkColor = new ComboBox { Width = 140 };
        foreach (var (key, _) in Inks) _inkColor.Items.Add(new ComboBoxItem().Loc(ContentControl.ContentProperty, loc, key));
        _inkColor.SelectedIndex = 0;
        _inkColor.SelectionChanged += (_, _) => ApplyInk();
        inkRow.Children.Add(_inkColor);
        root.Children.Add(inkRow);

        _tabs = new TabControl { Height = 230 };
        // rysuj
        _ink = new InkCanvas { Height = 180, Background = Brushes.Transparent };
        var inkBorder = new Border { Child = _ink, BorderThickness = new Thickness(1) };
        inkBorder.SetResourceReference(Border.BorderBrushProperty, ThemeKeys.BorderStrong);
        inkBorder.SetResourceReference(Border.BackgroundProperty, ThemeKeys.InputBackground);
        var drawPanel = new DockPanel();
        var clear = Ui.Button(loc, "Annot.Sign.Clear", (_, _) => _ink.Strokes.Clear());
        clear.HorizontalAlignment = HorizontalAlignment.Right;
        DockPanel.SetDock(clear, Dock.Bottom);
        drawPanel.Children.Add(clear);
        drawPanel.Children.Add(inkBorder);
        _tabs.Items.Add(new TabItem { Content = drawPanel }.Loc(HeaderedContentControl.HeaderProperty, loc, "Annot.Sign.Draw"));
        // wpisz
        var typePanel = new StackPanel { Margin = new Thickness(4) };
        _typed = new TextBox { Text = defaultName };
        _typed.Tip(loc, "Annot.Sign.Name");
        _font = new ComboBox { Margin = new Thickness(0, 6, 0, 6) };
        var installed = new HashSet<string>(Fonts.SystemFontFamilies.Select(f => f.Source), StringComparer.OrdinalIgnoreCase);
        foreach (var f in ScriptFonts.Where(installed.Contains).DefaultIfEmpty("Segoe UI")) _font.Items.Add(f);
        _font.SelectedIndex = 0;
        _typedPreview = new TextBlock { FontSize = 40, Margin = new Thickness(0, 8, 0, 0) };
        _typed.TextChanged += (_, _) => UpdateTyped();
        _font.SelectionChanged += (_, _) => UpdateTyped();
        typePanel.Children.Add(_typed);
        typePanel.Children.Add(_font);
        typePanel.Children.Add(_typedPreview);
        _tabs.Items.Add(new TabItem { Content = typePanel }.Loc(HeaderedContentControl.HeaderProperty, loc, "Annot.Sign.Type"));
        // obraz
        var imgPanel = new DockPanel { Margin = new Thickness(4) };
        var browse = Ui.Button(loc, "Annot.Sign.Browse", (_, _) => Browse());
        browse.HorizontalAlignment = HorizontalAlignment.Left;
        DockPanel.SetDock(browse, Dock.Top);
        imgPanel.Children.Add(browse);
        _imagePreview = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(0, 8, 0, 0) };
        imgPanel.Children.Add(_imagePreview);
        _tabs.Items.Add(new TabItem { Content = imgPanel }.Loc(HeaderedContentControl.HeaderProperty, loc, "Annot.Sign.Image"));
        root.Children.Add(_tabs);

        root.Children.Add(Ui.ButtonRow(
            Ui.Button(loc, "Annot.Dialog.Ok", (_, _) => Accept(), isDefault: true),
            Ui.Button(loc, "Annot.Dialog.Cancel", (_, _) => DialogResult = false, isCancel: true)));
        Content = root;
        ApplyInk();
        UpdateTyped();
    }

    public byte[]? Png { get; private set; }

    private Color InkColor => Inks[Math.Max(0, _inkColor.SelectedIndex)].Color;

    private void ApplyInk()
    {
        var c = InkColor;
        _ink.DefaultDrawingAttributes = new DrawingAttributes { Color = c, Width = 2.5, Height = 2.5, FitToCurve = true, IgnorePressure = false };
        foreach (var s in _ink.Strokes) s.DrawingAttributes.Color = c;
        UpdateTyped();
    }

    private void UpdateTyped()
    {
        if (_typedPreview is null) return;
        _typedPreview.Text = _typed.Text;
        _typedPreview.FontFamily = new FontFamily(_font.SelectedItem as string ?? "Segoe UI");
        _typedPreview.Foreground = new SolidColorBrush(InkColor);
    }

    private void Browse()
    {
        var filter = new FileFilter(_loc["Annot.Filter.Images"], "*.png", "*.jpg", "*.jpeg");
        var files = _dialogs?.OpenFiles(_loc["Annot.Sign.Browse"], new[] { filter });
        if (files is not { Count: > 0 }) return;
        try
        {
            var data = File.ReadAllBytes(files[0]);
            var bmp = new BitmapImage();
            using (var ms = new MemoryStream(data))
            {
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
            }
            bmp.Freeze();
            _imageData = data;
            _imagePreview.Source = bmp;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException)
        {
            _dialogs?.ShowError(ex);
        }
    }

    private void Accept()
    {
        Png = _tabs.SelectedIndex switch
        {
            0 => _ink.Strokes.Count > 0 ? RenderStrokes(_ink.Strokes) : null,
            1 => string.IsNullOrWhiteSpace(_typed.Text) ? null : RenderText(_typed.Text, _font.SelectedItem as string ?? "Segoe UI", InkColor),
            _ => _imageData is null ? null : NormalizeImage(_imageData),
        };
        if (Png is null) { _dialogs?.ShowMessage(_loc["Annot.Sign.Empty"]); return; }
        DialogResult = true;
    }

    private static byte[] Encode(BitmapSource src)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>Kreski → PNG (3× gęstość, przezroczyste tło, przycięte do obrysu).</summary>
    public static byte[] RenderStrokes(StrokeCollection strokes)
    {
        var bounds = strokes.GetBounds();
        bounds.Inflate(4, 4);
        const double scale = 3;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y));
            strokes.Draw(dc);
        }
        var rtb = new RenderTargetBitmap(Math.Max(1, (int)(bounds.Width * scale)), Math.Max(1, (int)(bounds.Height * scale)), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        return Encode(rtb);
    }

    public static byte[] RenderText(string text, string family, Color color)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(family), 120, new SolidColorBrush(color), 1.0);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) dc.DrawText(ft, new Point(10, 5));
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(ft.WidthIncludingTrailingWhitespace + 20), (int)Math.Ceiling(ft.Height + 10), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        return Encode(rtb);
    }

    /// <summary>Obraz → PNG (białe tło skanu podpisu zamieniane na przezroczyste).</summary>
    public static byte[] NormalizeImage(byte[] data)
    {
        var dec = BitmapDecoder.Create(new MemoryStream(data), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var src = new FormatConvertedBitmap(dec.Frames[0], PixelFormats.Bgra32, null, 0);
        int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        src.CopyPixels(px, stride, 0);
        for (int i = 0; i < px.Length; i += 4)
            if (px[i] > 235 && px[i + 1] > 235 && px[i + 2] > 235) px[i + 3] = 0;
        return Encode(BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride));
    }
}
