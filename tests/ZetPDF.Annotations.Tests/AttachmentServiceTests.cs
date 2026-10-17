using System.Text;
using ZetPDF.TestData;

namespace ZetPDF.Annotations.Tests;

public class AttachmentServiceTests
{
    private readonly AttachmentService _svc = new();

    [Fact]
    public async Task Lists_existing_attachment()
    {
        var list = await _svc.GetAttachmentsAsync(SamplePdfFactory.WithMetadataAttachmentAndJavaScript());
        var a = Assert.Single(list);
        Assert.Equal("załącznik.txt", a.Name);
        var data = await _svc.ExtractAsync(SamplePdfFactory.WithMetadataAttachmentAndJavaScript(), a.Name);
        Assert.Equal("Treść załącznika – zażółć", Encoding.UTF8.GetString(data));
    }

    [Fact]
    public async Task Add_extract_remove()
    {
        var dir = TestPaths.NewDirectory();
        var file = Path.Combine(dir, "dane źródłowe.csv");
        await File.WriteAllTextAsync(file, "a,b\n1,2 – ąę");
        var pdf = await _svc.AddAsync(SamplePdfFactory.Text(1), file, "Opis ąę");
        pdf = await _svc.AddAsync(pdf, file, "drugi");
        var list = await _svc.GetAttachmentsAsync(pdf);
        Assert.Equal(2, list.Count);
        Assert.Contains(list, x => x.Name == "dane źródłowe.csv" && x.Description == "Opis ąę" && x.Size == new FileInfo(file).Length);
        Assert.Contains(list, x => x.Name == "dane źródłowe (2).csv");
        Assert.Equal(await File.ReadAllBytesAsync(file), await _svc.ExtractAsync(pdf, "dane źródłowe.csv"));
        var removed = await _svc.RemoveAsync(pdf, "dane źródłowe.csv");
        Assert.Single(await _svc.GetAttachmentsAsync(removed));
    }

    [Fact]
    public async Task File_attachment_annotation_is_listed()
    {
        var dir = TestPaths.NewDirectory();
        var file = Path.Combine(dir, "notatka.txt");
        await File.WriteAllTextAsync(file, "abc");
        var (pdf, _) = await new AnnotationService().AddAsync(SamplePdfFactory.Text(1),
            new Core.Contracts.AnnotationSpec { Kind = Core.Contracts.AnnotationKind.FileAttachment, PageIndex = 0, Rect = new(100, 400, 100, 400), AttachmentPath = file });
        var a = Assert.Single(await _svc.GetAttachmentsAsync(pdf));
        Assert.Equal(0, a.PageIndex);
        Assert.Equal("abc", Encoding.UTF8.GetString(await _svc.ExtractAsync(pdf, "notatka.txt")));
    }
}
