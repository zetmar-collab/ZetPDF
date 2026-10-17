using PdfSharp.Pdf;
using ZetPDF.Annotations.Pdf;
using ZetPDF.Core.Contracts;
using static ZetPDF.Annotations.Pdf.PdfLow;

namespace ZetPDF.Annotations.Forms;

/// <summary>
/// Operacje na formularzu AcroForm w otwartym dokumencie PDFsharp (synchroniczne). PDFsharp 6.2.4 nie ma API tworzenia
/// pól – słowniki budowane ręcznie wg PDF 32000-1 §12.7.
/// </summary>
internal static class FormEngine
{
    public static List<FormFieldInfo> Read(PdfDocument doc)
    {
        var pages = FieldTree.WidgetPages(doc);
        var result = new List<FormFieldInfo>();
        foreach (var f in FieldTree.Terminals(doc))
        {
            var w = f.Widgets.FirstOrDefault();
            int page = -1, tab = 0;
            if (w is not null && pages.TryGetValue(w, out var pi)) { page = pi.Page; tab = pi.Index; }
            else if (w is not null) page = PageIndexOf(doc, Dict(w, "/P"));
            var kind = f.Kind;
            var opts = f.Options;
            var exports = kind is FormFieldKind.CheckBox or FormFieldKind.RadioButton
                ? f.Widgets.Select(FieldNode.OnState).Where(s => s is not null).Select(s => s!).Distinct().ToList()
                : opts.Select(o => o.Export).ToList();
            result.Add(new FormFieldInfo
            {
                FullName = f.FullName,
                Kind = kind,
                PageIndex = page,
                Rect = w is not null ? PdfLow.Rect(w) ?? default : default,
                Value = f.Value,
                DefaultValue = f.DefaultValue,
                Options = opts.Select(o => o.Display).ToList(),
                ExportValues = exports,
                Tooltip = Text(f.Dict, "/TU"),
                Required = (f.Flags & FieldNode.FfRequired) != 0,
                ReadOnly = (f.Flags & FieldNode.FfReadOnly) != 0,
                Multiline = (f.Flags & FieldNode.FfMultiline) != 0,
                IsSigned = kind == FormFieldKind.Signature && f.Inherited("/V") is PdfDictionary,
                TabOrder = tab,
            });
        }
        return result;
    }

    /// <summary>Widżety pola z wymiarem (dla UI – opcje mają kilka widżetów).</summary>
    public static List<(string Name, FormFieldKind Kind, int Page, PdfRect Rect, string? OnState)> Widgets(PdfDocument doc)
    {
        var pages = FieldTree.WidgetPages(doc);
        var list = new List<(string, FormFieldKind, int, PdfRect, string?)>();
        foreach (var f in FieldTree.Terminals(doc))
            foreach (var w in f.Widgets)
            {
                int page = pages.TryGetValue(w, out var pi) ? pi.Page : PageIndexOf(doc, Dict(w, "/P"));
                if (page < 0) continue;
                list.Add((f.FullName, f.Kind, page, PdfLow.Rect(w) ?? default, FieldNode.OnState(w)));
            }
        return list;
    }

    public static void SetValue(PdfDocument doc, FieldNode f, string value)
    {
        var kind = f.Kind;
        switch (kind)
        {
            case FormFieldKind.CheckBox or FormFieldKind.RadioButton:
                // wartość może być nazwą eksportu albo "Yes"/"On" dla pojedynczego pola wyboru
                var states = f.Widgets.Select(FieldNode.OnState).Where(s => s is not null).Select(s => s!).Distinct().ToList();
                if (string.IsNullOrEmpty(value) || value == "Off") value = "Off";
                else if (!states.Contains(value))
                {
                    if (kind == FormFieldKind.CheckBox && states.Count >= 1 && value is "Yes" or "On" or "true" or "1") value = states[0];
                    else if (states.Count == 0) value = "Yes";
                    else value = "Off";
                }
                f.Dict.Elements["/V"] = Name("/" + value);
                break;
            case FormFieldKind.ComboBox or FormFieldKind.ListBox:
                // wartość wyświetlana → eksport
                var opt = f.Options.FirstOrDefault(o => o.Display == value || o.Export == value);
                var v = opt.Export ?? value;
                f.Dict.Elements["/V"] = Str(v);
                if (kind == FormFieldKind.ListBox)
                {
                    int idx = f.Options.FindIndex(o => o.Export == v);
                    if (idx >= 0) f.Dict.Elements["/I"] = new PdfArray(doc, new PdfInteger(idx));
                    else f.Dict.Elements.Remove("/I");
                }
                break;
            case FormFieldKind.PushButton or FormFieldKind.Signature:
                return;
            default:
                if (value.Length == 0) f.Dict.Elements.Remove("/V");
                else f.Dict.Elements["/V"] = Str(value);
                break;
        }
        FieldAppearance.Build(doc, f);
    }

    /// <summary>Wygląd wszystkich pól + /NeedAppearances false.</summary>
    public static void RefreshAppearances(PdfDocument doc)
    {
        foreach (var f in FieldTree.Terminals(doc)) FieldAppearance.Build(doc, f);
        if (FieldTree.AcroForm(doc) is { } acro) acro.Elements["/NeedAppearances"] = new PdfBoolean(false);
    }

    public static PdfDictionary EnsureAcroForm(PdfDocument doc)
    {
        var catalog = doc.Internals.Catalog;
        var acro = Dict(catalog, "/AcroForm");
        if (acro is null)
        {
            acro = NewDict(doc);
            doc.Internals.AddObject(acro);
            catalog.Elements["/AcroForm"] = acro.Reference!;
        }
        if (ArrayOf(acro, "/Fields") is null) acro.Elements["/Fields"] = new PdfArray(doc);
        var dr = Dict(acro, "/DR");
        if (dr is null) { dr = NewDict(doc); acro.Elements["/DR"] = dr; }
        var fonts = Dict(dr, "/Font");
        if (fonts is null) { fonts = NewDict(doc); dr.Elements["/Font"] = fonts; }
        if (!fonts.Elements.ContainsKey("/Helv"))
        {
            var helv = NewDict(doc, "/Font");
            helv.Elements["/Subtype"] = Name("/Type1");
            helv.Elements["/BaseFont"] = Name("/Helvetica");
            helv.Elements["/Encoding"] = Name("/WinAnsiEncoding");
            doc.Internals.AddObject(helv);
            fonts.Elements["/Helv"] = helv.Reference!;
        }
        if (!acro.Elements.ContainsKey("/DA")) acro.Elements["/DA"] = new PdfString("/Helv 0 Tf 0 g");
        return acro;
    }

    // ------------------------------------------------------------------ tworzenie

    public static void Create(PdfDocument doc, FormFieldSpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.Name)) throw new ArgumentException("Field name is required.", nameof(spec));
        if (spec.PageIndex < 0 || spec.PageIndex >= doc.PageCount) throw new ArgumentOutOfRangeException(nameof(spec));
        var acro = EnsureAcroForm(doc);
        var fields = ArrayOf(acro, "/Fields")!;
        var page = doc.Pages[spec.PageIndex];
        var annots = EnsureAnnots(page);
        var existing = FieldTree.Find(doc, spec.Name);

        if (spec.Kind == FormFieldKind.RadioButton)
        {
            var group = existing;
            if (group is not null && group.Kind != FormFieldKind.RadioButton) throw new ArgumentException("Field name already used: " + spec.Name);
            PdfDictionary parent;
            if (group is null)
            {
                parent = NewDict(doc);
                parent.Elements["/FT"] = Name("/Btn");
                parent.Elements["/T"] = Str(spec.Name);
                parent.Elements["/Ff"] = new PdfInteger(FieldNode.FfRadio | FieldNode.FfNoToggleToOff | CommonFlags(spec));
                parent.Elements["/V"] = Name("/Off");
                parent.Elements["/Kids"] = new PdfArray(doc);
                parent.Elements["/DA"] = new PdfString(AnnotModel.BuildDa(0, spec.Style.Color));
                if (spec.Tooltip.Length > 0) parent.Elements["/TU"] = Str(spec.Tooltip);
                doc.Internals.AddObject(parent);
                fields.Elements.Add(parent.Reference!);
            }
            else
            {
                parent = group.Dict;
                // pole scalone z jednym widżetem – rozdzielamy, żeby dodać kolejny
                if (ArrayOf(parent, "/Kids") is null) throw new InvalidOperationException("Unsupported radio group structure.");
            }
            int n = (ArrayOf(parent, "/Kids")?.Elements.Count ?? 0) + 1;
            string export = SafeName(string.IsNullOrWhiteSpace(spec.ExportValue) ? "Choice" + n : spec.ExportValue!);
            var w = NewWidget(doc, page, spec.Rect);
            w.Elements["/Parent"] = parent.Reference!;
            var priv = NewDict(doc);
            priv.Elements["/On"] = Name("/" + export);
            w.Elements[AnnotModel.PrivateKey] = priv;
            w.Elements["/MK"] = Mk(doc, null);
            ArrayOf(parent, "/Kids")!.Elements.Add(w.Reference!);
            annots.Elements.Add(w.Reference!);
            if (spec.DefaultValue == export)
            {
                parent.Elements["/DV"] = Name("/" + export);
                parent.Elements["/V"] = Name("/" + export);
            }
            var node = FieldTree.Find(doc, spec.Name)!;
            FieldAppearance.BuildStates(doc, node, w, radio: true);
            foreach (var kid in node.Widgets) FieldAppearance.SyncAs(node, kid);
            acro.Elements["/NeedAppearances"] = new PdfBoolean(false);
            return;
        }

        if (existing is not null) throw new ArgumentException("Field name already used: " + spec.Name);
        var f = NewWidget(doc, page, spec.Rect);
        f.Elements["/T"] = Str(spec.Name);
        if (spec.Tooltip.Length > 0) f.Elements["/TU"] = Str(spec.Tooltip);
        int ff = CommonFlags(spec);
        double fontSize = spec.Style.FontSize;
        f.Elements["/DA"] = new PdfString(AnnotModel.BuildDa(fontSize, spec.Style.Color));
        var privf = NewDict(doc);
        if (!string.IsNullOrWhiteSpace(spec.Style.FontFamily) && spec.Style.FontFamily != "Arial") privf.Elements["/Font"] = Str(spec.Style.FontFamily);
        switch (spec.Kind)
        {
            case FormFieldKind.Text or FormFieldKind.Date:
                f.Elements["/FT"] = Name("/Tx");
                if (spec.Multiline && spec.Kind == FormFieldKind.Text) ff |= FieldNode.FfMultiline;
                if (spec.Kind == FormFieldKind.Date) privf.Elements["/DateFormat"] = Str(string.IsNullOrWhiteSpace(spec.DateFormat) ? "yyyy-MM-dd" : spec.DateFormat!);
                if (spec.DefaultValue.Length > 0) { f.Elements["/DV"] = Str(spec.DefaultValue); f.Elements["/V"] = Str(spec.DefaultValue); }
                f.Elements["/MK"] = Mk(doc, null);
                break;
            case FormFieldKind.CheckBox:
                f.Elements["/FT"] = Name("/Btn");
                var on = SafeName(string.IsNullOrWhiteSpace(spec.ExportValue) ? "Yes" : spec.ExportValue!);
                var pc = NewDict(doc);
                pc.Elements["/On"] = Name("/" + on);
                f.Elements[AnnotModel.PrivateKey] = pc;
                bool chk = spec.DefaultValue == on || spec.DefaultValue is "Yes" or "On" or "true";
                f.Elements["/V"] = Name(chk ? "/" + on : "/Off");
                f.Elements["/DV"] = Name(chk ? "/" + on : "/Off");
                f.Elements["/MK"] = Mk(doc, null);
                break;
            case FormFieldKind.ComboBox or FormFieldKind.ListBox:
                f.Elements["/FT"] = Name("/Ch");
                if (spec.Kind == FormFieldKind.ComboBox) ff |= FieldNode.FfCombo;
                f.Elements["/Opt"] = OptArray(doc, spec.Options);
                if (spec.DefaultValue.Length > 0) { f.Elements["/DV"] = Str(spec.DefaultValue); f.Elements["/V"] = Str(spec.DefaultValue); }
                f.Elements["/MK"] = Mk(doc, null);
                break;
            case FormFieldKind.PushButton:
                f.Elements["/FT"] = Name("/Btn");
                ff |= FieldNode.FfPushButton;
                var mk = Mk(doc, new PdfColor(255, 220, 220, 220));
                mk.Elements["/CA"] = Str(spec.ButtonCaption ?? spec.Name);
                f.Elements["/MK"] = mk;
                SetAction(doc, f, spec);
                break;
            case FormFieldKind.Signature:
                f.Elements["/FT"] = Name("/Sig");
                f.Elements["/MK"] = Mk(doc, null);
                break;
        }
        if (privf.Elements.Count > 0)
        {
            if (Dict(f, AnnotModel.PrivateKey) is { } existingPriv) foreach (var k in privf.Elements.Keys) existingPriv.Elements[k] = privf.Elements[k];
            else f.Elements[AnnotModel.PrivateKey] = privf;
        }
        f.Elements["/Ff"] = new PdfInteger(ff);
        fields.Elements.Add(f.Reference!);
        annots.Elements.Add(f.Reference!);
        var created = FieldTree.Find(doc, spec.Name)!;
        FieldAppearance.Build(doc, created, forceStates: true);
        acro.Elements["/NeedAppearances"] = new PdfBoolean(false);
    }

    private static int CommonFlags(FormFieldSpec spec) => (spec.ReadOnly ? FieldNode.FfReadOnly : 0) | (spec.Required ? FieldNode.FfRequired : 0);

    private static string SafeName(string s)
    {
        var chars = s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray();
        return chars.Length == 0 ? "Yes" : new string(chars);
    }

    private static PdfArray OptArray(PdfDocument doc, IReadOnlyList<string> options)
    {
        var a = new PdfArray(doc);
        foreach (var o in options.Where(o => !string.IsNullOrWhiteSpace(o))) a.Elements.Add(Str(o.Trim()));
        return a;
    }

    private static PdfDictionary NewWidget(PdfDocument doc, PdfPage page, PdfRect rect)
    {
        var w = NewDict(doc, "/Annot");
        w.Elements["/Subtype"] = Name("/Widget");
        w.Elements["/Rect"] = RectArray(doc, rect);
        w.Elements["/F"] = new PdfInteger(AnnotationStore.FlagPrint);
        w.Elements["/NM"] = Str(NewName());
        if (page.Reference is not null) w.Elements["/P"] = page.Reference;
        var bs = NewDict(doc, "/Border");
        bs.Elements["/W"] = new PdfInteger(1);
        bs.Elements["/S"] = Name("/S");
        w.Elements["/BS"] = bs;
        doc.Internals.AddObject(w);
        return w;
    }

    private static PdfDictionary Mk(PdfDocument doc, PdfColor? bg)
    {
        var mk = NewDict(doc);
        mk.Elements["/BC"] = Arr(doc, 0.45, 0.45, 0.45);
        var b = bg ?? PdfColor.White;
        mk.Elements["/BG"] = ColorArray(doc, b);
        return mk;
    }

    private static void SetAction(PdfDocument doc, PdfDictionary f, FormFieldSpec spec)
    {
        f.Elements.Remove("/A");
        switch (spec.ButtonAction)
        {
            case FormButtonAction.ResetForm:
                var a = NewDict(doc, "/Action");
                a.Elements["/S"] = Name("/ResetForm");
                f.Elements["/A"] = a;
                break;
            case FormButtonAction.OpenUrl when !string.IsNullOrWhiteSpace(spec.ButtonUrl):
                var u = NewDict(doc, "/Action");
                u.Elements["/S"] = Name("/URI");
                u.Elements["/URI"] = new PdfString(spec.ButtonUrl!.Trim());
                f.Elements["/A"] = u;
                break;
        }
    }

    // ------------------------------------------------------------------ zmiana / usuwanie / kolejność

    public static void Update(PdfDocument doc, string fullName, FormFieldSpec spec)
    {
        var f = FieldTree.Find(doc, fullName) ?? throw new KeyNotFoundException("Field not found: " + fullName);
        var d = f.Dict;
        int ff = f.Flags & ~(FieldNode.FfReadOnly | FieldNode.FfRequired | FieldNode.FfMultiline);
        ff |= CommonFlags(spec);
        if (spec.Multiline && f.Kind == FormFieldKind.Text) ff |= FieldNode.FfMultiline;
        d.Elements["/Ff"] = new PdfInteger(ff);
        if (spec.Tooltip.Length > 0) d.Elements["/TU"] = Str(spec.Tooltip); else d.Elements.Remove("/TU");
        // nazwa (część końcowa)
        var lastDot = fullName.LastIndexOf('.');
        var prefix = lastDot >= 0 ? fullName[..(lastDot + 1)] : string.Empty;
        var newPartial = spec.Name.StartsWith(prefix, StringComparison.Ordinal) ? spec.Name[prefix.Length..] : spec.Name;
        if (newPartial.Length > 0 && newPartial != fullName[prefix.Length..])
        {
            if (FieldTree.Find(doc, prefix + newPartial) is not null) throw new ArgumentException("Field name already used: " + spec.Name);
            d.Elements["/T"] = Str(newPartial);
        }
        var kind = f.Kind;
        if (kind is FormFieldKind.Text or FormFieldKind.Date or FormFieldKind.ComboBox or FormFieldKind.ListBox)
        {
            if (spec.DefaultValue.Length > 0) d.Elements["/DV"] = Str(spec.DefaultValue); else d.Elements.Remove("/DV");
        }
        if (kind is FormFieldKind.ComboBox or FormFieldKind.ListBox) d.Elements["/Opt"] = OptArray(doc, spec.Options);
        if (kind != FormFieldKind.RadioButton)
            d.Elements["/DA"] = new PdfString(AnnotModel.BuildDa(spec.Style.FontSize, spec.Style.Color));
        var priv = Dict(d, AnnotModel.PrivateKey) ?? NewDict(doc);
        if (kind == FormFieldKind.Date) priv.Elements["/DateFormat"] = Str(string.IsNullOrWhiteSpace(spec.DateFormat) ? "yyyy-MM-dd" : spec.DateFormat!);
        if (!string.IsNullOrWhiteSpace(spec.Style.FontFamily) && spec.Style.FontFamily != "Arial") priv.Elements["/Font"] = Str(spec.Style.FontFamily);
        else priv.Elements.Remove("/Font");
        if (priv.Elements.Count > 0) d.Elements[AnnotModel.PrivateKey] = priv;
        if (kind == FormFieldKind.PushButton)
        {
            var w0 = f.Widgets[0];
            var mk = Dict(w0, "/MK") ?? Mk(doc, new PdfColor(255, 220, 220, 220));
            mk.Elements["/CA"] = Str(spec.ButtonCaption ?? spec.Name);
            w0.Elements["/MK"] = mk;
            SetAction(doc, w0, spec);
        }
        // położenie: pojedynczy widżet albo opcja o danej wartości eksportu
        PdfDictionary? target = f.Widgets.Count == 1 ? f.Widgets[0]
            : spec.ExportValue is { } ev ? f.Widgets.FirstOrDefault(w => FieldNode.OnState(w) == ev) : null;
        if (target is not null && !spec.Rect.IsEmpty) target.Elements["/Rect"] = RectArray(doc, spec.Rect);
        var fresh = FieldTree.Find(doc, prefix + (newPartial.Length > 0 ? newPartial : fullName[prefix.Length..])) ?? f;
        FieldAppearance.Build(doc, fresh, forceStates: true);
    }

    /// <summary>Przesuwa/zmienia rozmiar jednego widżetu (bez zmiany innych właściwości pola).</summary>
    public static void MoveWidget(PdfDocument doc, string fullName, string? onState, PdfRect rect)
    {
        var f = FieldTree.Find(doc, fullName) ?? throw new KeyNotFoundException("Field not found: " + fullName);
        var w = f.Widgets.Count == 1 ? f.Widgets[0] : f.Widgets.FirstOrDefault(x => FieldNode.OnState(x) == onState) ?? f.Widgets[0];
        w.Elements["/Rect"] = RectArray(doc, rect);
        if (f.Kind is FormFieldKind.CheckBox or FormFieldKind.RadioButton) FieldAppearance.BuildStates(doc, f, w, f.Kind == FormFieldKind.RadioButton);
        else FieldAppearance.Build(doc, f);
    }

    /// <summary>Pełna specyfikacja istniejącego pola (okno właściwości).</summary>
    public static FormFieldSpec Describe(PdfDocument doc, string fullName)
    {
        var f = FieldTree.Find(doc, fullName) ?? throw new KeyNotFoundException("Field not found: " + fullName);
        var info = Read(doc).First(i => i.FullName == fullName);
        AnnotModel.ParseDa(f.DaText, out double size, out var color);
        var w = f.Widgets[0];
        var action = Dict(w, "/A") ?? Dict(f.Dict, "/A");
        var s = NameOf(action ?? NewDict(doc), "/S");
        return new FormFieldSpec
        {
            Kind = info.Kind,
            Name = fullName,
            PageIndex = info.PageIndex,
            Rect = info.Rect,
            DefaultValue = info.DefaultValue,
            Options = info.Options,
            ExportValue = FieldNode.OnState(w),
            Tooltip = info.Tooltip,
            Required = info.Required,
            ReadOnly = info.ReadOnly,
            Multiline = info.Multiline,
            Style = new TextStyle
            {
                FontSize = size,
                Color = color ?? PdfColor.Black,
                FontFamily = Dict(f.Dict, AnnotModel.PrivateKey) is { } p && Text(p, "/Font") is { Length: > 0 } fam ? fam : "Arial",
            },
            DateFormat = f.DateFormat.Length > 0 ? f.DateFormat : null,
            ButtonAction = s switch { "/ResetForm" => FormButtonAction.ResetForm, "/URI" => FormButtonAction.OpenUrl, _ => FormButtonAction.None },
            ButtonUrl = action is not null && s == "/URI" ? Text(action, "/URI") : null,
            ButtonCaption = Dict(w, "/MK") is { } mk ? Text(mk, "/CA") : null,
        };
    }

    public static void Delete(PdfDocument doc, IReadOnlyList<string> names)
    {
        var acro = FieldTree.AcroForm(doc);
        if (acro is null) return;
        var nodes = FieldTree.Terminals(doc).Where(f => names.Contains(f.FullName)).ToList();
        var widgets = new HashSet<PdfDictionary>(nodes.SelectMany(n => n.Widgets), ReferenceEqualityComparer.Instance);
        var fieldDicts = new HashSet<PdfDictionary>(nodes.Select(n => n.Dict), ReferenceEqualityComparer.Instance);
        for (int p = 0; p < doc.PageCount; p++)
            if (Annots(doc.Pages[p]) is { } arr)
                for (int i = arr.Elements.Count - 1; i >= 0; i--)
                    if (Deref(arr.Elements[i]) is PdfDictionary d && widgets.Contains(d)) arr.Elements.RemoveAt(i);
        if (ArrayOf(acro, "/Fields") is { } fields) Prune(fields, fieldDicts);
    }

    private static void Prune(PdfArray arr, HashSet<PdfDictionary> doomed)
    {
        for (int i = arr.Elements.Count - 1; i >= 0; i--)
        {
            if (Deref(arr.Elements[i]) is not PdfDictionary f) continue;
            if (doomed.Contains(f)) { arr.Elements.RemoveAt(i); continue; }
            if (ArrayOf(f, "/Kids") is { } kids && kids.Elements.Select(Deref).OfType<PdfDictionary>().Any(k => k.Elements.ContainsKey("/T")))
            {
                Prune(kids, doomed);
                if (kids.Elements.Count == 0) arr.Elements.RemoveAt(i);
            }
        }
    }

    public static void SetTabOrder(PdfDocument doc, int pageIndex, IReadOnlyList<string> ordered)
    {
        var page = doc.Pages[pageIndex];
        var arr = Annots(page);
        if (arr is null) return;
        var terminals = FieldTree.Terminals(doc).ToDictionary(t => t.FullName, StringComparer.Ordinal);
        var orderedWidgets = new List<PdfDictionary>();
        foreach (var name in ordered)
            if (terminals.TryGetValue(name, out var t))
                orderedWidgets.AddRange(t.Widgets);
        var set = new HashSet<PdfDictionary>(orderedWidgets, ReferenceEqualityComparer.Instance);
        var slots = new List<int>();
        for (int i = 0; i < arr.Elements.Count; i++)
            if (Deref(arr.Elements[i]) is PdfDictionary d && set.Contains(d)) slots.Add(i);
        var present = orderedWidgets.Where(w => slots.Any(s => ReferenceEquals(Deref(arr.Elements[s]), w))).ToList();
        var items = present.Select(w => (PdfItem)(w.Reference is { } r ? r : w)).ToList();
        for (int k = 0; k < slots.Count && k < items.Count; k++) arr.Elements[slots[k]] = items[k];
        // PDF 2.0: /W = kolejność widżetów wg /Annots (PDFium także używa kolejności tablicy)
        page.Elements["/Tabs"] = Name("/W");
    }

    public static void Reset(PdfDocument doc, IReadOnlyList<string>? names)
    {
        foreach (var f in FieldTree.Terminals(doc))
        {
            if (names is not null && !names.Contains(f.FullName)) continue;
            var dv = f.Inherited("/DV");
            var kind = f.Kind;
            if (kind is FormFieldKind.PushButton or FormFieldKind.Signature) continue;
            if (dv is null)
            {
                if (kind is FormFieldKind.CheckBox or FormFieldKind.RadioButton) f.Dict.Elements["/V"] = Name("/Off");
                else f.Dict.Elements.Remove("/V");
                f.Dict.Elements.Remove("/I");
                FieldAppearance.Build(doc, f);
            }
            else SetValue(doc, f, TextOf(dv));
        }
        if (FieldTree.AcroForm(doc) is { } acro) acro.Elements["/NeedAppearances"] = new PdfBoolean(false);
    }
}
