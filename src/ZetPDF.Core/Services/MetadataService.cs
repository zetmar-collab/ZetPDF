using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;

namespace ZetPDF.Core.Services;

/// <summary>Implementacja <see cref="IMetadataService"/> na PDFsharp.</summary>
public sealed class MetadataService : IMetadataService
{
    public Task<DocumentMetadata> GetAsync(byte[] pdf, CancellationToken ct = default) => Task.Run(() =>
        PdfSharpEdit.Read(pdf, doc =>
        {
            var i = doc.Info;
            return new DocumentMetadata
            {
                Title = i.Title ?? string.Empty,
                Author = i.Author ?? string.Empty,
                Subject = i.Subject ?? string.Empty,
                Keywords = i.Keywords ?? string.Empty,
                Creator = i.Creator ?? string.Empty,
                Producer = i.Producer ?? string.Empty,
                Created = i.CreationDate == default ? null : new DateTimeOffset(i.CreationDate),
                Modified = i.ModificationDate == default ? null : new DateTimeOffset(i.ModificationDate),
                PageCount = doc.PageCount,
                PdfVersion = $"{doc.Version / 10}.{doc.Version % 10}",
            };
        }), ct);

    public Task<byte[]> SetAsync(byte[] pdf, DocumentMetadata metadata, CancellationToken ct = default) =>
        PdfSharpEdit.ModifyAsync(pdf, doc =>
        {
            doc.Info.Title = metadata.Title;
            doc.Info.Author = metadata.Author;
            doc.Info.Subject = metadata.Subject;
            doc.Info.Keywords = metadata.Keywords;
            if (!string.IsNullOrEmpty(metadata.Creator)) doc.Info.Creator = metadata.Creator;
            doc.Info.ModificationDate = DateTime.Now;
        }, ct);
}
