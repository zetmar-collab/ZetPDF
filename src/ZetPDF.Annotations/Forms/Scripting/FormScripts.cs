using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using ZetPDF.Annotations.Pdf;
using static ZetPDF.Annotations.Pdf.PdfLow;

namespace ZetPDF.Annotations.Forms.Scripting;

/// <summary>Które skrypty formularza wolno wykonywać (ustawienie dokumentu w ZetPDF, nie zapisywane w pliku).</summary>
public enum ScriptPolicy
{
    /// <summary>Żadnych skryptów.</summary>
    Off,
    /// <summary>Tylko skrypty złożone wyłącznie z wywołań wbudowanych funkcji AF* z literałami (domyślnie).</summary>
    AfOnly,
    /// <summary>Dowolny JavaScript pól (za zgodą użytkownika) – w piaskownicy Jint z limitami.</summary>
    All,
}

/// <summary>Zdarzenie pola formularza (/AA): K – naciśnięcie klawisza, F – format, V – walidacja, C – obliczenie.</summary>
public enum ScriptTrigger { Keystroke, Format, Validate, Calculate }

/// <summary>Skrypt w dokumencie. <see cref="FieldName"/> null = skrypt dokumentu (nigdy nie wykonywany automatycznie).</summary>
public sealed record FormScriptInfo(string? FieldName, string Kind, string Code)
{
    public ScriptTrigger? Trigger => Kind switch { "K" => ScriptTrigger.Keystroke, "F" => ScriptTrigger.Format, "V" => ScriptTrigger.Validate, "C" => ScriptTrigger.Calculate, _ => null };
    public bool IsEditable => FieldName is not null && Trigger is not null;
}

/// <summary>
/// Odczyt i zapis skryptów JavaScript: akcje pól /AA (/K /F /V /C), akcje /A widżetów (przyciski), skrypty dokumentu
/// (/Names /JavaScript, /OpenAction, /AA katalogu) oraz kolejność obliczeń /AcroForm /CO (ISO 32000-1 §12.6.3, §12.7.2).
/// </summary>
internal static partial class FormScripts
{
    public static string Key(ScriptTrigger t) => t switch { ScriptTrigger.Keystroke => "K", ScriptTrigger.Format => "F", ScriptTrigger.Validate => "V", _ => "C" };

    /// <summary>Kod akcji JavaScript (/S /JavaScript, /JS – tekst albo strumień) albo null.</summary>
    public static string? JsOf(PdfItem? action)
    {
        if (Deref(action) is not PdfDictionary a || NameOf(a, "/S") != "/JavaScript") return null;
        return Text(a, "/JS");
    }

    /// <summary>Skrypt zdarzenia pola (pole albo jego widżety).</summary>
    public static string? FieldScript(FieldNode f, ScriptTrigger t)
    {
        var key = "/" + Key(t);
        if (Dict(f.Dict, "/AA") is { } aa && JsOf(aa.Elements[key]) is { } js) return js;
        foreach (var w in f.Widgets)
            if (!ReferenceEquals(w, f.Dict) && Dict(w, "/AA") is { } waa && JsOf(waa.Elements[key]) is { } wjs) return wjs;
        return null;
    }

    public static List<FormScriptInfo> List(PdfDocument doc)
    {
        var list = new List<FormScriptInfo>();
        var catalog = doc.Internals.Catalog;
        if (Dict(catalog, "/Names") is { } names && Dict(names, "/JavaScript") is { } tree)
            WalkNameTree(tree, (n, a) => { if (JsOf(a) is { } js) list.Add(new FormScriptInfo(null, "Doc:" + n, js)); }, 0);
        if (JsOf(catalog.Elements["/OpenAction"]) is { } open) list.Add(new FormScriptInfo(null, "OpenAction", open));
        if (Dict(catalog, "/AA") is { } caa)
            foreach (var k in caa.Elements.Keys)
                if (JsOf(caa.Elements[k]) is { } js) list.Add(new FormScriptInfo(null, "DocAction:" + k.TrimStart('/'), js));
        foreach (var f in FieldTree.Terminals(doc))
        {
            foreach (var t in Enum.GetValues<ScriptTrigger>())
                if (FieldScript(f, t) is { } js) list.Add(new FormScriptInfo(f.FullName, Key(t), js));
            foreach (var w in f.Widgets)
                if (JsOf(w.Elements["/A"]) is { } js) list.Add(new FormScriptInfo(f.FullName, "Action", js));
        }
        return list;
    }

    private static void WalkNameTree(PdfDictionary node, Action<string, PdfItem?> visit, int depth)
    {
        if (depth > 16) return;
        if (ArrayOf(node, "/Names") is { } arr)
            for (int i = 0; i + 1 < arr.Elements.Count; i += 2) visit(TextOf(arr.Elements[i]), arr.Elements[i + 1]);
        if (ArrayOf(node, "/Kids") is { } kids)
            foreach (var k in kids.Elements)
                if (Deref(k) is PdfDictionary kd) WalkNameTree(kd, visit, depth + 1);
    }

    /// <summary>Ustawia (pusty kod = usuwa) skrypt zdarzenia pola; obliczenia dopisywane/usuwane z /CO.</summary>
    public static void SetFieldScript(PdfDocument doc, FieldNode f, ScriptTrigger t, string? code)
    {
        var key = "/" + Key(t);
        foreach (var w in f.Widgets)
            if (!ReferenceEquals(w, f.Dict) && Dict(w, "/AA") is { } waa) waa.Elements.Remove(key);
        var aa = Dict(f.Dict, "/AA");
        if (string.IsNullOrWhiteSpace(code))
        {
            aa?.Elements.Remove(key);
            if (aa is { Elements.Count: 0 }) f.Dict.Elements.Remove("/AA");
        }
        else
        {
            if (aa is null) { aa = NewDict(doc); f.Dict.Elements["/AA"] = aa; }
            var a = NewDict(doc, "/Action");
            a.Elements["/S"] = Name("/JavaScript");
            a.Elements["/JS"] = Str(code);
            aa.Elements[key] = a;
        }
        if (t != ScriptTrigger.Calculate || FieldTree.AcroForm(doc) is not { } acro) return;
        var co = ArrayOf(acro, "/CO");
        if (string.IsNullOrWhiteSpace(code))
        {
            if (co is not null) RemoveFrom(co, f.Dict);
            return;
        }
        if (co is null) { co = new PdfArray(doc); acro.Elements["/CO"] = co; }
        if (f.Dict.Reference is null) doc.Internals.AddObject(f.Dict);
        if (!co.Elements.Any(e => ReferenceEquals(Deref(e), f.Dict))) co.Elements.Add(f.Dict.Reference!);
    }

    /// <summary>Pola z obliczeniami w kolejności /CO (pozostałe – w kolejności drzewa pól).</summary>
    public static List<FieldNode> CalculationOrder(PdfDocument doc, List<FieldNode> terminals)
    {
        var withCalc = terminals.Where(f => FieldScript(f, ScriptTrigger.Calculate) is not null).ToList();
        var result = new List<FieldNode>();
        if (FieldTree.AcroForm(doc) is { } acro && ArrayOf(acro, "/CO") is { } co)
            foreach (var e in co.Elements)
                if (Deref(e) is PdfDictionary d && withCalc.FirstOrDefault(f => ReferenceEquals(f.Dict, d)) is { } f && !result.Contains(f)) result.Add(f);
        result.AddRange(withCalc.Where(f => !result.Contains(f)));
        return result;
    }

    // ------------------------------------------------------------------ rozpoznanie skryptów „tylko AF*”

    /// <summary>Wbudowane funkcje formularzy (Acrobat AForm) zaimplementowane w ZetPDF.</summary>
    public static readonly IReadOnlySet<string> AfFunctions = new HashSet<string>(StringComparer.Ordinal)
    {
        "AFSimple_Calculate", "AFNumber_Format", "AFNumber_Keystroke", "AFPercent_Format", "AFPercent_Keystroke",
        "AFDate_FormatEx", "AFDate_KeystrokeEx", "AFDate_Format", "AFDate_Keystroke", "AFRange_Validate",
        "AFSpecial_Format", "AFSpecial_Keystroke",
    };

    private const string Lit = @"(?:-?\d+(?:\.\d+)?|""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*'|true|false|null)";
    private const string ArrayLit = @"(?:new\s+Array\s*\(\s*(?:" + Lit + @"(?:\s*,\s*" + Lit + @")*)?\s*\)|\[\s*(?:" + Lit + @"(?:\s*,\s*" + Lit + @")*)?\s*\])";
    private const string Arg = "(?:" + ArrayLit + "|" + Lit + ")";

    [GeneratedRegex(@"^\s*(?:(?<fn>AF[A-Za-z_]+)\s*\(\s*(?:" + Arg + @"(?:\s*,\s*" + Arg + @")*)?\s*\)\s*;?\s*)+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 500)]
    private static partial Regex AfOnlyRegex();

    /// <summary>Czy skrypt to wyłącznie wywołania znanych funkcji AF* z literałami (bezpieczne przy polityce AfOnly).</summary>
    public static bool IsAfOnly(string code)
    {
        try
        {
            var m = AfOnlyRegex().Match(code);
            return m.Success && m.Groups["fn"].Captures.All(c => AfFunctions.Contains(c.Value));
        }
        catch (RegexMatchTimeoutException) { return false; }
    }

    public static bool MayRun(string code, ScriptPolicy policy) => policy switch
    {
        ScriptPolicy.All => true,
        ScriptPolicy.AfOnly => IsAfOnly(code),
        _ => false,
    };
}
