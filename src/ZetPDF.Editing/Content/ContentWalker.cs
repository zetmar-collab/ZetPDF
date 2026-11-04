using PdfSharp.Pdf;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using ZetPDF.Core.Contracts;
using ZetPDF.Editing.Pdf;
using ZetPDF.Ocr.Pdf;

namespace ZetPDF.Editing.Content;

/// <summary>Glif w strumieniu: operator, pozycja w nim, kod, prostokąt w przestrzeni użytkownika strony.</summary>
internal sealed record GlyphInfo(int OpIndex, int GlyphIndex, int Code, PdfRect Box, double Advance, double FontSize, double HScale, bool TwoByte, string? Font = null);

/// <summary>Obiekt strony z zakresem operatorów [Start, End] w sekwencji najwyższego poziomu.</summary>
internal sealed record ContentObject(PageObjectKind Kind, int Start, int End, PdfRect Bounds, PdfMatrix Matrix, string? XObjectName = null);

/// <summary>
/// Interpreter strumienia treści strony (operatory najwyższego poziomu; formularze XObject jako całość):
/// stan graficzny i tekstowy (q/Q, cm, BT/ET, Tm/Td/TD/T*, Tc/Tw/Tz/TL/Ts/Tf), pozycje glifów (szerokości z czcionek),
/// obiekty (obrazy i formularze z operatora Do, ścieżki, bloki tekstu). Pozwala usunąć glify z operatorów
/// Tj/TJ/'/" (zastąpienie przesunięciem w TJ – pozostały tekst zostaje na miejscu) i przekształcać obiekty.
/// </summary>
internal sealed class ContentWalker
{
    private readonly PdfPage _page;
    private readonly Dictionary<string, FontMetrics> _fonts = new();

    private readonly int _maxGlyphs;

    /// <param name="maxGlyphs">Limit glifów (pamięć zależy od danych z pliku); po przekroczeniu <see cref="ContentTokenException"/>.</param>
    public ContentWalker(PdfPage page, int maxGlyphs = int.MaxValue)
    {
        _page = page;
        _maxGlyphs = maxGlyphs;
        Sequence = PageContent.Parse(page);
        Walk();
    }

    public CSequence Sequence { get; }
    public List<GlyphInfo> Glyphs { get; } = new();
    public List<ContentObject> Objects { get; } = new();
    /// <summary>Czy wszystkie operatory tekstu miały obsługiwane czcionki (szerokości).</summary>
    public bool AllFontsSupported { get; private set; } = true;

    private COperator Op(int i) => (COperator)Sequence[i];

    private sealed class State
    {
        public PdfMatrix Ctm = PdfMatrix.Identity;
        public double Tc, Tw, Th = 1, Tl, Ts, Fs = 1;
        public string? Font;
        public State Clone() => (State)MemberwiseClone();
    }

    private void Walk()
    {
        var st = new State();
        var stack = new Stack<State>();
        PdfMatrix tm = PdfMatrix.Identity, tlm = PdfMatrix.Identity;
        int textStart = -1;
        PdfRect? textBounds = null;
        int pathStart = -1;
        bool clip = false;
        var pathPts = new List<PdfPoint>();

        for (int i = 0; i < Sequence.Count; i++)
        {
            if (Sequence[i] is not COperator op) continue;
            var a = op.Operands;
            double N(int k) => k < a.Count ? Num(a[k]) : 0;
            switch (op.OpCode.Name)
            {
                case "q": stack.Push(st.Clone()); break;
                case "Q": if (stack.Count > 0) st = stack.Pop(); break;
                case "cm": st.Ctm = new PdfMatrix(N(0), N(1), N(2), N(3), N(4), N(5)).Then(st.Ctm); break;
                case "BT": tm = tlm = PdfMatrix.Identity; textStart = i; textBounds = null; break;
                case "ET":
                    if (textStart >= 0 && textBounds is { } tb) Objects.Add(new ContentObject(PageObjectKind.Text, textStart, i, tb, st.Ctm));
                    textStart = -1;
                    break;
                case "Tc": st.Tc = N(0); break;
                case "Tw": st.Tw = N(0); break;
                case "Tz": st.Th = N(0) / 100.0; break;
                case "TL": st.Tl = N(0); break;
                case "Ts": st.Ts = N(0); break;
                case "Tf":
                    st.Font = a.Count > 0 && a[0] is CName fn ? fn.Name : null;
                    st.Fs = N(1);
                    break;
                case "Td": tlm = PdfMatrix.Translation(N(0), N(1)).Then(tlm); tm = tlm; break;
                case "TD": st.Tl = -N(1); tlm = PdfMatrix.Translation(N(0), N(1)).Then(tlm); tm = tlm; break;
                case "Tm": tlm = tm = new PdfMatrix(N(0), N(1), N(2), N(3), N(4), N(5)); break;
                case "T*": tlm = PdfMatrix.Translation(0, -st.Tl).Then(tlm); tm = tlm; break;
                case "Tj":
                    if (a.Count > 0 && a[0] is CString s1) Show(i, new CObject[] { s1 }, st, ref tm, ref textBounds);
                    break;
                case "TJ":
                    if (a.Count > 0 && a[0] is CArray arr) Show(i, arr.ToArray(), st, ref tm, ref textBounds);
                    break;
                case "'":
                    tlm = PdfMatrix.Translation(0, -st.Tl).Then(tlm); tm = tlm;
                    if (a.Count > 0 && a[0] is CString s2) Show(i, new CObject[] { s2 }, st, ref tm, ref textBounds);
                    break;
                case "\"":
                    st.Tw = N(0); st.Tc = N(1);
                    tlm = PdfMatrix.Translation(0, -st.Tl).Then(tlm); tm = tlm;
                    if (a.Count > 2 && a[2] is CString s3) Show(i, new CObject[] { s3 }, st, ref tm, ref textBounds);
                    break;
                case "Do":
                    if (a.Count > 0 && a[0] is CName xn)
                    {
                        var kind = XObjectKind(xn.Name);
                        if (kind is { } k)
                            Objects.Add(new ContentObject(k, i, i, Bounds(st.Ctm, new PdfPoint(0, 0), new PdfPoint(1, 0), new PdfPoint(0, 1), new PdfPoint(1, 1)), st.Ctm, xn.Name));
                    }
                    break;
                case "m": case "l": case "c": case "v": case "y": case "re": case "h":
                    if (pathStart < 0) { pathStart = i; pathPts.Clear(); clip = false; }
                    if (op.OpCode.Name == "re" && a.Count >= 4)
                    {
                        double x = N(0), y = N(1), w = N(2), h = N(3);
                        pathPts.AddRange(new[] { new PdfPoint(x, y), new PdfPoint(x + w, y), new PdfPoint(x, y + h), new PdfPoint(x + w, y + h) }.Select(st.Ctm.Transform));
                    }
                    else
                        for (int k = 0; k + 1 < a.Count; k += 2) pathPts.Add(st.Ctm.Transform(new PdfPoint(N(k), N(k + 1))));
                    break;
                case "W": case "W*": clip = true; break;
                case "S": case "s": case "f": case "F": case "f*": case "B": case "B*": case "b": case "b*":
                    if (pathStart >= 0 && !clip && pathPts.Count > 0)
                        Objects.Add(new ContentObject(PageObjectKind.Shape, pathStart, i, BoundsOf(pathPts), st.Ctm));
                    pathStart = -1;
                    break;
                case "n": pathStart = -1; break;
            }
        }
    }

    private PageObjectKind? XObjectKind(string name)
    {
        var res = PdfObjects.Resolve(_page.Elements["/Resources"]) as PdfDictionary;
        var xo = res is null ? null : PdfObjects.Resolve(res.Elements["/XObject"]) as PdfDictionary;
        if (xo is null || PdfObjects.Resolve(xo.Elements[name]) is not PdfDictionary d) return null;
        return d.Elements.GetName("/Subtype") switch
        {
            "/Image" => PageObjectKind.Image,
            "/Form" => PageObjectKind.Form,
            _ => null,
        };
    }

    private FontMetrics Metrics(string? name)
    {
        if (name is null) return FontMetrics.Unsupported;
        if (_fonts.TryGetValue(name, out var m)) return m;
        var res = PdfObjects.Resolve(_page.Elements["/Resources"]) as PdfDictionary;
        var fonts = res is null ? null : PdfObjects.Resolve(res.Elements["/Font"]) as PdfDictionary;
        m = FontMetrics.From(fonts is null ? null : PdfObjects.Resolve(fonts.Elements[name]) as PdfDictionary);
        _fonts[name] = m;
        return m;
    }

    private void Show(int opIndex, IEnumerable<CObject> parts, State st, ref PdfMatrix tm, ref PdfRect? textBounds)
    {
        var font = Metrics(st.Font);
        if (!font.IsSupported) AllFontsSupported = false;
        int g = 0;
        foreach (var part in parts)
        {
            if (part is CString cs)
            {
                var v = cs.Value;
                int step = font.TwoByte ? 2 : 1;
                for (int k = 0; k + step - 1 < v.Length; k += step)
                {
                    int code = font.TwoByte ? ((v[k] & 0xFF) << 8) | (v[k + 1] & 0xFF) : v[k] & 0xFF;
                    double w0 = font.IsSupported ? font.Width(code) / 1000.0 : 0.5;
                    bool space = !font.TwoByte && code == 32;
                    double tx = (w0 * st.Fs + st.Tc + (space ? st.Tw : 0)) * st.Th;
                    var trm = new PdfMatrix(st.Fs * st.Th, 0, 0, st.Fs, 0, st.Ts).Then(tm).Then(st.Ctm);
                    var box = Bounds(trm, new PdfPoint(0, -0.2), new PdfPoint(w0, -0.2), new PdfPoint(0, 0.8), new PdfPoint(w0, 0.8));
                    if (Glyphs.Count >= _maxGlyphs) throw new ContentTokenException("Too many glyphs.");
                    Glyphs.Add(new GlyphInfo(opIndex, g++, code, box, tx, st.Fs, st.Th, font.TwoByte, st.Font));
                    if (!space && code != 0) textBounds = textBounds is { } tb ? tb.Union(box) : box;
                    tm = PdfMatrix.Translation(tx, 0).Then(tm);
                }
            }
            else
            {
                double n = Num(part);
                tm = PdfMatrix.Translation(-n / 1000.0 * st.Fs * st.Th, 0).Then(tm);
            }
        }
    }

    private static PdfRect Bounds(PdfMatrix m, params PdfPoint[] pts) => BoundsOf(pts.Select(m.Transform).ToList());

    private static PdfRect BoundsOf(IReadOnlyList<PdfPoint> pts) =>
        new(pts.Min(p => p.X), pts.Min(p => p.Y), pts.Max(p => p.X), pts.Max(p => p.Y));

    public static double Num(CObject o) => o switch
    {
        CInteger i => i.Value,
        CReal r => r.Value,
        _ => 0,
    };

    // ------------------------------------------------------------------ modyfikacje

    /// <summary>
    /// Usuwa glify spełniające warunek. Operator z usuniętymi glifami jest zastępowany przez TJ, w którym usunięte glify
    /// zastąpiono przesunięciem o ich szerokość (pozostały tekst się nie przesuwa). Zwraca liczbę usuniętych glifów.
    /// </summary>
    public int RemoveGlyphs(Func<GlyphInfo, bool> remove, out byte[] content)
    {
        var byOp = Glyphs.GroupBy(g => g.OpIndex).ToDictionary(g => g.Key, g => g.ToList());
        int removed = 0;
        var output = new CSequence();
        for (int i = 0; i < Sequence.Count; i++)
        {
            if (Sequence[i] is not COperator op || !byOp.TryGetValue(i, out var glyphs) || !glyphs.Any(remove))
            {
                output.Add(Sequence[i]);
                continue;
            }
            var name = op.OpCode.Name;
            IEnumerable<CObject> parts;
            if (name == "TJ") parts = ((CArray)op.Operands[0]).ToArray();
            else if (name == "\"")
            {
                output.Add(Make("Tw", op.Operands[0]));
                output.Add(Make("Tc", op.Operands[1]));
                output.Add(Make("T*"));
                parts = new[] { op.Operands[2] };
            }
            else if (name == "'")
            {
                output.Add(Make("T*"));
                parts = new[] { op.Operands[0] };
            }
            else parts = new[] { op.Operands[0] };

            var font = glyphs[0];
            var arr = new CArray();
            int gi = 0;
            var sb = new System.Text.StringBuilder();
            bool twoByte = false;
            foreach (var part in parts)
            {
                if (part is CString cs)
                {
                    int step = glyphs[0].TwoByte ? 2 : 1;
                    twoByte |= step == 2;
                    for (int k = 0; k + step - 1 < cs.Value.Length; k += step, gi++)
                    {
                        var g = gi < glyphs.Count ? glyphs[gi] : null;
                        if (g is not null && remove(g))
                        {
                            removed++;
                            if (sb.Length > 0) { arr.Add(new CString { Value = sb.ToString(), CStringType = cs.CStringType }); sb.Clear(); }
                            double fs = g.FontSize * g.HScale;
                            if (Math.Abs(fs) > 1e-9) arr.Add(new CReal { Value = -g.Advance / fs * 1000.0 });
                        }
                        else sb.Append(cs.Value, k, step);
                    }
                    if (sb.Length > 0) { arr.Add(new CString { Value = sb.ToString(), CStringType = cs.CStringType }); sb.Clear(); }
                }
                else arr.Add(part);
            }
            _ = font;
            var tj = OpCodes.OperatorFromName("TJ");
            tj.Operands.Add(arr);
            output.Add(tj);
        }
        content = output.ToContent();
        return removed;
    }


    private static COperator Make(string name, params CObject[] operands)
    {
        var op = OpCodes.OperatorFromName(name);
        foreach (var o in operands) op.Operands.Add(o);
        return op;
    }

    /// <summary>Owija zakres operatorów w "q M cm … Q".</summary>
    public byte[] WrapWithMatrix(int start, int end, PdfMatrix m)
    {
        var output = new CSequence();
        for (int i = 0; i < Sequence.Count; i++)
        {
            if (i == start)
            {
                output.Add(Make("q"));
                output.Add(Make("cm", new CReal { Value = m.A }, new CReal { Value = m.B }, new CReal { Value = m.C }, new CReal { Value = m.D }, new CReal { Value = m.E }, new CReal { Value = m.F }));
            }
            output.Add(Sequence[i]);
            if (i == end) output.Add(Make("Q"));
        }
        return output.ToContent();
    }

    /// <summary>Usuwa zakres operatorów (zachowując q/Q i operatory stanu, które w nim są – żeby nie rozbić bilansu).</summary>
    public byte[] RemoveRange(int start, int end)
    {
        var output = new CSequence();
        for (int i = 0; i < Sequence.Count; i++)
        {
            if (i >= start && i <= end && Sequence[i] is COperator op)
            {
                var n = op.OpCode.Name;
                bool keep = n is "q" or "Q" or "cm" or "gs" or "Tf" or "Tc" or "Tw" or "Tz" or "TL" or "Ts" or "BT" or "ET" or "rg" or "RG" or "g" or "G" or "k" or "K" or "cs" or "CS" or "sc" or "SC" or "scn" or "SCN" or "w";
                if (!keep) continue;
            }
            output.Add(Sequence[i]);
        }
        return output.ToContent();
    }

    /// <summary>Zastępuje operator Do obrazu: inna nazwa zasobu, opcjonalnie poprzedzona macierzą (w przestrzeni jednostkowej obrazu).</summary>
    public byte[] ReplaceDo(int index, string newName, PdfMatrix? unitMatrix)
    {
        var output = new CSequence();
        for (int i = 0; i < Sequence.Count; i++)
        {
            if (i != index) { output.Add(Sequence[i]); continue; }
            if (unitMatrix is { } m)
            {
                output.Add(Make("q"));
                output.Add(Make("cm", new CReal { Value = m.A }, new CReal { Value = m.B }, new CReal { Value = m.C }, new CReal { Value = m.D }, new CReal { Value = m.E }, new CReal { Value = m.F }));
            }
            output.Add(Make("Do", new CName(newName)));
            if (unitMatrix is not null) output.Add(Make("Q"));
        }
        return output.ToContent();
    }
}
