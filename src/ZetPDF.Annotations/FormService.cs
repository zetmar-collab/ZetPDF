using ZetPDF.Annotations.Forms;
using ZetPDF.Annotations.Forms.Scripting;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Core.Localization;

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

    internal Task<byte[]> MoveWidgetAsync(byte[] pdf, string fullName, string? onState, PdfRect rect, CancellationToken ct = default) =>
        PdfSharpEdit.ModifyAsync(pdf, doc => FormEngine.MoveWidget(doc, fullName, onState, rect), ct);

    internal Task<FormFieldSpec> DescribeAsync(byte[] pdf, string fullName, CancellationToken ct = default) =>
        Task.Run(() => PdfSharpEdit.Read(pdf, doc => FormEngine.Describe(doc, fullName)), ct);

    /// <summary>Ustawia wartości; skrypty pól wg polityki domyślnej (tylko wbudowane funkcje AF*: obliczenia, formaty, walidacja).</summary>
    public Task<byte[]> SetValuesAsync(byte[] pdf, IReadOnlyDictionary<string, string> values, CancellationToken ct = default) =>
        SetValuesAsync(pdf, values, ScriptPolicy.AfOnly, null, ct);

    /// <summary>Ustawia wartości z wykonaniem skryptów pól wg polityki dokumentu; raport (komunikaty, błędy) – opcjonalnie.</summary>
    public Task<byte[]> SetValuesAsync(byte[] pdf, IReadOnlyDictionary<string, string> values, ScriptPolicy policy, ScriptReport? report, CancellationToken ct = default) =>
        PdfSharpEdit.ModifyAsync(pdf, doc =>
        {
            var display = FormCalculation.Apply(doc, values, policy, report ?? new ScriptReport(), ct);
            FormEngine.RefreshAppearances(doc, display);
        }, ct);

    /// <summary>Przelicza obliczenia i formaty wszystkich pól (bez zmiany wartości wpisanych).</summary>
    public Task<byte[]> RecalculateAsync(byte[] pdf, ScriptPolicy policy, ScriptReport? report = null, CancellationToken ct = default) =>
        PdfSharpEdit.ModifyAsync(pdf, doc =>
        {
            var display = FormCalculation.Apply(doc, null, policy, report ?? new ScriptReport(), ct);
            FormEngine.RefreshAppearances(doc, display);
        }, ct);

    public Task<byte[]> ResetAsync(byte[] pdf, IReadOnlyList<string>? fieldNames = null, CancellationToken ct = default) =>
        ResetAsync(pdf, fieldNames, ScriptPolicy.AfOnly, ct);

    public Task<byte[]> ResetAsync(byte[] pdf, IReadOnlyList<string>? fieldNames, ScriptPolicy policy, CancellationToken ct = default) =>
        PdfSharpEdit.ModifyAsync(pdf, doc =>
        {
            FormEngine.Reset(doc, fieldNames);
            var display = FormCalculation.Apply(doc, null, policy, new ScriptReport(), ct);
            FormEngine.RefreshAppearances(doc, display);
        }, ct);

    /// <summary>Skrypty JavaScript dokumentu i pól (podgląd w oknie Skrypty formularza).</summary>
    public Task<IReadOnlyList<FormScriptInfo>> GetScriptsAsync(byte[] pdf, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<FormScriptInfo>>(() => PdfSharpEdit.Read(pdf, FormScripts.List), ct);

    /// <summary>Ustawia (pusty kod – usuwa) skrypt zdarzenia pola: Keystroke, Format, Validate, Calculate (/CO).</summary>
    public Task<byte[]> SetFieldScriptAsync(byte[] pdf, string fullName, ScriptTrigger trigger, string? code, CancellationToken ct = default) =>
        PdfSharpEdit.ModifyAsync(pdf, doc =>
        {
            var f = FieldTree.Find(doc, fullName) ?? throw new KeyNotFoundException("Field not found: " + fullName).WithMessageKey("Annot.Error.FieldNotFound", fullName);
            FormScripts.SetFieldScript(doc, f, trigger, code);
        }, ct);

    /// <summary>Tworzy wiele pól w jednym zapisie (jeden krok Cofnij).</summary>
    public Task<byte[]> CreateFieldsAsync(byte[] pdf, IReadOnlyList<FormFieldSpec> specs, CancellationToken ct = default) =>
        PdfSharpEdit.ModifyAsync(pdf, doc =>
        {
            foreach (var spec in specs)
            {
                ct.ThrowIfCancellationRequested();
                FormEngine.Create(doc, spec);
            }
        }, ct);

    /// <summary>Propozycje pól z bitmap stron i tekstu; pomija miejsca, w których już jest widżet.</summary>
    public async Task<IReadOnlyList<ZetPDF.Annotations.Forms.Recognition.FieldProposal>> RecognizeFieldsAsync(byte[] pdf, IPdfRenderer renderer,
        ZetPDF.Annotations.Forms.Recognition.FieldGenericNames generic, IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        var widgets = await GetWidgetsAsync(pdf, ct).ConfigureAwait(false);
        var fields = await GetFieldsAsync(pdf, ct).ConfigureAwait(false);
        return await ZetPDF.Annotations.Forms.Recognition.FieldRecognizer.RecognizeAsync(pdf, renderer, widgets.Select(w => (w.Page, w.Rect)).ToList(),
            fields.Select(f => f.FullName).Concat(widgets.Select(w => w.Name)), generic, progress, ct).ConfigureAwait(false);
    }

    public Task<byte[]> CreateFieldAsync(byte[] pdf, FormFieldSpec spec, CancellationToken ct = default) =>
        PdfSharpEdit.ModifyAsync(pdf, doc => FormEngine.Create(doc, spec), ct);

    /// <summary>
    /// Pole kodu kreskowego (QR/PDF417/DataMatrix) kodujące wybrane pola: pole tekstowe tylko do odczytu, dane w /V,
    /// symbol w /AP. Odświeżane przy każdej zmianie wartości pól.
    /// </summary>
    public Task<byte[]> CreateBarcodeFieldAsync(byte[] pdf, FormFieldSpec spec, BarcodeOptions options, CancellationToken ct = default) =>
        PdfSharpEdit.ModifyAsync(pdf, doc =>
        {
            FormEngine.Create(doc, spec with { Kind = FormFieldKind.Text, ReadOnly = true, Multiline = false, DefaultValue = string.Empty });
            var node = FieldTree.Find(doc, spec.Name)!;
            BarcodeEngine.Write(doc, node, options);
            if (node.Widgets.FirstOrDefault() is { } w && Pdf.PdfLow.Dict(w, "/MK") is { } mk) mk.Elements.Remove("/BC");
            BarcodeEngine.UpdateAll(doc);
        }, ct);

    /// <summary>Ustawienia pola kodu kreskowego albo null, gdy pole nim nie jest.</summary>
    public Task<BarcodeOptions?> GetBarcodeOptionsAsync(byte[] pdf, string fullName, CancellationToken ct = default) =>
        Task.Run(() => PdfSharpEdit.Read(pdf, doc => FieldTree.Find(doc, fullName) is { } f ? BarcodeEngine.Options(f) : null), ct);

    /// <summary>Pola, które mogą być źródłem danych kodu (bez przycisków, podpisów i pól kodu).</summary>
    internal Task<List<string>> GetBarcodeSourceNamesAsync(byte[] pdf, CancellationToken ct = default) =>
        Task.Run(() => PdfSharpEdit.Read(pdf, doc => BarcodeEngine.Sources(FieldTree.Terminals(doc), new BarcodeOptions()).Select(f => f.FullName).Distinct().ToList()), ct);

    public Task<byte[]> SetBarcodeOptionsAsync(byte[] pdf, string fullName, BarcodeOptions options, CancellationToken ct = default) =>
        PdfSharpEdit.ModifyAsync(pdf, doc =>
        {
            var f = FieldTree.Find(doc, fullName) ?? throw new KeyNotFoundException("Field not found: " + fullName).WithMessageKey("Annot.Error.FieldNotFound", fullName);
            BarcodeEngine.Write(doc, f, options);
            BarcodeEngine.UpdateAll(doc);
        }, ct);

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
