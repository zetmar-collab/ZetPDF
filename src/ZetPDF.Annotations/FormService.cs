using ZetPDF.Annotations.Forms;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;

namespace ZetPDF.Annotations;

/// <summary>
/// Formularze AcroForm: odczyt, wypełnianie z regeneracją /AP, reset, tworzenie/zmiana/usuwanie pól (słowniki ręcznie –
/// PDFsharp 6.2.4 nie ma API tworzenia), kolejność tabulacji, import/eksport danych FDF/XFDF/CSV.
/// Pole podpisu (/FT /Sig, puste) jest kontraktem dla security-signatures.
/// </summary>
public sealed class FormService : IFormService
{
    public Task<IReadOnlyList<FormFieldInfo>> GetFieldsAsync(byte[] pdf, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<FormFieldInfo>>(() => PdfSharpEdit.Read(pdf, FormEngine.Read), ct);

    /// <summary>Widżety (pola z kilkoma widżetami – np. opcje – zwracane osobno) dla interfejsu.</summary>
    internal Task<List<(string Name, FormFieldKind Kind, int Page, PdfRect Rect, string? OnState)>> GetWidgetsAsync(byte[] pdf, CancellationToken ct = default) =>
        Task.Run(() => PdfSharpEdit.Read(pdf, FormEngine.Widgets), ct);

    public Task<byte[]> SetValuesAsync(byte[] pdf, IReadOnlyDictionary<string, string> values, CancellationToken ct = default) =>
        PdfSharpEdit.ModifyAsync(pdf, doc =>
        {
            var fields = FieldTree.Terminals(doc).GroupBy(f => f.FullName).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            foreach (var (name, value) in values)
                if (fields.TryGetValue(name, out var f)) FormEngine.SetValue(doc, f, value ?? string.Empty);
            FormEngine.RefreshAppearances(doc);
        }, ct);

    public Task<byte[]> ResetAsync(byte[] pdf, IReadOnlyList<string>? fieldNames = null, CancellationToken ct = default) =>
        PdfSharpEdit.ModifyAsync(pdf, doc => FormEngine.Reset(doc, fieldNames), ct);

    public Task<byte[]> CreateFieldAsync(byte[] pdf, FormFieldSpec spec, CancellationToken ct = default) =>
        PdfSharpEdit.ModifyAsync(pdf, doc => FormEngine.Create(doc, spec), ct);

    public Task<byte[]> UpdateFieldAsync(byte[] pdf, string fullName, FormFieldSpec spec, CancellationToken ct = default) =>
        PdfSharpEdit.ModifyAsync(pdf, doc => FormEngine.Update(doc, fullName, spec), ct);

    public Task<byte[]> DeleteFieldsAsync(byte[] pdf, IReadOnlyList<string> fullNames, CancellationToken ct = default) =>
        PdfSharpEdit.ModifyAsync(pdf, doc => FormEngine.Delete(doc, fullNames), ct);

    public Task<byte[]> SetTabOrderAsync(byte[] pdf, int pageIndex, IReadOnlyList<string> orderedFieldNames, CancellationToken ct = default) =>
        PdfSharpEdit.ModifyAsync(pdf, doc => FormEngine.SetTabOrder(doc, pageIndex, orderedFieldNames), ct);

    public async Task ExportDataAsync(byte[] pdf, Stream output, FormDataFormat format, CancellationToken ct = default)
    {
        var fields = await GetFieldsAsync(pdf, ct).ConfigureAwait(false);
        var bytes = FormDataIO.Export(fields, format);
        await output.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    public async Task<byte[]> ImportDataAsync(byte[] pdf, Stream input, FormDataFormat format, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await input.CopyToAsync(ms, ct).ConfigureAwait(false);
        var values = FormDataIO.Import(ms.ToArray(), format);
        return await SetValuesAsync(pdf, values, ct).ConfigureAwait(false);
    }

    /// <summary>Format danych z rozszerzenia pliku.</summary>
    public static FormDataFormat FormatFromPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".fdf" => FormDataFormat.Fdf,
        ".xfdf" or ".xml" => FormDataFormat.Xfdf,
        _ => FormDataFormat.Csv,
    };
}
