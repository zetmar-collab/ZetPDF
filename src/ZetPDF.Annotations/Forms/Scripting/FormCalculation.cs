using PdfSharp.Pdf;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Annotations.Forms.Scripting;

/// <summary>
/// Przebieg zmian wartości ze skryptami pól (kolejność jak w Acrobacie): dla każdej zmienianej wartości Keystroke
/// (willCommit) i Validate – event.rc = false odrzuca zmianę; potem obliczenia /C w kolejności /CO; na końcu formaty /F
/// (tekst wyświetlany w wyglądzie, /V zostaje surowa). Skrypty spoza polityki są pomijane; dokumentowe nie są wykonywane.
/// </summary>
internal static class FormCalculation
{
    private sealed class Host : IScriptFieldHost
    {
        private readonly PdfDocument _doc;
        private readonly Dictionary<string, FieldNode> _fields;

        public Host(PdfDocument doc, Dictionary<string, FieldNode> fields)
        {
            _doc = doc;
            _fields = fields;
        }

        public bool Has(string name) => _fields.ContainsKey(name);
        public string Get(string name) => _fields.TryGetValue(name, out var f) ? f.Value : string.Empty;
        public IEnumerable<string> AllNames() => _fields.Keys;

        public void Set(string name, string value)
        {
            if (_fields.TryGetValue(name, out var f) && f.Value != value) FormEngine.SetValue(_doc, f, value);
        }
    }

    public static string Localize(string key) =>
        AnnotationsModule.Strings.GetString(key, System.Globalization.CultureInfo.CurrentUICulture) ?? key;

    /// <summary>Stosuje zmiany (null = tylko przeliczenie) i zwraca teksty wyświetlane wg skryptów formatu.</summary>
    /// <param name="budget">Łączny budżet przebiegu (czas, liczba zdarzeń); null = domyślny (<see cref="ScriptBudget"/>).</param>
    public static Dictionary<string, string> Apply(PdfDocument doc, IReadOnlyDictionary<string, string>? changes, ScriptPolicy policy, ScriptReport report, CancellationToken ct, ScriptBudget? budget = null)
    {
        var terminals = FieldTree.Terminals(doc);
        var byName = terminals.GroupBy(f => f.FullName).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var runner = new ScriptRunner(new Host(doc, byName), report, Localize, ct, budget ?? new ScriptBudget());
        bool Can(string code)
        {
            if (FormScripts.MayRun(code, policy)) return true;
            report.Skipped++;
            return false;
        }

        if (changes is not null)
            foreach (var (name, raw) in changes)
            {
                if (!byName.TryGetValue(name, out var f)) continue;
                var value = raw ?? string.Empty;
                bool rejected = false;
                if (f.Kind is FormFieldKind.Text or FormFieldKind.Date or FormFieldKind.ComboBox or FormFieldKind.ListBox && value != f.Value)
                    foreach (var t in new[] { ScriptTrigger.Keystroke, ScriptTrigger.Validate })
                        if (FormScripts.FieldScript(f, t) is { } js && Can(js) && runner.RunEvent(js, name, value, t.ToString()) is { Rc: false })
                        {
                            rejected = true;
                            report.Rejected.Add(name);
                            break;
                        }
                if (!rejected) FormEngine.SetValue(doc, f, value);
            }

        if (policy != ScriptPolicy.Off)
            foreach (var f in FormScripts.CalculationOrder(doc, terminals))
            {
                var js = FormScripts.FieldScript(f, ScriptTrigger.Calculate)!;
                if (!Can(js)) continue;
                if (runner.RunEvent(js, f.FullName, f.Value, nameof(ScriptTrigger.Calculate)) is { Rc: true } r && r.Value != f.Value)
                    FormEngine.SetValue(doc, f, r.Value);
            }

        var display = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in terminals)
        {
            if (FormScripts.FieldScript(f, ScriptTrigger.Format) is not { } js || f.Value.Length == 0) continue;
            if (!Can(js)) continue;
            if (runner.RunEvent(js, f.FullName, f.Value, nameof(ScriptTrigger.Format)) is { Rc: true } r) display[f.FullName] = r.Value;
        }
        return display;
    }
}
