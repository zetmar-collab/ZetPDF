using Jint;
using ZetPDF.Annotations.Forms.Scripting;

namespace ZetPDF.Annotations.Tests;

/// <summary>
/// Utwardzone funkcje wbudowane preludium (replace/replaceAll, JSON.stringify, fill, concat): wynik musi być taki sam
/// jak w czystym silniku Jint (bez preludium) – porównanie z natywną implementacją, nie z ręcznie wpisanym tekstem.
/// </summary>
public class ScriptSandboxBuiltinsTests
{
    private sealed class MemoryHost : IScriptFieldHost
    {
        private readonly Dictionary<string, string> _v = new() { ["C"] = "" };
        public bool Has(string name) => _v.ContainsKey(name);
        public string Get(string name) => _v[name];
        public void Set(string name, string value) => _v[name] = value;
        public IEnumerable<string> AllNames() => _v.Keys;
    }

    public static IEnumerable<object[]> Expressions() => new[]
    {
        """JSON.stringify({ a: [1, { b: 'ż\n"' }], c: new Number(2), s: new String('q'), t: new Boolean(false), d: [], e: {} }, null, 2)""",
        """JSON.stringify({ a: [1, { b: 2 }], c: 'x' }, null, '--')""",
        """JSON.stringify({ a: [1, { b: 2 }] }, null, 40)""",
        """JSON.stringify({ x: undefined, y: 1, z: [] }, null, -5)""",
        """JSON.stringify([undefined, function () { }, NaN, Infinity, -0, 'a"b\u0001', null, true])""",
        """String(JSON.stringify(undefined)) + String(JSON.stringify(function () { }))""",
        """JSON.stringify({ a: 1, b: [2, 3], c: { d: 4 } }, function (k, v) { return typeof v === 'number' ? v * 10 : v; })""",
        """JSON.stringify({ a: 1, b: 2, c: { a: 5, z: 6 } }, ['a', 'c'])""",
        """JSON.stringify([new Date(0), { toJSON: function (k) { return 'key:' + k; } }])""",
        """'ab-cd'.replace(/(\w+)-(?<y>\w+)/, "$2|$<y>|$1|$&|$$|$`x$'|$9|$0|$")""",
        """'a1b22c333'.replace(/(\d)(\d)?/g, '[$1,$2,$01,$10]')""",
        """'aXbXc'.replace('X', '[$&]') + 'aXbXc'.replaceAll('X', function (m, i, s) { return i + s.length; })""",
        """'aaa'.replace(/a/g, '$`|') + 'abc'.replace(/(?<n>b)/, '<$<n>$<zz>>')""",
        """'x'.replace(/(y)?x/, '[$1]') + 'xyz'.replaceAll('', '-')""",
        """[1, 2, 3, 4].fill(0, 1, -1).join('') + [1, 2, 3].fill(9).join('') + [1, 2, 3].fill(7, -2).join('')""",
        """'q'.concat(1, null, undefined, [2, 3]) + ''.concat()""",
        """['a', null, undefined, 1].join() + [1, [2, 3]].join(';') + [1.5, 2].toLocaleString().length""",
        """'ab'.repeat(3) + 'x'.padStart(4, 'ab') + 'x'.padEnd(3) + '|'""",
    }.Select(e => new object[] { e });

    [Theory]
    [MemberData(nameof(Expressions))]
    public void Hardened_builtins_match_native_jint(string expression)
    {
        var native = new Engine(o => o.Constraints.RegexTimeout = RelaxedTime).Evaluate(expression).ToString();
        var report = new ScriptReport();
        var r = FunctionalRunner(new MemoryHost(), report).RunEvent("event.value = " + expression + ";", "C", "", "Calculate");
        Assert.Empty(report.Errors);
        Assert.Equal(native, r!.Value.Value);
    }

    private static readonly TimeSpan RelaxedTime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Runner do testów wyniku funkcji: limity czasu liczą czas zegarowy (wyrażenie regularne 200 ms łącznie z JIT), więc pod
    /// obciążeniem przekraczał je poprawny skrypt i test padał bez związku z tym, co sprawdza. Reszta limitów bez zmian.
    /// </summary>
    internal static ScriptRunner FunctionalRunner(IScriptFieldHost host, ScriptReport report) =>
        new(host, report, k => k, CancellationToken.None, new ScriptBudget(RelaxedTime), new ScriptTimeLimits(RelaxedTime), RelaxedTime);

    [Theory]
    [InlineData("event.value = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!'.replace(/(a+)+$/, 'x');")]
    public void Regex_timeout_is_a_script_error_and_a_limit_hit(string script)
    {
        var report = new ScriptReport();
        var host = new MemoryHost();
        var r = new ScriptRunner(host, report, k => k, CancellationToken.None, new ScriptBudget(RelaxedTime), scriptTimeout: RelaxedTime).RunEvent(script, "C", "", "Calculate");
        Assert.Null(r);
        Assert.Contains(report.Errors, e => e.Contains(nameof(System.Text.RegularExpressions.RegexMatchTimeoutException)));
        Assert.True(report.HitLimits);
        Assert.Equal("", host.Get("C"));
    }

    [Fact]
    public void Default_regex_limit_stays_200_ms() => Assert.Equal(TimeSpan.FromMilliseconds(200), ScriptTimeLimits.Default.Regex);
}
