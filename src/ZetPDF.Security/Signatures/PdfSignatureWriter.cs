using System.Globalization;
using System.Text;
using ZetPDF.Core.Contracts;
using ZetPDF.Security.Pdf;
using ZetPDF.Core.Localization;

namespace ZetPDF.Security.Signatures;

/// <summary>Wygląd widocznego podpisu (gotowy strumień formularza + opcjonalny obraz).</summary>
internal sealed record AppearanceStreams(CosStream Form, CosStream? Image, string ImageResourceName);

/// <summary>
/// Wstawia słownik podpisu (zwykły lub DocTimeStamp) zapisem przyrostowym: rezerwuje /Contents i /ByteRange,
/// tworzy lub wypełnia pole podpisu, dopisuje widżet do strony i /AcroForm (/SigFlags 3), a potem –
/// po obliczeniu zakresów – wstawia podpis CMS (lub token znacznika czasu) w zarezerwowane miejsce.
/// </summary>
internal static class PdfSignatureWriter
{
    private const int ByteRangeWidth = 64;

    public sealed record Placement
    {
        /// <summary>Istniejące puste pole podpisu (pełna nazwa) albo null → nowe pole.</summary>
        public string? FieldName { get; init; }
        public int PageIndex { get; init; }
        /// <summary>Pusty = podpis niewidoczny.</summary>
        public PdfRect Rect { get; init; }
        public string NewFieldPrefix { get; init; } = "Signature";
    }

    /// <summary>Kontekst do budowy wyglądu: wymiary pola i obrót strony.</summary>
    public sealed record AppearanceContext(double Width, double Height, int PageRotation);

    /// <summary>
    /// Tworzy podpisany plik. <paramref name="signer"/> dostaje dane objęte /ByteRange i zwraca DER (CMS lub token TSA).
    /// <paramref name="reservedBytes"/> – miejsce na DER; gdy za mało, wołający ponawia z większą wartością.
    /// </summary>
    public static async Task<byte[]> SignAsync(
        byte[] pdf, string? password, Placement placement, CosDictionary signatureDictionary,
        Func<AppearanceContext, AppearanceStreams?>? appearance,
        Func<byte[], CancellationToken, Task<byte[]>> signer, int reservedBytes, CancellationToken ct)
    {
        var doc = CosDocument.Open(pdf, password);
        if (doc.IsEncrypted && doc.Security?.FileKey is null) throw new PdfPasswordRequiredException();
        var upd = new IncrementalUpdate(doc);

        var sig = (CosDictionary)signatureDictionary.Clone();
        sig["ByteRange"] = new CosPlaceholder("ByteRange", ByteRangeWidth, (byte)' ');
        sig["Contents"] = new CosPlaceholder("Contents", reservedBytes * 2 + 2, (byte)'0');
        var sigRef = upd.Add(sig);

        var pages = doc.GetPageReferences();
        PlaceField(doc, upd, pages, placement, sigRef, appearance);

        var bytes = upd.Write();
        var (brOff, brLen) = upd.Placeholders["ByteRange"];
        var (cOff, cLen) = upd.Placeholders["Contents"];
        long afterContents = cOff + cLen;
        var br = $"[0 {cOff} {afterContents} {bytes.LongLength - afterContents}]";
        if (br.Length > brLen) throw new InvalidOperationException("ByteRange placeholder too small.");
        Encoding.ASCII.GetBytes(br.PadRight(brLen)).CopyTo(bytes, brOff);
        bytes[cOff] = (byte)'<';
        bytes[cOff + cLen - 1] = (byte)'>';

        var signed = new byte[cOff + (bytes.Length - afterContents)];
        Buffer.BlockCopy(bytes, 0, signed, 0, cOff);
        Buffer.BlockCopy(bytes, (int)afterContents, signed, cOff, (int)(bytes.Length - afterContents));
        var der = await signer(signed, ct).ConfigureAwait(false);
        if (der.Length > reservedBytes) throw new SignatureSpaceException(der.Length);
        var hex = Encoding.ASCII.GetBytes(Convert.ToHexString(der));
        hex.CopyTo(bytes, cOff + 1);
        return bytes;
    }

    private static void PlaceField(CosDocument doc, IncrementalUpdate upd, IReadOnlyList<CosReference> pages, Placement placement,
        CosReference sigRef, Func<AppearanceContext, AppearanceStreams?>? appearance)
    {
        var catalogRef = doc.CatalogReference;
        var catalog = (CosDictionary)doc.Catalog.Clone();
        bool catalogChanged = false;

        // AcroForm
        CosReference? acroRef = catalog["AcroForm"] as CosReference;
        CosDictionary acro = acroRef is not null
            ? (CosDictionary)(doc.ResolveDict(acroRef)?.Clone() ?? new CosDictionary())
            : (doc.Resolve(catalog["AcroForm"]) as CosDictionary)?.Clone() as CosDictionary ?? new CosDictionary();

        var fields = doc.GetFields();
        CosReference widgetRef;
        CosDictionary widget;
        int pageIndex;
        PdfRect rect;

        if (placement.FieldName is { } fieldName)
        {
            var field = fields.FirstOrDefault(f => f.FullName == fieldName && f.FieldType == "Sig")
                ?? throw new InvalidOperationException($"Signature field '{fieldName}' not found.").WithMessageKey("Security.Error.FieldNotFound", fieldName);
            if (field.Dictionary["V"] is not null) throw new InvalidOperationException($"Signature field '{fieldName}' is already signed.").WithMessageKey("Security.Error.FieldSigned", fieldName);
            if (field.Reference is null || field.Widgets.Count == 0) throw new InvalidOperationException($"Signature field '{fieldName}' has no widget.").WithMessageKey("Security.Error.FieldNoWidget", fieldName);
            widgetRef = field.Widgets[0];
            bool merged = widgetRef.Equals(field.Reference);
            var fieldDict = (CosDictionary)field.Dictionary.Clone();
            fieldDict["V"] = sigRef;
            widget = merged ? fieldDict : (CosDictionary)(doc.ResolveDict(widgetRef)!.Clone());
            pageIndex = doc.FindWidgetPage(widgetRef, pages);
            rect = CosDocument.ToRect(doc.Resolve(widget["Rect"]));
            if (!merged) upd.Set(field.Reference, fieldDict);
        }
        else
        {
            pageIndex = placement.PageIndex;
            if (pageIndex < 0 || pageIndex >= pages.Count) throw new ArgumentOutOfRangeException(nameof(placement), "Page index out of range.");
            rect = placement.Rect;
            var name = UniqueFieldName(fields.Select(f => f.FullName), placement.NewFieldPrefix);
            widget = new CosDictionary
            {
                ["Type"] = new CosName("Annot"),
                ["Subtype"] = new CosName("Widget"),
                ["FT"] = new CosName("Sig"),
                ["T"] = CosString.FromText(name),
                ["F"] = new CosNumber(132), // Print + Locked
                ["Rect"] = rect.IsEmpty ? CosArray.OfNumbers(0, 0, 0, 0) : CosArray.OfNumbers(rect.Left, rect.Bottom, rect.Right, rect.Top),
                ["P"] = pages[pageIndex],
                ["V"] = sigRef,
            };
            widgetRef = upd.Reserve();

            // /Annots strony
            var pageRef = pages[pageIndex];
            var page = (CosDictionary)doc.ResolveDict(pageRef)!.Clone();
            if (page["Annots"] is CosReference annotsRef)
            {
                var arr = (doc.Resolve(annotsRef) as CosArray)?.Clone() as CosArray ?? new CosArray();
                arr.Add(widgetRef);
                upd.Set(annotsRef, arr);
            }
            else
            {
                var arr = (page["Annots"] as CosArray)?.Clone() as CosArray ?? new CosArray();
                arr.Add(widgetRef);
                page["Annots"] = arr;
                upd.Set(pageRef, page);
            }

            // /Fields
            if (acro["Fields"] is CosReference fieldsRef)
            {
                var arr = (doc.Resolve(fieldsRef) as CosArray)?.Clone() as CosArray ?? new CosArray();
                arr.Add(widgetRef);
                upd.Set(fieldsRef, arr);
            }
            else
            {
                var arr = (acro["Fields"] as CosArray)?.Clone() as CosArray ?? new CosArray();
                arr.Add(widgetRef);
                acro["Fields"] = arr;
            }
        }

        // wygląd
        if (!rect.IsEmpty && appearance is not null && pageIndex >= 0)
        {
            var pageDict = doc.ResolveDict(pages[pageIndex])!;
            int rotation = (int)(((doc.GetInheritedPageAttribute(pageDict, "Rotate") as CosNumber)?.AsLong ?? 0) % 360 + 360) % 360;
            bool swap = rotation is 90 or 270;
            var ctx = new AppearanceContext(swap ? rect.Height : rect.Width, swap ? rect.Width : rect.Height, rotation);
            var ap = appearance(ctx);
            if (ap is not null)
            {
                var form = ap.Form;
                if (ap.Image is not null)
                {
                    if (ap.Image.Dictionary["SMask"] is SMaskHolder mask) ap.Image.Dictionary["SMask"] = upd.Add(mask.Stream);
                    var imgRef = upd.Add(ap.Image);
                    var res = form.Dictionary["Resources"] as CosDictionary ?? new CosDictionary();
                    var xo = res["XObject"] as CosDictionary ?? new CosDictionary();
                    xo[ap.ImageResourceName] = imgRef;
                    res["XObject"] = xo;
                    form.Dictionary["Resources"] = res;
                }
                if (rotation != 0)
                {
                    // obrót treści przeciwnie do /Rotate strony, by podpis był prosty na ekranie
                    double rad = rotation * Math.PI / 180;
                    form.Dictionary["Matrix"] = CosArray.OfNumbers(Math.Round(Math.Cos(rad)), Math.Round(Math.Sin(rad)), -Math.Round(Math.Sin(rad)), Math.Round(Math.Cos(rad)), 0, 0);
                    widget["MK"] = new CosDictionary { ["R"] = new CosNumber(rotation) };
                }
                var apRef = upd.Add(form);
                widget["AP"] = new CosDictionary { ["N"] = apRef };
            }
        }
        else if (!widget.ContainsKey("AP"))
        {
            // niewidoczny podpis – pusty wygląd (wymagany przez PDF/A i część czytników)
            var empty = new CosStream(new CosDictionary
            {
                ["Type"] = new CosName("XObject"),
                ["Subtype"] = new CosName("Form"),
                ["BBox"] = CosArray.OfNumbers(0, 0, 0, 0),
            }, Array.Empty<byte>());
            widget["AP"] = new CosDictionary { ["N"] = upd.Add(empty) };
        }
        if (widget["F"] is CosNumber f) widget["F"] = new CosNumber(f.AsLong | 4); // Print
        upd.Set(widgetRef, widget);

        acro["SigFlags"] = new CosNumber(3); // SignaturesExist | AppendOnly
        if (acroRef is not null) upd.Set(acroRef, acro);
        else
        {
            catalog["AcroForm"] = upd.Add(acro);
            catalogChanged = true;
        }
        if (catalogChanged) upd.Set(catalogRef, catalog);
    }

    public static string UniqueFieldName(IEnumerable<string> existing, string prefix)
    {
        var set = new HashSet<string>(existing, StringComparer.Ordinal);
        for (int i = 1; ; i++)
        {
            var n = prefix + i.ToString(CultureInfo.InvariantCulture);
            if (!set.Contains(n)) return n;
        }
    }

    /// <summary>Data w formacie PDF: D:YYYYMMDDHHmmSS+HH'mm'.</summary>
    public static string PdfDate(DateTimeOffset t)
    {
        var off = t.Offset;
        var sign = off < TimeSpan.Zero ? "-" : "+";
        off = off.Duration();
        return $"D:{t:yyyyMMddHHmmss}{sign}{off.Hours:D2}'{off.Minutes:D2}'";
    }

    public static DateTimeOffset? ParsePdfDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (s.StartsWith("D:", StringComparison.Ordinal)) s = s[2..];
        var digits = new string(s.TakeWhile(char.IsDigit).ToArray());
        if (digits.Length < 4) return null;
        int Part(int start, int len, int def) => digits.Length >= start + len ? int.Parse(digits.AsSpan(start, len), CultureInfo.InvariantCulture) : def;
        try
        {
            var rest = s[digits.Length..];
            var offset = TimeSpan.Zero;
            if (rest.Length > 0 && (rest[0] == '+' || rest[0] == '-'))
            {
                var od = new string(rest[1..].Where(char.IsDigit).ToArray());
                int oh = od.Length >= 2 ? int.Parse(od.AsSpan(0, 2), CultureInfo.InvariantCulture) : 0;
                int om = od.Length >= 4 ? int.Parse(od.AsSpan(2, 2), CultureInfo.InvariantCulture) : 0;
                offset = new TimeSpan(oh, om, 0);
                if (rest[0] == '-') offset = -offset;
            }
            return new DateTimeOffset(Part(0, 4, 1), Part(4, 2, 1), Part(6, 2, 1), Part(8, 2, 0), Part(10, 2, 0), Part(12, 2, 0), offset);
        }
        catch (ArgumentException) { return null; }
    }
}

/// <summary>Podpis (CMS) nie mieści się w zarezerwowanym miejscu – ponów z większą rezerwą.</summary>
internal sealed class SignatureSpaceException : Exception
{
    public SignatureSpaceException(int required) : base($"Signature needs {required} bytes.") => Required = required;
    public int Required { get; }
}
