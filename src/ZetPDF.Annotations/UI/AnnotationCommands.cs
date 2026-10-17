using System.Windows;
using System.Windows.Input;
using ZetPDF.Annotations.Pdf;
using ZetPDF.Annotations.UI.Dialogs;
using ZetPDF.Annotations.UI.Signatures;
using ZetPDF.Annotations.UI.Tools;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Annotations.UI;

/// <summary>Obsługa 48 poleceń wstążki modułu Annotations (komentarze, formularze, Wypełnij i podpisz, załączniki).</summary>
internal sealed class AnnotationCommands
{
    private readonly AnnotContext _ctx;
    private readonly ICommandRegistry _registry;
    private readonly ILocalizationService _loc;
    private readonly List<ToggleCommand> _toggles = new();
    private readonly Dictionary<string, IViewerTool> _tools = new();
    private readonly SelectAnnotationTool _select;
    private string? _radioGroup;
    private string _stampId = "Approved";
    private byte[]? _imageStamp;
    private byte[]? _signature;
    private readonly HashSet<Guid> _flattenOnSave = new();

    public AnnotationCommands(AnnotContext ctx, ICommandRegistry registry, SelectAnnotationTool select)
    {
        _ctx = ctx;
        _registry = registry;
        _loc = ctx.Loc;
        _select = select;
    }

    private IDialogService? Dialogs => _ctx.Dialogs;

    public void RegisterAll()
    {
        // ---------------------------------------------------------------- wybór
        _select.OpenProperties = sel => AnnotationActions.OpenAsync(_ctx, sel);
        _select.DeleteSelected = sel => AnnotationActions.DeleteAsync(_ctx, sel);
        Tool(_select, DocumentPermissions.None);

        // ---------------------------------------------------------------- adiustacja
        Tool(new MarkupTool(_ctx, CommandIds.Comment.Highlight, AnnotationKind.Highlight));
        Tool(new MarkupTool(_ctx, CommandIds.Comment.Underline, AnnotationKind.Underline));
        Tool(new MarkupTool(_ctx, CommandIds.Comment.Squiggly, AnnotationKind.Squiggly));
        Tool(new MarkupTool(_ctx, CommandIds.Comment.StrikeOut, AnnotationKind.StrikeOut));
        Tool(new DragTool(_ctx, CommandIds.Comment.AreaHighlight, DragShape.Rectangle, (s, p, a, b) =>
            AddStyled(s, CommandIds.Comment.AreaHighlight, new AnnotationSpec { Kind = AnnotationKind.AreaHighlight, PageIndex = p, Rect = PdfRect.FromPoints(a, b) })));

        // ---------------------------------------------------------------- tekst
        Tool(new TextTool(_ctx, CommandIds.Comment.Note, AnnotationKind.Note));
        Tool(new TextTool(_ctx, CommandIds.Comment.TextBox, AnnotationKind.FreeText));
        Tool(new TextTool(_ctx, CommandIds.Comment.Typewriter, AnnotationKind.Typewriter));
        Tool(new TextTool(_ctx, CommandIds.Comment.Callout, AnnotationKind.Callout));

        // ---------------------------------------------------------------- rysunki
        Tool(new DragTool(_ctx, CommandIds.Comment.Rectangle, DragShape.Rectangle, (s, p, a, b) =>
            AddStyled(s, CommandIds.Comment.Rectangle, new AnnotationSpec { Kind = AnnotationKind.Square, PageIndex = p, Rect = PdfRect.FromPoints(a, b) })));
        Tool(new DragTool(_ctx, CommandIds.Comment.Oval, DragShape.Ellipse, (s, p, a, b) =>
            AddStyled(s, CommandIds.Comment.Oval, new AnnotationSpec { Kind = AnnotationKind.Circle, PageIndex = p, Rect = PdfRect.FromPoints(a, b) })));
        Tool(new DragTool(_ctx, CommandIds.Comment.Line, DragShape.Line, (s, p, a, b) =>
            AddStyled(s, CommandIds.Comment.Line, new AnnotationSpec { Kind = AnnotationKind.Line, PageIndex = p, Points = new[] { a, b } })));
        Tool(new DragTool(_ctx, CommandIds.Comment.Arrow, DragShape.Arrow, (s, p, a, b) =>
            AddStyled(s, CommandIds.Comment.Arrow, new AnnotationSpec { Kind = AnnotationKind.Arrow, PageIndex = p, Points = new[] { a, b } })));
        Tool(new PolyTool(_ctx, CommandIds.Comment.Polygon, closed: true));
        Tool(new PolyTool(_ctx, CommandIds.Comment.Polyline, closed: false));
        Tool(new InkTool(_ctx, CommandIds.Comment.Pencil));

        // ---------------------------------------------------------------- stemple
        var stampTool = new ClickTool(_ctx, CommandIds.Comment.Stamp, (s, p, at) => PlaceStamp(s, p, at), () => new Size(150, 50));
        _tools[CommandIds.Comment.Stamp] = stampTool;
        var stampToggle = new ToggleCommand(_ => Activate(stampTool), () => IsActive(CommandIds.Comment.Stamp) && _imageStamp is null, _ => CanAnnotate());
        _toggles.Add(stampToggle);
        _registry.Register(CommandIds.Comment.Stamp, stampToggle);
        RefreshStampMenu();
        _registry.Register(CommandIds.Comment.CustomStamp, Doc(_ => { PickCustomStamp(); return Task.CompletedTask; }, DocumentPermissions.Annotate));

        // ---------------------------------------------------------------- zarządzanie komentarzami
        _registry.Register(CommandIds.Comment.Import, Doc(ImportCommentsAsync, DocumentPermissions.Annotate));
        _registry.Register(CommandIds.Comment.ExportAll, Doc(ExportCommentsAsync, DocumentPermissions.None));
        // Pokaż/Ukryj komentarze – tylko widok (render bez FPDF_ANNOT, widżety pól zostają); plik bez zmian
        _registry.Register(CommandIds.Comment.ShowAll, new RelayCmd(_ => SetCommentsVisible(true), _ => _ctx.ActiveView is not null));
        _registry.Register(CommandIds.Comment.HideAll, new RelayCmd(_ => SetCommentsVisible(false), _ => _ctx.ActiveView is not null));
        _registry.Register(CommandIds.Comment.DeleteAll, Doc(DeleteAllAsync, DocumentPermissions.Annotate));

        // ---------------------------------------------------------------- załączniki
        _registry.Register(CommandIds.Attach.AddFile, Doc(AttachFileAsync, DocumentPermissions.ModifyContent));

        // ---------------------------------------------------------------- formularze – tworzenie
        FieldTool(CommandIds.Form.TextField, FormFieldKind.Text, "Text", new Size(150, 22));
        FieldTool(CommandIds.Form.CheckBox, FormFieldKind.CheckBox, "CheckBox", new Size(14, 14));
        FieldTool(CommandIds.Form.RadioButton, FormFieldKind.RadioButton, "RadioGroup", new Size(14, 14));
        FieldTool(CommandIds.Form.ComboBox, FormFieldKind.ComboBox, "Combo", new Size(150, 22));
        FieldTool(CommandIds.Form.ListBox, FormFieldKind.ListBox, "List", new Size(150, 60));
        FieldTool(CommandIds.Form.Button, FormFieldKind.PushButton, "Button", new Size(80, 24));
        FieldTool(CommandIds.Form.SignatureField, FormFieldKind.Signature, "Signature", new Size(180, 50));
        FieldTool(CommandIds.Form.DateField, FormFieldKind.Date, "Date", new Size(100, 22));
        _registry.Register(CommandIds.Form.TabOrder, Doc(TabOrderAsync, DocumentPermissions.ModifyContent));
        var hl = new ToggleCommand(_ =>
        {
            _ctx.Settings.HighlightFields = !_ctx.Settings.HighlightFields;
            _ctx.SaveSettings();
            _ctx.ActiveView?.InvalidateOverlays();
        }, () => _ctx.Settings.HighlightFields);
        _toggles.Add(hl);
        _registry.Register(CommandIds.Form.HighlightFields, hl);

        // ---------------------------------------------------------------- formularze – dane
        _registry.Register(CommandIds.Form.ImportData, Doc(ImportDataAsync, DocumentPermissions.FillForms));
        _registry.Register(CommandIds.Form.ExportData, Doc(ExportDataAsync, DocumentPermissions.None));
        _registry.Register(CommandIds.Form.Reset, Doc(ResetFormAsync, DocumentPermissions.FillForms));

        // ---------------------------------------------------------------- Wypełnij i podpisz
        _registry.Register(CommandIds.FillSign.Open, Doc(_ =>
        {
            _ctx.Shell?.SetContextualTabVisible(ContextualTabIds.FillSign, true);
            Activate(_tools[CommandIds.FillSign.Text]);
            return Task.CompletedTask;
        }, DocumentPermissions.None));
        Tool(new TextTool(_ctx, CommandIds.FillSign.Text, AnnotationKind.Typewriter));
        Tool(new ClickTool(_ctx, CommandIds.FillSign.Check, (s, p, at) => PlaceMark(s, p, at, StampLibrary.Check, CommandIds.FillSign.Check), () => new Size(14, 14)));
        Tool(new ClickTool(_ctx, CommandIds.FillSign.Cross, (s, p, at) => PlaceMark(s, p, at, StampLibrary.Cross, CommandIds.FillSign.Cross), () => new Size(14, 14)));
        Tool(new ClickTool(_ctx, CommandIds.FillSign.Dot, (s, p, at) => PlaceMark(s, p, at, StampLibrary.Dot, CommandIds.FillSign.Dot), () => new Size(8, 8)));
        Tool(new DragTool(_ctx, CommandIds.FillSign.Line, DragShape.Line, (s, p, a, b) =>
            AddStyled(s, CommandIds.FillSign.Line, new AnnotationSpec { Kind = AnnotationKind.Line, PageIndex = p, Points = new[] { a, new PdfPoint(b.X, a.Y) } }), new Size(100, 0)));
        var sigTool = new ClickTool(_ctx, CommandIds.FillSign.Signature, PlaceSignature, () => SignatureSize());
        _tools[CommandIds.FillSign.Signature] = sigTool;
        var sigToggle = new ToggleCommand(_ => StartSignature(null), () => IsActive(CommandIds.FillSign.Signature), _ => CanAnnotate());
        _toggles.Add(sigToggle);
        _registry.Register(CommandIds.FillSign.Signature, sigToggle);
        RefreshSignatureMenu();
        _registry.Register(CommandIds.FillSign.ManageSignatures, new RelayCmd(_ => ManageSignatures()));
        // Spłaszcz przy zapisie – przełącznik per dokument; spłaszcza hak IDocumentManager.AddSaveTransform (sesja bez zmian)
        var flatten = new ToggleCommand(_ => ToggleFlattenOnSave(),
            () => _ctx.Documents.ActiveDocument is { } d && IsFlattenOnSave(d),
            _ => _ctx.Documents.ActiveDocument is { } d && d.Can(DocumentPermissions.ModifyContent));
        _toggles.Add(flatten);
        _registry.Register(CommandIds.FillSign.Flatten, flatten);
        _ctx.Documents.AddSaveTransform(FlattenOnSaveAsync);
        _ctx.Documents.DocumentClosed += (_, s) => { lock (_flattenOnSave) _flattenOnSave.Remove(s.Id); };
        _ctx.Documents.ActiveDocumentChanged += (_, _) => RaiseToggles();
        _registry.Register(CommandIds.FillSign.Close, new RelayCmd(_ =>
        {
            _ctx.Shell?.SetContextualTabVisible(ContextualTabIds.FillSign, false);
            _ctx.Tools?.ActivateDefault();
        }));

        // ---------------------------------------------------------------- styl narzędzia (polecenie dodatkowe)
        try
        {
            _registry.RegisterExtra(new CommandDescriptor("Annot.ToolStyle", FeatureModuleId.Annotations, CommandKind.Button, "", "", "", "Annot.Cmd.ToolStyle"),
                "Comment", "Comment.Drawing", new RelayCmd(_ => EditToolStyle()));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException) { }

        if (_ctx.Tools is { } tools) tools.ActiveToolChanged += (_, _) => { RaiseToggles(); };
        if (_ctx.Viewer is { } viewer) viewer.ActiveViewChanged += (_, _) => RaiseToggles();
    }

    // ================================================================== pomocniki rejestracji

    private bool CanAnnotate() => _ctx.ActiveView is { } v && v.Session.Can(DocumentPermissions.Annotate);

    private bool IsActive(string id) => _ctx.Tools?.IsActive(id) == true;

    private void Activate(IViewerTool tool)
    {
        if (tool.Id == CommandIds.Form.RadioButton) _radioGroup = null;
        _ctx.Tools?.Activate(tool);
        RaiseToggles();
    }

    private void RaiseToggles()
    {
        foreach (var t in _toggles) t.RaiseIsCheckedChanged();
        CommandManager.InvalidateRequerySuggested();
    }

    private void Tool(IViewerTool tool, DocumentPermissions permission = DocumentPermissions.Annotate)
    {
        _tools[tool.Id] = tool;
        var t = new ToggleCommand(_ =>
        {
            if (IsActive(tool.Id)) _ctx.Tools?.ActivateDefault();
            else Activate(tool);
        }, () => IsActive(tool.Id), _ => _ctx.ActiveView is { } v && v.Session.Can(permission));
        _toggles.Add(t);
        _registry.Register(tool.Id, t);
    }

    private ICommand Doc(Func<IPdfDocumentSession, Task> action, DocumentPermissions permission) =>
        new ActiveDocumentCommand(_ctx.Documents, Dialogs, action, permission);

    private Task AddStyled(IPdfDocumentSession session, string toolId, AnnotationSpec spec)
    {
        var s = _ctx.Settings.StyleFor(toolId);
        return _ctx.AddAsync(session, spec with
        {
            Color = s.Color,
            FillColor = spec.Kind is AnnotationKind.Square or AnnotationKind.Circle ? s.Fill : null,
            Opacity = s.Opacity,
            LineWidth = s.LineWidth,
        });
    }

    // ================================================================== stemple

    private void RefreshStampMenu()
    {
        var items = new List<MenuItemDescriptor>();
        var choose = new RelayCmd(p =>
        {
            if (p is string id)
            {
                _stampId = id;
                _imageStamp = null;
                Activate(_tools[CommandIds.Comment.Stamp]);
            }
            else if (p is byte[] img)
            {
                _imageStamp = img;
                Activate(_tools[CommandIds.Comment.Stamp]);
            }
        });
        foreach (var s in StampLibrary.Standard) items.Add(new MenuItemDescriptor("Annot.Stamp." + s.Id, s.TextKey, null, choose, s.Id));
        if (_ctx.Paths is { } paths && Directory.Exists(paths.StampsDirectory))
            foreach (var f in Directory.GetFiles(paths.StampsDirectory, "*.png").Concat(Directory.GetFiles(paths.StampsDirectory, "*.jpg")).OrderBy(f => f))
            {
                var path = f;
                items.Add(new MenuItemDescriptor("Annot.Stamp.File." + Path.GetFileName(f), null, Path.GetFileNameWithoutExtension(f),
                    new RelayCmd(_ => { try { _imageStamp = File.ReadAllBytes(path); Activate(_tools[CommandIds.Comment.Stamp]); } catch (IOException ex) { Dialogs?.ShowError(ex); } })));
            }
        _registry.SetMenuItems(CommandIds.Comment.Stamp, items);
    }

    private Task PlaceStamp(IPdfDocumentSession s, int page, PdfPoint at)
    {
        var style = _ctx.Settings.StyleFor(CommandIds.Comment.Stamp);
        if (_imageStamp is { } img)
        {
            var (pw, ph) = AppearanceBuilder.ImageSize(img);
            double w = 150, h = pw > 0 ? w * ph / pw : 60;
            return _ctx.AddAsync(s, new AnnotationSpec { Kind = AnnotationKind.Stamp, PageIndex = page, Rect = new PdfRect(at.X, at.Y - h, at.X + w, at.Y), ImageData = img, Opacity = style.Opacity });
        }
        var def = StampLibrary.Find(_stampId);
        return _ctx.AddAsync(s, new AnnotationSpec
        {
            Kind = AnnotationKind.Stamp,
            PageIndex = page,
            Rect = new PdfRect(at.X, at.Y, at.X, at.Y),
            StampName = _stampId,
            Color = def?.Color ?? style.Color,
            Opacity = style.Opacity,
        });
    }

    private void PickCustomStamp()
    {
        var files = Dialogs?.OpenFiles(_loc["Cmd.Comment.CustomStamp"], new[] { new FileFilter(_loc["Annot.Filter.Images"], "*.png", "*.jpg", "*.jpeg") });
        if (files is not { Count: > 0 }) return;
        try
        {
            var data = File.ReadAllBytes(files[0]);
            AppearanceBuilder.ImageSize(data); // weryfikacja formatu
            if (_ctx.Paths is { } paths)
            {
                Directory.CreateDirectory(paths.StampsDirectory);
                var target = Path.Combine(paths.StampsDirectory, Path.GetFileName(files[0]));
                if (!File.Exists(target)) File.Copy(files[0], target);
                RefreshStampMenu();
            }
            _imageStamp = data;
            Activate(_tools[CommandIds.Comment.Stamp]);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Dialogs?.ShowError(ex); }
    }

    // ================================================================== komentarze: import/eksport/usuwanie

    private async Task ImportCommentsAsync(IPdfDocumentSession s)
    {
        var files = Dialogs?.OpenFiles(_loc["Cmd.Comment.Import"], new[]
        {
            new FileFilter(_loc["Annot.Filter.Comments"], "*.xfdf", "*.fdf", "*.xml"), FileFilter.All(_loc),
        });
        if (files is not { Count: > 0 }) return;
        var data = await File.ReadAllBytesAsync(files[0]);
        await _ctx.RunAsync(s, "Annot.Undo.Import", (pdf, ct) => _ctx.Annotations.ImportCommentsAsync(pdf, new MemoryStream(data), ct));
    }

    private async Task ExportCommentsAsync(IPdfDocumentSession s)
    {
        var name = Path.GetFileNameWithoutExtension(s.DisplayName) + ".xfdf";
        var path = Dialogs?.SaveFile(_loc["Cmd.Comment.ExportAll"], new[] { new FileFilter("XFDF", "*.xfdf"), new FileFilter("FDF", "*.fdf") }, name);
        if (path is null) return;
        var source = s.FilePath is { } fp ? Path.GetFileName(fp) : s.DisplayName;
        using var ms = new MemoryStream();
        if (Path.GetExtension(path).Equals(".fdf", StringComparison.OrdinalIgnoreCase)) await _ctx.Annotations.ExportFdfAsync(s.Current.GetBytes(), ms, source);
        else await _ctx.Annotations.ExportXfdfAsync(s.Current.GetBytes(), ms, source);
        await WriteFile(path, ms.ToArray());
        _ctx.Shell?.SetStatus(_loc.Format("Annot.Export.Done", Path.GetFileName(path)));
    }

    private async Task WriteFile(string path, byte[] data)
    {
        if (_ctx.Services.GetService(typeof(IFileService)) is IFileService fs) await fs.WriteAllBytesSafeAsync(path, data);
        else await File.WriteAllBytesAsync(path, data);
    }

    private void SetCommentsVisible(bool visible)
    {
        if (_ctx.ActiveView is not { } view) return;
        view.ShowAnnotations = visible;
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task DeleteAllAsync(IPdfDocumentSession s)
    {
        if (Dialogs is { } d && !d.Confirm(_loc["Annot.DeleteAll.Confirm"])) return;
        await _ctx.RunAsync(s, "Annot.Undo.DeleteAll", (pdf, ct) => _ctx.Annotations.DeleteAllAsync(pdf, ct));
        _ctx.Selection = null;
    }

    private async Task AttachFileAsync(IPdfDocumentSession s)
    {
        var files = Dialogs?.OpenFiles(_loc["Cmd.Attach.AddFile"], new[] { FileFilter.All(_loc) }, multiselect: true);
        if (files is not { Count: > 0 }) return;
        await _ctx.RunAsync(s, "Annot.Undo.AttachAdd", async (pdf, ct) =>
        {
            foreach (var f in files) pdf = await _ctx.Attachments.AddAsync(pdf, f, string.Empty, ct);
            return pdf;
        }, DocumentChangeKind.Attachments, DocumentPermissions.ModifyContent);
        _ctx.Shell?.ShowPanel(PanelIds.Attachments);
    }

    // ================================================================== formularze

    private void FieldTool(string id, FormFieldKind kind, string prefix, Size defaultSize)
    {
        var tool = new DragTool(_ctx, id, DragShape.Rectangle, (s, page, a, b) => CreateField(s, kind, prefix, page, a, b, defaultSize), defaultSize);
        Tool(tool, DocumentPermissions.ModifyContent);
    }

    private async Task CreateField(IPdfDocumentSession s, FormFieldKind kind, string prefix, int page, PdfPoint a, PdfPoint b, Size defaultSize)
    {
        var rect = PdfRect.FromPoints(a, b);
        if (kind is FormFieldKind.CheckBox or FormFieldKind.RadioButton)
        {
            double side = Math.Max(10, Math.Min(rect.Width, rect.Height));
            if (rect.Width < 4 || rect.Height < 4) side = defaultSize.Width;
            rect = new PdfRect(rect.Left, rect.Top - side, rect.Left + side, rect.Top);
        }
        var existing = (await _ctx.Forms.GetFieldsAsync(s.Current.GetBytes())).Select(f => f.FullName).ToHashSet(StringComparer.Ordinal);
        string name;
        string? export = null;
        if (kind == FormFieldKind.RadioButton && _radioGroup is not null && existing.Contains(_radioGroup)) name = _radioGroup;
        else
        {
            int n = 1;
            while (existing.Contains(prefix + n)) n++;
            name = prefix + n;
            if (kind == FormFieldKind.RadioButton) _radioGroup = name;
        }
        if (kind == FormFieldKind.RadioButton) export = null; // Choice{n}
        var spec = new FormFieldSpec
        {
            Kind = kind,
            Name = name,
            PageIndex = page,
            Rect = rect,
            ExportValue = export,
            ButtonCaption = kind == FormFieldKind.PushButton ? _loc["Annot.Field.DefaultCaption"] : null,
            DateFormat = kind == FormFieldKind.Date ? "yyyy-MM-dd" : null,
        };
        if (await _ctx.RunAsync(s, "Annot.Undo.FieldAdd", (pdf, ct) => _ctx.Forms.CreateFieldAsync(pdf, spec, ct),
                DocumentChangeKind.FormFields | DocumentChangeKind.Annotations, DocumentPermissions.ModifyContent, new[] { page }))
            _ctx.Selection = new SelectedItem(s, null, name, page, rect);
    }

    private async Task TabOrderAsync(IPdfDocumentSession s)
    {
        int page = _ctx.Viewer?.GetView(s)?.CurrentPageIndex ?? 0;
        var fields = (await _ctx.Forms.GetFieldsAsync(s.Current.GetBytes())).Where(f => f.PageIndex == page).ToList();
        if (fields.Count == 0) { Dialogs?.ShowMessage(_loc.Format("Annot.TabOrder.NoFields", page + 1)); return; }
        var dlg = new TabOrderDialog(_loc, page, fields);
        if (Ui.Show(Dialogs, dlg) != true) return;
        var order = dlg.Order;
        await _ctx.RunAsync(s, "Annot.Undo.TabOrder", (pdf, ct) => _ctx.Forms.SetTabOrderAsync(pdf, page, order, ct), DocumentChangeKind.FormFields, DocumentPermissions.ModifyContent, new[] { page });
    }

    private static IReadOnlyList<FileFilter> DataFilters(ILocalizationService loc) => new[]
    {
        new FileFilter("FDF", "*.fdf"), new FileFilter("XFDF", "*.xfdf"), new FileFilter(loc["Annot.Filter.Csv"], "*.csv"),
    };

    private async Task ImportDataAsync(IPdfDocumentSession s)
    {
        var files = Dialogs?.OpenFiles(_loc["Cmd.Form.ImportData"], new[] { new FileFilter(_loc["Annot.Filter.FormData"], "*.fdf", "*.xfdf", "*.xml", "*.csv") });
        if (files is not { Count: > 0 }) return;
        var data = await File.ReadAllBytesAsync(files[0]);
        var fmt = FormService.FormatFromPath(files[0]);
        await _ctx.RunAsync(s, "Annot.Undo.FormImport", (pdf, ct) => _ctx.Forms.ImportDataAsync(pdf, new MemoryStream(data), fmt, ct), DocumentChangeKind.FormFields, DocumentPermissions.FillForms);
    }

    private async Task ExportDataAsync(IPdfDocumentSession s)
    {
        var path = Dialogs?.SaveFile(_loc["Cmd.Form.ExportData"], DataFilters(_loc), Path.GetFileNameWithoutExtension(s.DisplayName) + ".fdf");
        if (path is null) return;
        using var ms = new MemoryStream();
        await _ctx.Forms.ExportDataAsync(s.Current.GetBytes(), ms, FormService.FormatFromPath(path));
        await WriteFile(path, ms.ToArray());
        _ctx.Shell?.SetStatus(_loc.Format("Annot.Export.Done", Path.GetFileName(path)));
    }

    private async Task ResetFormAsync(IPdfDocumentSession s)
    {
        if (Dialogs is { } d && !d.Confirm(_loc["Annot.Form.ResetConfirm"])) return;
        await _ctx.RunAsync(s, "Annot.Undo.FormReset", (pdf, ct) => _ctx.Forms.ResetAsync(pdf, null, ct), DocumentChangeKind.FormFields, DocumentPermissions.FillForms);
    }

    // ================================================================== Wypełnij i podpisz

    private Task PlaceMark(IPdfDocumentSession s, int page, PdfPoint at, string mark, string toolId)
    {
        var style = _ctx.Settings.StyleFor(toolId);
        double size = mark == StampLibrary.Dot ? 8 : 14;
        return _ctx.AddAsync(s, new AnnotationSpec
        {
            Kind = AnnotationKind.Stamp,
            PageIndex = page,
            Rect = new PdfRect(at.X - size / 2, at.Y - size / 2, at.X + size / 2, at.Y + size / 2),
            StampName = mark,
            Color = style.Color,
            Opacity = style.Opacity,
        });
    }

    private SignatureStore? Store => _ctx.Paths is { } p ? new SignatureStore(p.SignaturesDirectory) : null;

    private void RefreshSignatureMenu()
    {
        var items = new List<MenuItemDescriptor>
        {
            new("Annot.Sign.NewItem", "Annot.Sign.NewItem", null, new RelayCmd(_ => NewSignature())),
        };
        if (Store is { } store)
            foreach (var path in store.List().Take(10))
            {
                var p = path;
                items.Add(new MenuItemDescriptor("Annot.Sign.File." + Path.GetFileName(p), null, Path.GetFileNameWithoutExtension(p), new RelayCmd(_ => StartSignature(p))));
            }
        _registry.SetMenuItems(CommandIds.FillSign.Signature, items);
    }

    private void StartSignature(string? path)
    {
        if (!CanAnnotate()) return;
        path ??= _ctx.Settings.LastSignature is { } last && File.Exists(last) ? last : Store?.List().FirstOrDefault();
        if (path is null) { NewSignature(); return; }
        try { _signature = File.ReadAllBytes(path); }
        catch (IOException ex) { Dialogs?.ShowError(ex); return; }
        _ctx.Settings.LastSignature = path;
        _ctx.SaveSettings();
        Activate(_tools[CommandIds.FillSign.Signature]);
    }

    private void NewSignature()
    {
        var dlg = new SignatureDialog(_loc, Dialogs, _ctx.Author);
        if (Ui.Show(Dialogs, dlg) != true || dlg.Png is not { } png) return;
        string? path = null;
        try { path = Store?.Save(png); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Dialogs?.ShowError(ex); }
        RefreshSignatureMenu();
        if (path is not null) StartSignature(path);
        else { _signature = png; Activate(_tools[CommandIds.FillSign.Signature]); }
    }

    private void ManageSignatures()
    {
        if (Store is not { } store) return;
        var dlg = new SignatureManagerDialog(_loc, Dialogs, store, _ctx.Author);
        bool use = Ui.Show(Dialogs, dlg) == true;
        RefreshSignatureMenu();
        if (use && dlg.SelectedPath is { } p) StartSignature(p);
    }

    private Size SignatureSize()
    {
        if (_signature is null) return new Size(150, 50);
        try
        {
            var (w, h) = AppearanceBuilder.ImageSize(_signature);
            return new Size(150, w > 0 ? 150.0 * h / w : 50);
        }
        catch (Exception) { return new Size(150, 50); }
    }

    private Task PlaceSignature(IPdfDocumentSession s, int page, PdfPoint at)
    {
        if (_signature is not { } png) return Task.CompletedTask;
        var size = SignatureSize();
        return _ctx.AddAsync(s, new AnnotationSpec
        {
            Kind = AnnotationKind.Stamp,
            PageIndex = page,
            Rect = new PdfRect(at.X, at.Y - size.Height, at.X + size.Width, at.Y),
            ImageData = png,
            Contents = _loc["Annot.Sign.Contents"],
        });
    }

    internal bool IsFlattenOnSave(IPdfDocumentSession s)
    {
        lock (_flattenOnSave) return _flattenOnSave.Contains(s.Id);
    }

    private void ToggleFlattenOnSave()
    {
        if (_ctx.Documents.ActiveDocument is not { } s) return;
        bool on = !IsFlattenOnSave(s);
        if (on && Dialogs is { } d && !d.Confirm(_loc["Annot.Flatten.Confirm"])) return;
        lock (_flattenOnSave)
        {
            if (on) _flattenOnSave.Add(s.Id);
            else _flattenOnSave.Remove(s.Id);
        }
        _ctx.Shell?.SetStatus(_loc[on ? "Annot.Flatten.On" : "Annot.Flatten.Off"]);
        RaiseToggles();
    }

    /// <summary>Hak przed zapisem: spłaszcza komentarze i pola w zapisywanych bajtach, jeśli włączono to dla dokumentu.</summary>
    private async Task<byte[]> FlattenOnSaveAsync(IPdfDocumentSession s, byte[] pdf, CancellationToken ct)
    {
        if (!IsFlattenOnSave(s)) return pdf;
        if (_ctx.Pages is { } pages)
        {
            try { return await pages.FlattenAsync(pdf, new FlattenOptions(), null, ct); }
            catch (NotImplementedException) { }
        }
        return await _ctx.Annotations.FlattenAsync(pdf, includeFormFields: true, ct: ct);
    }

    // ================================================================== styl narzędzia

    private void EditToolStyle()
    {
        var id = _ctx.Tools?.ActiveTool.Id;
        if (id is null || !_tools.ContainsKey(id) || id == CommandIds.Tool.SelectAnnotation) id = CommandIds.Comment.Rectangle;
        var parts = StyleParts.Color | StyleParts.Opacity;
        if (id is CommandIds.Comment.Rectangle or CommandIds.Comment.Oval or CommandIds.Comment.Polygon or CommandIds.Comment.TextBox or CommandIds.Comment.Callout) parts |= StyleParts.Fill;
        if (id is not (CommandIds.Comment.Highlight or CommandIds.Comment.AreaHighlight or CommandIds.Comment.Note or CommandIds.Comment.Stamp)) parts |= StyleParts.LineWidth;
        if (id is CommandIds.Comment.TextBox or CommandIds.Comment.Typewriter or CommandIds.Comment.Callout or CommandIds.FillSign.Text) parts |= StyleParts.Font;
        var current = _ctx.Settings.StyleFor(id);
        var dlg = new StyleDialog(_loc, "Annot.Style.Title", parts, current, info: _loc.Format("Annot.Style.For", _loc["Cmd." + id]));
        if (Ui.Show(Dialogs, dlg) != true) return;
        _ctx.Settings.Styles[id] = dlg.Result;
        _ctx.SaveSettings();
    }
}

/// <summary>Proste polecenie (bez wymogu dokumentu).</summary>
internal sealed class RelayCmd : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _can;

    public RelayCmd(Action<object?> execute, Func<object?, bool>? can = null)
    {
        _execute = execute;
        _can = can;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _can?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);
}
