using System.Diagnostics;
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
