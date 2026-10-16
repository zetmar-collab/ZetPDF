using ZetPDF.Core.Contracts;

namespace ZetPDF.Annotations;

/// <summary>ZAŚLEPKA szkieletu – do zastąpienia implementacją przez właściciela modułu.</summary>
public sealed class FormService : IFormService
{
    // ---- IFormService
    public Task<IReadOnlyList<FormFieldInfo>> GetFieldsAsync(byte[] pdf, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> SetValuesAsync(byte[] pdf, IReadOnlyDictionary<string, string> values, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> ResetAsync(byte[] pdf, IReadOnlyList<string>? fieldNames = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> CreateFieldAsync(byte[] pdf, FormFieldSpec spec, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> UpdateFieldAsync(byte[] pdf, string fullName, FormFieldSpec spec, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> DeleteFieldsAsync(byte[] pdf, IReadOnlyList<string> fullNames, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> SetTabOrderAsync(byte[] pdf, int pageIndex, IReadOnlyList<string> orderedFieldNames, CancellationToken ct = default) => throw new NotImplementedException();
    public Task ExportDataAsync(byte[] pdf, Stream output, FormDataFormat format, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> ImportDataAsync(byte[] pdf, Stream input, FormDataFormat format, CancellationToken ct = default) => throw new NotImplementedException();
}
