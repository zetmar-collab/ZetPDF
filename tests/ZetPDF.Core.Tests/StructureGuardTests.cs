using System.IO.Compression;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Content.Objects;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.TestData;

namespace ZetPDF.Core.Tests;

/// <summary>
/// Szybkie odrzucanie, parser treści i dekodowanie z limitem. Skan nie decyduje o bezpieczeństwie: pliki ze strumieniami obiektów i zaszyfrowane
/// sprawdza sonda w osobnym procesie (ZetPDF.App.Tests.PdfProbeProcessTests).
/// </summary>
public class StructureGuardTests
{
    private static byte[] Latin(string s) => Encoding.Latin1.GetBytes(s);
    private static string Arrays(int d) => new string('[', d) + "1" + new string(']', d);
    private static string Dicts(int d) => string.Concat(Enumerable.Repeat("<</A ", d)) + "1" + string.Concat(Enumerable.Repeat(">>", d));

    private static byte[] Plain(string nested)
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Latin(s));
        var off = new long[5];
        W("%PDF-1.4\n");
        off[1] = ms.Position; W("1 0 obj\n<</Type/Catalog/Pages 2 0 R>>\nendobj\n");
        off[2] = ms.Position; W("2 0 obj\n<</Type/Pages/Kids[3 0 R]/Count 1>>\nendobj\n");
        off[3] = ms.Position; W("3 0 obj\n<</Type/Page/Parent 2 0 R/MediaBox[0 0 100 100]/X 4 0 R>>\nendobj\n");
        off[4] = ms.Position; W("4 0 obj\n" + nested + "\nendobj\n");
        long x = ms.Position;
        W("xref\n0 5\n0000000000 65535 f \n");
        for (int i = 1; i < 5; i++) W(off[i].ToString("D10") + " 00000 n \n");
        W("trailer\n<</Size 5/Root 1 0 R>>\nstartxref\n" + x + "\n%%EOF\n");
        return ms.ToArray();
    }

    public static IEnumerable<object[]> DeepFiles()
    {
        foreach (int d in new[] { 300, 10_000, 100_000, 1_000_000 })
        {
            yield return new object[] { "tablice " + d, Plain(Arrays(d)) };
            yield return new object[] { "słowniki " + d, Plain(Dicts(d)) };
        }
        yield return new object[] { "nawias w napisie", Plain(string.Concat(Enumerable.Repeat("[(])", 20_000))) };
        yield return new object[] { "niepasujące zamknięcia", Plain(string.Concat(Enumerable.Repeat("[>>", 20_000))) };
        yield return new object[] { "endobj w tablicy", Plain(string.Concat(Enumerable.Repeat("[ endobj ", 20_000))) };
        yield return new object[] { "stream w tablicy", Plain(string.Concat(Enumerable.Repeat("[ stream ", 20_000))) };
        yield return new object[] { "komentarz nie ukrywa", Plain("%komentarz\n" + Arrays(5000)) };
    }

    [Theory]
    [MemberData(nameof(DeepFiles))]
    public void Obvious_depth_is_rejected_before_pdfsharp(string name, byte[] pdf)
    {
        Assert.NotEmpty(name);
        Assert.Throws<PdfCorruptException>(() => PdfStructureGuard.RejectObviousDepth(pdf));
        Assert.Throws<PdfCorruptException>(() => PdfSharpEdit.Read(pdf, d => d.PageCount));
        Assert.Throws<PdfCorruptException>(() => PdfSharpEdit.Modify(pdf, _ => { }));
    }

    [Fact]
    public void Object_hidden_inside_a_fake_stream_region_is_still_seen()
    {
        var pdf = Plain("<<>>\nstream\n 7 0 obj\n" + Arrays(5000) + "\nendobj\nendstream");
        Assert.Throws<PdfCorruptException>(() => PdfStructureGuard.RejectObviousDepth(pdf));
    }

    [Fact]
    public void Quadratic_object_markers_hit_the_budget_not_the_clock()
    {
        var pdf = Plain("[ " + string.Concat(Enumerable.Repeat("1 0 obj ", 200_000)) + "]");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Throws<PdfCorruptException>(() => PdfStructureGuard.RejectObviousDepth(pdf));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), sw.Elapsed.ToString());
    }

    [Fact]
    public void Normal_documents_pass_the_quick_scan_and_open()
    {
        foreach (var pdf in new[] { SamplePdfFactory.Text(3), SamplePdfFactory.WithForm(), SamplePdfFactory.WithAnnotations(), SamplePdfFactory.WithBookmarks(), Plain(Arrays(100)) })
        {
            PdfStructureGuard.RejectObviousDepth(pdf);
            Assert.True(PdfSharpEdit.Read(pdf, d => d.PageCount) > 0);
        }
    }

    [Fact]
    public void Text_encrypt_in_content_means_nothing()
    {
        // ten plik nie jest zaszyfrowany, choć zawiera taki napis
        var pdf = Plain("(/Encrypt 9 0 R)");
        Assert.False(PdfSecurityHelper.IsEncrypted(pdf));
    }

    [Fact]
    public void Random_binary_stream_does_not_count()
    {
        var random = new byte[4 * 1024 * 1024];
        new Random(7).NextBytes(random);
        var pdf = Latin("%PDF-1.4\n1 0 obj\n<</Length " + random.Length + ">>\nstream\n").Concat(random).Concat(Latin("\nendstream\nendobj\n")).ToArray();
        PdfStructureGuard.RejectObviousDepth(pdf);
    }

    [Fact]
    public async Task Default_probe_rejects_everything_and_errors_fail_closed()
    {
        Assert.False(await new UnconfiguredPdfProbe().IsSafeAsync(SamplePdfFactory.Text(1), null, default));
        Assert.False(await new ProcessPdfProbe(@"C:\nie ma takiego pliku.exe").IsSafeAsync(SamplePdfFactory.Text(1), null, default));
    }

    // ------------------------------------------------------------------ treść

    [Theory]
    [InlineData(100)]
    [InlineData(10_000)]
    [InlineData(400_000)]
    public void Deep_content_never_reaches_parser_on_a_small_stack(int depth) =>
        Assert.Throws<ContentTokenException>(() => PdfContentParser.Parse(Latin(new string('[', depth) + "(x)" + new string(']', depth) + " TJ")));

    [Theory]
    [InlineData("q ] Q")]
    [InlineData("[ >> ]")]
    [InlineData("<< ]")]
    [InlineData("[ (x)")]
    public void Mismatched_closing_is_rejected_not_clamped(string content) =>
        Assert.Throws<ContentTokenException>(() => ContentTokenizer.Scan(Latin(content)));

    [Fact]
    public void Too_many_array_brackets_are_rejected_regardless_of_parser_view()
    {
        var data = new byte[PdfContentParser.MaxArrayBrackets + 1];
        Array.Fill(data, (byte)'[');
        var ex = Assert.Throws<ContentTokenException>(() => PdfContentParser.Parse(data));
        Assert.True(ex.IsLimit);
    }

    [Fact]
    public void Normal_content_parses_and_inline_dictionary_keeps_operands()
    {
        var seq = PdfContentParser.Parse(Latin("q BT /F1 12 Tf [(a) -5 (b)] TJ ET /P <</MCID 1>> BDC EMC Q"));
        Assert.Contains(seq.OfType<COperator>(), o => o.OpCode.Name == "TJ");
    }

    [Fact]
    public void Big_stack_propagates_exceptions_and_timeout()
    {
        Assert.Throws<InvalidOperationException>(() => BigStack.Run<int>(() => throw new InvalidOperationException()));
        Assert.Equal(5, BigStack.Run(() => 5));
        Assert.Throws<TimeoutException>(() => BigStack.Run(() =>
        {
            Thread.Sleep(2000);
            return 1;
        }, timeout: TimeSpan.FromMilliseconds(50)));
    }

    // ------------------------------------------------------------------ dekompresja i filtry

    private static byte[] Deflate(byte[] plain)
    {
        var z = new MemoryStream();
        using (var zl = new ZLibStream(z, CompressionLevel.Optimal, true)) zl.Write(plain);
        return z.ToArray();
    }

    private static PdfDictionary Stream(PdfDocument doc, byte[] raw, PdfItem filter)
    {
        var d = new PdfDictionary(doc);
        d.CreateStream(raw);
        d.Elements["/Filter"] = filter;
        return d;
    }

    private static string A85(byte[] data)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < data.Length; i += 4)
        {
            int n = Math.Min(4, data.Length - i);
            uint v = 0;
            for (int k = 0; k < 4; k++) v = v << 8 | (k < n ? data[i + k] : (byte)0);
            var g = new char[5];
            for (int k = 4; k >= 0; k--)
            {
                g[k] = (char)('!' + v % 85);
                v /= 85;
            }
            sb.Append(g, 0, n + 1);
        }
        return sb.Append("~>").ToString();
    }

    [Fact]
    public void Stream_limit_stops_during_inflation()
    {
        using var doc = new PdfDocument();
        var bomb = Stream(doc, Deflate(new byte[64 * 1024 * 1024]), new PdfName("/FlateDecode"));
        Assert.True(bomb.Stream.Value.Length < 200 * 1024);
        long before = GC.GetTotalAllocatedBytes(true);
        Assert.Throws<PdfContentLimitException>(() => PdfStreamLimits.Decode(bomb, 1024 * 1024));
        Assert.True(GC.GetTotalAllocatedBytes(true) - before < 16 * 1024 * 1024);
        Assert.Equal(Latin("q Q"), PdfStreamLimits.Decode(Stream(doc, Deflate(Latin("q Q")), new PdfName("/FlateDecode")), 1024));
    }

    [Theory]
    [InlineData("/ASCIIHexDecode", "4142>", "AB")]
    [InlineData("/AHx", "41 42>", "AB")]
    [InlineData("/RunLengthDecode", "\u0001AB\u00FFC\u0080", "ABCC")]
    public void Other_filters_decode(string filter, string raw, string expected)
    {
        using var doc = new PdfDocument();
        Assert.Equal(Latin(expected), PdfStreamLimits.Decode(Stream(doc, Latin(raw), new PdfName(filter))));
    }

    [Fact]
    public void Ascii85_matches_original()
    {
        using var doc = new PdfDocument();
        var plain = Latin("Treść strumienia, dłuższa niż grupa pięciu znaków!");
        Assert.Equal(plain, PdfStreamLimits.Decode(Stream(doc, Latin(A85(plain)), new PdfName("/ASCII85Decode"))));
    }

    [Fact]
    public void Lzw_matches_known_vector_from_pdf_specification()
    {
        // przykład z ISO 32000-1, 7.4.4.2: 45 45 45 45 45 65 45 45 45 66
        using var doc = new PdfDocument();
        var raw = new byte[] { 0x80, 0x0B, 0x60, 0x50, 0x22, 0x0C, 0x0C, 0x85, 0x01 };
        var expected = new byte[] { 45, 45, 45, 45, 45, 65, 45, 45, 45, 66 };
        Assert.Equal(expected, PdfStreamLimits.Decode(Stream(doc, raw, new PdfName("/LZWDecode"))));
    }

    [Fact]
    public void Filter_names_with_hash_escapes_arrays_and_references_are_recognized()
    {
        using var doc = new PdfDocument();
        var raw = Deflate(Latin("q Q"));
        Assert.Equal(Latin("q Q"), PdfStreamLimits.Decode(Stream(doc, raw, new PdfName("/Fl#61teDecode"))));
        Assert.Equal(Latin("q Q"), PdfStreamLimits.Decode(Stream(doc, raw, new PdfArray(doc, new PdfName("/Fl#61te#44ecode")))));
        var indirect = new PdfArray(doc, new PdfName("/FlateDecode"));
        doc.Internals.AddObject(indirect);
        Assert.Equal(Latin("q Q"), PdfStreamLimits.Decode(Stream(doc, raw, indirect.Reference!)));
    }

    [Fact]
    public void Bomb_behind_a_filter_chain_stops_at_the_limit()
    {
        using var doc = new PdfDocument();
        var inner = Deflate(new byte[8 * 1024 * 1024]);
        var hexed = Encoding.ASCII.GetBytes(Convert.ToHexString(inner) + ">");
        var chain = Stream(doc, hexed, new PdfArray(doc, new PdfName("/AHx"), new PdfName("/Fl")));
        Assert.Throws<PdfContentLimitException>(() => PdfStreamLimits.Decode(chain, 1024 * 1024));
        var doubled = Stream(doc, Deflate(Deflate(new byte[8 * 1024 * 1024])), new PdfArray(doc, new PdfName("/Fl"), new PdfName("/Fl")));
        Assert.Throws<PdfContentLimitException>(() => PdfStreamLimits.Decode(doubled, 1024 * 1024));
        Assert.Equal(8 * 1024 * 1024, PdfStreamLimits.Decode(doubled).Length);
    }

    [Fact]
    public void Unsupported_or_too_long_chain_is_unreadable_not_passed_to_pdfsharp()
    {
        using var doc = new PdfDocument();
        Assert.Throws<PdfStreamUnreadableException>(() => PdfStreamLimits.Decode(Stream(doc, Latin("x"), new PdfName("/DCTDecode"))));
        Assert.Throws<PdfStreamUnreadableException>(() => PdfStreamLimits.Decode(Stream(doc, Latin("x"), new PdfName("/Nieznany"))));
        var longChain = new PdfArray(doc);
        for (int i = 0; i <= PdfStreamLimits.MaxFilters; i++) longChain.Elements.Add(new PdfName("/AHx"));
        Assert.Throws<PdfStreamUnreadableException>(() => PdfStreamLimits.Decode(Stream(doc, Latin("41>"), longChain)));
        Assert.Throws<PdfStreamUnreadableException>(() => PdfStreamLimits.Decode(Stream(doc, Latin("nie jest zlib"), new PdfName("/FlateDecode"))));
    }

    [Fact]
    public void Zip_bomb_with_png_predictor_stops_during_inflation()
    {
        using var doc = new PdfDocument();
        var d = Stream(doc, Deflate(new byte[64 * 1024 * 1024]), new PdfName("/FlateDecode"));
        var parms = new PdfDictionary(doc);
        parms.Elements["/Predictor"] = new PdfInteger(15);
        parms.Elements["/Columns"] = new PdfInteger(100);
        d.Elements["/DecodeParms"] = parms;
        long before = GC.GetTotalAllocatedBytes(true);
        Assert.Throws<PdfContentLimitException>(() => PdfStreamLimits.Decode(d, 1024 * 1024));
        Assert.True(GC.GetTotalAllocatedBytes(true) - before < 16 * 1024 * 1024);
    }

    [Theory]
    [InlineData(10, 3, 8, new byte[] { 0, 1, 2, 3, 1, 1, 1, 1 }, new byte[] { 1, 2, 3, 1, 2, 3 })]
    [InlineData(11, 3, 8, new byte[] { 1, 1, 1, 1 }, new byte[] { 1, 2, 3 })]
    [InlineData(13, 2, 8, new byte[] { 0, 4, 6, 3, 2, 2 }, new byte[] { 4, 6, 4, 7 })]
    [InlineData(14, 2, 8, new byte[] { 0, 10, 20, 4, 1, 1 }, new byte[] { 10, 20, 11, 21 })]
    [InlineData(2, 3, 8, new byte[] { 1, 1, 1 }, new byte[] { 1, 2, 3 })]
    [InlineData(2, 4, 4, new byte[] { 0x11, 0x11 }, new byte[] { 0x12, 0x34 })]
    public void Predictors_decode_correctly(int predictor, int columns, int bits, byte[] input, byte[] expected)
    {
        using var doc = new PdfDocument();
        var d = Stream(doc, Deflate(input), new PdfName("/FlateDecode"));
        var parms = new PdfDictionary(doc);
        parms.Elements["/Predictor"] = new PdfInteger(predictor);
        parms.Elements["/Columns"] = new PdfInteger(columns);
        parms.Elements["/BitsPerComponent"] = new PdfInteger(bits);
        d.Elements["/DecodeParms"] = new PdfArray(doc, parms);
        Assert.Equal(expected, PdfStreamLimits.Decode(d));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Bad_predictor_parameters_are_unreadable(int bits)
    {
        using var doc = new PdfDocument();
        var d = Stream(doc, Deflate(new byte[] { 0, 1 }), new PdfName("/FlateDecode"));
        var parms = new PdfDictionary(doc);
        parms.Elements["/Predictor"] = new PdfInteger(12);
        parms.Elements["/BitsPerComponent"] = new PdfInteger(bits);
        d.Elements["/DecodeParms"] = parms;
        Assert.Throws<PdfStreamUnreadableException>(() => PdfStreamLimits.Decode(d));
    }

    [Fact]
    public void Predictor_stream_decodes_through_the_same_limit()
    {
        using var doc = new PdfDocument();
        // PNG Up, kolumny 3: wiersz 1 = 1 2 3, wiersz 2 = 1 1 1 względem poprzedniego
        var raw = Deflate(new byte[] { 2, 1, 2, 3, 2, 1, 1, 1 });
        var d = Stream(doc, raw, new PdfName("/FlateDecode"));
        var parms = new PdfDictionary(doc);
        parms.Elements["/Predictor"] = new PdfInteger(12);
        parms.Elements["/Columns"] = new PdfInteger(3);
        d.Elements["/DecodeParms"] = parms;
        Assert.Equal(new byte[] { 1, 2, 3, 2, 3, 4 }, PdfStreamLimits.Decode(d));
        Assert.Throws<PdfContentLimitException>(() => PdfStreamLimits.Decode(d, 2));
    }
}
