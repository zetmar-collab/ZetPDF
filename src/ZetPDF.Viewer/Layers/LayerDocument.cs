using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using ZetPDF.Core.Documents;

namespace ZetPDF.Viewer.Layers;

/// <summary>Węzeł drzewa warstw: warstwa (<see cref="OcgIndex"/> w /OCGs) albo sama etykieta grupy (-1).</summary>
public sealed record LayerNode(string Title, int OcgIndex, bool Visible, bool Locked)
{
    public List<LayerNode> Children { get; } = new();
}

/// <summary>Warstwy dokumentu do pokazania. <see cref="Truncated"/>: dokument ma ich więcej niż limit.</summary>
public sealed record LayerTree(IReadOnlyList<LayerNode> Roots, int LayerCount, bool Truncated)
{
    public static LayerTree Empty { get; } = new(Array.Empty<LayerNode>(), 0, false);
}

/// <summary>
/// Odczyt i zmiana /OCProperties. Publiczne API PDFium 157 nie pozwala przełączać warstw przy renderze (wśród 458 eksportów pdfium.dll nie ma
/// funkcji OC, sprawdzone), więc widok renderuje kopię bajtów ze zmienionym /D. Dane z pliku są niezaufane: /Order iteracyjnie, każda tablica
/// odwiedzana raz (cykl i współdzielenie nie zapętlają), limit głębokości i liczby węzłów.
/// </summary>
public static class LayerDocument
{
    public const int MaxDepth = 32;
    public const int MaxNodes = 10_000;
    private const int MaxTitle = 256;

    private static PdfItem? Resolve(PdfItem? item) => item is PdfReference r ? r.Value : item;

    public static Task<LayerTree> ReadAsync(byte[] pdf, CancellationToken ct = default) => Task.Run(() => PdfSharpEdit.Read(pdf, doc => Read(doc, ct)), ct);

    /// <summary>Kopia bajtów z widocznością po zmianach; plik sesji zostaje bez zmian.</summary>
    public static Task<byte[]> ApplyToCopyAsync(byte[] pdf, IReadOnlyDictionary<int, bool> visible, CancellationToken ct = default) =>
        PdfSharpEdit.ModifyAsync(pdf, doc => ApplyVisibility(doc, visible), ct);

    private static List<PdfDictionary> Ocgs(PdfDictionary ocp)
    {
        var list = new List<PdfDictionary>();
        if (Resolve(ocp.Elements["/OCGs"]) is PdfArray array)
            foreach (var e in array.Elements)
                if (Resolve(e) is PdfDictionary d) list.Add(d);
        return list;
    }

    private static HashSet<PdfDictionary> SetOf(PdfItem? item)
    {
        var set = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        if (Resolve(item) is PdfArray array)
            foreach (var e in array.Elements)
                if (Resolve(e) is PdfDictionary d) set.Add(d);
        return set;
    }

    /// <summary>Stan jak w CPDF_OCContext z PDFium: /BaseState, /ON, /OFF, a /Usage /View /ViewState warstwy ma pierwszeństwo, także bez /AS (sprawdzone testem).</summary>
    private static Dictionary<PdfDictionary, bool> DefaultStates(PdfDictionary ocp, List<PdfDictionary> ocgs)
    {
        var d = Resolve(ocp.Elements["/D"]) as PdfDictionary;
        bool baseOn = d is null || d.Elements.GetName("/BaseState") != "/OFF";
        var on = SetOf(d?.Elements["/ON"]);
        var off = SetOf(d?.Elements["/OFF"]);
        var states = new Dictionary<PdfDictionary, bool>(ReferenceEqualityComparer.Instance);
        foreach (var o in ocgs)
            states[o] = off.Contains(o) ? false : on.Contains(o) || baseOn;
        foreach (var o in ocgs)
            if (ViewUsage(o) is { } view && view.Elements.ContainsKey("/ViewState"))
                states[o] = view.Elements.GetName("/ViewState") != "/OFF";
        return states;
    }

    private static PdfDictionary? ViewUsage(PdfDictionary ocg) =>
        Resolve(ocg.Elements["/Usage"]) is PdfDictionary usage ? Resolve(usage.Elements["/View"]) as PdfDictionary : null;

    private static string Title(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= MaxTitle ? text : text[..MaxTitle];
    }

    private sealed class Frame(PdfArray array, List<LayerNode> target)
    {
        public PdfArray Array { get; } = array;
        public List<LayerNode> Target { get; } = target;
        public int Next { get; set; }
    }

    internal static LayerTree Read(PdfDocument doc, CancellationToken ct = default)
    {
        if (Resolve(doc.Internals.Catalog.Elements["/OCProperties"]) is not PdfDictionary ocp) return LayerTree.Empty;
        var ocgs = Ocgs(ocp);
        if (ocgs.Count == 0) return LayerTree.Empty;
        var states = DefaultStates(ocp, ocgs);
        var index = new Dictionary<PdfDictionary, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < ocgs.Count; i++) index.TryAdd(ocgs[i], i);
        var d = Resolve(ocp.Elements["/D"]) as PdfDictionary;
        var locked = SetOf(d?.Elements["/Locked"]);
        LayerNode Node(PdfDictionary o) => new(Title(o.Elements.GetString("/Name")), index[o], states[o], locked.Contains(o));

        var roots = new List<LayerNode>();
        int count = 0;
        bool truncated = false;
        if (d is not null && Resolve(d.Elements["/Order"]) is PdfArray order)
        {
            var visited = new HashSet<PdfArray>(ReferenceEqualityComparer.Instance) { order };
            var stack = new Stack<Frame>();
            stack.Push(new Frame(order, roots));
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var frame = stack.Peek();
                if (frame.Next >= frame.Array.Elements.Count)
                {
                    stack.Pop();
                    continue;
                }
                var item = Resolve(frame.Array.Elements[frame.Next++]);
                if (count >= MaxNodes)
                {
                    truncated = true;
                    break;
                }
                if (item is PdfDictionary o && index.ContainsKey(o))
                {
                    frame.Target.Add(Node(o));
                    count++;
                }
                else if (item is PdfArray nested)
                {
                    if (!visited.Add(nested) || stack.Count >= MaxDepth) continue;
                    var target = frame.Target;
                    if (Resolve(nested.Elements.Count > 0 ? nested.Elements[0] : null) is PdfString label)
                    {
                        var group = new LayerNode(Title(label.Value), -1, true, false);
                        frame.Target.Add(group);
                        count++;
                        target = group.Children;
                    }
                    else if (frame.Target.LastOrDefault() is { OcgIndex: >= 0 } previous) target = previous.Children;
                    stack.Push(new Frame(nested, target));
                }
            }
        }
        else
        {
            foreach (var o in ocgs)
            {
                if (count >= MaxNodes)
                {
                    truncated = true;
                    break;
                }
                roots.Add(Node(o));
                count++;
            }
        }
        return new LayerTree(roots, ocgs.Count, truncated);
    }

    /// <summary>
    /// Zapisuje widoczność do /D jako pełne /ON i /OFF. /AS usuwa, a istniejące /ViewState ustawia na ten sam stan, bo inaczej nadpisałyby wybór. Klucz mapy: indeks w /OCGs.
    /// </summary>
    public static void ApplyVisibility(PdfDocument doc, IReadOnlyDictionary<int, bool> visible)
    {
        if (Resolve(doc.Internals.Catalog.Elements["/OCProperties"]) is not PdfDictionary ocp) return;
        var ocgs = Ocgs(ocp);
        var states = DefaultStates(ocp, ocgs);
        if (Resolve(ocp.Elements["/D"]) is not PdfDictionary d)
        {
            d = new PdfDictionary(doc);
            ocp.Elements["/D"] = d;
        }
        var on = new PdfArray(doc);
        var off = new PdfArray(doc);
        for (int i = 0; i < ocgs.Count; i++)
        {
            if (ocgs[i].Reference is not { } reference) continue;
            bool state = visible.TryGetValue(i, out var chosen) ? chosen : states[ocgs[i]];
            (state ? on : off).Elements.Add(reference);
            if (ViewUsage(ocgs[i]) is { } view && view.Elements.ContainsKey("/ViewState")) view.Elements["/ViewState"] = new PdfName(state ? "/ON" : "/OFF");
        }
        d.Elements["/BaseState"] = new PdfName("/ON");
        d.Elements["/ON"] = on;
        d.Elements["/OFF"] = off;
        d.Elements.Remove("/AS");
    }
}
