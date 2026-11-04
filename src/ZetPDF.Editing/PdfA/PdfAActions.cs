using PdfSharp.Pdf;
using ZetPDF.Editing.Pdf;

namespace ZetPDF.Editing.PdfA;

/// <summary>
/// Reguły akcji wspólne dla sprawdzania i konwersji. Akcja to słownik wskazany z miejsca na akcję (/A, /OpenAction, /PA, wpisy /AA, łańcuch
/// /Next wychodzący od takiego słownika), a nie słownik o określonych kluczach. Decyzja przez listę dozwolonych: brak /S lub nieznane /S to akcja zabroniona.
/// Zawartość słownika nie zwalnia go z niczego: /P, /K, /Type kontroluje autor pliku.
/// Lista wg ISO 19005-2 6.5.1 (z pamięci reguł; norma nie była dostępna): zabronione Launch, Sound, Movie, ResetForm, ImportData, Hide, SetOCGState,
/// Rendition, Trans, GoTo3DView, JavaScript, przestarzałe set-state i no-op, akcje nazwane inne niż nawigacja po stronach.
/// Wg 6.4.1 pole i widżet nie mogą mieć /A ani /AA; wg 6.5.2 katalog i strona nie mogą mieć /AA. Konserwatywnie /AA znika wszędzie.
/// </summary>
internal static class PdfAActions
{
    public static readonly HashSet<string> AllowedTypes = new() { "/GoTo", "/GoToR", "/GoToE", "/Thread", "/URI", "/SubmitForm", "/Named" };
    public static readonly HashSet<string> AllowedNamed = new() { "NextPage", "PrevPage", "FirstPage", "LastPage" };
    public static readonly string[] PlaceKeys = { "/A", "/OpenAction", "/PA" };

    public static bool IsAllowed(PdfDictionary action)
    {
        var s = action.Elements.GetName("/S");
        if (!AllowedTypes.Contains(s)) return false;
        return s != "/Named" || AllowedNamed.Contains(action.Elements.GetName("/N").TrimStart('/'));
    }

    /// <summary>
    /// /A elementu struktury to obiekt atrybutów, a nie akcja: wyróżnia go /O przy braku /S. Bez /S nie ma też czego wykonać,
    /// a słownik z /S zawsze liczy się jak akcja, więc dopisanie /O niczego nie ukryje.
    /// </summary>
    public static bool IsStructureAttribute(PdfDictionary d) => !d.Elements.ContainsKey("/S") && d.Elements.ContainsKey("/O");

    /// <summary>Słowniki akcji wskazane z danego klucza. /OpenAction jako tablica to miejsce docelowe, a nie akcje.</summary>
    public static IEnumerable<PdfDictionary> ActionsAt(PdfDictionary owner, string key)
    {
        switch (PdfObjects.Resolve(owner.Elements[key]))
        {
            case PdfDictionary a when key != "/A" || !IsStructureAttribute(a):
                yield return a;
                break;
            case PdfArray list when key != "/OpenAction":
                foreach (var e in list.Elements)
                    if (PdfObjects.Resolve(e) is PdfDictionary d && !IsStructureAttribute(d)) yield return d;
                break;
        }
    }

    /// <summary>Akcja i jej łańcuch /Next (słownik lub tablica) w kolejności wykonania; cykle przerywa zbiór odwiedzonych. Element razem z wpisem, który go wskazuje.</summary>
    public static List<(PdfItem Raw, PdfDictionary Action)> Chain(PdfItem start)
    {
        var result = new List<(PdfItem, PdfDictionary)>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<PdfItem>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            var raw = stack.Pop();
            var item = PdfObjects.Resolve(raw);
            if (item is null || !seen.Add(item)) continue;
            if (item is PdfArray list)
            {
                for (int i = list.Elements.Count - 1; i >= 0; i--) stack.Push(list.Elements[i]);
            }
            else if (item is PdfDictionary a)
            {
                result.Add((raw, a));
                if (a.Elements[("/Next")] is { } next) stack.Push(next);
            }
        }
        return result;
    }

    /// <summary>Pole formularza: z drzewa /AcroForm /Fields (iteracyjnie, odporne na cykle) albo dowolny słownik widżetu lub z /FT.</summary>
    public static HashSet<PdfDictionary> FieldTree(PdfDocument doc)
    {
        var result = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<PdfItem?>();
        if (PdfObjects.Resolve(doc.Internals.Catalog.Elements["/AcroForm"]) is PdfDictionary acro) stack.Push(acro.Elements["/Fields"]);
        while (stack.Count > 0)
        {
            var item = PdfObjects.Resolve(stack.Pop());
            if (item is PdfArray list) { foreach (var e in list.Elements) stack.Push(e); }
            else if (item is PdfDictionary d && result.Add(d)) stack.Push(d.Elements["/Kids"]);
        }
        return result;
    }

    public static bool IsField(PdfDictionary d, HashSet<PdfDictionary> tree) =>
        tree.Contains(d) || d.Elements.GetName("/Subtype") == "/Widget" || d.Elements.ContainsKey("/FT");
}
