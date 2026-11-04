using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using UglyToad.PdfPig;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Editing.PdfA;
using ZetPDF.Editing.Pdf;
using ZetPDF.TestData;
using PigDocument = UglyToad.PdfPig.PdfDocument;
using PdfDocument = PdfSharp.Pdf.PdfDocument;

namespace ZetPDF.Editing.Tests;

public class PdfATests
{
    private readonly PdfAService _svc = new(TestEnv.Renderer);

    private static PdfARuleResult Rule(PdfACheckReport r, string id) => r.Results.Single(x => x.Rule == id);

    private static void AssertFailed(PdfACheckReport r, string id) => Assert.Equal(PdfARuleStatus.Failed, Rule(r, id).Status);

    private static void AssertPassed(PdfACheckReport r, string id) => Assert.Equal(PdfARuleStatus.Passed, Rule(r, id).Status);

    private static string Latin(byte[] b) => Encoding.Latin1.GetString(b);

    private async Task<byte[]> Convert(byte[] pdf, bool rasterize = true, bool remove = true)
    {
        var result = await _svc.ConvertAsync(pdf, new PdfAConvertOptions { AllowRasterize = rasterize, AllowContentRemoval = remove });
        Assert.True(result.Pdf is not null, result.Reason + ": " + string.Join(", ", result.After?.Results.Where(r => r.Status == PdfARuleStatus.Failed).Select(r => r.Rule + "=" + r.Count + " " + string.Join(";", r.Examples ?? Array.Empty<string>())) ?? Array.Empty<string>()));
        return result.Pdf!;
    }

    /// <summary>Wymagania wyniku sprawdzane PDFsharp i PdfPig, niezależnie od kodu sprawdzającego reguły.</summary>
    private static void AssertArchiveStructure(byte[] pdf)
    {
        PdfSharpEdit.Read(pdf, doc =>
        {
            var catalog = doc.Internals.Catalog;
            var meta = Assert.IsType<PdfDictionary>(PdfObjects.Resolve(catalog.Elements["/Metadata"]));
            Assert.False(meta.Elements.ContainsKey("/Filter"));
            var xmp = Encoding.UTF8.GetString(meta.Stream.Value);
            Assert.Contains("<pdfaid:part>2</pdfaid:part>", xmp);
            Assert.Contains("<pdfaid:conformance>B</pdfaid:conformance>", xmp);
            Assert.StartsWith("<?xpacket begin=", xmp);
            var intents = Assert.IsType<PdfArray>(PdfObjects.Resolve(catalog.Elements["/OutputIntents"]));
            var intent = Assert.IsType<PdfDictionary>(PdfObjects.Resolve(intents.Elements[0]));
            Assert.Equal("/GTS_PDFA1", intent.Elements.GetName("/S"));
            var profile = Assert.IsType<PdfDictionary>(PdfObjects.Resolve(intent.Elements["/DestOutputProfile"]));
            Assert.Equal(3, profile.Elements.GetInteger("/N"));
            return 0;
        });
        var text = Latin(pdf);
        Assert.DoesNotContain("/Encrypt", text);
        Assert.DoesNotContain("/JavaScript", text);
        Assert.DoesNotContain("/JS ", text);
        Assert.Contains("/ID", text);
        using var pig = PigDocument.Open(pdf);
        Assert.True(pig.NumberOfPages > 0);
    }

    // ------------------------------------------------------------------ reguły na próbkach

    [Fact]
    public async Task Regular_pdf_fails_identification_rules_and_passes_structure_rules()
    {
        var report = await _svc.CheckAsync(SamplePdfFactory.Text(2));
        AssertFailed(report, "PdfaId");
        AssertFailed(report, "OutputIntent");
        AssertPassed(report, "Encryption");
        AssertPassed(report, "JavaScript");
        AssertPassed(report, "Fonts");
        AssertPassed(report, "TrailerId");
        AssertPassed(report, "Header");
        Assert.False(report.NoFailures);
        Assert.Contains(report.Results, r => r.Status == PdfARuleStatus.NotChecked);
    }

    [Fact]
    public async Task Encrypted_file_fails_without_password_and_session_encryption_is_reported()
    {
        var report = await _svc.CheckAsync(SamplePdfFactory.EncryptedAes256());
        AssertFailed(report, "Encryption");
        var saved = await _svc.CheckAsync(SamplePdfFactory.Text(1), savedEncrypted: true);
        AssertFailed(saved, "Encryption");
    }

    [Fact]
    public async Task JavaScript_and_attachment_are_detected()
    {
        var report = await _svc.CheckAsync(SamplePdfFactory.WithMetadataAttachmentAndJavaScript());
        AssertFailed(report, "JavaScript");
        AssertFailed(report, "Attachments");
    }

    [Fact]
    public async Task Unembedded_font_is_detected_with_name_and_page()
    {
        var report = await _svc.CheckAsync(ExtraSamples.WithLiteralText(new[] { "Tekst Helvetica" }));
        var fonts = Rule(report, "Fonts");
        Assert.Equal(PdfARuleStatus.Failed, fonts.Status);
        Assert.Contains("Helvetica (1)", fonts.Examples!);
    }

    [Fact]
    public async Task Hidden_text_with_unembedded_font_is_allowed()
    {
        var pdf = PdfSharpEdit.Modify(ExtraSamples.WithLiteralText(new[] { "x" }), doc =>
        {
            var content = Encoding.ASCII.GetBytes("BT 3 Tr /F1 12 Tf 10 10 Td (ukryty) Tj ET\n");
            Ocr.Pdf.PageContent.Replace(doc.Pages[0], content);
        });
        AssertPassed(await _svc.CheckAsync(pdf), "Fonts");
    }

    [Fact]
    public async Task Cmyk_and_transparency_and_interpolation()
    {
        var cmyk = PdfSharpEdit.Modify(SamplePdfFactory.Text(1), doc =>
            Ocr.Pdf.PageContent.Append(doc.Pages[0], Encoding.ASCII.GetBytes("0 0 0 1 k 10 10 50 50 re f\n")));
        var report = await _svc.CheckAsync(cmyk);
        AssertFailed(report, "DeviceColors");
        Assert.Contains("1", Rule(report, "DeviceColors").Examples!);

        var transparent = PdfSharpEdit.Modify(SamplePdfFactory.Text(1), doc =>
        {
            var group = new PdfDictionary(doc);
            group.Elements["/S"] = new PdfName("/Transparency");
            doc.Pages[0].Elements["/Group"] = group;
        });
        Assert.Equal(PdfARuleStatus.Info, Rule(await _svc.CheckAsync(transparent), "Transparency").Status);
    }

    [Fact]
    public async Task Forbidden_actions_multimedia_and_hidden_annotations_are_detected()
    {
        var pdf = PdfSharpEdit.Modify(SamplePdfFactory.Text(1), doc =>
        {
            var launch = new PdfDictionary(doc);
            launch.Elements["/S"] = new PdfName("/Launch");
            doc.Internals.Catalog.Elements["/OpenAction"] = launch;
            var page = doc.Pages[0];
            var screen = new PdfDictionary(doc);
            screen.Elements["/Subtype"] = new PdfName("/Screen");
            screen.Elements["/Rect"] = new PdfArray(doc, new PdfInteger(0), new PdfInteger(0), new PdfInteger(10), new PdfInteger(10));
            PdfObjects.AddAnnotation(page, screen);
            var hidden = new PdfDictionary(doc);
            hidden.Elements["/Subtype"] = new PdfName("/Text");
            hidden.Elements["/Rect"] = new PdfArray(doc, new PdfInteger(0), new PdfInteger(0), new PdfInteger(10), new PdfInteger(10));
            hidden.Elements.SetInteger("/F", 2);
            PdfObjects.AddAnnotation(page, hidden);
        });
        var report = await _svc.CheckAsync(pdf);
        AssertFailed(report, "Actions");
        AssertFailed(report, "Multimedia");
        AssertFailed(report, "Annotations");
        var plan = await _svc.PlanAsync(pdf);
        Assert.Equal(1, plan.ActionsToRemove);
        Assert.Equal(2, plan.AnnotationsToRemove);
        Assert.True(plan.RemovesContent);

        var fixedPdf = await Convert(pdf);
        var after = await _svc.CheckAsync(fixedPdf);
        AssertPassed(after, "Actions");
        AssertPassed(after, "Multimedia");
        AssertPassed(after, "Annotations");
    }

    // ------------------------------------------------------------------ pliki spreparowane

    private static byte[] WithPageContent(string content, Action<PdfDocument, PdfDictionary> resources) => PdfSharpEdit.Modify(SamplePdfFactory.Text(1), doc =>
    {
        var page = doc.Pages[0];
        var res = new PdfDictionary(doc);
        page.Elements["/Resources"] = res;
        resources(doc, res);
        Ocr.Pdf.PageContent.Replace(page, Encoding.ASCII.GetBytes(content));
    });

    private static PdfDictionary Form(PdfDocument doc, string content)
    {
        var form = new PdfDictionary(doc);
        form.Elements["/Type"] = new PdfName("/XObject");
        form.Elements["/Subtype"] = new PdfName("/Form");
        form.Elements["/BBox"] = new PdfArray(doc, new PdfInteger(0), new PdfInteger(0), new PdfInteger(10), new PdfInteger(10));
        form.CreateStream(Encoding.ASCII.GetBytes(content));
        doc.Internals.AddObject(form);
        return form;
    }

    private static void Links(PdfDocument doc, PdfDictionary from, PdfDictionary to)
    {
        var xobjects = new PdfDictionary(doc);
        xobjects.Elements["/X"] = to.Reference!;
        var res = new PdfDictionary(doc);
        res.Elements["/XObject"] = xobjects;
        from.Elements["/Resources"] = res;
    }

    [Fact]
    public async Task Type0_font_pointing_at_itself_does_not_crash_and_counts_as_unembedded()
    {
        var pdf = WithPageContent("BT /F1 12 Tf 10 10 Td (x) Tj ET\n", (doc, res) =>
        {
            var font = new PdfDictionary(doc);
            font.Elements["/Type"] = new PdfName("/Font");
            font.Elements["/Subtype"] = new PdfName("/Type0");
            font.Elements["/BaseFont"] = new PdfName("/Petla");
            doc.Internals.AddObject(font);
            font.Elements["/DescendantFonts"] = new PdfArray(doc, font.Reference!);
            var fonts = new PdfDictionary(doc);
            fonts.Elements["/F1"] = font.Reference!;
            res.Elements["/Font"] = fonts;
        });
        AssertFailed(await _svc.CheckAsync(pdf), "Fonts");
        Assert.Equal(new[] { 0 }, (await _svc.PlanAsync(pdf)).PagesToRasterize);
        AssertPassed(await _svc.CheckAsync(await Convert(pdf)), "Fonts");
    }

    [Fact]
    public async Task Form_xobject_cycle_is_reported_as_unreadable_without_crash()
    {
        var pdf = WithPageContent("/X Do\n", (doc, res) =>
        {
            var a = Form(doc, "/X Do\n");
            var b = Form(doc, "/X Do\n");
            Links(doc, a, b);
            Links(doc, b, a);
            var xobjects = new PdfDictionary(doc);
            xobjects.Elements["/X"] = a.Reference!;
            res.Elements["/XObject"] = xobjects;
        });
        AssertFailed(await _svc.CheckAsync(pdf), "UnreadableContent");
        Assert.Equal(new[] { 0 }, (await _svc.PlanAsync(pdf)).PagesToRasterize);
        var converted = await _svc.CheckAsync(await Convert(pdf));
        Assert.DoesNotContain(converted.Results, r => r.Rule == "UnreadableContent");
    }

    [Fact]
    public async Task Very_deep_form_nesting_is_unreadable_without_crash()
    {
        var pdf = WithPageContent("/X Do\n", (doc, res) =>
        {
            var first = Form(doc, "/X Do\n");
            var current = first;
            for (int i = 0; i < 500; i++)
            {
                var next = Form(doc, "/X Do\n");
                Links(doc, current, next);
                current = next;
            }
            var xobjects = new PdfDictionary(doc);
            xobjects.Elements["/X"] = first.Reference!;
            res.Elements["/XObject"] = xobjects;
        });
        AssertFailed(await _svc.CheckAsync(pdf), "UnreadableContent");
    }

    [Fact]
    public async Task Nested_forms_within_the_limit_are_read_normally()
    {
        var pdf = WithPageContent("/X Do\n", (doc, res) =>
        {
            var first = Form(doc, "q Q\n");
            var second = Form(doc, "0 0 0 1 k\n");
            Links(doc, first, second);
            var xobjects = new PdfDictionary(doc);
            xobjects.Elements["/X"] = first.Reference!;
            res.Elements["/XObject"] = xobjects;
        });
        var report = await _svc.CheckAsync(pdf);
        Assert.DoesNotContain(report.Results, r => r.Rule == "UnreadableContent");
        AssertFailed(report, "DeviceColors"); // kolor z formularza zagnieżdżonego
    }

    private static PdfDictionary Script(PdfDocument doc, bool indirect)
    {
        var js = new PdfDictionary(doc);
        js.Elements["/S"] = new PdfName("/JavaScript");
        js.Elements["/JS"] = new PdfString("app.alert(1)");
        if (indirect) doc.Internals.AddObject(js);
        return js;
    }

    /// <summary>Pole z /AA i przycisk z /A JavaScript w drzewie /AcroForm /Fields, także poza stronami i w polu nadrzędnym; pętla w /Kids.</summary>
    private static byte[] WithScriptedFields() => PdfSharpEdit.Modify(SamplePdfFactory.Text(1), doc =>
    {
        var fields = new PdfArray(doc);
        var parent = new PdfDictionary(doc);
        parent.Elements["/T"] = new PdfString("rodzic");
        var aa = new PdfDictionary(doc);
        aa.Elements["/K"] = Script(doc, indirect: true).Reference!;
        parent.Elements["/AA"] = aa;
        doc.Internals.AddObject(parent);
        var button = new PdfDictionary(doc);
        button.Elements["/T"] = new PdfString("przycisk");
        button.Elements["/FT"] = new PdfName("/Btn");
        button.Elements["/A"] = Script(doc, indirect: false); // akcja bezpośrednio w słowniku
        button.Elements["/Parent"] = parent.Reference!;
        doc.Internals.AddObject(button);
        parent.Elements["/Kids"] = new PdfArray(doc, button.Reference!, parent.Reference!);
        fields.Elements.Add(parent.Reference!);
        var acro = new PdfDictionary(doc);
        acro.Elements["/Fields"] = fields;
        doc.Internals.Catalog.Elements["/AcroForm"] = acro;
    });

    [Fact]
    public async Task Scripts_on_form_fields_are_detected_and_removed_from_the_whole_file()
    {
        var input = WithScriptedFields();
        var report = await _svc.CheckAsync(input);
        AssertFailed(report, "JavaScript");
        AssertFailed(report, "Actions");

        var pdf = await Convert(input);
        Assert.DoesNotContain("/JavaScript", Latin(pdf));
        Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex(@"/AA\s*[<\d]"), Latin(pdf));
        PdfSharpEdit.Read(pdf, doc =>
        {
            foreach (var d in PdfATree.Dictionaries(doc)) Assert.False(d.Elements.ContainsKey("/AA"));
            return 0;
        });
        var after = await _svc.CheckAsync(pdf);
        AssertPassed(after, "JavaScript");
        AssertPassed(after, "Actions");
    }

    /// <summary>Akcja JavaScript z dopisanymi /P, /K i /Type: autor pliku próbuje nie wyglądać jak akcja.</summary>
    private static PdfDictionary DisguisedScript(PdfDocument doc)
    {
        var js = new PdfDictionary(doc);
        js.Elements["/S"] = new PdfName("/JavaScript");
        js.Elements["/JS"] = new PdfString("app.alert(1)");
        js.Elements["/P"] = new PdfInteger(1);
        js.Elements["/K"] = new PdfInteger(1);
        js.Elements["/Type"] = new PdfName("/StructElem");
        doc.Internals.AddObject(js);
        return js;
    }

    [Fact]
    public async Task Disguised_script_in_open_action_and_field_is_reported_and_removed()
    {
        var input = PdfSharpEdit.Modify(SamplePdfFactory.Text(1), doc =>
        {
            doc.Internals.Catalog.Elements["/OpenAction"] = DisguisedScript(doc).Reference!;
            var field = new PdfDictionary(doc);
            field.Elements["/T"] = new PdfString("pole");
            field.Elements["/FT"] = new PdfName("/Btn");
            field.Elements["/A"] = DisguisedScript(doc).Reference!;
            doc.Internals.AddObject(field);
            var acro = new PdfDictionary(doc);
            acro.Elements["/Fields"] = new PdfArray(doc, field.Reference!);
            doc.Internals.Catalog.Elements["/AcroForm"] = acro;
        });
        AssertFailed(await _svc.CheckAsync(input), "JavaScript");
        Assert.Equal(2, (await _svc.PlanAsync(input)).ActionsToRemove);
        var pdf = await Convert(input);
        Assert.DoesNotContain("/JavaScript", Latin(pdf));
        Assert.DoesNotContain("app.alert", Latin(pdf));
        AssertPassed(await _svc.CheckAsync(pdf), "JavaScript");
    }

    /// <summary>Drzewo struktury z elementami o podanych /S; drugi argument dokłada klucze do elementu.</summary>
    private static byte[] WithStructure(string type, Action<PdfDocument, PdfDictionary>? extra = null) => PdfSharpEdit.Modify(SamplePdfFactory.Text(1), doc =>
    {
        var element = new PdfDictionary(doc);
        element.Elements["/Type"] = new PdfName("/StructElem");
        element.Elements["/S"] = new PdfName(type);
        element.Elements["/K"] = new PdfInteger(0);
        doc.Internals.AddObject(element);
        extra?.Invoke(doc, element);
        var root = new PdfDictionary(doc);
        root.Elements["/Type"] = new PdfName("/StructTreeRoot");
        root.Elements["/K"] = element.Reference!;
        doc.Internals.AddObject(root);
        element.Elements["/P"] = root.Reference!;
        doc.Internals.Catalog.Elements["/StructTreeRoot"] = root.Reference!;
    });

    [Theory]
    [InlineData("/P")]
    [InlineData("/Sound")]
    [InlineData("/Movie")]
    public async Task Structure_elements_are_not_mistaken_for_actions(string type)
    {
        var input = WithStructure(type);
        var report = await _svc.CheckAsync(input);
        AssertPassed(report, "Actions");
        AssertPassed(report, "JavaScript");
        // element zostaje w wyniku
        var pdf = await Convert(input);
        Assert.Contains(type, PdfSharpEdit.Read(pdf, d => ((PdfDictionary)PdfObjects.Resolve(((PdfDictionary)PdfObjects.Resolve(d.Internals.Catalog.Elements["/StructTreeRoot"])!).Elements["/K"])!).Elements.GetName("/S")));
    }

    [Fact]
    public async Task Same_type_outside_the_structure_tree_is_an_action()
    {
        var input = PdfSharpEdit.Modify(SamplePdfFactory.Text(1), doc =>
        {
            var sound = new PdfDictionary(doc);
            sound.Elements["/S"] = new PdfName("/Sound");
            sound.Elements["/K"] = new PdfInteger(0);
            sound.Elements["/Type"] = new PdfName("/StructElem");
            doc.Internals.AddObject(sound);
            doc.Internals.Catalog.Elements["/OpenAction"] = sound.Reference!;
        });
        AssertFailed(await _svc.CheckAsync(input), "Actions");
    }

    private static PdfDictionary Action(PdfDocument doc, string? type, bool disguised = false)
    {
        var a = new PdfDictionary(doc);
        if (type is not null) a.Elements["/S"] = new PdfName(type);
        a.Elements["/JS"] = new PdfString("zlosliwy()");
        if (disguised)
        {
            a.Elements["/P"] = new PdfInteger(1);
            a.Elements["/K"] = new PdfInteger(1);
            a.Elements["/Type"] = new PdfName("/StructElem");
        }
        doc.Internals.AddObject(a);
        return a;
    }

    private static PdfDictionary Uri(PdfDocument doc)
    {
        var a = new PdfDictionary(doc);
        a.Elements["/S"] = new PdfName("/URI");
        a.Elements["/URI"] = new PdfString("https://example.com/");
        doc.Internals.AddObject(a);
        return a;
    }

    /// <summary>Akcja w każdym z miejsc: /OpenAction, /A łącza, /AA strony, zakładka, pole, /Next dozwolonej akcji.</summary>
    private static byte[] WithActionEverywhere(string? type, bool disguised) => PdfSharpEdit.Modify(SamplePdfFactory.Text(1), doc =>
    {
        var catalog = doc.Internals.Catalog;
        catalog.Elements["/OpenAction"] = Action(doc, type, disguised).Reference!;
        var page = doc.Pages[0];
        var trigger = new PdfDictionary(doc);
        trigger.Elements["/O"] = Action(doc, type, disguised).Reference!;
        page.Elements["/AA"] = trigger;

        var link = new PdfDictionary(doc);
        link.Elements["/Subtype"] = new PdfName("/Link");
        link.Elements["/Rect"] = new PdfArray(doc, new PdfInteger(0), new PdfInteger(0), new PdfInteger(10), new PdfInteger(10));
        link.Elements.SetInteger("/F", 4);
        link.Elements["/A"] = Action(doc, type, disguised).Reference!;
        PdfObjects.AddAnnotation(page, link);

        var link2 = new PdfDictionary(doc);
        link2.Elements["/Subtype"] = new PdfName("/Link");
        link2.Elements["/Rect"] = new PdfArray(doc, new PdfInteger(20), new PdfInteger(0), new PdfInteger(30), new PdfInteger(10));
        link2.Elements.SetInteger("/F", 4);
        var head = Uri(doc);
        head.Elements["/Next"] = Action(doc, type, disguised).Reference!;
        link2.Elements["/A"] = head.Reference!;
        PdfObjects.AddAnnotation(page, link2);

        var item = new PdfDictionary(doc);
        item.Elements["/Title"] = new PdfString("zakładka");
        item.Elements["/A"] = Action(doc, type, disguised).Reference!;
        doc.Internals.AddObject(item);
        var outlines = new PdfDictionary(doc);
        outlines.Elements["/First"] = item.Reference!;
        outlines.Elements["/Last"] = item.Reference!;
        doc.Internals.AddObject(outlines);
        item.Elements["/Parent"] = outlines.Reference!;
        catalog.Elements["/Outlines"] = outlines.Reference!;

        var field = new PdfDictionary(doc);
        field.Elements["/T"] = new PdfString("pole");
        field.Elements["/A"] = Action(doc, type, disguised).Reference!;
        doc.Internals.AddObject(field);
        var acro = new PdfDictionary(doc);
        acro.Elements["/Fields"] = new PdfArray(doc, field.Reference!);
        catalog.Elements["/AcroForm"] = acro;
    });

    private static void AssertNoExecutionPath(byte[] pdf)
    {
        Assert.DoesNotContain("zlosliwy", Latin(pdf));
        PdfSharpEdit.Read(pdf, doc =>
        {
            foreach (var d in PdfATree.Dictionaries(doc))
            {
                Assert.False(d.Elements.ContainsKey("/AA"));
                foreach (var key in PdfAActions.PlaceKeys)
                    foreach (var start in PdfAActions.ActionsAt(d, key))
                        foreach (var (_, action) in PdfAActions.Chain(start)) Assert.True(PdfAActions.IsAllowed(action), key);
            }
            return 0;
        });
    }

    [Theory]
    [InlineData("/ResetForm", false)]
    [InlineData("/Hide", false)]
    [InlineData("/Launch", false)]
    [InlineData("/JavaScript", false)]
    [InlineData("/ResetForm", true)]
    [InlineData("/Hide", true)]
    [InlineData("/Launch", true)]
    [InlineData("/JavaScript", true)]
    [InlineData("/NieznanyTyp", true)]
    [InlineData(null, false)]
    [InlineData(null, true)]
    public async Task Forbidden_action_is_found_and_removed_in_every_place(string? type, bool disguised)
    {
        var input = WithActionEverywhere(type, disguised);
        var before = await _svc.CheckAsync(input);
        AssertFailed(before, type == "/JavaScript" ? "JavaScript" : "Actions");
        var pdf = await Convert(input);
        AssertNoExecutionPath(pdf);
        var after = await _svc.CheckAsync(pdf);
        AssertPassed(after, "Actions");
        AssertPassed(after, "JavaScript");
    }

    [Fact]
    public async Task Next_cycle_ends_and_allowed_actions_stay_in_order()
    {
        var input = PdfSharpEdit.Modify(SamplePdfFactory.Text(1), doc =>
        {
            var first = Uri(doc);
            var bad = Action(doc, "/Launch");
            var second = Uri(doc);
            second.Elements["/URI"] = new PdfString("https://example.org/drugi");
            first.Elements["/Next"] = bad.Reference!;
            bad.Elements["/Next"] = new PdfArray(doc, second.Reference!, Action(doc, "/JavaScript").Reference!);
            second.Elements["/Next"] = first.Reference!; // cykl
            doc.Internals.Catalog.Elements["/OpenAction"] = first.Reference!;
        });
        AssertFailed(await _svc.CheckAsync(input), "Actions");
        var pdf = await Convert(input);
        AssertNoExecutionPath(pdf);
        PdfSharpEdit.Read(pdf, doc =>
        {
            var chain = PdfAActions.Chain(doc.Internals.Catalog.Elements["/OpenAction"]!);
            Assert.Equal(new[] { "https://example.com/", "https://example.org/drugi" }, chain.Select(c => c.Action.Elements.GetString("/URI")));
            return 0;
        });
    }

    [Fact]
    public async Task Forbidden_head_of_chain_gives_way_to_allowed_action()
    {
        var input = PdfSharpEdit.Modify(SamplePdfFactory.Text(1), doc =>
        {
            var bad = Action(doc, "/Launch");
            bad.Elements["/Next"] = Uri(doc).Reference!;
            doc.Internals.Catalog.Elements["/OpenAction"] = bad.Reference!;
        });
        var pdf = await Convert(input);
        Assert.Equal("/URI", PdfSharpEdit.Read(pdf, doc => ((PdfDictionary)PdfObjects.Resolve(doc.Internals.Catalog.Elements["/OpenAction"])!).Elements.GetName("/S")));
    }

    [Fact]
    public async Task Allowed_actions_stay_and_destination_array_is_not_an_action()
    {
        var input = PdfSharpEdit.Modify(SamplePdfFactory.WithBookmarks(), doc =>
        {
            var page = doc.Pages[0];
            doc.Internals.Catalog.Elements["/OpenAction"] = new PdfArray(doc, page.Reference!, new PdfName("/Fit"));
            var link = new PdfDictionary(doc);
            link.Elements["/Subtype"] = new PdfName("/Link");
            link.Elements["/Rect"] = new PdfArray(doc, new PdfInteger(0), new PdfInteger(0), new PdfInteger(10), new PdfInteger(10));
            link.Elements.SetInteger("/F", 4);
            link.Elements["/A"] = Uri(doc).Reference!;
            PdfObjects.AddAnnotation(page, link);
            var goTo = new PdfDictionary(doc);
            goTo.Elements["/S"] = new PdfName("/GoTo");
            goTo.Elements["/D"] = new PdfArray(doc, page.Reference!, new PdfName("/Fit"));
            var link2 = new PdfDictionary(doc);
            link2.Elements["/Subtype"] = new PdfName("/Link");
            link2.Elements["/Rect"] = new PdfArray(doc, new PdfInteger(20), new PdfInteger(0), new PdfInteger(30), new PdfInteger(10));
            link2.Elements.SetInteger("/F", 4);
            link2.Elements["/A"] = goTo;
            PdfObjects.AddAnnotation(page, link2);
        });
        var report = await _svc.CheckAsync(input);
        AssertPassed(report, "Actions");
        Assert.Equal(0, (await _svc.PlanAsync(input)).ActionsToRemove);
        var pdf = await Convert(input);
        PdfSharpEdit.Read(pdf, doc =>
        {
            Assert.IsType<PdfArray>(PdfObjects.Resolve(doc.Internals.Catalog.Elements["/OpenAction"]));
            var kinds = PdfObjects.Annotations(doc.Pages[0]).Select(a => ((PdfDictionary)PdfObjects.Resolve(a.Elements["/A"])!).Elements.GetName("/S")).ToList();
            Assert.Equal(new[] { "/URI", "/GoTo" }, kinds);
            return 0;
        });
    }

    [Fact]
    public async Task Structure_element_with_attribute_object_is_not_touched()
    {
        var input = WithStructure("/P", (doc, e) =>
        {
            var attribute = new PdfDictionary(doc);
            attribute.Elements["/O"] = new PdfName("/Layout");
            e.Elements["/A"] = attribute;
        });
        var report = await _svc.CheckAsync(input);
        AssertPassed(report, "Actions");
        var pdf = await Convert(input);
        Assert.Contains("/Layout", Latin(pdf));
    }

    [Fact]
    public async Task Named_actions_only_navigation()
    {
        byte[] With(string name) => PdfSharpEdit.Modify(SamplePdfFactory.Text(1), doc =>
        {
            var a = new PdfDictionary(doc);
            a.Elements["/S"] = new PdfName("/Named");
            a.Elements["/N"] = new PdfName(name);
            doc.Internals.Catalog.Elements["/OpenAction"] = a;
        });
        AssertPassed(await _svc.CheckAsync(With("/NextPage")), "Actions");
        AssertFailed(await _svc.CheckAsync(With("/Print")), "Actions");
    }

    // ------------------------------------------------------------------ konwersja

    [Fact]
    public async Task Converted_text_pdf_has_pdfaid_output_intent_and_keeps_text()
    {
        var pdf = await Convert(SamplePdfFactory.Text(2));
        AssertArchiveStructure(pdf);
        var report = await _svc.CheckAsync(pdf);
        Assert.True(report.NoFailures, string.Join(", ", report.Results.Where(r => r.Status == PdfARuleStatus.Failed).Select(r => r.Rule)));
        Assert.Contains(TestEnv.Squash(SamplePdfFactory.PolishPangram), TestEnv.Squash(TestEnv.PdfPigText(pdf)));
    }

    [Fact]
    public async Task Encrypted_document_opened_with_password_converts_without_encrypt()
    {
        var decrypted = await TestEnv.Renderer.RemoveSecurityAsync(SamplePdfFactory.EncryptedAes256(), SamplePdfFactory.UserPassword);
        var pdf = await Convert(decrypted);
        AssertArchiveStructure(pdf);
    }

    [Fact]
    public async Task Encrypted_input_is_refused_with_clear_error()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _svc.ConvertAsync(SamplePdfFactory.EncryptedAes256(), new PdfAConvertOptions()));
    }

    [Fact]
    public async Task JavaScript_and_attachment_are_removed_only_with_consent()
    {
        var input = SamplePdfFactory.WithMetadataAttachmentAndJavaScript();
        var refused = await _svc.ConvertAsync(input, new PdfAConvertOptions());
        Assert.Null(refused.Pdf);
        Assert.Equal(PdfANotConverted.RemovesContent, refused.Reason);
        Assert.Equal(1, refused.Plan.Attachments);

        var pdf = await Convert(input);
        AssertArchiveStructure(pdf);
        Assert.DoesNotContain("/EmbeddedFiles", Latin(pdf));
        Assert.Equal("Tajny tytuł", PdfSharpEdit.Read(pdf, d => d.Info.Title));
    }

    [Fact]
    public async Task Standard_font_is_embedded_from_the_system_without_rasterizing()
    {
        var input = ExtraSamples.WithLiteralText(new[] { "Tekst do osadzenia Helvetica" });
        var plan = await _svc.PlanAsync(input);
        Assert.Empty(plan.PagesToRasterize);
        Assert.Contains("Helvetica → Arial", plan.FontsToEmbed);

        var pdf = await Convert(input, rasterize: false, remove: false);
        AssertArchiveStructure(pdf);
        Assert.Contains("Tekst do osadzenia Helvetica", TestEnv.PdfPigText(pdf));
        AssertPassed(await _svc.CheckAsync(pdf), "Fonts");
        PdfSharpEdit.Read(pdf, doc =>
        {
            var fonts = (PdfDictionary)PdfObjects.Resolve(((PdfDictionary)PdfObjects.Resolve(doc.Pages[0].Elements["/Resources"])!).Elements["/Font"])!;
            var font = (PdfDictionary)PdfObjects.Resolve(fonts.Elements["/F1"])!;
            var descriptor = (PdfDictionary)PdfObjects.Resolve(font.Elements["/FontDescriptor"])!;
            Assert.True(descriptor.Elements.ContainsKey("/FontFile2"));
            return 0;
        });
    }

    /// <summary>Czcionka o nieznanej nazwie, bez pliku: nie da się osadzić, więc tylko rasteryzacja za zgodą.</summary>
    private static byte[] WithUnknownFont() => PdfSharpEdit.Modify(ExtraSamples.WithLiteralText(new[] { "Tekst nieznana czcionka" }), doc =>
    {
        var fonts = (PdfDictionary)PdfObjects.Resolve(((PdfDictionary)PdfObjects.Resolve(doc.Pages[0].Elements["/Resources"])!).Elements["/Font"])!;
        ((PdfDictionary)PdfObjects.Resolve(fonts.Elements["/F1"])!).Elements["/BaseFont"] = new PdfName("/NieznanaCzcionkaXYZ");
    });

    [Fact]
    public async Task Font_that_cannot_be_embedded_needs_consent_then_page_becomes_image_with_hidden_text()
    {
        var input = WithUnknownFont();
        var refused = await _svc.ConvertAsync(input, new PdfAConvertOptions { AllowContentRemoval = true });
        Assert.Null(refused.Pdf);
        Assert.Equal(PdfANotConverted.NeedsRasterize, refused.Reason);
        Assert.Equal(new[] { 0 }, refused.Plan.PagesToRasterize);

        var pdf = await Convert(input);
        AssertArchiveStructure(pdf);
        // tekst nadal można wyszukać, a strona ma tylko obraz i ukrytą warstwę
        Assert.Contains("nieznana", await TestEnv.PdfiumText(pdf));
        var raw = TestEnv.RawStreams(pdf);
        Assert.Contains("/ImPage Do", raw);
        Assert.Contains("3 Tr", raw);
        AssertPassed(await _svc.CheckAsync(pdf), "Fonts");
        using var rendered = await TestEnv.Renderer.OpenAsync(pdf);
        Assert.Equal(1, rendered.PageCount);
    }

    [Fact]
    public async Task Cmyk_page_is_rasterized_and_result_has_no_cmyk()
    {
        var input = PdfSharpEdit.Modify(SamplePdfFactory.Text(2), doc =>
            Ocr.Pdf.PageContent.Append(doc.Pages[1], Encoding.ASCII.GetBytes("0 1 0 0 k 10 10 50 50 re f\n")));
        Assert.Equal(new[] { 1 }, (await _svc.PlanAsync(input)).PagesToRasterize);
        var pdf = await Convert(input);
        var report = await _svc.CheckAsync(pdf);
        AssertPassed(report, "DeviceColors");
        AssertPassed(report, "XObjects");
        Assert.True(report.NoFailures);
    }

    [Fact]
    public async Task Form_widgets_without_appearance_need_rasterizing_and_fields_do_not_dangle()
    {
        var pdf = await _svc.PlanAsync(SamplePdfFactory.WithForm());
        // Próbka ma widżety z wyglądem lub bez; plan i konwersja mają się zgadzać.
        var result = await _svc.ConvertAsync(SamplePdfFactory.WithForm(), new PdfAConvertOptions { AllowRasterize = true, AllowContentRemoval = true });
        Assert.True(result.Pdf is not null, string.Join(", ", result.After?.Results.Where(r => r.Status == PdfARuleStatus.Failed).Select(r => r.Rule) ?? Array.Empty<string>()));
        if (pdf.PagesToRasterize.Count > 0) Assert.Empty(PdfSharpEdit.Read(result.Pdf!, d => PdfObjects.Annotations(d.Pages[0])));
    }

    [Fact]
    public async Task Missing_system_profile_gives_clear_error_and_invalid_profile_is_rejected()
    {
        var svc = new PdfAService(TestEnv.Renderer, null, () => null);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ConvertAsync(SamplePdfFactory.Text(1), new PdfAConvertOptions()));
        Assert.Contains("sRGB", ex.Message);
        Assert.False(IccProfile.IsValid(new byte[200], 3));
    }

    [Fact]
    public async Task Rules_failing_after_conversion_mean_no_output()
    {
        // Profil z poprawnym nagłówkiem, ale CMYK: po zapisie reguła profilu wyjściowego nie przejdzie, więc pliku nie ma.
        var bad = new byte[200];
        Encoding.ASCII.GetBytes("CMYK").CopyTo(bad, 16);
        Encoding.ASCII.GetBytes("acsp").CopyTo(bad, 36);
        var svc = new PdfAService(TestEnv.Renderer, null, () => bad);
        var result = await svc.ConvertAsync(SamplePdfFactory.Text(1), new PdfAConvertOptions());
        Assert.Null(result.Pdf);
        Assert.Equal(PdfANotConverted.RulesStillFailing, result.Reason);
        AssertFailed(result.After!, "OutputIntent");
    }

    [Fact]
    public void Report_in_html_escapes_text_from_the_document()
    {
        var report = new PdfACheckReport(new[] { new PdfARuleResult("Fonts", PdfARuleStatus.Failed, 1, new[] { "<script>alert(1)</script> (1)" }) });
        var loc = new ZetPDF.Core.Localization.LocalizationService();
        loc.RegisterResources(EditingModule.Strings);
        var html = PdfAChecker.BuildReport(report, loc, "<b>plik</b>.pdf", html: true);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("<b>plik", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains(Conversion.HtmlWriter.Escape(loc["Edit.PdfA.Disclaimer"]), html);
    }

    [Fact]
    public void Every_rule_has_texts_in_both_languages()
    {
        var ids = PdfAChecker.Report(new PdfAFacts()).Results.Select(r => r.Rule).Concat(new[] { "Transparency", "Signed", "UnreadableContent" });
        foreach (var culture in new[] { "en", "pl" })
        {
            var c = System.Globalization.CultureInfo.GetCultureInfo(culture);
            foreach (var id in ids)
            {
                Assert.False(string.IsNullOrEmpty(EditingModule.Strings.GetString("Edit.PdfA.Rule." + id, c)), id);
                Assert.False(string.IsNullOrEmpty(EditingModule.Strings.GetString("Edit.PdfA.Rule." + id + ".Desc", c)), id);
            }
        }
    }
}
