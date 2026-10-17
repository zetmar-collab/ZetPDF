using System.Globalization;
using System.Text;

namespace ZetPDF.Pages.Content;

internal enum TokenKind { Number, Name, String, HexString, ArrayStart, ArrayEnd, DictStart, DictEnd, Keyword, Operator, InlineImageData }

/// <summary>Token strumienia treści: rodzaj i zakres bajtów w źródle (bez kopiowania).</summary>
internal readonly record struct ContentToken(TokenKind Kind, int Start, int Length)
{
    public int End => Start + Length;
}

/// <summary>
/// Minimalny lekser strumieni treści PDF (ISO 32000-1, 7.2 i 7.8.2). Zachowuje zakresy bajtów, dzięki czemu
/// przepisywanie strumienia (skala szarości, usuwanie warstw) kopiuje bez zmian wszystko poza podmienianymi
/// operacjami. Obsługuje obrazy w treści (BI … ID dane EI).
/// </summary>
internal sealed class ContentLexer
{
    private readonly byte[] _data;
    private int _pos;
    private bool _pendingInlineData;

    public ContentLexer(byte[] data) => _data = data;

    public byte[] Data => _data;

    public static bool IsWhite(byte c) => c is 0 or 9 or 10 or 12 or 13 or 32;

    public static bool IsDelimiter(byte c) => c is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    public IEnumerable<ContentToken> Tokens()
    {
        while (true)
        {
            if (_pendingInlineData)
            {
                _pendingInlineData = false;
                var t = ReadInlineImageData();
                if (t.Length > 0) yield return t;
                continue;
            }
            SkipWhiteAndComments();
            if (_pos >= _data.Length) yield break;
            var tok = Next();
            if (tok.Kind == TokenKind.Operator && Text(tok) == "ID") _pendingInlineData = true;
            yield return tok;
        }
    }

    public string Text(ContentToken t) => Encoding.Latin1.GetString(_data, t.Start, t.Length);

    public double Number(ContentToken t) =>
        double.TryParse(Text(t), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private void SkipWhiteAndComments()
    {
        while (_pos < _data.Length)
        {
            byte c = _data[_pos];
            if (IsWhite(c)) { _pos++; continue; }
            if (c == '%')
            {
                while (_pos < _data.Length && _data[_pos] != '\n' && _data[_pos] != '\r') _pos++;
                continue;
            }
            break;
        }
    }

    private ContentToken Next()
    {
        int start = _pos;
        byte c = _data[_pos];
        switch (c)
        {
            case (byte)'/':
                _pos++;
                while (_pos < _data.Length && !IsWhite(_data[_pos]) && !IsDelimiter(_data[_pos])) _pos++;
                return new(TokenKind.Name, start, _pos - start);
            case (byte)'(':
                SkipLiteralString();
                return new(TokenKind.String, start, _pos - start);
            case (byte)'<':
                if (_pos + 1 < _data.Length && _data[_pos + 1] == '<') { _pos += 2; return new(TokenKind.DictStart, start, 2); }
                _pos++;
                while (_pos < _data.Length && _data[_pos] != '>') _pos++;
                if (_pos < _data.Length) _pos++;
                return new(TokenKind.HexString, start, _pos - start);
            case (byte)'>':
                if (_pos + 1 < _data.Length && _data[_pos + 1] == '>') { _pos += 2; return new(TokenKind.DictEnd, start, 2); }
                _pos++;
                return new(TokenKind.Operator, start, 1);
            case (byte)'[':
                _pos++;
                return new(TokenKind.ArrayStart, start, 1);
            case (byte)']':
                _pos++;
                return new(TokenKind.ArrayEnd, start, 1);
            case (byte)'{' or (byte)'}' or (byte)')':
                _pos++;
                return new(TokenKind.Operator, start, 1);
        }
        while (_pos < _data.Length && !IsWhite(_data[_pos]) && !IsDelimiter(_data[_pos])) _pos++;
        if (_pos == start) _pos++;
        var tok = new ContentToken(TokenKind.Operator, start, _pos - start);
        var first = _data[start];
        if (first is (byte)'+' or (byte)'-' or (byte)'.' || (first >= '0' && first <= '9')) return tok with { Kind = TokenKind.Number };
        var text = Text(tok);
        if (text is "true" or "false" or "null") return tok with { Kind = TokenKind.Keyword };
        return tok;
    }

    private void SkipLiteralString()
    {
        int depth = 0;
        while (_pos < _data.Length)
        {
            byte c = _data[_pos++];
            if (c == '\\') { _pos++; continue; }
            if (c == '(') depth++;
            else if (c == ')') { depth--; if (depth == 0) return; }
        }
    }

    /// <summary>Dane obrazu w treści: od bajtu po "ID " do białego znaku przed "EI" (zakończonego białym znakiem lub końcem).</summary>
    private ContentToken ReadInlineImageData()
    {
        if (_pos < _data.Length && IsWhite(_data[_pos])) _pos++; // jeden biały znak po ID
        int start = _pos;
        for (int i = _pos; i + 1 < _data.Length; i++)
        {
            if (_data[i] == 'E' && _data[i + 1] == 'I' && (i == 0 || IsWhite(_data[i - 1])) && (i + 2 >= _data.Length || IsWhite(_data[i + 2]) || IsDelimiter(_data[i + 2])))
            {
                _pos = i;
                return new(TokenKind.InlineImageData, start, Math.Max(0, i - 1 - start));
            }
        }
        _pos = _data.Length;
        return new(TokenKind.InlineImageData, start, _data.Length - start);
    }
}

/// <summary>Operacja: operandy (tokeny) + operator. Operandy złożone (tablice, słowniki) jako płaskie tokeny.</summary>
internal sealed class ContentOperation
{
    public ContentOperation(string op, ContentToken opToken, List<ContentToken> operands)
    {
        Operator = op;
        OperatorToken = opToken;
        Operands = operands;
    }

    public string Operator { get; }
    public ContentToken OperatorToken { get; }
    public List<ContentToken> Operands { get; }

    /// <summary>Początek pierwszego operandu (lub operatora).</summary>
    public int Start => Operands.Count > 0 ? Operands[0].Start : OperatorToken.Start;
    public int End => OperatorToken.End;
}

/// <summary>Grupuje tokeny w operacje (operator kończy listę operandów).</summary>
internal static class ContentParser
{
    public static IEnumerable<ContentOperation> Operations(ContentLexer lexer)
    {
        var operands = new List<ContentToken>();
        foreach (var t in lexer.Tokens())
        {
            if (t.Kind == TokenKind.Operator)
            {
                yield return new ContentOperation(lexer.Text(t), t, operands);
                operands = new List<ContentToken>();
            }
            else operands.Add(t);
        }
    }

    /// <summary>Wartości liczbowe operandów (pomija nieliczbowe).</summary>
    public static double[] Numbers(ContentLexer lexer, ContentOperation op) =>
        op.Operands.Where(o => o.Kind == TokenKind.Number).Select(lexer.Number).ToArray();
}
