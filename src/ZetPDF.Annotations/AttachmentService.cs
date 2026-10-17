using PdfSharp.Pdf;
using ZetPDF.Annotations.Pdf;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using static ZetPDF.Annotations.Pdf.PdfLow;
using ZetPDF.Core.Localization;

namespace ZetPDF.Annotations;

/// <summary>
/// Załączniki: pliki osadzone w drzewie /EmbeddedFiles oraz adnotacje FileAttachment (nazwa = nazwa pliku;
/// dla adnotacji z tą samą nazwą co plik w drzewie pierwszeństwo ma drzewo).
/// </summary>
public sealed class AttachmentService : IAttachmentService
{
    public Task<IReadOnlyList<AttachmentInfo>> GetAttachmentsAsync(byte[] pdf, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<AttachmentInfo>>(() => PdfSharpEdit.Read(pdf, doc =>
        {
            var list = new List<AttachmentInfo>();
            foreach (var e in EmbeddedFiles.List(doc))
                list.Add(new AttachmentInfo(e.Name, Text(e.FileSpec, "/Desc"), EmbeddedFiles.Size(e.FileSpec), EmbeddedFiles.Modified(e.FileSpec), null));
            foreach (var (spec, page, _) in AnnotationFiles(doc))
                list.Add(new AttachmentInfo(EmbeddedFiles.FileName(spec), Text(spec, "/Desc"), EmbeddedFiles.Size(spec), EmbeddedFiles.Modified(spec), page));
            return list;
        }), ct);

    private static IEnumerable<(PdfDictionary Spec, int Page, PdfDictionary Annot)> AnnotationFiles(PdfDocument doc)
    {
        foreach (var a in AnnotationStore.All(doc))
            if (NameOf(a.Dict, "/Subtype") == "/FileAttachment" && Dict(a.Dict, "/FS") is { } fs)
                yield return (fs, a.PageIndex, a.Dict);
    }

    public Task<byte[]> ExtractAsync(byte[] pdf, string name, CancellationToken ct = default) =>
        Task.Run(() => PdfSharpEdit.Read(pdf, doc =>
        {
            var e = EmbeddedFiles.List(doc).FirstOrDefault(x => x.Name == name);
            if (e is not null) return EmbeddedFiles.Data(e.FileSpec);
            var a = AnnotationFiles(doc).FirstOrDefault(x => EmbeddedFiles.FileName(x.Spec) == name);
            if (a.Spec is not null) return EmbeddedFiles.Data(a.Spec);
            throw new FileNotFoundException("Attachment not found.", name).WithMessageKey("Annot.Error.AttachmentNotFound", name);
        }), ct);

    public Task<byte[]> AddAsync(byte[] pdf, string filePath, string description, CancellationToken ct = default) =>
        PdfSharpEdit.ModifyAsync(pdf, doc =>
        {
            var entries = EmbeddedFiles.List(doc);
            var name = EmbeddedFiles.UniqueName(entries.Select(e => e.Name), Path.GetFileName(filePath));
            var spec = EmbeddedFiles.CreateFileSpec(doc, filePath, name, description);
            entries.Add(new EmbeddedFiles.Entry(name, spec));
            EmbeddedFiles.Write(doc, entries);
        }, ct);

    public Task<byte[]> RemoveAsync(byte[] pdf, string name, CancellationToken ct = default) =>
        PdfSharpEdit.ModifyAsync(pdf, doc =>
        {
            var entries = EmbeddedFiles.List(doc);
            if (entries.RemoveAll(e => e.Name == name) > 0)
            {
                EmbeddedFiles.Write(doc, entries);
                return;
            }
            var targets = AnnotationFiles(doc).Where(x => EmbeddedFiles.FileName(x.Spec) == name).Select(x => x.Annot).ToList();
            if (targets.Count == 0) throw new FileNotFoundException("Attachment not found.", name).WithMessageKey("Annot.Error.AttachmentNotFound", name);
            AnnotationStore.Remove(doc, targets);
        }, ct);
}
