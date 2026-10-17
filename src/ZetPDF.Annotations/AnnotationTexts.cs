using System.Globalization;
using ZetPDF.Annotations.Pdf;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Annotations;

/// <summary>
/// Teksty „danych” wpisywanych do PDF (stemple, temat /Subj) w języku interfejsu oraz autor z Ustawień.
/// Bez usług (testy) – kultura bieżącego wątku i nazwa użytkownika Windows.
/// </summary>
internal sealed class AnnotationTexts : IAnnotationTexts
{
    private readonly ILocalizationService? _loc;
    private readonly ISettingsService? _settings;

    public AnnotationTexts(ILocalizationService? loc = null, ISettingsService? settings = null)
    {
        _loc = loc;
        _settings = settings;
    }

    private CultureInfo Culture => _loc?.CurrentCulture ?? CultureInfo.CurrentUICulture;

    public string DefaultAuthor
    {
        get
        {
            var a = _settings?.Current.AuthorName;
            return string.IsNullOrWhiteSpace(a) ? Environment.UserName : a!;
        }
    }

    public string Get(string key) => AnnotationsModule.Strings.GetString(key, Culture) ?? key;

    public string StampText(StampDefinition stamp) => Get(stamp.TextKey);

    public string KindName(AnnotationKind kind) => Get("Annot.Kind." + kind);

    public string FormatStampDate(DateTimeOffset when) => when.LocalDateTime.ToString("g", Culture);
}
