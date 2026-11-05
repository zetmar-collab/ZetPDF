using ZetPDF.Core.Contracts;
using ZetPDF.Core.Localization;

namespace ZetPDF.Core.Documents;

/// <summary>
/// Szybkie odrzucenie plików oczywiście zbyt głębokich, zanim ruszy cokolwiek droższego. To NIE jest dowód bezpieczeństwa: skan nie widzi strumieni
/// obiektów ani danych zaszyfrowanych i zgaduje, co zobaczy parser. Bezpieczeństwo pliku z dysku rozstrzyga sonda w osobnym procesie
/// (<see cref="PdfFileProbes"/>, docs/DECYZJE.md D-44). Skan jest iteracyjny, niepasujące zamknięcie nie zmniejsza głębokości, budżet bajtów
/// chroni przed kwadratowym „1 0 obj 1 0 obj”.
/// </summary>
public static class PdfStructureGuard
{
    public const int MaxDepth = 256;

    /// <summary>Wyjątek pliku uszkodzonego lub zbyt złożonego, z komunikatem dla użytkownika.</summary>
    public static PdfCorruptException TooComplex() =>
        new PdfCorruptException("The file is damaged or is too complex to read safely.").WithMessageKey("Core.Error.TooComplex");

    public static void RejectObviousDepth(byte[] pdf, CancellationToken ct = default)
    {
        var open = new byte[MaxDepth + 1];
        long budget = pdf.Length * 16L + 1024 * 1024;
        var span = pdf.AsSpan();
        int pos = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int rel = span[pos..].IndexOf("obj"u8);
            if (rel < 0) break;
            int at = pos + rel;
            pos = at + 3;
            if (IsObjectStart(pdf, at)) ScanValue(pdf, pos, open, ref budget, ct);
        }
        pos = 0;
        while (true)
        {
            int rel = span[pos..].IndexOf("trailer"u8);
            if (rel < 0) break;
            pos += rel + 7;
            ScanValue(pdf, pos, open, ref budget, ct);
        }
    }

    private static bool IsObjectStart(byte[] d, int at)
    {
        int i = at - 1;
        while (i >= 0 && IsWhite(d[i])) i--;
        int digits = 0;
        while (i >= 0 && d[i] is >= (byte)'0' and <= (byte)'9') { i--; digits++; }
        if (digits == 0) return false;
        int white = 0;
        while (i >= 0 && IsWhite(d[i])) { i--; white++; }
        if (white == 0) return false;
        digits = 0;
        while (i >= 0 && d[i] is >= (byte)'0' and <= (byte)'9') { i--; digits++; }
        return digits > 0;
    }

    private static void ScanValue(byte[] d, int i, byte[] open, ref long budget, CancellationToken ct)
    {
        int n = d.Length, depth = 0, count = 0;
        while (i < n)
        {
            if ((++count & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            if (--budget < 0) throw TooComplex();
            byte c = d[i];
            if (IsWhite(c)) { i++; continue; }
            switch (c)
            {
                case (byte)'%':
                    while (i < n && d[i] != '\n' && d[i] != '\r') { i++; budget--; }
                    continue;
                case (byte)'(':
                {
                    int level = 0;
                    for (; i < n; i++)
                    {
                        budget--;
                        byte s = d[i];
                        if (s == '\\') i++;
                        else if (s == '(') level++;
                        else if (s == ')' && --level == 0) { i++; break; }
                    }
                    continue;
                }
                case (byte)'<':
                    if (i + 1 < n && d[i + 1] == '<')
                    {
                        if (depth >= MaxDepth) throw TooComplex();
                        open[depth++] = (byte)'<';
                        i += 2;
                        continue;
                    }
                    while (i < n && d[i] != '>') { i++; budget--; }
                    i++;
                    continue;
                case (byte)'>':
                    if (i + 1 < n && d[i + 1] == '>')
                    {
                        if (depth > 0 && open[depth - 1] == '<') depth--;
                        i += 2;
                    }
                    else i++;
                    continue;
                case (byte)'[':
                    if (depth >= MaxDepth) throw TooComplex();
                    open[depth++] = (byte)'[';
                    i++;
                    continue;
                case (byte)']':
                    if (depth > 0 && open[depth - 1] == '[') depth--;
                    i++;
                    continue;
            }
            int start = i;
            while (i < n && !IsWhite(d[i]) && !IsDelimiter(d[i])) i++;
            if (i == start) { i++; continue; }
            budget -= i - start;
            if (depth == 0)
            {
                var word = d.AsSpan(start, i - start);
                if (word.SequenceEqual("stream"u8) || word.SequenceEqual("endobj"u8) || word.SequenceEqual("endstream"u8) || word.SequenceEqual("xref"u8) || word.SequenceEqual("startxref"u8) || word.SequenceEqual("trailer"u8)) return;
            }
        }
    }

    private static bool IsWhite(byte c) => c is 0 or 9 or 10 or 12 or 13 or 32;

    private static bool IsDelimiter(byte c) => c is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';
}
