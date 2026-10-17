using System.Security.Cryptography.X509Certificates;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.IntegrationTests.Infrastructure;
using ZetPDF.TestData;

namespace ZetPDF.IntegrationTests;

/// <summary>
/// Struktury spotykane w plikach z innych programów: strumienie obiektów + xref jako strumień (PDF 1.5),
/// aktualizacje przyrostowe. Próbki generowane ręcznie (ExtraSamples).
/// </summary>
public class RealWorldStructureTests
{
    public static IEnumerable<object[]> Samples() => new[] { new object[] { "objstm" }, new object[] { "incremental" } };

    private static byte[] Sample(string name) => name == "objstm" ? ExtraSamples.WithObjectStreams() : ExtraSamples.WithIncrementalUpdate();

    [Fact]
    public async Task Samples_are_valid_for_independent_readers()
    {
        var o = ExtraSamples.WithObjectStreams();
        Assert.Contains("Strumienie obiektow", Inspect.PigText(o));
        Assert.Contains(ExtraSamples.SecretPhrase, await Inspect.PdfiumText(o));
        var inc = ExtraSamples.WithIncrementalUpdate();
        using var pig = UglyToad.PdfPig.PdfDocument.Open(inc);
        Assert.Equal("Rewizja 2 – źdźbło", pig.Information.Title);
        Assert.Equal(1, await Inspect.PdfiumPageCount(inc));
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task Module_operations_work_on_files_with_object_streams_and_incremental_updates(string name)
    {
        using var host = new TestHost();
        var path = TestPaths.WriteSample(Sample(name), $"struktura {name} ąę.pdf");
        var session = (await host.Documents.OpenAsync(path))!;
        var failures = new List<string>();
        foreach (var opName in new[] { "Pages.Rotate", "Pages.Watermark", "Annot.Note", "Annot.Highlight", "Edit.AddText", "Edit.AddShape", "Edit.AddLink", "Viewer.Bookmarks", "Core.Metadata", "Pages.Sanitize", "Pages.Grayscale" })
        {
            var op = Operations.Get(opName);
            try
            {
                var before = session.Current.GetBytes();
                if (opName == "Pages.Rotate")
                {
                    // dokumenty jednostronicowe – obrót strony 0
                    await session.ExecuteAsync(new DelegateDocumentCommand("Test", (pdf, _, _) => host.Get<IPageOperations>().RotateAsync(pdf, new[] { 0 }, 90)));
                    Assert.Equal(90, Inspect.Read(session.Current.GetBytes(), d => d.Pages[0].Rotate));
                }
                else if (opName == "Viewer.Bookmarks")
                {
                    await session.ExecuteAsync(new DelegateDocumentCommand("Test", (pdf, _, _) => host.Get<IBookmarkService>().SetBookmarksAsync(pdf, new[] { new BookmarkNode("Zakładka ąę", 0, BookmarkNode.NoChildren) })));
                    Assert.Equal("Zakładka ąę", Assert.Single(await host.Get<IBookmarkService>().GetBookmarksAsync(session.Current.GetBytes())).Title);
                }
                else if (opName == "Edit.AddLink")
                {
                    await session.ExecuteAsync(new DelegateDocumentCommand("Test", (pdf, _, _) => host.Get<ILinkService>().AddLinkAsync(pdf, 0, new PdfRect(56, 60, 200, 90), new LinkTarget(Uri: "https://example.com/"))));
                    Assert.Single(await host.Get<ILinkService>().GetLinksAsync(session.Current.GetBytes()));
                }
                else
                {
                    Assert.True(await session.ExecuteAsync(new DelegateDocumentCommand("Test", (pdf, _, _) => op.Run(host, pdf), DocumentChangeKind.All, op.Permission)));
                    if (opName is not "Pages.Sanitize" and not "Pages.Grayscale") await op.Verify(host, session.Current.GetBytes());
                }
                Assert.True(await Inspect.PdfiumPageCount(session.Current.GetBytes()) == 1);
                await session.History.UndoAsync();
                Assert.Equal(before, session.Current.GetBytes());
                await session.History.RedoAsync();
            }
            catch (Exception ex) { failures.Add($"{opName}: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}"); }
        }
        var outPath = Path.Combine(Path.GetDirectoryName(path)!, "po operacjach ść.pdf");
        await host.Documents.SaveAsync(session, outPath);
        Assert.Equal(1, await Inspect.PdfiumPageCount(File.ReadAllBytes(outPath)));
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public async Task Redaction_removes_phrase_from_object_stream_document()
    {
        using var host = new TestHost();
        var red = host.Get<IRedactionService>();
        var src = ExtraSamples.WithObjectStreams();
        var hit = Assert.Single(await red.FindAsync(src, new RedactionSearch { Phrases = new[] { ExtraSamples.SecretPhrase } }));
        var result = await red.ApplyAsync(await red.MarkAsync(src, new[] { new RedactionMark(0, hit.Quads) }), new RedactionApplyOptions());
        Assert.False(Inspect.ContainsAnywhere(result, ExtraSamples.SecretPhrase));
        Assert.Contains("Strumienieobiektow", Inspect.Squash(await Inspect.PdfiumText(result)));
    }

    [Fact]
    public async Task Metadata_of_incremental_update_reads_latest_revision()
    {
        using var host = new TestHost();
        var m = await host.Get<IMetadataService>().GetAsync(ExtraSamples.WithIncrementalUpdate());
        Assert.Equal("Rewizja 2 – źdźbło", m.Title);
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task Signing_incrementally_works_on_object_stream_and_incremental_files(string name)
    {
        using var host = new TestHost();
        var sig = host.Get<ISignatureService>();
        var pfx = await sig.CreateSelfSignedIdAsync(new DigitalIdRequest("Podpis Struktury", "Ą", "s@example.com", 1, "p"));
        using var cert = new X509Certificate2(pfx, "p", X509KeyStorageFlags.EphemeralKeySet);
        var src = Sample(name);
        var signed = await sig.SignAsync(src, new SignRequest { Certificate = new SigningCertificate(CertificateSource.PfxFile, cert), Rect = new PdfRect(72, 400, 300, 480) }, null);
        Assert.True(signed.AsSpan(0, src.Length).SequenceEqual(src), "podpis nie jest przyrostowy");
        var r = Assert.Single(await sig.ValidateAsync(signed, null));
        Assert.True(r.IntegrityOk);
        Assert.True(r.CoversWholeDocument);
        Assert.Equal(1, await Inspect.PdfiumPageCount(signed));
        Assert.Equal(1, Inspect.PageCount(signed));
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task Encrypting_and_merging_files_with_these_structures(string name)
    {
        using var host = new TestHost();
        var src = Sample(name);
        var enc = await host.Get<ISecurityService>().EncryptAsync(src, new PdfEncryptionSettings("ą", "ę"));
        Assert.Equal(1, await Inspect.PdfiumPageCount(enc, "ą"));
        var merged = await host.Get<IPageOperations>().MergeAsync(new[] { new MergeSource(TestPaths.WriteSample(src, "a.pdf")), new MergeSource(TestPaths.WriteSample(SamplePdfFactory.Text(1), "b.pdf")) }, new MergeOptions());
        Assert.Equal(2, Inspect.PageCount(merged));
    }
}
