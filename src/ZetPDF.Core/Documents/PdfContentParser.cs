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
    public const int LargeBytes = 1024 * 1024 * 1024;

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

/// <summary>
/// Dekodowanie strumieni z limitem działającym w trakcie dekompresji. <c>PdfStream.UnfilteredValue</c> z PDFsharp rozpakowuje całość do pamięci,
/// a 1 MB danych Flate to około 1 GB po rozpakowaniu, więc najpierw przechodzi kontrola strumieniowa, a dopiero potem dekodowanie.
/// </summary>
public static class PdfStreamLimits
{
    /// <summary>Domyślny limit strumienia treści.</summary>
    public const int DefaultContentBytes = 256 * 1024 * 1024;

    /// <summary>Limit metadanych i wartości pól zapisanych w strumieniach.</summary>
    public const int SmallDataBytes = 32 * 1024 * 1024;

    public static byte[] Decode(PdfSharp.Pdf.PdfDictionary stream, int maxBytes = DefaultContentBytes)
    {
        var s = stream.Stream;
        if (s is null) return Array.Empty<byte>();
        var raw = s.Value ?? Array.Empty<byte>();
        var filters = Filters(stream);
        if (filters.Count == 0)
        {
            if (raw.Length > maxBytes) throw TooLarge();
            return raw;
        }
        var data = raw;
        for (int i = 0; i < filters.Count; i++)
        {
            bool last = i == filters.Count - 1;
            switch (filters[i])
            {
                case "/FlateDecode" or "/Fl":
                    try { data = InflateLimited(data, maxBytes); }
                    catch (InvalidDataException) { return s.UnfilteredValue ?? Array.Empty<byte>(); }
                    break;
                case "/ASCII85Decode" or "/A85" or "/ASCIIHexDecode" or "/AHx":
                    if (data.Length / 4 * 5 > maxBytes) throw TooLarge();
                    if (!last) data = PdfSharp.Pdf.Filters.Filtering.Decode(data, filters[i]) ?? Array.Empty<byte>();
                    break;
                case "/RunLengthDecode" or "/RL":
                    if (data.Length * 128L > maxBytes) throw TooLarge();
                    if (!last) data = PdfSharp.Pdf.Filters.Filtering.Decode(data, filters[i]) ?? Array.Empty<byte>();
                    break;
                case "/LZWDecode" or "/LZW":
                    if (data.Length * 4096L > maxBytes) throw TooLarge();
                    if (!last) data = PdfSharp.Pdf.Filters.Filtering.Decode(data, filters[i]) ?? Array.Empty<byte>();
                    break;
                default:
                    // filtry obrazów (DCT, JPX, CCITT, JBIG2) nie są dekodowane przez UnfilteredValue
                    return s.UnfilteredValue ?? Array.Empty<byte>();
            }
        }
        // Kontrola przeszła: pełne dekodowanie z predyktorami przez PDFsharp. Predyktor nie powiększa danych.
        return filters.Count == 1 && filters[0] is "/FlateDecode" or "/Fl" && !stream.Elements.ContainsKey("/DecodeParms") && !stream.Elements.ContainsKey("/DP")
            ? data
            : s.UnfilteredValue ?? Array.Empty<byte>();
    }

    private static List<string> Filters(PdfSharp.Pdf.PdfDictionary stream)
    {
        var list = new List<string>();
        var item = stream.Elements["/Filter"];
        if (item is PdfSharp.Pdf.Advanced.PdfReference r) item = r.Value;
        if (item is PdfSharp.Pdf.PdfName name) list.Add(name.Value);
        else if (item is PdfSharp.Pdf.PdfArray array)
            foreach (var e in array.Elements.Take(16))
                if ((e is PdfSharp.Pdf.Advanced.PdfReference er ? er.Value : e) is PdfSharp.Pdf.PdfName n) list.Add(n.Value);
        return list;
    }

    /// <summary>Rozpakowanie Flate z przerwaniem po przekroczeniu limitu.</summary>
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
        int read;
        while ((read = z.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + read > maxBytes) throw TooLarge();
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static PdfContentLimitException TooLarge() => new PdfContentLimitException("Decoded stream exceeds the size limit.").WithMessageKey("Core.Error.ContentTooLarge");
}
