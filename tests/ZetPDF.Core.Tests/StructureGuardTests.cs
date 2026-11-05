using System.IO.Compression;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Content.Objects;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.TestData;

namespace ZetPDF.Core.Tests;

/// <summary>
/// Pliki i treść spreparowane pod przepełnienie stosu PDFsharp. Kontrola musi odrzucić je PRZED parserem: gdyby przepuściła, proces testów padnie
/// (StackOverflow), co też będzie widoczne. Pomiary w osobnym procesie: scratch\DepthProbe, wynik w docs/DECYZJE.md D-42.
/// </summary>
public class StructureGuardTests
{
    private static byte[] Latin(string s) => Encoding.Latin1.GetBytes(s);
    private static string Arrays(int d) => new string('[', d) + "1" + new string(']', d);
    private static string Dicts(int d) => string.Concat(Enumerable.Repeat("<</A ", d)) + "1" + string.Concat(Enumerable.Repeat(">>", d));

    /// <summary>Minimalny PDF z obiektem 4 podanym wprost.</summary>
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

    /// <summary>Obiekt 4 w strumieniu obiektów, z tabelą odsyłaczy jako strumieniem.</summary>
    private static byte[] InObjectStream(string nested, string extraDict = "")
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Latin(s));
        var off = new long[7];
        W("%PDF-1.5\n");
        off[1] = ms.Position; W("1 0 obj\n<</Type/Catalog/Pages 2 0 R>>\nendobj\n");
        off[2] = ms.Position; W("2 0 obj\n<</Type/Pages/Kids[3 0 R]/Count 1>>\nendobj\n");
        off[3] = ms.Position; W("3 0 obj\n<</Type/Page/Parent 2 0 R/MediaBox[0 0 100 100]/X 4 0 R>>\nendobj\n");
        var z = new MemoryStream();
        using (var zl = new ZLibStream(z, CompressionLevel.Optimal, true)) zl.Write(Latin("4 0 " + nested + "\n"));
        off[5] = ms.Position;
        W("5 0 obj\n<</Type/ObjStm/N 1/First 4/Filter/FlateDecode" + extraDict + "/Length " + z.Length + ">>\nstream\n");
        ms.Write(z.ToArray());
        W("\nendstream\nendobj\n");
        long x = ms.Position;
        var xr = new MemoryStream();
        void E(int t, long a, int b)
        {
            xr.WriteByte((byte)t);
            xr.WriteByte((byte)(a >> 16));
            xr.WriteByte((byte)(a >> 8));
            xr.WriteByte((byte)a);
            xr.WriteByte((byte)b);
        }
        E(0, 0, 0); E(1, off[1], 0); E(1, off[2], 0); E(1, off[3], 0); E(2, 5, 0); E(1, off[5], 0); E(1, x, 0);
        W("6 0 obj\n<</Type/XRef/Size 7/W[1 3 1]/Root 1 0 R/Length " + xr.Length + ">>\nstream\n");
        ms.Write(xr.ToArray());
        W("\nendstream\nendobj\nstartxref\n" + x + "\n%%EOF\n");
        return ms.ToArray();
    }

    public static IEnumerable<object[]> DeepFiles()
    {
        foreach (int d in new[] { 300, 10_000, 100_000, 1_000_000 })
        {
            yield return new object[] { "tablice " + d, Plain(Arrays(d)) };
            yield return new object[] { "słowniki " + d, Plain(Dicts(d)) };
            yield return new object[] { "strumień obiektów, tablice " + d, InObjectStream(Arrays(d)) };
            yield return new object[] { "strumień obiektów, słowniki " + d, InObjectStream(Dicts(d)) };
        }
        // Zmylenie skanera: nawias zamykający w napisie, niepasujące zamknięcia, słowa kluczowe w tablicy.
        yield return new object[] { "nawias w napisie", Plain(string.Concat(Enumerable.Repeat("[(])", 20_000))) };
        yield return new object[] { "niepasujące zamknięcia", Plain(string.Concat(Enumerable.Repeat("[>>", 20_000))) };
        yield return new object[] { "endobj w tablicy", Plain(string.Concat(Enumerable.Repeat("[ endobj ", 20_000))) };
        yield return new object[] { "stream w tablicy", Plain(string.Concat(Enumerable.Repeat("[ stream ", 20_000))) };
        yield return new object[] { "komentarz nie ukrywa", Plain("%komentarz\n" + Arrays(5000)) };
    }

    [Theory]
    [MemberData(nameof(DeepFiles))]
    public void Deep_file_is_rejected_before_pdfsharp(string name, byte[] pdf)
    {
        Assert.NotEmpty(name);
        var ex = Assert.Throws<PdfCorruptException>(() => PdfStructureGuard.Check(pdf));
        Assert.Contains("nested", ex.Message);
        Assert.Throws<PdfCorruptException>(() => PdfSharpEdit.Read(pdf, d => d.PageCount));
        Assert.Throws<PdfCorruptException>(() => PdfSharpEdit.Modify(pdf, _ => { }));
    }

    [Fact]
    public void Object_hidden_inside_a_fake_stream_region_is_still_seen()
    {
        // „stream” zatrzymuje skan obiektu, ale każdy nowy „n g obj”, także wewnątrz danych, to nowy punkt startu
        var pdf = Plain("<<>>\nstream\n 7 0 obj\n" + Arrays(5000) + "\nendobj\nendstream");
        Assert.Throws<PdfCorruptException>(() => PdfStructureGuard.Check(pdf));
    }

    [Fact]
    public void Object_stream_entry_offset_into_a_string_is_seen()
    {
        // przesunięcie w nagłówku wskazuje wnętrze napisu, a dla parsera tablice są prawdziwe
        var inner = "(" + Arrays(5000) + ")";
        var z = new MemoryStream();
        var body = "4 1 " + inner;
        using (var zl = new ZLibStream(z, CompressionLevel.Optimal, true)) zl.Write(Latin(body));
        var pdf = Latin("%PDF-1.5\n5 0 obj\n<</Type/ObjStm/N 1/First 4/Filter/FlateDecode>>\nstream\n").Concat(z.ToArray()).Concat(Latin("\nendstream\nendobj\n")).ToArray();
        Assert.Throws<PdfCorruptException>(() => PdfStructureGuard.Check(pdf));
    }

    [Fact]
    public void Inflation_bomb_in_object_stream_is_rejected_without_allocating_it()
    {
        var z = new MemoryStream();
        using (var zl = new ZLibStream(z, CompressionLevel.Optimal, true))
        {
            var block = new byte[1024 * 1024];
            Array.Fill(block, (byte)' ');
            for (int i = 0; i < 400; i++) zl.Write(block);
        }
        var pdf = Latin("%PDF-1.5\n5 0 obj\n<</Type/ObjStm/N 1/First 4/Filter/FlateDecode>>\nstream\n").Concat(z.ToArray()).Concat(Latin("\nendstream\nendobj\n")).ToArray();
        long before = GC.GetTotalAllocatedBytes(true);
        Assert.Throws<PdfCorruptException>(() => PdfStructureGuard.Check(pdf));
        Assert.True(GC.GetTotalAllocatedBytes(true) - before < 1500L * 1024 * 1024);
    }

    [Fact]
    public void Quadratic_object_markers_hit_the_budget_not_the_clock()
    {
        var pdf = Plain("[ " + string.Concat(Enumerable.Repeat("1 0 obj ", 200_000)) + "]");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Throws<PdfCorruptException>(() => PdfStructureGuard.Check(pdf));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), sw.Elapsed.ToString());
    }

    [Fact]
    public void Normal_documents_pass_and_shallow_nesting_opens()
    {
        foreach (var pdf in new[] { SamplePdfFactory.Text(3), SamplePdfFactory.WithForm(), SamplePdfFactory.WithAnnotations(), SamplePdfFactory.WithBookmarks(), Plain(Arrays(100)), InObjectStream(Arrays(100)) })
        {
            Assert.Equal(0, PdfStructureGuard.Check(pdf).UnverifiedObjectStreamBytes);
            Assert.True(PdfSharpEdit.Read(pdf, d => d.PageCount) > 0);
        }
    }

    [Fact]
    public void Encrypted_sample_passes_and_binary_data_does_not_count()
    {
        Assert.Equal(0, PdfStructureGuard.Check(SamplePdfFactory.EncryptedAes256()).UnverifiedObjectStreamBytes);
        var random = new byte[4 * 1024 * 1024];
        new Random(7).NextBytes(random);
        var pdf = Latin("%PDF-1.4\n1 0 obj\n<</Length " + random.Length + ">>\nstream\n").Concat(random).Concat(Latin("\nendstream\nendobj\n")).ToArray();
        PdfStructureGuard.Check(pdf);
    }

    [Fact]
    public void Unverifiable_object_stream_without_encryption_is_rejected()
    {
        // dwa filtry: skaner tego nie rozpakuje, a PDFsharp tak, więc to droga ukrycia zagnieżdżenia
        var pdf = InObjectStream(Arrays(100)).AsSpan().ToArray();
        var text = Encoding.Latin1.GetString(pdf).Replace("/Filter/FlateDecode", "/Filter[/FlateDecode]/Foo/ASCIIDecode", StringComparison.Ordinal);
        var bytes = Encoding.Latin1.GetBytes(text);
        Assert.True(PdfStructureGuard.Check(bytes).UnverifiedObjectStreamBytes > 0);
        Assert.Throws<PdfCorruptException>(() => PdfSharpEdit.Read(bytes, d => d.PageCount));
    }

    [Fact]
    public void Object_stream_that_cannot_be_inflated_is_reported_as_unverified()
    {
        var pdf = Latin("%PDF-1.5\n/Encrypt 9 0 R\n5 0 obj\n<</Type/ObjStm/N 1/First 4/Filter/FlateDecode/Length 16>>\nstream\nxxxxxxxxxxxxxxxx\nendstream\nendobj\n");
        Assert.True(PdfStructureGuard.Check(pdf).UnverifiedObjectStreamBytes > 0);
        Assert.True(PdfSecurityHelper.IsEncrypted(pdf));
    }

    // ------------------------------------------------------------------ treść

    [Theory]
    [InlineData(100)]
    [InlineData(10_000)]
    [InlineData(400_000)]
    public void Deep_content_never_reaches_parser_on_a_small_stack(int depth)
    {
        Assert.Throws<ContentTokenException>(() => PdfContentParser.Parse(Latin(new string('[', depth) + "(x)" + new string(']', depth) + " TJ")));
    }

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

    // ------------------------------------------------------------------ dekompresja

    private static PdfDictionary FlateStream(PdfDocument doc, byte[] plain)
    {
        var z = new MemoryStream();
        using (var zl = new ZLibStream(z, CompressionLevel.Optimal, true)) zl.Write(plain);
        var d = new PdfDictionary(doc);
        d.CreateStream(z.ToArray());
        d.Elements["/Filter"] = new PdfName("/FlateDecode");
        return d;
    }

    [Fact]
    public void Stream_limit_stops_during_inflation()
    {
        using var doc = new PdfDocument();
        var bomb = FlateStream(doc, new byte[64 * 1024 * 1024]);
        Assert.True(bomb.Stream.Value.Length < 200 * 1024);
        long before = GC.GetTotalAllocatedBytes(true);
        Assert.Throws<PdfContentLimitException>(() => PdfStreamLimits.Decode(bomb, 1024 * 1024));
        Assert.True(GC.GetTotalAllocatedBytes(true) - before < 16 * 1024 * 1024);
        var small = FlateStream(doc, Latin("q Q"));
        Assert.Equal(Latin("q Q"), PdfStreamLimits.Decode(small, 1024));
    }

    [Theory]
    [InlineData("/ASCIIHexDecode", "4142>", "AB")]
    [InlineData("/AHx", "4142>", "AB")]
    public void Other_filters_still_decode(string filter, string raw, string expected)
    {
        using var doc = new PdfDocument();
        var d = new PdfDictionary(doc);
        d.CreateStream(Latin(raw));
        d.Elements["/Filter"] = new PdfName(filter);
        Assert.Equal(Latin(expected), PdfStreamLimits.Decode(d));
    }

    [Fact]
    public void Lzw_with_big_input_is_refused_by_ratio_bound()
    {
        using var doc = new PdfDocument();
        var d = new PdfDictionary(doc);
        d.CreateStream(new byte[100 * 1024]);
        d.Elements["/Filter"] = new PdfName("/LZWDecode");
        Assert.Throws<PdfContentLimitException>(() => PdfStreamLimits.Decode(d, 1024 * 1024));
    }
}
