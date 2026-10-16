using ZetPDF.Core.Contracts;

namespace ZetPDF.App;

/// <summary>
/// Lista WSZYSTKICH modułów – wpisana raz w szkielecie. Agenci modułów jej nie edytują
/// (wypełniają swoje klasy *Module). Kolejność = kolejność Initialize (renderer i widok najpierw).
/// </summary>
public static class ModuleCatalog
{
    public static IReadOnlyList<IFeatureModule> CreateAll() => new IFeatureModule[]
    {
        new ZetPDF.Rendering.RenderingModule(),
        new ZetPDF.Viewer.ViewerModule(),
        new ZetPDF.Pages.PagesModule(),
        new ZetPDF.Annotations.AnnotationsModule(),
        new ZetPDF.Ocr.OcrModule(),
        new ZetPDF.Editing.EditingModule(),
        new ZetPDF.Security.SecurityModule(),
    };
}
