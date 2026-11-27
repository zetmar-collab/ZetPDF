using ZetPDF.Core.Contracts;
using ZetPDF.IntegrationTests.Infrastructure;
using ZetPDF.TestData;

namespace ZetPDF.IntegrationTests;

/// <summary>Formularz po scaleniu i wyodrębnieniu stron daje się wypełnić prawdziwym silnikiem formularzy.</summary>
public class FormCarryFillTests
{
    [Fact]
    public async Task Merged_forms_are_fillable_independently()
    {
        using var host = new TestHost();
        var pages = host.Get<IPageOperations>();
        var forms = host.Get<IFormService>();
        var merged = await pages.MergeAsync(new[] { new MergeSource("", Data: SamplePdfFactory.WithForm()), new MergeSource("", Data: SamplePdfFactory.WithForm()) }, new MergeOptions());

        var filled = await forms.SetValuesAsync(merged, new Dictionary<string, string> { ["imie"] = "Zażółć", ["imie_2"] = "Gęślą" });

        var fields = await forms.GetFieldsAsync(filled);
        Assert.Equal("Zażółć", fields.Single(f => f.FullName == "imie").Value);
        Assert.Equal("Gęślą", fields.Single(f => f.FullName == "imie_2").Value);
        Assert.Equal(1, fields.Single(f => f.FullName == "imie_2").PageIndex);
    }

    [Fact]
    public async Task Extracted_form_page_is_fillable()
    {
        using var host = new TestHost();
        var extracted = await host.Get<IPageOperations>().ExtractAsync(SamplePdfFactory.WithForm(), new[] { 0 });
        var forms = host.Get<IFormService>();
        var filled = await forms.SetValuesAsync(extracted, new Dictionary<string, string> { ["miasto"] = "Kraków" });
        Assert.Equal("Kraków", (await forms.GetFieldsAsync(filled)).Single(f => f.FullName == "miasto").Value);
    }
}
