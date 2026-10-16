using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ZetPDF.Core.Contracts;

namespace ZetPDF.App.Dialogs;

/// <summary>
/// Właściwości dokumentu (Ctrl+D): edycja tytułu, autora, tematu i słów kluczowych (zapis przez polecenie
/// z Cofnij/Ponów – wynik w <see cref="Result"/>) oraz informacje tylko do odczytu (plik, strony, wersja PDF,
/// daty, aplikacja, zabezpieczenia).
/// </summary>
public partial class DocumentPropertiesWindow : Window
{
    private readonly DocumentMetadata _original;

    public DocumentPropertiesWindow(IPdfDocumentSession session, DocumentMetadata metadata, ILocalizationService loc)
    {
        InitializeComponent();
        _original = metadata;
        TitleBox.Text = metadata.Title;
        AuthorBox.Text = metadata.Author;
        SubjectBox.Text = metadata.Subject;
        KeywordsBox.Text = metadata.Keywords;

        var canEdit = session.Can(DocumentPermissions.ModifyContent);
        foreach (var box in new[] { TitleBox, AuthorBox, SubjectBox, KeywordsBox }) box.IsReadOnly = !canEdit;
        ReadOnlyNote.Visibility = canEdit ? Visibility.Collapsed : Visibility.Visible;

        var culture = loc.CurrentCulture;
        string Date(DateTimeOffset? d) => d is null ? "—" : d.Value.LocalDateTime.ToString("g", culture);
        var size = session.FilePath is { } p && File.Exists(p) ? new FileInfo(p).Length : session.Current.Length;
        var info = new List<(string Key, string Value)>
        {
            ("Shell.Properties.FileName", session.FilePath is null ? session.DisplayName : System.IO.Path.GetFileName(session.FilePath)),
            ("Shell.Properties.Location", session.FilePath is null ? loc["Shell.Properties.NotSaved"] : System.IO.Path.GetDirectoryName(session.FilePath) ?? string.Empty),
            ("Shell.Properties.FileSize", FormatSize(size, culture)),
            ("Shell.Properties.Pages", metadata.PageCount.ToString(culture)),
            ("Shell.Properties.PdfVersion", metadata.PdfVersion),
            ("Shell.Properties.Created", Date(metadata.Created)),
            ("Shell.Properties.Modified", Date(metadata.Modified)),
            ("Shell.Properties.Creator", Empty(metadata.Creator)),
            ("Shell.Properties.Producer", Empty(metadata.Producer)),
            ("Shell.Properties.Security", Security(session.SecurityInfo, loc)),
            ("Shell.Properties.Permissions", Permissions(session.Permissions, loc)),
        };
        for (int i = 0; i < info.Count; i++)
        {
            InfoGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Margin = new Thickness(0, 0, 16, 10), VerticalAlignment = VerticalAlignment.Top };
            label.SetBinding(TextBlock.TextProperty, Shell.Loc.Bind(loc, info[i].Key));
            label.SetResourceReference(TextBlock.ForegroundProperty, Core.Theming.ThemeKeys.TextSecondary);
            var value = new TextBox { Text = info[i].Value, IsReadOnly = true, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, Padding = new Thickness(0), MinHeight = 0, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
            Grid.SetRow(label, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            InfoGrid.Children.Add(label);
            InfoGrid.Children.Add(value);
        }

        OkButton.Click += (_, _) =>
        {
            var updated = metadata with
            {
                Title = TitleBox.Text.Trim(),
                Author = AuthorBox.Text.Trim(),
                Subject = SubjectBox.Text.Trim(),
                Keywords = KeywordsBox.Text.Trim(),
            };
            Result = canEdit && IsChanged(updated) ? updated : null;
            DialogResult = true;
        };
        Loaded += (_, _) => TitleBox.Focus();
    }

    /// <summary>Zmienione metadane do zapisania (null = bez zmian).</summary>
    public DocumentMetadata? Result { get; private set; }

    private bool IsChanged(DocumentMetadata m) =>
        m.Title != _original.Title || m.Author != _original.Author || m.Subject != _original.Subject || m.Keywords != _original.Keywords;

    private static string Empty(string s) => string.IsNullOrWhiteSpace(s) ? "—" : s;

    internal static string FormatSize(long bytes, CultureInfo culture)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1)
        {
            v /= 1024;
            u++;
        }
        return u == 0 ? $"{bytes.ToString(culture)} B" : $"{v.ToString("0.##", culture)} {units[u]} ({bytes.ToString("N0", culture)} B)";
    }

    private static string Security(PdfSecurityInfo s, ILocalizationService loc)
    {
        if (!s.IsEncrypted) return loc["Shell.Properties.Security.None"];
        var method = s.Method switch
        {
            PdfEncryptionMethod.Aes256 => "AES-256",
            PdfEncryptionMethod.Aes128 => "AES-128",
            PdfEncryptionMethod.Rc4_128 => "RC4 128",
            PdfEncryptionMethod.Rc4_40 => "RC4 40",
            _ => "?",
        };
        return loc.Format(s.OpenedWithOwnerPassword ? "Shell.Properties.Security.Owner" : "Shell.Properties.Security.User", method);
    }

    private static string Permissions(DocumentPermissions p, ILocalizationService loc)
    {
        if (p == DocumentPermissions.All) return loc["Shell.Properties.Permissions.All"];
        var list = new List<string>();
        void Add(DocumentPermissions flag, string key)
        {
            if ((p & flag) == flag) list.Add(loc[key]);
        }
        Add(DocumentPermissions.Print, "Shell.Properties.Permission.Print");
        Add(DocumentPermissions.ModifyContent, "Shell.Properties.Permission.Modify");
        Add(DocumentPermissions.CopyContent, "Shell.Properties.Permission.Copy");
        Add(DocumentPermissions.Annotate, "Shell.Properties.Permission.Annotate");
        Add(DocumentPermissions.FillForms, "Shell.Properties.Permission.FillForms");
        Add(DocumentPermissions.AssembleDocument, "Shell.Properties.Permission.Assemble");
        return list.Count == 0 ? loc["Shell.Properties.Permissions.None"] : string.Join(", ", list);
    }
}
