using System.IO.Compression;

namespace ZetPDF.Security.Pdf;

/// <summary>Dekodowanie filtrów potrzebnych do struktury pliku (strumienie xref i obiektów): Flate + predyktory PNG.</summary>
internal static class CosFilters
{
    public static byte[] Decode(CosStream stream, Func<CosObject?, CosObject?> resolve)
    {
        var filter = resolve(stream.Dictionary["Filter"]);
        var parms = resolve(stream.Dictionary["DecodeParms"]);
        var data = stream.Data;
        if (filter is null) return data;
        var filters = filter is CosArray fa ? fa.Items.Select(i => resolve(i)).ToList() : new List<CosObject?> { filter };
        var parmList = parms is CosArray pa ? pa.Items.Select(i => resolve(i)).ToList() : new List<CosObject?> { parms };
        for (int i = 0; i < filters.Count; i++)
        {
            var name = (filters[i] as CosName)?.Value;
            var p = i < parmList.Count ? parmList[i] as CosDictionary : null;
            data = name switch
            {
                "FlateDecode" or "Fl" => ApplyPredictor(Inflate(data), p, resolve),
                _ => throw new NotSupportedException($"Filter {name} is not supported for structural streams."),
            };
        }
        return data;
    }

    public static byte[] Inflate(byte[] data)
    {
        try
        {
            return ZetPDF.Core.Documents.PdfStreamLimits.InflateLimited(data, ZetPDF.Core.Documents.PdfStreamLimits.DefaultContentBytes);
        }
        catch (InvalidDataException) when (data.Length > 2)
        {
            // bez nagłówka zlib albo uszkodzona suma kontrolna – próba surowego deflate
            using var d = new DeflateStream(new MemoryStream(data, 2, data.Length - 2), CompressionMode.Decompress);
            using var ms = new MemoryStream();
            var buffer = new byte[81920];
            try
            {
                int read;
                while ((read = d.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (ms.Length + read > ZetPDF.Core.Documents.PdfStreamLimits.DefaultContentBytes) throw new FormatException("Decoded stream exceeds the size limit.");
                    ms.Write(buffer, 0, read);
                }
            }
            catch (InvalidDataException) { }
            return ms.ToArray();
        }
    }

    public static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    private static byte[] ApplyPredictor(byte[] data, CosDictionary? parms, Func<CosObject?, CosObject?> resolve)
    {
        if (parms is null) return data;
        int predictor = (resolve(parms["Predictor"]) as CosNumber)?.AsInt ?? 1;
        if (predictor < 10) return data; // 1 = brak; 2 (TIFF) w strukturze praktycznie nie występuje
        int colors = (resolve(parms["Colors"]) as CosNumber)?.AsInt ?? 1;
        int bpc = (resolve(parms["BitsPerComponent"]) as CosNumber)?.AsInt ?? 8;
        int columns = (resolve(parms["Columns"]) as CosNumber)?.AsInt ?? 1;
        int bpp = Math.Max(1, colors * bpc / 8);
        int rowLen = (columns * colors * bpc + 7) / 8;
        var output = new MemoryStream();
        var prev = new byte[rowLen];
        var row = new byte[rowLen];
        int pos = 0;
        while (pos < data.Length)
        {
            int type = data[pos++];
            int n = Math.Min(rowLen, data.Length - pos);
            Array.Clear(row);
            Array.Copy(data, pos, row, 0, n);
            pos += n;
            for (int i = 0; i < rowLen; i++)
            {
                int left = i >= bpp ? row[i - bpp] : 0;
                int up = prev[i];
                int upLeft = i >= bpp ? prev[i - bpp] : 0;
                row[i] = type switch
                {
                    1 => (byte)(row[i] + left),
                    2 => (byte)(row[i] + up),
                    3 => (byte)(row[i] + ((left + up) >> 1)),
                    4 => (byte)(row[i] + Paeth(left, up, upLeft)),
                    _ => row[i],
                };
            }
            output.Write(row, 0, rowLen);
            (prev, row) = (row, prev);
        }
        return output.ToArray();
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
