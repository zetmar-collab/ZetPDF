using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Documents;
using ZetPDF.Core.Services;
using ZetPDF.Annotations.Forms.Scripting;
using ZetPDF.Core.Contracts;
using ZetPDF.TestData;

namespace ZetPDF.Annotations.Tests;

/// <summary>JavaScript w formularzach (Jint w piaskownicy): obliczenia AF*, kolejność /CO, formaty, walidacja, limity.</summary>
public class FormScriptTests
{
    private readonly FormService _forms = new();

    private static readonly PdfRect RA = new(100, 700, 250, 720), RB = new(100, 670, 250, 690), RC = new(100, 640, 250, 660);

    private async Task<byte[]> SumFormAsync(string? calc = "AFSimple_Calculate(\"SUM\", new Array (\"A\", \"B\"));")
    {
        var pdf = SamplePdfFactory.Text(1);
        foreach (var (n, r) in new[] { ("A", RA), ("B", RB), ("C", RC) })
            pdf = await _forms.CreateFieldAsync(pdf, new FormFieldSpec { Kind = FormFieldKind.Text, Name = n, PageIndex = 0, Rect = r });
        if (calc is not null) pdf = await _forms.SetFieldScriptAsync(pdf, "C", ScriptTrigger.Calculate, calc);
        return pdf;
    }

    private async Task<string> ValueAsync(byte[] pdf, string name) => (await _forms.GetFieldsAsync(pdf)).Single(f => f.FullName == name).Value;

    [Fact]
    public async Task Sum_field_is_recalculated_when_a_source_changes()
    {
        var pdf = await SumFormAsync();
        Assert.Contains(await _forms.GetScriptsAsync(pdf), s => s.FieldName == "C" && s.Trigger == ScriptTrigger.Calculate);
        pdf = await _forms.SetValuesAsync(pdf, new Dictionary<string, string> { ["A"] = "10", ["B"] = "5,5" });
        Assert.Equal("15.5", await ValueAsync(pdf, "C"));
        pdf = await _forms.SetValuesAsync(pdf, new Dictionary<string, string> { ["A"] = "1" });
        Assert.Equal("6.5", await ValueAsync(pdf, "C"));
        Assert.True(await RenderCheck.NonWhitePixels(pdf, 0, RC) > 10);
        // usunięcie skryptu – brak dalszych obliczeń
        pdf = await _forms.SetFieldScriptAsync(pdf, "C", ScriptTrigger.Calculate, "");
        pdf = await _forms.SetValuesAsync(pdf, new Dictionary<string, string> { ["A"] = "100" });
        Assert.Equal("6.5", await ValueAsync(pdf, "C"));
    }

    [Fact]
    public async Task Calculation_order_follows_CO_and_format_changes_only_display()
    {
        var pdf = await SumFormAsync();
        // D = C * 2 (dowolny JS – wymaga polityki All); /CO: C przed D
        pdf = await _forms.CreateFieldAsync(pdf, new FormFieldSpec { Kind = FormFieldKind.Text, Name = "D", PageIndex = 0, Rect = new PdfRect(100, 610, 250, 630) });
        pdf = await _forms.SetFieldScriptAsync(pdf, "D", ScriptTrigger.Calculate, "event.value = this.getField(\"C\").value * 2;");
        pdf = await _forms.SetFieldScriptAsync(pdf, "C", ScriptTrigger.Format, "AFNumber_Format(2, 2, 0, 0, \" zł\", false);");
        var report = new ScriptReport();
        pdf = await _forms.SetValuesAsync(pdf, new Dictionary<string, string> { ["A"] = "1000", ["B"] = "234,5" }, ScriptPolicy.All, report);
        Assert.Empty(report.Errors);
        Assert.Equal("1234.5", await ValueAsync(pdf, "C"));
        Assert.Equal("2469", await ValueAsync(pdf, "D"));
        // polityka domyślna (tylko AF*) pomija skrypt D
        var r2 = new ScriptReport();
        pdf = await _forms.SetValuesAsync(pdf, new Dictionary<string, string> { ["A"] = "0" }, ScriptPolicy.AfOnly, r2);
        Assert.Equal("234.5", await ValueAsync(pdf, "C"));
        Assert.Equal("2469", await ValueAsync(pdf, "D"));
        Assert.True(r2.Skipped > 0);
    }

    [Fact]
    public async Task Validation_rejects_value_out_of_range()
    {
        var pdf = await SumFormAsync();
        pdf = await _forms.SetFieldScriptAsync(pdf, "A", ScriptTrigger.Validate, "AFRange_Validate(true, 0, true, 100);");
        var report = new ScriptReport();
        pdf = await _forms.SetValuesAsync(pdf, new Dictionary<string, string> { ["A"] = "150" }, ScriptPolicy.AfOnly, report);
        Assert.Equal("", await ValueAsync(pdf, "A"));
        Assert.Contains("A", report.Rejected);
        Assert.Single(report.Alerts);
        pdf = await _forms.SetValuesAsync(pdf, new Dictionary<string, string> { ["A"] = "50" });
        Assert.Equal("50", await ValueAsync(pdf, "A"));
    }

    [Theory]
    [InlineData("while (true) { }")]
    [InlineData("for (;;) { var x = 1; }")]
    [InlineData("function f() { return f(); } f();")]
    [InlineData("var a = []; while (true) a.push(new Array(10000).join('x'));")]
    [InlineData("event.value = eval('1+1');")]
    [InlineData("event.value = new Function('return 1')();")]
    public async Task Runaway_or_forbidden_scripts_are_stopped_without_hanging(string script)
    {
        var pdf = await SumFormAsync(script);
        pdf = await _forms.SetValuesAsync(pdf, new Dictionary<string, string> { ["C"] = "7" });
        var sw = Stopwatch.StartNew();
        var report = new ScriptReport();
        var result = await _forms.SetValuesAsync(pdf, new Dictionary<string, string> { ["A"] = "1" }, ScriptPolicy.All, report);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), sw.Elapsed.ToString());
        Assert.NotEmpty(report.Errors);
        Assert.Equal("7", await ValueAsync(result, "C"));
        Assert.Equal("1", await ValueAsync(result, "A"));
    }

    [Theory]
    [InlineData("event.value = 'x'.repeat(1e9);")]
    [InlineData("event.value = 'x'.padStart(1e9);")]
    [InlineData("event.value = 'x'.padEnd(2e9, 'ab');")]
    [InlineData("event.value = Array(1e9).join('x');")]
    [InlineData("event.value = new Array(50000).fill('x'.repeat(1000)).join('');")]
    [InlineData("var s = 'x'; while (true) s = s + s;")]
    public async Task Huge_allocations_are_script_errors_and_process_survives(string script)
    {
        var pdf = await SumFormAsync(script);
        var report = new ScriptReport();
        var result = await _forms.SetValuesAsync(pdf, new Dictionary<string, string> { ["A"] = "1" }, ScriptPolicy.All, report);
        Assert.NotEmpty(report.Errors);
        Assert.True(report.HitLimits || report.Errors.Count > 0);
        Assert.Equal("", await ValueAsync(result, "C"));
        Assert.Equal("1", await ValueAsync(result, "A"));
    }

    /// <summary>
    /// Ataki z kontroli etapu 3 (recenzent): podmiana funkcji używanych przez ograniczenia, valueOf/getter zwracający
    /// inną wartość przy drugim odczycie, wzmacniacze alokacji poza repeat/pad/join, bufory binarne, konstruktor
    /// Function przez prototyp, getter event.value z pętlą, rekurencja w getterze, ogromny literał.
    /// </summary>
    [Theory]
    [InlineData("Math.floor = function () { return 0; }; event.value = 'x'.repeat(3e8).length;")]
    [InlineData("Number = function () { return 1; }; event.value = 'x'.padEnd(3e8).length;")]
    [InlineData("var c = 0; event.value = 'x'.padStart({ valueOf: function () { return c++ ? 3e8 : 1; } }).length; if (event.value < 3e8) throw 'ok';")]
    [InlineData("var c = 0; event.value = Array.prototype.join.call({ get length() { return c++ ? 3e8 : 1; } }).length; if (event.value < 3e8) throw 'ok';")]
    [InlineData("var s = 'x'.repeat(1e6); event.value = s.replace(/x/g, s.substring(0, 1000)).length;")]
    [InlineData("var s = 'x'.repeat(1e6); event.value = s.replaceAll('x', 'abcdefgh').length;")]
    [InlineData("var s = 'x'.repeat(1e6), re = /x/, c = 0; Object.defineProperty(re, 'global', { get: function () { return c++ > 0; } }); event.value = s.replace(re, 'abcdefgh').length;")]
    [InlineData("var s = 'x'.repeat(1e6); event.value = s.replace(/x/g, function () { return 'abcdefghij'; }).length;")]
    [InlineData("var s = 'x'.repeat(1e6); event.value = s.concat.apply(s, Array(1000).fill(s)).length;")]
    [InlineData("var s = 'x'.repeat(1e6); event.value = Array(1000).fill(s).toLocaleString().length;")]
    [InlineData("var s = 'x'.repeat(1e6); event.value = JSON.stringify(Array(1000).fill(s)).length;")]
    [InlineData("event.value = new ArrayBuffer(1e9).byteLength;")]
    [InlineData("event.value = new Uint8Array(1e9).length;")]
    [InlineData("event.value = (function () { }).constructor('return 1')();")]
    [InlineData("event.value = this.constructor.constructor('return this')();")]
    [InlineData("event.value = Object.getPrototypeOf(function* () { }).constructor('yield 1')().next().value;")]
    [InlineData("Object.defineProperty(event, 'value', { get: function () { while (true) { } } });")]
    [InlineData("var o = { get x() { return this.x; } }; event.value = o.x;")]
    public void Sandbox_attacks_end_as_script_errors_quickly(string script)
    {
        var host = new MemoryHost();
        var report = new ScriptReport();
        var sw = Stopwatch.StartNew();
        var r = new ScriptRunner(host, report, k => k, CancellationToken.None).RunEvent(script, "C", "1", "Calculate");
        Assert.Null(r);
        Assert.NotEmpty(report.Errors);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), sw.Elapsed.ToString());
        Assert.Equal("", host.Get("C"));
    }

    [Theory]
    [InlineData("String.prototype.repeat = function () { return 'hacked'; }; event.value = 'ab'.repeat(2);", "abab")]
    [InlineData("event.value = '12345678'.replace(/(\\d)(?=(\\d{3})+$)/g, '$1 ');", "12 345 678")]
    [InlineData("event.value = 'a-b-c'.replaceAll('-', '+') + ['x', 1, null].join('|') + 'q'.concat(1, 2);", "a+b+cx|1|q12")]
    [InlineData("event.value = JSON.stringify({ a: [1, 'x', { b: null }], c: true, d: undefined });", "{\"a\":[1,\"x\",{\"b\":null}],\"c\":true}")]
    [InlineData("event.value = JSON.stringify({ a: 1, b: 2 }, ['b']) + JSON.stringify([new Date(0)]);", "{\"b\":2}[\"1970-01-01T00:00:00.000Z\"]")]
    [InlineData("event.value = typeof ArrayBuffer + typeof Uint8Array + typeof DataView;", "undefinedundefinedundefined")]
    public void Hardened_builtins_keep_normal_behaviour(string script, string expected)
    {
        var host = new MemoryHost();
        var report = new ScriptReport();
        var r = new ScriptRunner(host, report, k => k, CancellationToken.None).RunEvent(script, "C", "1", "Calculate");
        Assert.Empty(report.Errors);
        Assert.Equal(expected, r!.Value.Value);
    }

    [Fact]
    public void Prototype_pollution_does_not_leak_to_the_next_script()
    {
        var host = new MemoryHost();
        var report = new ScriptReport();
        var runner = new ScriptRunner(host, report, k => k, CancellationToken.None);
        runner.RunEvent("Object.prototype.polluted = 'yes'; String.prototype.trim = function () { return 'evil'; }; event.value = 'a';", "C", "", "Calculate");
        var r = runner.RunEvent("event.value = typeof ({}).polluted + ' ' + '  x '.trim();", "C", "", "Calculate");
        Assert.Empty(report.Errors);
        Assert.Equal("undefined x", r!.Value.Value);
    }

    [Fact]
    public void Huge_script_literal_is_not_parsed()
    {
        var host = new MemoryHost();
        var report = new ScriptReport();
        var code = "event.value = '" + new string('x', 5_000_000) + "';";
        var sw = Stopwatch.StartNew();
        var r = new ScriptRunner(host, report, k => k, CancellationToken.None).RunEvent(code, "C", "", "Calculate");
        Assert.Null(r);
        Assert.Single(report.Errors);
        Assert.True(report.HitLimits);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), sw.Elapsed.ToString());
    }

    private sealed class MemoryHost : IScriptFieldHost
    {
        private readonly Dictionary<string, string> _v = new() { ["C"] = "" };
        public bool Has(string name) => _v.ContainsKey(name);
        public string Get(string name) => _v[name];
        public void Set(string name, string value) => _v[name] = value;
        public IEnumerable<string> AllNames() => _v.Keys;
    }

    [Fact]
    public async Task Infinite_loops_in_many_fields_are_limited_by_the_total_budget()
    {
        var pdf = SamplePdfFactory.Text(1);
        pdf = Core.Documents.PdfSharpEdit.Modify(pdf, doc =>
        {
            for (int i = 0; i < 300; i++)
            {
                Forms.FormEngine.Create(doc, new FormFieldSpec { Kind = FormFieldKind.Text, Name = "L" + i, PageIndex = 0, Rect = new PdfRect(50 + i % 10 * 50, 100 + i / 10 * 20, 90 + i % 10 * 50, 115 + i / 10 * 20) });
                FormScripts.SetFieldScript(doc, Forms.FieldTree.Find(doc, "L" + i)!, ScriptTrigger.Calculate, "while (true) { }");
            }
        });
        // każdy skrypt przerywa limit instrukcji/czasu; łączny czas ogranicza budżet przebiegu (tu 300 ms)
        var report = new ScriptReport();
        var sw = Stopwatch.StartNew();
        Core.Documents.PdfSharpEdit.Modify(pdf, doc => { FormCalculation.Apply(doc, null, ScriptPolicy.All, report, CancellationToken.None, new ScriptBudget(TimeSpan.FromMilliseconds(300))); });
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), sw.Elapsed.ToString());
        Assert.True(report.BudgetExceeded);
        Assert.True(report.LimitHits >= 1);
        Assert.True(report.Executed + report.Errors.Count < 300);
        // domyślny budżet przez usługę: kończy się w rozsądnym czasie
        var r2 = new ScriptReport();
        sw.Restart();
        await _forms.RecalculateAsync(pdf, ScriptPolicy.All, r2);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), sw.Elapsed.ToString());
        Assert.True(r2.LimitHits >= 1);
    }

    [Fact]
    public async Task Five_thousand_calculated_fields_are_cut_by_the_budget()
    {
        var pdf = SamplePdfFactory.Text(1);
        pdf = Core.Documents.PdfSharpEdit.Modify(pdf, doc =>
        {
            Forms.FormEngine.Create(doc, new FormFieldSpec { Kind = FormFieldKind.Text, Name = "A", PageIndex = 0, Rect = new PdfRect(10, 10, 20, 20) });
            for (int i = 0; i < 5000; i++)
            {
                Forms.FormEngine.Create(doc, new FormFieldSpec { Kind = FormFieldKind.Text, Name = "F" + i, PageIndex = 0, Rect = new PdfRect(10 + i % 50 * 11, 30 + i / 50 * 7, 20 + i % 50 * 11, 36 + i / 50 * 7) });
            }
            foreach (var f in Forms.FieldTree.Terminals(doc).Where(f => f.FullName != "A"))
                FormScripts.SetFieldScript(doc, f, ScriptTrigger.Calculate, "var s = 0; for (var i = 0; i < 2000; i++) s += i; event.value = s + Number(this.getField('A').value);");
        });
        var report = new ScriptReport();
        var sw = Stopwatch.StartNew();
        await _forms.SetValuesAsync(pdf, new Dictionary<string, string> { ["A"] = "1" }, ScriptPolicy.All, report);
        Assert.True(report.BudgetExceeded);
        Assert.True(report.Executed <= ScriptBudget.DefaultMaxEvents);
        Assert.True(report.Skipped > 0);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), sw.Elapsed.ToString());
    }

    [Fact]
    public async Task Sandbox_has_no_clr_network_or_file_access()
    {
        var pdf = await SumFormAsync("event.value = [typeof System, typeof importNamespace, typeof require, typeof fetch, typeof XMLHttpRequest, typeof process, typeof clrHelper].join('|');");
        var report = new ScriptReport();
        pdf = await _forms.SetValuesAsync(pdf, new Dictionary<string, string> { ["A"] = "1" }, ScriptPolicy.All, report);
        Assert.Empty(report.Errors);
        Assert.Equal("undefined|undefined|undefined|undefined|undefined|undefined|undefined", await ValueAsync(pdf, "C"));
        // funkcje hosta nie zdradzają typów CLR
        pdf = await _forms.SetFieldScriptAsync(pdf, "C", ScriptTrigger.Calculate, "event.value = typeof __get.GetType + '|' + typeof __get.constructor;");
        pdf = await _forms.SetValuesAsync(pdf, new Dictionary<string, string> { ["A"] = "2" }, ScriptPolicy.All, new ScriptReport());
        Assert.Equal("undefined|function", await ValueAsync(pdf, "C"));
    }

    [Theory]
    [InlineData("AFNumber_Format(2, 2, 0, 0, \" zł\", false);", "1234.5", "1.234,50 zł")]
    [InlineData("AFNumber_Format(2, 0, 2, 0, \"$\", true);", "-1234.5", "($1,234.50)")]
    [InlineData("AFPercent_Format(1, 0);", "0.125", "12.5%")]
    [InlineData("AFDate_FormatEx(\"dd.mm.yyyy\");", "2026-10-21", "21.10.2026")]
    [InlineData("AFDate_FormatEx(\"mmm d, yyyy\");", "21.10.2026", "21.10.2026")]
    [InlineData("AFSimple_Calculate(\"AVG\", [\"x\", \"y\"]);", "", "3")]
    public void Acrobat_form_functions_produce_expected_values(string script, string value, string expected)
    {
        var host = new DictHost(new() { ["x"] = "2", ["y"] = "4" });
        var runner = new ScriptRunner(host, new ScriptReport(), k => k, CancellationToken.None);
        var r = runner.RunEvent(script, "z", value, "Format");
        Assert.NotNull(r);
        Assert.Equal(expected, r!.Value.Value);
    }

    [Theory]
    [InlineData("AFSimple_Calculate(\"SUM\", new Array (\"A\", \"B\"));", true)]
    [InlineData("AFNumber_Format(2, 0, 0, 0, \"\", true);\nAFNumber_Keystroke(2, 0, 0, 0, \"\", true);", true)]
    [InlineData("AFSimple_Calculate(\"SUM\", x);", false)]
    [InlineData("app.alert('x');", false)]
    [InlineData("AFUnknown(1);", false)]
    [InlineData("AFNumber_Format(2); while(true){}", false)]
    public void Af_only_detection(string code, bool expected) => Assert.Equal(expected, FormScripts.IsAfOnly(code));

    [Fact]
    public async Task Document_scripts_are_listed_but_not_executed()
    {
        var pdf = SamplePdfFactory.WithMetadataAttachmentAndJavaScript();
        var scripts = await _forms.GetScriptsAsync(pdf);
        Assert.Contains(scripts, s => s.FieldName is null && !s.IsEditable);
    }

    [Fact]
    public void Repeated_limit_hits_turn_scripts_off_for_the_document()
    {
        var s = new ServiceCollection();
        s.AddZetPdfCore(new AppPaths(TestPaths.NewDirectory()));
        new AnnotationsModule().ConfigureServices(s);
        using var sp = s.BuildServiceProvider();
        var ctx = sp.GetRequiredService<UI.AnnotContext>();
        var session = new StubSession();
        ctx.SetScriptPolicy(session, ScriptPolicy.All);
        for (int i = 1; i < UI.AnnotContext.ScriptStrikeLimit; i++)
        {
            ctx.ShowScriptReport(session, new ScriptReport { BudgetExceeded = true });
            Assert.Equal(ScriptPolicy.All, ctx.ScriptPolicyFor(session));
        }
        ctx.ShowScriptReport(session, new ScriptReport { LimitHits = 1 });
        Assert.Equal(ScriptPolicy.Off, ctx.ScriptPolicyFor(session));
        // brak pamięci – od razu
        var other = new StubSession();
        ctx.ShowScriptReport(other, new ScriptReport { OutOfMemory = true });
        Assert.Equal(ScriptPolicy.Off, ctx.ScriptPolicyFor(other));
    }

    private sealed class StubSession : IPdfDocumentSession
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string? FilePath => null;
        public string DisplayName => "stub";
        public DocumentSnapshot Current => throw new NotSupportedException();
        public bool IsModified => false;
        public PdfSecurityInfo SecurityInfo => throw new NotSupportedException();
        public DocumentPermissions Permissions => DocumentPermissions.All;
        public IUndoRedoService History => throw new NotSupportedException();
        public Task<bool> ExecuteAsync(IDocumentCommand command, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) => throw new NotSupportedException();
        public event EventHandler<DocumentChangedEventArgs>? Changed { add { } remove { } }
        public event EventHandler? StateChanged { add { } remove { } }
    }

    private sealed class DictHost : IScriptFieldHost
    {
        private readonly Dictionary<string, string> _v;
        public DictHost(Dictionary<string, string> v) => _v = v;
        public bool Has(string name) => _v.ContainsKey(name);
        public string Get(string name) => _v.TryGetValue(name, out var s) ? s : "";
        public void Set(string name, string value) => _v[name] = value;
        public IEnumerable<string> AllNames() => _v.Keys;
    }
}
