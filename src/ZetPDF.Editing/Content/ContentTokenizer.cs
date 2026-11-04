using System.Text;

namespace ZetPDF.Editing.Content;

/// <summary>Operator w bajtach treści: [Start, End) obejmuje operandy i nazwę; dla obrazu w treści całe BI … EI.</summary>
internal readonly record struct OpToken(string Name, int Start, int End, bool InlineImage);

/// <summary>Treść nie daje się bezpiecznie przeczytać: zbyt głębokie zagnieżdżenie, zbyt wiele operatorów, urwany napis.</summary>
internal sealed class ContentTokenException(string message) : Exception(message);

/// <summary>
/// Minimalny, iteracyjny skaner strumienia treści: znajduje granice operatorów w oryginalnych bajtach, żeby dało się wstawiać znaczniki
/// bez ponownego zapisu reszty treści. Uruchamiany przed parserem PDFsharp, który jest rekurencyjny: zagnieżdżenie głębsze niż
/// <see cref="MaxDepth"/> kończy się wyjątkiem, zanim tablica z pliku zdąży przepełnić stos (tego nie da się złapać).
/// </summary>
internal static class ContentTokenizer
{
    public const int MaxDepth = 64;

    public static List<OpToken> Scan(byte[] data, int maxOps, CancellationToken ct = default)
    {
        var ops = new List<OpToken>();
        int i = 0, n = data.Length, depth = 0, opStart = -1;
        while (i < n)
        {
            if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            byte c = data[i];
            if (IsWhite(c)) { i++; continue; }
            if (c == '%')
            {
                while (i < n && data[i] != '\n' && data[i] != '\r') i++;
                continue;
            }
            if (opStart < 0) opStart = i;
            switch (c)
            {
                case (byte)'(':
                    i = SkipString(data, i);
                    continue;
                case (byte)'<':
                    if (i + 1 < n && data[i + 1] == '<')
                    {
                        if (++depth > MaxDepth) throw new ContentTokenException("Nesting too deep.");
                        i += 2;
                        continue;
                    }
                    while (i < n && data[i] != '>') i++;
                    i++;
                    continue;
                case (byte)'>':
                    if (i + 1 < n && data[i + 1] == '>')
                    {
                        depth = Math.Max(0, depth - 1);
                        i += 2;
                        continue;
                    }
                    i++;
                    continue;
                case (byte)'[':
                    if (++depth > MaxDepth) throw new ContentTokenException("Nesting too deep.");
                    i++;
                    continue;
                case (byte)']':
                    depth = Math.Max(0, depth - 1);
                    i++;
                    continue;
                case (byte)'{':
                case (byte)'}':
                case (byte)')':
                    i++;
                    continue;
                case (byte)'/':
                    i++;
                    while (i < n && !IsWhite(data[i]) && !IsDelimiter(data[i])) i++;
                    continue;
            }

            int start = i;
            while (i < n && !IsWhite(data[i]) && !IsDelimiter(data[i])) i++;
            if (i == start) { i++; continue; }
            if (IsOperand(data, start, i)) continue;
            var name = Encoding.ASCII.GetString(data, start, i - start);
            if (depth == 0)
            {
                if (name == "BI")
                {
                    i = SkipInlineImage(data, i);
                    Add(ops, new OpToken("EI", start, i, true), maxOps);
                    opStart = -1;
                    continue;
                }
                Add(ops, new OpToken(name, opStart, i, false), maxOps);
                opStart = -1;
            }
        }
        if (depth != 0) throw new ContentTokenException("Unbalanced array or dictionary.");
        return ops;
    }

    private static void Add(List<OpToken> ops, OpToken op, int maxOps)
    {
        if (ops.Count >= maxOps) throw new ContentTokenException("Too many operators.");
        ops.Add(op);
    }

    private static bool IsOperand(byte[] d, int start, int end)
    {
        byte c = d[start];
        if (c is >= (byte)'0' and <= (byte)'9' or (byte)'+' or (byte)'-' or (byte)'.') return true;
        int len = end - start;
        return (len == 4 && Match(d, start, "true")) || (len == 5 && Match(d, start, "false")) || (len == 4 && Match(d, start, "null"));
    }

    private static bool Match(byte[] d, int start, string s)
    {
        for (int k = 0; k < s.Length; k++)
            if (d[start + k] != s[k]) return false;
        return true;
    }

    private static int SkipString(byte[] d, int i)
    {
        int level = 0;
        for (; i < d.Length; i++)
        {
            byte c = d[i];
            if (c == '\\') i++;
            else if (c == '(') level++;
            else if (c == ')' && --level == 0) return i + 1;
        }
        throw new ContentTokenException("Unterminated string.");
    }

    /// <summary>Od BI przez słownik do ID, potem dane do EI otoczonego białymi znakami.</summary>
    private static int SkipInlineImage(byte[] d, int i)
    {
        int n = d.Length;
        while (i + 1 < n && !(d[i] == 'I' && d[i + 1] == 'D' && IsWhite(d[i - 1]) && (i + 2 >= n || IsWhite(d[i + 2])))) i++;
        if (i + 1 >= n) throw new ContentTokenException("Inline image without ID.");
        i += 2;
        while (i + 1 < n)
        {
            if (d[i] == 'E' && d[i + 1] == 'I' && IsWhite(d[i - 1]) && (i + 2 >= n || IsWhite(d[i + 2]))) return i + 2;
            i++;
        }
        throw new ContentTokenException("Inline image without EI.");
    }

    private static bool IsWhite(byte c) => c is 0 or 9 or 10 or 12 or 13 or 32;

    private static bool IsDelimiter(byte c) => c is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';
}
