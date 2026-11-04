using System.Diagnostics;
using Jint;
using Jint.Native;
using Jint.Runtime.Interop;

namespace ZetPDF.Annotations.Forms.Scripting;

/// <summary>Wynik wykonania skryptów formularza (komunikaty app.alert i błędy – do pokazania, nie wyskakują same).</summary>
public sealed class ScriptReport
{
    public List<string> Alerts { get; } = new();
    public List<string> Errors { get; } = new();
    /// <summary>Skrypty pominięte przez politykę (np. dowolny JavaScript przy „tylko AF*”) albo po wyczerpaniu budżetu.</summary>
    public int Skipped { get; set; }
    public int Executed { get; set; }
    /// <summary>Zmiany odrzucone przez skrypty Keystroke/Validate (pełne nazwy pól).</summary>
    public List<string> Rejected { get; } = new();
    /// <summary>Przekroczono łączny budżet przebiegu (czas lub liczba zdarzeń) – pozostałe skrypty pominięto.</summary>
    public bool BudgetExceeded { get; set; }
    /// <summary>Liczba skryptów przerwanych limitem (czas, instrukcje, pamięć, rekursja, długość łańcucha/tablicy).</summary>
    public int LimitHits { get; set; }
    /// <summary>Skrypt wyczerpał pamięć procesu (OutOfMemoryException złapany) – skrypty dokumentu należy wyłączyć.</summary>
    public bool OutOfMemory { get; set; }

    /// <summary>Czy przebieg przekroczył limity (do automatycznego wyłączenia skryptów dokumentu).</summary>
    public bool HitLimits => BudgetExceeded || LimitHits > 0 || OutOfMemory;
}

/// <summary>Łączny budżet jednego przebiegu skryptów (wszystkie zdarzenia w jednym poleceniu).</summary>
internal sealed class ScriptBudget
{
    public static TimeSpan DefaultTotal { get; set; } = TimeSpan.FromSeconds(2);
    public const int DefaultMaxEvents = 1000;

    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public ScriptBudget(TimeSpan? total = null, int maxEvents = DefaultMaxEvents)
    {
        Total = total ?? DefaultTotal;
        MaxEvents = maxEvents;
    }

    public TimeSpan Total { get; }
    public int MaxEvents { get; }
    public int Events { get; private set; }
    public TimeSpan Remaining => Total - _clock.Elapsed;

    /// <summary>Rezerwuje wykonanie jednego zdarzenia; false, gdy budżet wyczerpany.</summary>
    public bool TryStart(out TimeSpan remaining)
    {
        remaining = Remaining;
        if (Events >= MaxEvents || remaining <= TimeSpan.FromMilliseconds(5)) return false;
        Events++;
        return true;
    }
}

/// <summary>
/// Limity czasu jednego skryptu. Oba liczą czas zegarowy, więc na obciążonym komputerze poprawny skrypt może je przekroczyć
/// (wyrażenie regularne ma 200 ms łącznie z kompilacją JIT przy pierwszym użyciu). Program zawsze działa na <see cref="Default"/>;
/// inne wartości są dla testów sprawdzających wynik funkcji, a nie same limity.
/// </summary>
internal sealed record ScriptTimeLimits(TimeSpan Regex)
{
    public static ScriptTimeLimits Default { get; } = new(TimeSpan.FromMilliseconds(200));
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
/// Piaskownica JavaScript (Jint, BSD-2-Clause) działająca w procesie programu: bez dostępu do CLR (brak AllowClr, interop
/// wyłączony – do skryptu trafiają tylko funkcje hosta na łańcuchach znaków), bez eval/new Function, bez modułów, sieci
/// i plików (Jint ich nie ma). Limity na każde wykonanie: czas, liczba instrukcji, pamięć (sprawdzana między instrukcjami),
/// głębokość rekursji, rozmiar tablic, czas wyrażeń regularnych; w preludium – maks. długość łańcuchów z repeat/padStart/
/// padEnd/join i rozmiar tablic dla fill. Łącznie na przebieg: <see cref="ScriptBudget"/>. OutOfMemoryException z
/// wykonania skryptu jest łapany i zgłaszany jako błąd skryptu. Każde zdarzenie dostaje nowy silnik.
/// </summary>
internal sealed class ScriptRunner
{
    public static TimeSpan Timeout { get; set; } = TimeSpan.FromMilliseconds(750);
    public const int MaxStatements = 250_000;
    public const long MemoryLimit = 16 * 1024 * 1024;
    public const int MaxRecursion = 64;
    /// <summary>Maks. długość kodu jednego skryptu (znaki) – ogromny literał nie jest nawet parsowany.</summary>
    public const int MaxCodeLength = 256 * 1024;

    private static readonly Lazy<Prepared<Acornima.Ast.Script>> Prelude = new(() => Engine.PrepareScript(AcroJsPrelude.Source, "zetpdf-acroform.js"));

    private readonly IScriptFieldHost _host;
    private readonly ScriptReport _report;
    private readonly Func<string, string> _localize;
    private readonly CancellationToken _ct;
    private readonly ScriptBudget _budget;
    private readonly TimeSpan _scriptTimeout;
    private readonly TimeSpan _regexTimeout;

    public ScriptRunner(IScriptFieldHost host, ScriptReport report, Func<string, string> localize, CancellationToken ct, ScriptBudget? budget = null,
        ScriptTimeLimits? limits = null, TimeSpan? scriptTimeout = null)
    {
        _scriptTimeout = scriptTimeout ?? Timeout;
        _regexTimeout = (limits ?? ScriptTimeLimits.Default).Regex;
        _host = host;
        _report = report;
        _localize = localize;
        _ct = ct;
        _budget = budget ?? new ScriptBudget();
    }

    private Engine CreateEngine(TimeSpan timeout)
    {
        var engine = new Engine(o =>
        {
            o.Interop.Enabled = false;
            o.Interop.AllowGetType = false;
            o.Interop.AllowSystemReflection = false;
            o.Strict(false);
            o.DisableStringCompilation();
            o.TimeoutInterval(timeout);
            o.MaxStatements(MaxStatements);
            o.LimitMemory(MemoryLimit);
            o.LimitRecursion(MaxRecursion);
            o.Constraints.MaxArraySize = 100_000;
            o.Constraints.RegexTimeout = _regexTimeout;
            o.CancellationToken(_ct);
        });
        void Fn(string name, Func<JsValue, JsValue[], JsValue> f) => engine.SetValue(name, new ClrFunction(engine, name, f));
        string Arg(JsValue[] a, int i) => i < a.Length && !a[i].IsUndefined() && !a[i].IsNull() ? (a[i].IsString() ? a[i].AsString() : a[i].ToString()) : string.Empty;
        Fn("__get", (_, a) => _host.Has(Arg(a, 0)) ? new JsString(_host.Get(Arg(a, 0))) : JsString.Empty);
        Fn("__has", (_, a) => _host.Has(Arg(a, 0)) ? JsBoolean.True : JsBoolean.False);
        Fn("__all", (_, _) => new JsString(string.Join("\n", _host.AllNames())));
        Fn("__set", (_, a) =>
        {
            var n = Arg(a, 0);
            var v = Arg(a, 1);
            if (v.Length > AcroJsPrelude.MaxStringLength) throw new InvalidOperationException("Value too long.");
            if (_host.Has(n)) _host.Set(n, v);
            return JsValue.Undefined;
        });
        Fn("__alert", (_, a) =>
        {
            var m = Arg(a, 0);
            if (m.Length > 2000) m = m[..2000];
            if (_report.Alerts.Count < 50) _report.Alerts.Add(m.StartsWith('@') ? _localize(m[1..]) : m);
            return JsValue.Undefined;
        });
        engine.Execute(Prelude.Value);
        return engine;
    }

    /// <summary>Czy wyjątek oznacza przekroczenie limitu silnika (a nie zwykły błąd skryptu).</summary>
    private static bool IsLimit(Exception ex) =>
        ex is TimeoutException or InsufficientExecutionStackException or System.Text.RegularExpressions.RegexMatchTimeoutException
        || ex.GetType().Name is "StatementsCountOverflowException" or "MemoryLimitExceededException" or "RecursionDepthOverflowException"
        || ex.Message.Contains("ZetPDF limit", StringComparison.Ordinal);

    /// <summary>
    /// Wykonuje skrypt zdarzenia pola. Zwraca (rc, event.value jako tekst) albo null, gdy skrypt przerwano (błąd, limit)
    /// lub pominięto (wyczerpany budżet przebiegu).
    /// </summary>
    public (bool Rc, string Value)? RunEvent(string code, string fieldName, string value, string trigger, bool willCommit = true)
    {
        _ct.ThrowIfCancellationRequested();
        if (!_budget.TryStart(out var remaining))
        {
            if (!_report.BudgetExceeded && _report.Errors.Count < 50) _report.Errors.Add(_localize("Annot.Script.Budget"));
            _report.BudgetExceeded = true;
            _report.Skipped++;
            return null;
        }
        if (code.Length > MaxCodeLength)
        {
            _report.LimitHits++;
            if (_report.Errors.Count < 50) _report.Errors.Add($"{fieldName} [{trigger}]: {_localize("Annot.Script.TooLong")}");
            return null;
        }
        try
        {
            var engine = CreateEngine(remaining < _scriptTimeout ? remaining : _scriptTimeout);
            var ev = engine.GetValue("event").AsObject();
            ev.Set("value", new JsString(value));
            ev.Set("change", new JsString(value));
            ev.Set("willCommit", willCommit ? JsBoolean.True : JsBoolean.False);
            ev.Set("name", new JsString(trigger));
            ev.Set("targetName", new JsString(fieldName));
            ev.Set("target", engine.Evaluate("new Field(" + System.Text.Json.JsonSerializer.Serialize(fieldName) + ")"));
            engine.Execute(code, "field:" + fieldName + ":" + trigger);
            var result = engine.Evaluate("[__str(event.value), event.rc !== false]").AsArray();
            var text = result[0].AsString();
            if (text.Length > AcroJsPrelude.MaxStringLength) throw new InvalidOperationException("ZetPDF limit: value too long");
            _report.Executed++;
            return (result[1].AsBoolean(), text);
        }
        catch (OperationCanceledException) { throw; }
        catch (OutOfMemoryException ex)
        {
            // pojedyncza ogromna alokacja w skrypcie – nie wywracamy aplikacji; skrypty dokumentu zostaną wyłączone
            _report.OutOfMemory = true;
            _report.LimitHits++;
            AddError(fieldName, trigger, ex);
            GC.Collect();
            return null;
        }
        catch (Exception ex)
        {
            if (IsLimit(ex)) _report.LimitHits++;
            AddError(fieldName, trigger, ex);
            return null;
        }
    }

    private void AddError(string fieldName, string trigger, Exception ex)
    {
        if (_report.Errors.Count >= 50) return;
        var msg = ex.Message.Length > 300 ? ex.Message[..300] : ex.Message;
        _report.Errors.Add($"{fieldName} [{trigger}]: {ex.GetType().Name}: {msg}");
    }
}
