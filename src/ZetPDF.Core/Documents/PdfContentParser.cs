using System.Runtime.ExceptionServices;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Localization;

namespace ZetPDF.Core.Documents;

/// <summary>Praca na osobnym wątku z dużym stosem, bo przepełnienia stosu w kodzie bibliotek nie da się złapać.</summary>
public static class BigStack
{
    public const int DefaultBytes = 256 * 1024 * 1024;

    /// <summary>Uruchamia i czeka. Stos jest rezerwowany, a zajmowany dopiero w miarę użycia. Po limicie czasu wątek zostaje porzucony.</summary>
    public static T Run<T>(Func<T> work, int stackBytes = DefaultBytes, TimeSpan? timeout = null)
    {
        T result = default!;
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
        }, stackBytes)
        {
            IsBackground = true,
            Name = "ZetPDF BigStack",
        };
        thread.Start();
        if (timeout is { } limit)
        {
            if (!thread.Join(limit)) throw new TimeoutException("Parsing time limit exceeded.");
        }
        else thread.Join();
        error?.Throw();
        return result;
    }
}

/// <summary>
/// Jedyne wejście do <see cref="ContentReader"/>. Parser PDFsharp jest rekurencyjny, a zagnieżdżona tablica w treści wywraca program już przy
/// około 3000 poziomów na zwykłym stosie (sprawdzone uruchomieniem, docs/DECYZJE.md D-42). Ochrona nie zależy od zgodności dwóch parserów:
/// głębokość nie może przekroczyć liczby bajtów „[” w danych, więc limit tej liczby wraz ze stosem 256 MB (ponad 580 tys. poziomów,
/// zmierzone) wyklucza przepełnienie niezależnie od tego, jak parser rozumie dane. Ścisły skaner to kontrola dodatkowa, a głębokość wyniku
/// jest sprawdzana iteracyjnie, zanim trafi do kodu rekurencyjnego (np. ToContent) na zwykłym stosie.
/// </summary>
public static class PdfContentParser
{
    public const int MaxArrayBrackets = 500_000;
    public const int MaxResultDepth = ContentTokenizer.MaxDepth;
    public static readonly TimeSpan ParseTimeout = TimeSpan.FromSeconds(60);

    public static CSequence Parse(byte[] data, CancellationToken ct = default)
    {
        int brackets = 0;
        foreach (byte b in data)
            if (b == '[') brackets++;
        if (brackets > MaxArrayBrackets) throw new ContentTokenException("Too many arrays.", isLimit: true);
        ContentTokenizer.Scan(data, ct: ct);
        ct.ThrowIfCancellationRequested();
        var sequence = BigStack.Run(() => ContentReader.ReadContent(data), timeout: ParseTimeout);
        CheckDepth(sequence);
        return sequence;
    }

    /// <summary>Głębokość tablic w wyniku, bez rekurencji.</summary>
    private static void CheckDepth(CSequence sequence)
    {
        var stack = new Stack<(CSequence Items, int Depth)>();
        stack.Push((sequence, 0));
        while (stack.Count > 0)
        {
            var (items, depth) = stack.Pop();
            if (depth > MaxResultDepth) throw new ContentTokenException("Nesting too deep.");
            foreach (var item in items)
            {
                if (item is COperator op) stack.Push((op.Operands, depth + 1));
                else if (item is CArray array) stack.Push((array, depth + 1));
            }
        }
    }
}

/// <summary>Limit rozmiaru treści przekroczony (ochrona przed bombą kompresji).</summary>
public sealed class PdfContentLimitException(string message) : Exception(message);

/// <summary>Strumienia nie da się zdekodować: uszkodzone dane, nieobsługiwany filtr albo zbyt długi łańcuch.</summary>
public sealed class PdfStreamUnreadableException(string message) : Exception(message);

/// <summary>
/// Dekodowanie strumieni z narastającym limitem, liczonym w trakcie dekodowania każdego filtra. Nigdy nie wraca do <c>UnfilteredValue</c> PDFsharp,
/// które rozpakowuje całość do pamięci: nieobsługiwany filtr, za długi łańcuch lub limit to wyjątek. Nazwy filtrów pochodzą ze sparsowanego słownika,
/// po rozwinięciu referencji i zapisu #xx.
/// </summary>
public static class PdfStreamLimits
{
    /// <summary>Domyślny limit strumienia treści.</summary>
    public const int DefaultContentBytes = 256 * 1024 * 1024;

    /// <summary>Limit metadanych i wartości pól zapisanych w strumieniach.</summary>
    public const int SmallDataBytes = 32 * 1024 * 1024;

    /// <summary>Najdłuższy obsługiwany łańcuch filtrów.</summary>
    public const int MaxFilters = 4;

    public static byte[] Decode(PdfSharp.Pdf.PdfDictionary stream, int maxBytes = DefaultContentBytes)
    {
        var s = stream.Stream;
        if (s is null) return Array.Empty<byte>();
        var data = s.Value ?? Array.Empty<byte>();
        var filters = Filters(stream);
        if (filters.Count == 0)
        {
            if (data.Length > maxBytes) throw TooLarge();
            return data;
        }
        if (filters.Count > MaxFilters) throw new PdfStreamUnreadableException("Filter chain too long.");
        foreach (var filter in filters)
        {
            data = filter switch
            {
                "/FlateDecode" or "/Fl" => InflateLimited(data, maxBytes),
                "/LZWDecode" or "/LZW" => LzwDecodeLimited(data, maxBytes, EarlyChange(stream)),
                "/ASCII85Decode" or "/A85" => Ascii85Limited(data, maxBytes),
                "/ASCIIHexDecode" or "/AHx" => AsciiHexLimited(data, maxBytes),
                "/RunLengthDecode" or "/RL" => RunLengthLimited(data, maxBytes),
                _ => throw new PdfStreamUnreadableException("Unsupported filter: " + filter),
            };
        }
        // Predyktor nie powiększa danych, a rozmiar jest już sprawdzony.
        if (Resolve(stream.Elements["/DecodeParms"]) is { } parms && HasPredictor(parms))
        {
            try { return PdfSharp.Pdf.Filters.Filtering.Decode(s.Value!, Resolve(stream.Elements["/Filter"])!, parms) ?? throw new PdfStreamUnreadableException("Predictor failed."); }
            catch (Exception ex) when (ex is not PdfStreamUnreadableException and not OutOfMemoryException) { throw new PdfStreamUnreadableException(ex.Message); }
        }
        return data;
    }

    private static PdfSharp.Pdf.PdfItem? Resolve(PdfSharp.Pdf.PdfItem? item) => item is PdfSharp.Pdf.Advanced.PdfReference r ? r.Value : item;

    private static bool HasPredictor(PdfSharp.Pdf.PdfItem parms) => parms switch
    {
        PdfSharp.Pdf.PdfDictionary d => d.Elements.GetInteger("/Predictor") > 1,
        PdfSharp.Pdf.PdfArray a => a.Elements.Select(Resolve).OfType<PdfSharp.Pdf.PdfDictionary>().Any(d => d.Elements.GetInteger("/Predictor") > 1),
        _ => false,
    };

    private static bool EarlyChange(PdfSharp.Pdf.PdfDictionary stream)
    {
        var parms = Resolve(stream.Elements["/DecodeParms"]);
        var dict = parms as PdfSharp.Pdf.PdfDictionary ?? (parms as PdfSharp.Pdf.PdfArray)?.Elements.Select(Resolve).OfType<PdfSharp.Pdf.PdfDictionary>().FirstOrDefault(d => d.Elements.ContainsKey("/EarlyChange"));
        return dict is null || !dict.Elements.ContainsKey("/EarlyChange") || dict.Elements.GetInteger("/EarlyChange") != 0;
    }

    /// <summary>Nazwy filtrów ze słownika: nazwa lub tablica, także przez referencje. Zapis #xx rozwinięty, inny typ to błąd.</summary>
    internal static List<string> Filters(PdfSharp.Pdf.PdfDictionary stream)
    {
        var list = new List<string>();
        var item = Resolve(stream.Elements["/Filter"]);
        if (item is null) return list;
        if (item is PdfSharp.Pdf.PdfName name) list.Add(Unescape(name.Value));
        else if (item is PdfSharp.Pdf.PdfArray array)
        {
            if (array.Elements.Count > MaxFilters) throw new PdfStreamUnreadableException("Filter chain too long.");
            foreach (var e in array.Elements)
            {
                if (Resolve(e) is PdfSharp.Pdf.PdfName n) list.Add(Unescape(n.Value));
                else throw new PdfStreamUnreadableException("Bad filter entry.");
            }
        }
        else throw new PdfStreamUnreadableException("Bad filter entry.");
        return list;
    }

    private static string Unescape(string name)
    {
        if (!name.Contains('#')) return name;
        var sb = new System.Text.StringBuilder(name.Length);
        for (int i = 0; i < name.Length; i++)
        {
            if (name[i] == '#' && i + 2 < name.Length && Uri.IsHexDigit(name[i + 1]) && Uri.IsHexDigit(name[i + 2]))
            {
                sb.Append((char)Convert.ToInt32(name.Substring(i + 1, 2), 16));
                i += 2;
            }
            else sb.Append(name[i]);
        }
        return sb.ToString();
    }

    /// <summary>Rozpakowanie Flate z przerwaniem po przekroczeniu limitu. Dane urwane po części wyniku zwracają tę część, jak PDFium.</summary>
    public static byte[] InflateLimited(byte[] data, int maxBytes)
    {
        using var input = new MemoryStream(data, false);
        return InflateLimited(input, maxBytes);
    }

    public static byte[] InflateLimited(Stream input, int maxBytes)
    {
        using var z = new System.IO.Compression.ZLibStream(input, System.IO.Compression.CompressionMode.Decompress, leaveOpen: true);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        try
        {
            int read;
            while ((read = z.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + read > maxBytes) throw TooLarge();
                output.Write(buffer, 0, read);
            }
        }
        catch (InvalidDataException ex)
        {
            if (output.Length == 0) throw new PdfStreamUnreadableException(ex.Message);
        }
        return output.ToArray();
    }

    private static byte[] LzwDecodeLimited(byte[] data, int maxBytes, bool earlyChange)
    {
        const int Clear = 256, End = 257;
        using var output = new MemoryStream();
        var prefix = new int[4097];
        var suffix = new byte[4097];
        var scratch = new byte[4098];
        int next = 258, width = 9, previous = -1, bitBuffer = 0, bits = 0, early = earlyChange ? 1 : 0;
        byte first = 0;
        foreach (byte b in data)
        {
            bitBuffer = (bitBuffer << 8) | b;
            bits += 8;
            while (bits >= width)
            {
                int code = (bitBuffer >> (bits - width)) & ((1 << width) - 1);
                bits -= width;
                bitBuffer &= (1 << bits) - 1;
                if (code == Clear)
                {
                    next = 258;
                    width = 9;
                    previous = -1;
                    continue;
                }
                if (code == End) return output.ToArray();
                int length = 0, cur = code;
                if (code >= next)
                {
                    if (previous < 0 || code > next) throw new PdfStreamUnreadableException("Bad LZW data.");
                    scratch[length++] = first;
                    cur = previous;
                }
                while (cur >= 258)
                {
                    scratch[length++] = suffix[cur];
                    cur = prefix[cur];
                    if (length >= scratch.Length) throw new PdfStreamUnreadableException("Bad LZW data.");
                }
                if (cur > 255) throw new PdfStreamUnreadableException("Bad LZW data.");
                first = (byte)cur;
                scratch[length++] = first;
                if (output.Length + length > maxBytes) throw TooLarge();
                for (int i = length - 1; i >= 0; i--) output.WriteByte(scratch[i]);
                if (previous >= 0 && next < 4096)
                {
                    prefix[next] = previous;
                    suffix[next] = first;
                    next++;
                }
                previous = code;
                if (next + early >= 2048) width = 12;
                else if (next + early >= 1024) width = 11;
                else if (next + early >= 512) width = 10;
                else width = 9;
            }
        }
        return output.ToArray();
    }

    private static byte[] Ascii85Limited(byte[] data, int maxBytes)
    {
        using var output = new MemoryStream(Math.Min(data.Length, maxBytes));
        Span<byte> group = stackalloc byte[5];
        int count = 0;
        void Flush(int n, Span<byte> g)
        {
            for (int k = n; k < 5; k++) g[k] = 84;
            uint value = 0;
            for (int k = 0; k < 5; k++) value = value * 85 + g[k];
            for (int k = 0; k < n - 1; k++) output.WriteByte((byte)(value >> (24 - 8 * k)));
        }
        foreach (byte b in data)
        {
            if (b is 0 or 9 or 10 or 12 or 13 or 32) continue;
            if (b == '~') break;
            if (output.Length + 4 > maxBytes) throw TooLarge();
            if (b == 'z' && count == 0)
            {
                output.Write(new byte[4]);
                continue;
            }
            if (b < '!' || b > 'u') throw new PdfStreamUnreadableException("Bad ASCII85 data.");
            group[count++] = (byte)(b - '!');
            if (count == 5)
            {
                Flush(5, group);
                count = 0;
            }
        }
        if (count > 1) Flush(count, group);
        return output.ToArray();
    }

    private static byte[] AsciiHexLimited(byte[] data, int maxBytes)
    {
        using var output = new MemoryStream(Math.Min(data.Length / 2 + 1, maxBytes));
        int high = -1;
        foreach (byte b in data)
        {
            if (b is 0 or 9 or 10 or 12 or 13 or 32) continue;
            if (b == '>') break;
            if (!Uri.IsHexDigit((char)b)) throw new PdfStreamUnreadableException("Bad hex data.");
            int v = Convert.ToInt32(((char)b).ToString(), 16);
            if (high < 0) { high = v; continue; }
            if (output.Length >= maxBytes) throw TooLarge();
            output.WriteByte((byte)(high << 4 | v));
            high = -1;
        }
        if (high >= 0) output.WriteByte((byte)(high << 4));
        return output.ToArray();
    }

    private static byte[] RunLengthLimited(byte[] data, int maxBytes)
    {
        using var output = new MemoryStream();
        for (int i = 0; i < data.Length;)
        {
            int length = data[i++];
            if (length == 128) break;
            if (length < 128)
            {
                int n = Math.Min(length + 1, data.Length - i);
                if (output.Length + n > maxBytes) throw TooLarge();
                output.Write(data, i, n);
                i += n;
            }
            else
            {
                int n = 257 - length;
                if (i >= data.Length) break;
                if (output.Length + n > maxBytes) throw TooLarge();
                for (int k = 0; k < n; k++) output.WriteByte(data[i]);
                i++;
            }
        }
        return output.ToArray();
    }

    private static PdfContentLimitException TooLarge() => new PdfContentLimitException("Decoded stream exceeds the size limit.").WithMessageKey("Core.Error.ContentTooLarge");
}
