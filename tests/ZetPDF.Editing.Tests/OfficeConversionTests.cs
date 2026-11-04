using System.Diagnostics;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Editing.Conversion;
using ZetPDF.TestData;

namespace ZetPDF.Editing.Tests;

/// <summary>Pomija test, gdy aplikacji Office nie ma, podając przyczynę.</summary>
public sealed class OfficeFactAttribute : FactAttribute
{
    public OfficeFactAttribute(string extension)
    {
        if (!new OfficeComConverter().IsAvailableFor(extension)) Skip = $"Brak zainstalowanej aplikacji Office dla {extension}.";
    }
}

public class OfficeConversionTests
{
    private sealed class FakeOffice : IOfficeConverter
    {
        public bool Available = true;
        public readonly List<string> Converted = new();
        public Exception? Fail;
        public bool IsAvailableFor(string extension) => Available;
        public Task ConvertToPdfAsync(string source, string outputPdf, CancellationToken ct)
        {
            if (Fail is not null) throw Fail;
            Converted.Add(source);
            File.WriteAllBytes(outputPdf, SamplePdfFactory.Text(2));
            return Task.CompletedTask;
        }
    }

    private static string Empty(string name)
    {
        var path = Path.Combine(TestPaths.NewDirectory(), name);
        File.WriteAllBytes(path, new byte[] { 0x50, 0x4B, 3, 4 });
        return path;
    }

    [Fact]
    public async Task Office_files_are_combined_with_pdf_through_the_converter_and_temp_is_removed()
    {
        var fake = new FakeOffice();
        var svc = new ConversionService(TestEnv.Renderer, null, fake);
        var doc = Empty("raport ą.docx");
        var pdf = TestPaths.WriteSample(SamplePdfFactory.Text(1));
        var before = Directory.GetFiles(Path.GetTempPath(), "zetpdf-office-*").Length;
        var result = await svc.CombineFilesAsync(new[] { doc, pdf, Empty("a.xlsx"), Empty("b.PPTX") });
        Assert.Equal(7, PdfSharpEdit.Read(result, d => d.PageCount));
        Assert.Equal(3, fake.Converted.Count);
        Assert.Equal(before, Directory.GetFiles(Path.GetTempPath(), "zetpdf-office-*").Length);
    }

    [Fact]
    public async Task Without_office_there_is_a_clear_error_and_not_garbage_text()
    {
        foreach (var svc in new[] { new ConversionService(TestEnv.Renderer), new ConversionService(TestEnv.Renderer, null, new FakeOffice { Available = false }) })
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CombineFilesAsync(new[] { Empty("a.docx") }));
            Assert.Contains("Edit.Office.Required", ex.Data.Values.Cast<object?>().OfType<string>());
        }
    }

    [Fact]
    public async Task Other_formats_work_as_before_without_office()
    {
        var txt = Path.Combine(TestPaths.NewDirectory(), "a.txt");
        await File.WriteAllTextAsync(txt, "tekst");
        var result = await new ConversionService(TestEnv.Renderer).CombineFilesAsync(new[] { txt, TestPaths.WriteSample(SamplePdfFactory.Text(1)) });
        Assert.Equal(2, PdfSharpEdit.Read(result, d => d.PageCount));
    }

    [Fact]
    public async Task Converter_failure_reaches_the_caller()
    {
        var svc = new ConversionService(TestEnv.Renderer, null, new FakeOffice { Fail = new InvalidOperationException("x") });
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CombineFilesAsync(new[] { Empty("a.xlsx") }));
    }

    [Theory]
    [InlineData(".docx", "Word.Application")]
    [InlineData(".XLSX", "Excel.Application")]
    [InlineData(".pptx", "PowerPoint.Application")]
    [InlineData(".doc", null)]
    [InlineData(".pdf", null)]
    public void Only_listed_formats_map_to_applications(string ext, string? progId)
    {
        Assert.Equal(progId, OfficeFormats.ProgId(ext));
        Assert.Equal(progId is not null, OfficeFormats.IsOffice(ext));
    }

    /// <summary>Proces kończy się chwilę po Quit, a pod obciążeniem dłużej; po tym czasie zostać może tylko prawdziwy wyciek.</summary>
    private static async Task AssertNoNewProcess(string name, int[] before)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (Running(name).Except(before).Any() && DateTime.UtcNow < deadline) await Task.Delay(250);
        Assert.Empty(Running(name).Except(before));
    }

    private static int[] Running(string name) => Process.GetProcessesByName(name).Select(p => { using (p) return p.Id; }).ToArray();

    /// <summary>Jeden test COM naraz: testy porównują listę procesów Office przed i po.</summary>
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    private static async Task AssertConverts(string source, string processName, string expected)
    {
        await OneAtATime.WaitAsync();
        try
        {
            var before = Running(processName);
            var output = Path.Combine(Path.GetDirectoryName(source)!, "wynik ą.pdf");
            await new OfficeComConverter().ConvertToPdfAsync(source, output, default);
            var pdf = await File.ReadAllBytesAsync(output);
            Assert.Contains(TestEnv.Squash(expected), TestEnv.Squash(await TestEnv.PdfiumText(pdf)));
            // uruchomiony proces został zamknięty, procesy użytkownika zostały
            await AssertNoNewProcess(processName, before);
        }
        finally { OneAtATime.Release(); }
    }

    [OfficeFact(".xlsx")]
    public async Task Real_excel_converts_workbook_with_polish_path_and_closes()
    {
        var dir = Path.Combine(TestPaths.NewDirectory(), "Próbny folder");
        Directory.CreateDirectory(dir);
        var xlsx = Path.Combine(dir, "zażółć gęślą.xlsx");
        await new ConversionService(TestEnv.Renderer).PdfToXlsxAsync(SamplePdfFactory.Text(1), xlsx, new XlsxExportOptions());
        await AssertConverts(xlsx, "EXCEL", "Zażółć");
    }

    [OfficeFact(".xlsx")]
    public async Task Real_excel_failure_on_damaged_file_is_clear_and_process_closes()
    {
        await OneAtATime.WaitAsync();
        try
        {
            var before = Running("EXCEL");
            var path = Empty("uszkodzony.xlsx");
            await Assert.ThrowsAsync<InvalidOperationException>(() => new OfficeComConverter().ConvertToPdfAsync(path, path + ".pdf", default));
            await AssertNoNewProcess("EXCEL", before);
        }
        finally { OneAtATime.Release(); }
    }

    [OfficeFact(".docx")]
    public async Task Real_word_converts_document_and_closes()
    {
        var dir = TestPaths.NewDirectory();
        var docx = Path.Combine(dir, "dokument ą.docx");
        using (var word = WordprocessingDocument.Create(docx, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            word.AddMainDocumentPart().Document = new Document(new Body(new Paragraph(new Run(new DocumentFormat.OpenXml.Wordprocessing.Text("Zażółć gęślą jaźń")))));
        }
        await AssertConverts(docx, "WINWORD", "Zażółć gęślą jaźń");
    }
}
