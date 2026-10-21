using Jint;
using Jint.Native;
using Jint.Runtime.Interop;

namespace ZetPDF.Annotations.Forms.Scripting;

/// <summary>Wynik wykonania skryptów formularza (komunikaty app.alert i błędy – do pokazania, nie wyskakują same).</summary>
public sealed class ScriptReport
{
    public List<string> Alerts { get; } = new();
    public List<string> Errors { get; } = new();
    /// <summary>Skrypty pominięte przez politykę (np. dowolny JavaScript przy „tylko AF*”).</summary>
    public int Skipped { get; set; }
    public int Executed { get; set; }
    /// <summary>Zmiany odrzucone przez skrypty Keystroke/Validate (pełne nazwy pól).</summary>
    public List<string> Rejected { get; } = new();
}

/// <summary>Dostęp skryptu do pól (implementuje silnik formularza; tylko łańcuchy znaków – bez obiektów CLR w skrypcie).</summary>
internal interface IScriptFieldHost
{
    bool Has(string name);
    string Get(string name);
    void Set(string name, string value);
    IEnumerable<string> AllNames();
}

/// <summary>
/// Piaskownica JavaScript (Jint, BSD-2-Clause): bez dostępu do CLR (brak AllowClr, interop wyłączony – do skryptu trafiają
/// tylko funkcje hosta na łańcuchach znaków), bez eval/new Function, bez modułów, sieci i plików (Jint ich nie ma).
/// Limity na każde wykonanie: czas, liczba instrukcji, pamięć, głębokość rekursji, rozmiar tablic, czas wyrażeń regularnych.
/// Każde zdarzenie dostaje nowy silnik (brak stanu między skryptami; przygotowany prolog API parsowany raz).
/// </summary>
internal sealed class ScriptRunner
{
    public static TimeSpan Timeout { get; set; } = TimeSpan.FromMilliseconds(750);
    public const int MaxStatements = 250_000;
    public const long MemoryLimit = 16 * 1024 * 1024;
    public const int MaxRecursion = 64;

    private static readonly Lazy<Prepared<Acornima.Ast.Script>> Prelude = new(() => Engine.PrepareScript(AcroJsPrelude.Source, "zetpdf-acroform.js"));

    private readonly IScriptFieldHost _host;
    private readonly ScriptReport _report;
    private readonly Func<string, string> _localize;
    private readonly CancellationToken _ct;

    public ScriptRunner(IScriptFieldHost host, ScriptReport report, Func<string, string> localize, CancellationToken ct)
    {
        _host = host;
        _report = report;
        _localize = localize;
        _ct = ct;
    }

    private Engine CreateEngine()
    {
        var engine = new Engine(o =>
        {
            o.Interop.Enabled = false;
            o.Interop.AllowGetType = false;
            o.Interop.AllowSystemReflection = false;
            o.Strict(false);
            o.DisableStringCompilation();
            o.TimeoutInterval(Timeout);
            o.MaxStatements(MaxStatements);
            o.LimitMemory(MemoryLimit);
            o.LimitRecursion(MaxRecursion);
            o.Constraints.MaxArraySize = 100_000;
            o.Constraints.RegexTimeout = TimeSpan.FromMilliseconds(200);
            o.CancellationToken(_ct);
        });
        void Fn(string name, Func<JsValue, JsValue[], JsValue> f) => engine.SetValue(name, new ClrFunction(engine, name, f));
        string Arg(JsValue[] a, int i) => i < a.Length && !a[i].IsUndefined() && !a[i].IsNull() ? TypeConverterToString(a[i]) : string.Empty;
        Fn("__get", (_, a) => _host.Has(Arg(a, 0)) ? new JsString(_host.Get(Arg(a, 0))) : JsString.Empty);
        Fn("__has", (_, a) => _host.Has(Arg(a, 0)) ? JsBoolean.True : JsBoolean.False);
        Fn("__all", (_, _) => new JsString(string.Join("\n", _host.AllNames())));
        Fn("__set", (_, a) =>
        {
            var n = Arg(a, 0);
            if (_host.Has(n)) _host.Set(n, Arg(a, 1));
            return JsValue.Undefined;
        });
        Fn("__alert", (_, a) =>
        {
            var m = Arg(a, 0);
            if (_report.Alerts.Count < 50) _report.Alerts.Add(m.StartsWith('@') ? _localize(m[1..]) : m);
            return JsValue.Undefined;
        });
        engine.Execute(Prelude.Value);
        return engine;
    }

    private static string TypeConverterToString(JsValue v) => v.IsString() ? v.AsString() : v.ToString();

    /// <summary>
    /// Wykonuje skrypt zdarzenia pola. Zwraca (rc, event.value jako tekst) albo null, gdy skrypt przerwano (błąd, limit).
    /// </summary>
    public (bool Rc, string Value)? RunEvent(string code, string fieldName, string value, string trigger, bool willCommit = true)
    {
        _ct.ThrowIfCancellationRequested();
        try
        {
            var engine = CreateEngine();
            var ev = engine.GetValue("event").AsObject();
            ev.Set("value", new JsString(value));
            ev.Set("change", new JsString(value));
            ev.Set("willCommit", willCommit ? JsBoolean.True : JsBoolean.False);
            ev.Set("name", new JsString(trigger));
            ev.Set("targetName", new JsString(fieldName));
            ev.Set("target", engine.Evaluate("new Field(" + JsonString(fieldName) + ")"));
            engine.Execute(code, "field:" + fieldName + ":" + trigger);
            var result = engine.Evaluate("[String(event.value === null || event.value === undefined ? '' : event.value), event.rc !== false]").AsArray();
            _report.Executed++;
            return (result[1].AsBoolean(), result[0].AsString());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // błąd skryptu, przekroczony limit (czas, instrukcje, pamięć, rekursja) – skrypt przerwany, wartość bez zmian
            if (_report.Errors.Count < 50) _report.Errors.Add($"{fieldName} [{trigger}]: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static string JsonString(string s) => System.Text.Json.JsonSerializer.Serialize(s);
}
