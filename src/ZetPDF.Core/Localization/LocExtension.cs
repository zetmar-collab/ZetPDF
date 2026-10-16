using System.Windows.Data;
using System.Windows.Markup;

namespace ZetPDF.Core.Localization;

/// <summary>
/// Tekst zlokalizowany w XAML z odświeżaniem na żywo:
///   xmlns:loc="clr-namespace:ZetPDF.Core.Localization;assembly=ZetPDF.Core"
///   &lt;Button Content="{loc:Loc Pages.Dialog.Ok}" /&gt;
/// Zwraca Binding do LocalizationService.Current["klucz"] (PropertyChanged "Item[]" przy zmianie języka).
/// Opcjonalnie Format, np. {loc:Loc Pages.Count, Format='{}{0}:'}.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension() { }

    public LocExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    /// <summary>Opcjonalny StringFormat.</summary>
    public string? Format { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var source = LocalizationService.Current;
        if (source is null) return "[" + Key + "]"; // projektant XAML / brak usługi
        var binding = new Binding($"[{Key}]") { Source = source, Mode = BindingMode.OneWay };
        if (!string.IsNullOrEmpty(Format)) binding.StringFormat = Format;
        return binding.ProvideValue(serviceProvider);
    }
}
