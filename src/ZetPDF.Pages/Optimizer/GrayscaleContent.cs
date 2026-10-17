using System.Text;
using PdfSharp.Pdf;
using ZetPDF.Pages.Content;
using ZetPDF.Pages.Engine;

namespace ZetPDF.Pages.Optimizer;

/// <summary>
/// Przepisuje kolory w strumieniu treści na skalę szarości: rg/RG, k/K → g/G; cs/CS z przestrzenią RGB/CMYK
/// (Device, Cal, ICCBased N=3/4) → /DeviceGray z przeliczeniem sc/scn/SC/SCN. Wzorce (Pattern), Separation, DeviceN,
/// Indexed w treści i obrazy w treści (BI…EI) pozostają bez zmian (ograniczenie – opcja rasteryzacji).
/// Stan przestrzeni barw jest częścią stanu graficznego (q/Q) i przechodzi między kolejnymi strumieniami strony.
/// </summary>
internal sealed class GrayscaleContent
{
    private enum Space { Gray, Rgb, Cmyk, Other }

    private (Space Fill, Space Stroke) _state = (Space.Gray, Space.Gray);
    private readonly Stack<(Space, Space)> _stack = new();

    public bool Changed { get; private set; }

    public byte[] Convert(byte[] content, PdfDictionary? resources)
    {
        var lexer = new ContentLexer(content);
        var output = new MemoryStream(content.Length);
        int copied = 0;
        var colorSpaces = resources is null ? null : PdfIo.GetDict(resources, "/ColorSpace");
        foreach (var op in ContentParser.Operations(lexer))
        {
            string? replacement = null;
            switch (op.Operator)
            {
                case "q": _stack.Push(_state); break;
                case "Q": if (_stack.Count > 0) _state = _stack.Pop(); break;
                case "rg":
                case "RG":
                {
                    var n = ContentParser.Numbers(lexer, op);
                    if (n.Length == 3) replacement = $"{PdfIo.Num(Gray(n[0], n[1], n[2]))} {(op.Operator == "rg" ? "g" : "G")}";
                    if (op.Operator == "rg") _state.Fill = Space.Gray; else _state.Stroke = Space.Gray;
                    break;
                }
                case "k":
                case "K":
                {
                    var n = ContentParser.Numbers(lexer, op);
                    if (n.Length == 4) replacement = $"{PdfIo.Num(GrayCmyk(n[0], n[1], n[2], n[3]))} {(op.Operator == "k" ? "g" : "G")}";
                    if (op.Operator == "k") _state.Fill = Space.Gray; else _state.Stroke = Space.Gray;
                    break;
                }
                case "g": _state.Fill = Space.Gray; break;
                case "G": _state.Stroke = Space.Gray; break;
                case "cs":
                case "CS":
                {
                    var space = Space.Other;
                    if (op.Operands.Count == 1 && op.Operands[0].Kind == TokenKind.Name)
                        space = Classify(lexer.Text(op.Operands[0]), colorSpaces);
                    bool fill = op.Operator == "cs";
                    if (space is Space.Rgb or Space.Cmyk) replacement = "/DeviceGray " + op.Operator;
                    if (fill) _state.Fill = space; else _state.Stroke = space;
                    break;
                }
                case "sc":
                case "scn":
                case "SC":
                case "SCN":
                {
                    bool fill = op.Operator is "sc" or "scn";
                    var space = fill ? _state.Fill : _state.Stroke;
                    var n = ContentParser.Numbers(lexer, op);
                    bool onlyNumbers = n.Length == op.Operands.Count;
                    if (onlyNumbers && space == Space.Rgb && n.Length == 3) replacement = $"{PdfIo.Num(Gray(n[0], n[1], n[2]))} {op.Operator}";
                    else if (onlyNumbers && space == Space.Cmyk && n.Length == 4) replacement = $"{PdfIo.Num(GrayCmyk(n[0], n[1], n[2], n[3]))} {op.Operator}";
                    break;
                }
            }
            if (replacement is null) continue;
            output.Write(content, copied, op.Start - copied);
            output.Write(Encoding.Latin1.GetBytes(replacement));
            copied = op.End;
            Changed = true;
        }
        if (copied == 0) return content;
        output.Write(content, copied, content.Length - copied);
        return output.ToArray();
    }

    private static Space Classify(string name, PdfDictionary? colorSpaces)
    {
        switch (name)
        {
            case "/DeviceRGB": return Space.Rgb;
            case "/DeviceCMYK": return Space.Cmyk;
            case "/DeviceGray": return Space.Gray;
            case "/Pattern": return Space.Other;
        }
        var cs = colorSpaces?.Elements[name];
        if (cs is null) return Space.Other;
        var ch = ImageCodec.Channels(cs);
        if (ch == 3) return Space.Rgb;
        if (ch == 1) return Space.Gray;
        if (PdfIo.Resolve(cs) is PdfArray a && a.Elements.Count >= 2 && PdfIo.Resolve(a.Elements[0]) is PdfName { Value: "/ICCBased" }
            && PdfIo.Resolve(a.Elements[1]) is PdfDictionary icc && PdfIo.GetNumber(icc.Elements["/N"]) == 4) return Space.Cmyk;
        if (PdfIo.Resolve(cs) is PdfName n) return Classify(n.Value, null);
        return Space.Other;
    }

    public static double Gray(double r, double g, double b) => Math.Clamp(0.299 * r + 0.587 * g + 0.114 * b, 0, 1);

    public static double GrayCmyk(double c, double m, double y, double k) =>
        Gray((1 - c) * (1 - k), (1 - m) * (1 - k), (1 - y) * (1 - k));
}
