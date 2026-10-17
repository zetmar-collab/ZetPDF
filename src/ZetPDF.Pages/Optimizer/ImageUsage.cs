using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using ZetPDF.Core.Contracts;
using ZetPDF.Pages.Content;
using ZetPDF.Pages.Engine;

namespace ZetPDF.Pages.Optimizer;

/// <summary>
/// Największy rozmiar (w calach), w jakim obraz jest rysowany na stronach – z macierzy CTM w chwili operatora Do
/// (śledzenie q/Q/cm, także wewnątrz formularzy). Służy do wyliczenia efektywnego DPI przy zmniejszaniu obrazów.
/// </summary>
internal static class ImageUsage
{
    public static Dictionary<PdfObjectID, (double W, double H)> Collect(PdfDocument doc, CancellationToken ct)
    {
        var result = new Dictionary<PdfObjectID, (double, double)>();
        foreach (var page in doc.Pages)
        {
            ct.ThrowIfCancellationRequested();
            var res = PdfIo.GetDict(page, "/Resources");
            Walk(PdfIo.ReadContent(page), res, PdfMatrix.Identity, result, 0);
        }
        return result;
    }

    private static void Walk(byte[] content, PdfDictionary? resources, PdfMatrix start, Dictionary<PdfObjectID, (double W, double H)> result, int depth)
    {
        if (depth > 8 || content.Length == 0) return;
        var lexer = new ContentLexer(content);
        var stack = new Stack<PdfMatrix>();
        var ctm = start;
        var xobjects = resources is null ? null : PdfIo.GetDict(resources, "/XObject");
        foreach (var op in ContentParser.Operations(lexer))
        {
            switch (op.Operator)
            {
                case "q": stack.Push(ctm); break;
                case "Q": if (stack.Count > 0) ctm = stack.Pop(); break;
                case "cm":
                {
                    var n = ContentParser.Numbers(lexer, op);
                    if (n.Length == 6) ctm = new PdfMatrix(n[0], n[1], n[2], n[3], n[4], n[5]).Then(ctm);
                    break;
                }
                case "Do":
                {
                    if (xobjects is null || op.Operands.Count == 0) break;
                    var name = lexer.Text(op.Operands[^1]);
                    var item = xobjects.Elements[name];
                    if (PdfIo.Resolve(item) is not PdfDictionary x) break;
                    var sub = PdfIo.GetName(x, "/Subtype");
                    if (sub == "/Image" && item is PdfReference r)
                    {
                        double w = Math.Sqrt(ctm.A * ctm.A + ctm.B * ctm.B) / 72.0, h = Math.Sqrt(ctm.C * ctm.C + ctm.D * ctm.D) / 72.0;
                        if (result.TryGetValue(r.ObjectID, out var old)) result[r.ObjectID] = (Math.Max(old.W, w), Math.Max(old.H, h));
                        else result[r.ObjectID] = (w, h);
                    }
                    else if (sub == "/Form")
                    {
                        var mm = PdfIo.GetNumbers(x, "/Matrix");
                        var fm = mm is { Length: 6 } ? new PdfMatrix(mm[0], mm[1], mm[2], mm[3], mm[4], mm[5]) : PdfMatrix.Identity;
                        Walk(PdfIo.StreamData(x), PdfIo.GetDict(x, "/Resources") ?? resources, fm.Then(ctm), result, depth + 1);
                    }
                    break;
                }
            }
        }
    }
}
