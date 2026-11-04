using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Editing.Ui.Dialogs;
using ZetPDF.Editing.Ui.Tools;

namespace ZetPDF.Editing.Ui;

/// <summary>Podpięcie poleceń wstążki modułu (Edycja, Konwersja, OCR, Redakcja) do katalogu.</summary>
internal sealed class EditingCommands
{
    private readonly EditingContext _ctx;
    private readonly ICommandRegistry _registry;
    private readonly List<ToggleCommand> _toggles = new();
    private readonly ConditionalWeakTable<IDocumentView, RedactionOverlay> _overlays = new();
    private readonly ConditionalWeakTable<IPdfDocumentSession, OcrState> _ocr = new();
    private ShapeKind _shape = ShapeKind.Rectangle;

    private sealed class OcrState
    {
        public required OcrResult Result { get; init; }
        public required long Version { get; set; }
    }

    public EditingCommands(EditingContext ctx, ICommandRegistry registry)
    {
        _ctx = ctx;
        _registry = registry;
    }

    private ILocalizationService L => _ctx.Loc;

    public static FileFilter ImageFilter(ILocalizationService loc) => new(loc["Edit.Filter.Images"], "*.png", "*.jpg", "*.jpeg", "*.tif", "*.tiff", "*.bmp", "*.gif");

    private ActiveDocumentCommand Doc(Func<IPdfDocumentSession, Task> run, DocumentPermissions perm = DocumentPermissions.None, Func<IPdfDocumentSession, bool>? can = null) =>
        new(_ctx.Documents, _ctx.Dialogs, run, perm, can);

    private void Toggle(string id, Func<IViewerTool> tool, DocumentPermissions perm = DocumentPermissions.ModifyContent)
    {
        IViewerTool? instance = null;
        var t = new ToggleCommand(_ =>
        {
            var tools = _ctx.Tools;
            if (tools is null) return;
            if (tools.IsActive(id)) tools.ActivateDefault();
            else tools.Activate(instance ??= tool());
        }, () => _ctx.Tools?.IsActive(id) == true, _ => _ctx.Documents.ActiveDocument is { } d && d.Can(perm) && _ctx.View is not null);
        _toggles.Add(t);
        _registry.Register(id, t);
    }

    public void RegisterAll()
    {
        // ---- Edycja treści
        Toggle(CommandIds.Edit.EditText, () => new EditTextTool(_ctx));
        Toggle(CommandIds.Edit.EditObject, () => new EditObjectTool(_ctx));
        Toggle(CommandIds.Edit.AddText, () => new AddTextTool(_ctx));
        _registry.Register(CommandIds.Edit.AddImage, Doc(AddImageAsync, DocumentPermissions.ModifyContent, _ => _ctx.View is not null));
        RegisterShapes();
        _registry.Register(CommandIds.Edit.FindReplace, Doc(FindReplaceAsync, DocumentPermissions.ModifyContent));

        // ---- Łącza i zakładki
        Toggle(CommandIds.Edit.Link, () => new LinkTool(_ctx), DocumentPermissions.Annotate);
        _registry.Register(CommandIds.Edit.CreateLinksFromUrls, Doc(CreateLinksAsync, DocumentPermissions.Annotate));
        _registry.Register(CommandIds.Edit.RemoveAllLinks, Doc(RemoveLinksAsync, DocumentPermissions.Annotate));
        _registry.Register(CommandIds.Edit.BookmarksFromHeadings, Doc(BookmarksAsync, DocumentPermissions.AssembleDocument));

        // ---- Utwórz / konwersje
        _registry.Register(CommandIds.File.CreateFromFile, new SimpleCommand(CreateFromFileAsync, _ctx));
        _registry.Register(CommandIds.File.CombineFiles, new SimpleCommand(CombineAsync, _ctx));
        _registry.Register(CommandIds.File.CreateFromClipboard, new SimpleCommand(FromClipboardAsync, _ctx));
        _registry.Register(CommandIds.File.CreateFromScanner, new SimpleCommand(FromScannerAsync, _ctx));
        _registry.Register(CommandIds.File.CreateFromWeb, new SimpleCommand(FromWebAsync, _ctx));
        _registry.Register(CommandIds.Convert.ToImages, Doc(ToImagesAsync, DocumentPermissions.CopyContent));
        _registry.Register(CommandIds.Convert.ToText, Doc(ToTextAsync, DocumentPermissions.CopyContent));
        _registry.Register(CommandIds.Convert.ToDocx, Doc(ToDocxAsync, DocumentPermissions.CopyContent));
        _registry.Register(CommandIds.Convert.ToXlsx, Doc(ToXlsxAsync, DocumentPermissions.CopyContent));
        _registry.Register(CommandIds.Convert.ToHtml, Doc(ToHtmlAsync, DocumentPermissions.CopyContent));

        // ---- OCR
        _registry.Register(CommandIds.Ocr.RecognizeDocument, Doc(s => OcrAsync(s, quick: false), DocumentPermissions.ModifyContent));
        _registry.Register(CommandIds.Ocr.QuickRecognize, Doc(s => OcrAsync(s, quick: true), DocumentPermissions.ModifyContent));
        Toggle(CommandIds.Ocr.RecognizeRegion, () => new RectangleTool(_ctx, CommandIds.Ocr.RecognizeRegion, OcrRegionAsync));
        _registry.Register(CommandIds.Ocr.SuspectResults, Doc(SuspectAsync, DocumentPermissions.ModifyContent, s => _ocr.TryGetValue(s, out _)));

        // ---- Redakcja
        Toggle(CommandIds.Redact.MarkText, () => new RedactTextTool(_ctx, MarkAsync));
        Toggle(CommandIds.Redact.MarkArea, () => new RectangleTool(_ctx, CommandIds.Redact.MarkArea, (v, p, r) => MarkAsync(v, new[] { new RedactionMark(p, new[] { PdfQuad.FromRect(r) }) })));
        _registry.Register(CommandIds.Redact.SearchAndRedact, Doc(SearchRedactAsync, DocumentPermissions.ModifyContent));
        _registry.Register(CommandIds.Redact.Apply, Doc(ApplyRedactionsAsync, DocumentPermissions.ModifyContent));

        if (_ctx.Tools is { } tools) tools.ActiveToolChanged += (_, _) => RaiseToggles();
        if (_ctx.Viewer is { } viewer)
        {
            viewer.ActiveViewChanged += (_, v) => { AttachOverlay(v); RaiseToggles(); };
            AttachOverlay(viewer.ActiveView);
        }
        _ctx.Documents.ActiveDocumentChanged += (_, _) => RaiseToggles();
    }

    private void RaiseToggles()
    {
        foreach (var t in _toggles) t.RaiseIsCheckedChanged();
    }

    private void AttachOverlay(IDocumentView? view)
    {
        if (view is null || _overlays.TryGetValue(view, out _)) return;
        var overlay = new RedactionOverlay(view);
        _overlays.Add(view, overlay);
        view.AddOverlay(overlay);
        view.Session.Changed += (_, _) => view.InvalidateOverlays();
    }

    // ------------------------------------------------------------------ edycja

    private void RegisterShapes()
    {
        IViewerTool? tool = null;
        var cmd = new ToggleCommand(p =>
        {
            if (p is ShapeKind k) _shape = k;
            var tools = _ctx.Tools;
            if (tools is null) return;
            if (p is null && tools.IsActive(CommandIds.Edit.AddShape)) { tools.ActivateDefault(); return; }
            tools.Activate(tool ??= new RectangleTool(_ctx, CommandIds.Edit.AddShape, AddShapeAsync));
        }, () => _ctx.Tools?.IsActive(CommandIds.Edit.AddShape) == true, _ => _ctx.Documents.ActiveDocument is { } d && d.Can(DocumentPermissions.ModifyContent) && _ctx.View is not null);
        _toggles.Add(cmd);
        _registry.Register(CommandIds.Edit.AddShape, cmd);
        _registry.SetMenuItems(CommandIds.Edit.AddShape, new[]
        {
            new MenuItemDescriptor("Edit.Shape.Rectangle", "Edit.Shape.Rectangle", null, cmd, ShapeKind.Rectangle, ""),
            new MenuItemDescriptor("Edit.Shape.Ellipse", "Edit.Shape.Ellipse", null, cmd, ShapeKind.Ellipse, ""),
            new MenuItemDescriptor("Edit.Shape.Line", "Edit.Shape.Line", null, cmd, ShapeKind.Line, ""),
        });
    }

    private async Task AddShapeAsync(IDocumentView view, int page, PdfRect rect)
    {
        var s = _ctx.Settings;
        var spec = new ShapeSpec
        {
            Kind = _shape,
            Rect = _shape == ShapeKind.Line ? rect : rect, // linia: od lewego-dolnego do prawego-górnego narożnika przeciągnięcia
            Stroke = EditingSettings.Color(s.ShapeStroke),
            Fill = s.ShapeFilled ? EditingSettings.Color(s.ShapeFill) : null,
            LineWidth = s.ShapeLineWidth,
        };
        var svc = _ctx.Get<IObjectEditService>();
        await _ctx.ExecuteAsync(view.Session, "Edit.Undo.AddShape", (pdf, _, ct) => svc.AddShapeAsync(pdf, page, spec, ct), DocumentChangeKind.PageContent, pages: new[] { page });
    }

    private async Task AddImageAsync(IPdfDocumentSession session)
    {
        var view = _ctx.View;
        if (view is null) return;
        var files = _ctx.Dialogs?.OpenFiles(L["Edit.Image.Choose"],new[] { ImageFilter(L) });
        if (files is not { Count: > 0 }) return;
        var data = await File.ReadAllBytesAsync(files[0]);
        int page = view.CurrentPageIndex;
        var tr = view.GetPageTransform(page);
        var size = tr?.PageSize ?? PdfSize.A4;
        var origin = tr?.CropOrigin ?? new PdfPoint(0, 0);
        var (w, h) = await Task.Run(() => { var p = Imaging.ImageCodec.PrepareForPdf(data); return (p.Width / p.DpiX * 72, p.Height / p.DpiY * 72); });
        double scale = Math.Min(1, Math.Min(size.Width * 0.6 / w, size.Height * 0.6 / h));
        w *= scale; h *= scale;
        double x = origin.X + (size.Width - w) / 2, y = origin.Y + (size.Height - h) / 2;
        var svc = _ctx.Get<IObjectEditService>();
        await _ctx.ExecuteAsync(session, "Edit.Undo.AddImage", (pdf, _, ct) => svc.AddImageAsync(pdf, page, data, new PdfRect(x, y, x + w, y + h), ct), DocumentChangeKind.PageContent, pages: new[] { page });
        _ctx.Status(L["Edit.Image.Added"]);
    }

    private async Task FindReplaceAsync(IPdfDocumentSession session)
    {
        var dlg = new FindReplaceDialog(L, _ctx.View?.TextSelection?.Text?.Trim() ?? string.Empty);
        if (_ctx.ShowDialog(dlg) != true) return;
        var svc = _ctx.Get<ITextEditService>();
        FindReplaceResult? result = null;
        string find = dlg.Find, repl = dlg.Replace;
        var opts = dlg.Options;
        await _ctx.ExecuteAsync(session, "Edit.Undo.FindReplace", async (pdf, p, ct) => (result = await svc.FindReplaceAsync(pdf, find, repl, opts, p, ct)).Pdf, DocumentChangeKind.PageContent);
        if (result is null) return;
        _ctx.Message("Edit.FindReplace.Result", result.Replaced, result.Skipped);
        if (result.Covered > 0) _ctx.Dialogs?.ShowMessage(L.Format("Edit.FindReplace.CoverWarning", result.Covered), null, MessageSeverity.Warning);
    }

    // ------------------------------------------------------------------ łącza, zakładki

    private async Task CreateLinksAsync(IPdfDocumentSession session)
    {
        var svc = _ctx.Get<ILinkService>();
        int created = 0;
        await _ctx.ExecuteAsync(session, "Edit.Undo.CreateLinks", async (pdf, p, ct) => { var r = await svc.CreateLinksFromUrlsAsync(pdf, p, ct); created = r.Created; return r.Pdf; }, DocumentChangeKind.Annotations, DocumentPermissions.Annotate);
        _ctx.Message("Edit.Link.Created", created);
    }

    private async Task RemoveLinksAsync(IPdfDocumentSession session)
    {
        var svc = _ctx.Get<ILinkService>();
        var links = await svc.GetLinksAsync(session.Current.GetSharedBytes());
        if (links.Count == 0) { _ctx.Message("Edit.Link.NoneToRemove"); return; }
        if (_ctx.Dialogs?.Confirm(L.Format("Edit.Link.RemoveAllConfirm", links.Count)) == false) return;
        await _ctx.ExecuteAsync(session, "Edit.Undo.RemoveLinks", (pdf, _, ct) => svc.RemoveAllLinksAsync(pdf, ct), DocumentChangeKind.Annotations, DocumentPermissions.Annotate);
    }

    private async Task BookmarksAsync(IPdfDocumentSession session)
    {
        var svc = _ctx.Get<ILinkService>();
        var bookmarks = _ctx.Get<IBookmarkService>();
        var pdf = session.Current.GetSharedBytes();
        IReadOnlyList<BookmarkNode> tree = Array.Empty<BookmarkNode>();
        if (!await _ctx.RunAsync("Edit.Bookmarks.Progress", async (_, ct) => tree = await svc.GenerateBookmarksFromHeadingsAsync(pdf, new HeadingDetectionOptions(), ct))) return;
        if (tree.Count == 0) { _ctx.Message("Edit.Bookmarks.None"); return; }
        int count = Count(tree);
        var existing = await bookmarks.GetBookmarksAsync(pdf);
        var text = existing.Count > 0 ? L.Format("Edit.Bookmarks.ReplaceConfirm", count, Count(existing)) : L.Format("Edit.Bookmarks.CreateConfirm", count);
        if (_ctx.Dialogs?.Confirm(text, null, MessageSeverity.Info) == false) return;
        await _ctx.ExecuteAsync(session, "Edit.Undo.BookmarksFromHeadings", (b, _, ct) => bookmarks.SetBookmarksAsync(b, tree, ct), DocumentChangeKind.Bookmarks, DocumentPermissions.AssembleDocument);
        _ctx.Shell?.ShowPanel(PanelIds.Bookmarks);
    }

    private static int Count(IReadOnlyList<BookmarkNode> nodes) => nodes.Sum(n => 1 + Count(n.Children));

    // ------------------------------------------------------------------ tworzenie i konwersje

    private FileFilter[] CreateFilters() => new[]
    {
        new FileFilter(L["Edit.Filter.Supported"], "*.pdf", "*.png", "*.jpg", "*.jpeg", "*.tif", "*.tiff", "*.bmp", "*.gif", "*.txt", "*.docx", "*.xlsx", "*.pptx"),
        FileFilter.Pdf(L), ImageFilter(L), new FileFilter(L["Edit.Filter.Text"], "*.txt"), new FileFilter(L["Edit.Filter.Office"], "*.docx", "*.xlsx", "*.pptx"), FileFilter.All(L),
    };

    /// <summary>Pliki Office bez zainstalowanej aplikacji są pomijane z komunikatem; reszta działa jak dotąd.</summary>
    private IReadOnlyList<string> WithoutUnavailableOffice(IReadOnlyList<string> files)
    {
        var office = _ctx.Services.GetService(typeof(Conversion.IOfficeConverter)) as Conversion.IOfficeConverter;
        var missing = files.Where(f => Path.GetExtension(f) is var e && Conversion.OfficeFormats.IsOffice(e) && office?.IsAvailableFor(e) != true).ToList();
        if (missing.Count == 0) return files;
        _ctx.Message("Edit.Office.Missing", string.Join(", ", missing.Select(Path.GetFileName)));
        return files.Except(missing).ToList();
    }

    private async Task CreateFromFileAsync()
    {
        var files = _ctx.Dialogs?.OpenFiles(L["Edit.Create.ChooseFiles"], CreateFilters(), multiselect: true);
        if (files is not { Count: > 0 }) return;
        files = WithoutUnavailableOffice(files);
        if (files.Count == 0) return;
        var exts = files.Select(f => Path.GetExtension(f).ToLowerInvariant()).ToList();
        if (exts.All(e => e == ".pdf"))
        {
            foreach (var f in files) await _ctx.Documents.OpenAsync(f);
            return;
        }
        var conv = _ctx.Get<IConversionService>();
        byte[]? pdf = null;
        if (exts.All(e => ConversionService.ImageExtensions.Contains(e)))
        {
            var dlg = new ImagesToPdfDialog(L, files.Count);
            if (_ctx.ShowDialog(dlg) != true) return;
            var opts = dlg.Options;
            if (!await _ctx.RunAsync("Edit.Create.Progress", async (p, ct) => pdf = await conv.ImagesToPdfAsync(files, opts, p, ct))) return;
        }
        else if (!await _ctx.RunAsync("Edit.Create.Progress", async (p, ct) => pdf = await conv.CombineFilesAsync(files, p, ct))) return;
        if (pdf is not null) await _ctx.Documents.CreateNewAsync(pdf, Path.GetFileNameWithoutExtension(files[0]) + ".pdf");
    }

    private async Task CombineAsync()
    {
        var initial = _ctx.Documents.ActiveDocument?.FilePath is { } p ? new[] { p } : Array.Empty<string>();
        var dlg = new CombineFilesDialog(L, () => _ctx.Dialogs?.OpenFiles(L["Edit.Create.ChooseFiles"], CreateFilters(), multiselect: true), initial);
        if (_ctx.ShowDialog(dlg) != true) return;
        var files = WithoutUnavailableOffice(dlg.Files);
        if (files.Count == 0) return;
        var conv = _ctx.Get<IConversionService>();
        byte[]? pdf = null;
        if (!await _ctx.RunAsync("Edit.Combine.Progress", async (pr, ct) => pdf = await conv.CombineFilesAsync(files, pr, ct))) return;
        if (pdf is not null) await _ctx.Documents.CreateNewAsync(pdf, L["Edit.Combine.DocumentName"]);
    }

    private async Task FromClipboardAsync()
    {
        var conv = _ctx.Get<IConversionService>();
        byte[]? pdf = null;
        if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(image));
            using var ms = new MemoryStream();
            enc.Save(ms);
            var data = ms.ToArray();
            await _ctx.RunAsync("Edit.Create.Progress", async (_, ct) => pdf = await conv.ImageDataToPdfAsync(data, new ImageToPdfOptions(), ct));
        }
        else if (Clipboard.ContainsText())
        {
            var text = Clipboard.GetText();
            await _ctx.RunAsync("Edit.Create.Progress", async (_, ct) => pdf = await conv.TextToPdfAsync(text, null, ct));
        }
        else
        {
            _ctx.Message("Edit.Clipboard.Empty");
            return;
        }
        if (pdf is not null) await _ctx.Documents.CreateNewAsync(pdf, L["Edit.Clipboard.DocumentName"]);
    }

    /// <summary>Jedyne, obok TSA, połączenie sieciowe: tylko na to polecenie i po zatwierdzeniu adresu w oknie.</summary>
    private async Task FromWebAsync()
    {
        var printer = _ctx.Get<Conversion.IWebPdfPrinter>();
        if (!printer.IsAvailable) { _ctx.Message("Edit.Web.NoEdge"); return; }
        var dlg = new WebToPdfDialog(L, () => _ctx.Dialogs?.OpenFiles(L["Edit.Web.Browse"], new[] { new FileFilter(L["Edit.Filter.Html"], "*.html", "*.htm") })?.FirstOrDefault());
        if (_ctx.ShowDialog(dlg) != true || dlg.Source is not { } source) return;
        byte[]? pdf = null;
        if (!await _ctx.RunAsync("Edit.Web.Progress", async (_, ct) => pdf = await printer.PrintAsync(source, ct))) return;
        if (pdf is not null) await _ctx.Documents.CreateNewAsync(pdf, source.SuggestedName);
    }

    private async Task FromScannerAsync()
    {
        var backend = _ctx.Get<Scanning.IScannerBackend>();
        if (!backend.IsAvailable) { _ctx.Message("Edit.Scan.Unavailable"); return; }
        var workflow = new Scanning.ScanWorkflow(backend);
        IReadOnlyList<Scanning.ScannerInfo> devices = Array.Empty<Scanning.ScannerInfo>();
        if (!await _ctx.RunAsync("Edit.Scan.Progress", async (_, ct) => devices = await workflow.ListDevicesAsync(ct))) return;
        if (devices.Count == 0) { _ctx.Message("Edit.Scan.NoScanner"); return; }
        var dlg = new ScannerDialog(L, devices);
        if (_ctx.ShowDialog(dlg) != true) return;
        var settings = dlg.Settings;
        bool runOcr = dlg.RunOcr;

        var pages = await workflow.CollectAsync(settings,
            step => _ctx.RunAsync("Edit.Scan.Progress", (_, ct) => step(ct)),
            count => _ctx.Dialogs?.Confirm(L.Format("Edit.Scan.NextPrompt", count), L["Edit.Scan.NextTitle"], MessageSeverity.Info) == true);
        if (pages.Count == 0) { _ctx.Message("Edit.Scan.NothingScanned"); return; }

        var ocr = runOcr ? _ctx.Services.GetService(typeof(IOcrService)) as IOcrService : null;
        if (ocr is not null && !await ocr.IsAvailableAsync()) { _ctx.Message("Edit.Scan.OcrUnavailable"); ocr = null; }
        var ocrOptions = ocr is null ? null : new OcrOptions { Languages = _ctx.Settings.OcrLanguages, SkipPagesWithText = false, SuspectThreshold = _ctx.SettingsService?.Current.OcrSuspectThreshold ?? 70 };
        var conv = _ctx.Get<IConversionService>();
        var temp = _ctx.Get<IAppPaths>().TempDirectory;
        byte[]? pdf = null;
        if (!await _ctx.RunAsync("Edit.Scan.Building", async (p, ct) => pdf = await Scanning.ScanWorkflow.BuildPdfAsync(pages, conv, temp, ocr, ocrOptions, p, ct))) return;
        if (pdf is not null) await _ctx.Documents.CreateNewAsync(pdf, L["Edit.Scan.DocumentName"]);
    }

    private string BaseName(IPdfDocumentSession s) => Path.GetFileNameWithoutExtension(s.FilePath ?? s.DisplayName);

    private async Task ToImagesAsync(IPdfDocumentSession session)
    {
        var view = _ctx.View;
        var dlg = new ExportImagesDialog(L, view?.CurrentPageIndex ?? 0, view?.PageCount ?? 1, _ctx.Settings.ExportDpi);
        if (_ctx.ShowDialog(dlg) != true) return;
        var opts = dlg.Options;
        _ctx.SaveSettings(s => s.ExportDpi = opts.Dpi);
        var folder = _ctx.Dialogs?.SelectFolder(L["Edit.Convert.ToImages.Folder"], session.FilePath is { } fp ? Path.GetDirectoryName(fp) : null);
        if (folder is null) return;
        var conv = _ctx.Get<IConversionService>();
        var pdf = session.Current.GetSharedBytes();
        IReadOnlyList<string> files = Array.Empty<string>();
        if (!await _ctx.RunAsync("Edit.Convert.Progress", async (p, ct) => files = await conv.PdfToImagesAsync(pdf, opts, folder, BaseName(session), p, ct))) return;
        _ctx.Message("Edit.Convert.ToImages.Done", files.Count, folder);
    }

    private async Task ToTextAsync(IPdfDocumentSession session)
    {
        var view = _ctx.View;
        var dlg = new ExportOptionsDialog(L, docx: false, view?.CurrentPageIndex ?? 0, view?.PageCount ?? 1);
        if (_ctx.ShowDialog(dlg) != true) return;
        var path = _ctx.Dialogs?.SaveFile(L["Edit.Convert.ToText.Title"], new[] { new FileFilter(L["Edit.Filter.Text"], "*.txt") }, BaseName(session) + ".txt");
        if (path is null) return;
        var opts = dlg.TextOptions;
        var conv = _ctx.Get<IConversionService>();
        var pdf = session.Current.GetSharedBytes();
        string text = string.Empty;
        if (!await _ctx.RunAsync("Edit.Convert.Progress", async (p, ct) => text = await conv.PdfToTextAsync(pdf, opts, p, ct))) return;
        var bytes = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();
        if (_ctx.Files is { } files) await files.WriteAllBytesSafeAsync(path, bytes);
        else await File.WriteAllBytesAsync(path, bytes);
        _ctx.Status(L.Format("Edit.Convert.Saved", path));
    }

    private async Task ToDocxAsync(IPdfDocumentSession session)
    {
        var view = _ctx.View;
        var dlg = new ExportOptionsDialog(L, docx: true, view?.CurrentPageIndex ?? 0, view?.PageCount ?? 1);
        if (_ctx.ShowDialog(dlg) != true) return;
        var path = _ctx.Dialogs?.SaveFile(L["Edit.Convert.ToDocx.Title"], new[] { new FileFilter(L["Edit.Filter.Docx"], "*.docx") }, BaseName(session) + ".docx");
        if (path is null) return;
        var opts = dlg.DocxOptions;
        var conv = _ctx.Get<IConversionService>();
        var pdf = session.Current.GetSharedBytes();
        if (!await _ctx.RunAsync("Edit.Convert.Progress", (p, ct) => conv.PdfToDocxAsync(pdf, path, opts, p, ct))) return;
        _ctx.Status(L.Format("Edit.Convert.Saved", path));
    }

    private async Task ToHtmlAsync(IPdfDocumentSession session)
    {
        var view = _ctx.View;
        var dlg = new ExportHtmlDialog(L, view?.CurrentPageIndex ?? 0, view?.PageCount ?? 1);
        if (_ctx.ShowDialog(dlg) != true) return;
        var path = _ctx.Dialogs?.SaveFile(L["Edit.Convert.ToHtml.Title"], new[] { new FileFilter(L["Edit.Filter.Html"], "*.html", "*.htm") }, BaseName(session) + ".html");
        if (path is null) return;
        var opts = dlg.Options;
        var conv = _ctx.Get<IConversionService>();
        var pdf = session.Current.GetSharedBytes();
        string html = string.Empty;
        if (!await _ctx.RunAsync("Edit.Convert.Progress", async (p, ct) => html = await conv.PdfToHtmlAsync(pdf, opts, BaseName(session), p, ct))) return;
        var bytes = Encoding.UTF8.GetBytes(html);
        if (_ctx.Files is { } files) await files.WriteAllBytesSafeAsync(path, bytes);
        else await File.WriteAllBytesAsync(path, bytes);
        _ctx.Status(L.Format("Edit.Convert.Saved", path));
    }

    private async Task ToXlsxAsync(IPdfDocumentSession session)
    {
        var view = _ctx.View;
        var dlg = new ExportXlsxDialog(L, view?.CurrentPageIndex ?? 0, view?.PageCount ?? 1);
        if (_ctx.ShowDialog(dlg) != true) return;
        var path = _ctx.Dialogs?.SaveFile(L["Edit.Convert.ToXlsx.Title"], new[] { new FileFilter(L["Edit.Filter.Xlsx"], "*.xlsx") }, BaseName(session) + ".xlsx");
        if (path is null) return;
        var opts = dlg.Options;
        var conv = _ctx.Get<IConversionService>();
        var pdf = session.Current.GetSharedBytes();
        XlsxExportResult? result = null;
        if (!await _ctx.RunAsync("Edit.Convert.Progress", async (p, ct) => result = await conv.PdfToXlsxAsync(pdf, path, opts, p, ct)) || result is null) return;
        _ctx.Status(L.Format("Edit.Convert.ToXlsx.Done", path, result.Sheets, result.Tables));
        if (result.PagesWithoutText > 0) _ctx.Message("Edit.Convert.ToXlsx.NoTextWarning", result.PagesWithoutText);
    }

    // ------------------------------------------------------------------ OCR

    private async Task OcrAsync(IPdfDocumentSession session, bool quick)
    {
        var ocr = _ctx.Get<IOcrService>();
        var app = _ctx.SettingsService?.Current;
        var s = _ctx.Settings;
        OcrOptions options;
        bool review;
        if (quick)
        {
            options = new OcrOptions { Languages = s.OcrLanguages, SuspectThreshold = app?.OcrSuspectThreshold ?? 70 };
            review = false;
        }
        else
        {
            var view = _ctx.View;
            var dlg = new OcrDialog(L, view?.CurrentPageIndex ?? 0, view?.PageCount ?? 1, s.OcrLanguages, app?.OcrSuspectThreshold ?? 70, s.OcrReviewSuspects);
            if (_ctx.ShowDialog(dlg) != true) return;
            options = dlg.Options;
            review = dlg.Review;
            var langs = dlg.Languages;
            _ctx.SaveSettings(x => { x.OcrLanguages = langs; x.OcrReviewSuspects = review; });
            if (app is not null && app.OcrSuspectThreshold != dlg.Threshold) _ctx.SettingsService!.Update(a => a.OcrSuspectThreshold = dlg.Threshold);
        }
        await RecognizeAndApplyAsync(session, options, review);
    }

    private async Task RecognizeAndApplyAsync(IPdfDocumentSession session, OcrOptions options, bool review)
    {
        var ocr = _ctx.Get<IOcrService>();
        if (!await ocr.IsAvailableAsync()) { _ctx.Message("Edit.Ocr.Unavailable"); return; }
        var pdf = session.Current.GetSharedBytes();
        OcrResult? result = null;
        if (!await _ctx.RunAsync("Edit.Ocr.Progress", async (p, ct) => result = await ocr.RecognizeAsync(pdf, options, p, ct)) || result is null) return;
        int words = result.Pages.Sum(p => p.Words.Count);
        if (words == 0)
        {
            _ctx.Message(result.Pages.All(p => p.Skipped) ? "Edit.Ocr.AllSkipped" : "Edit.Ocr.NothingFound");
            return;
        }
        if (review && result.SuspectWords.Any())
        {
            var win = new OcrReviewWindow(L, result, pdf, _ctx.Renderer);
            if (_ctx.ShowDialog(win) != true) return;
            result = win.Corrected;
        }
        await ApplyLayerAsync(session, result);
        int skipped = result.Pages.Count(p => p.Skipped);
        _ctx.Status(L.Format("Edit.Ocr.Done", words, result.SuspectWords.Count(), skipped));
    }

    private async Task ApplyLayerAsync(IPdfDocumentSession session, OcrResult result)
    {
        var ocr = _ctx.Get<IOcrService>();
        var final = result;
        await _ctx.ExecuteAsync(session, "Edit.Undo.Ocr", (b, _, ct) => ocr.ApplyTextLayerAsync(b, final, ct), DocumentChangeKind.PageContent, DocumentPermissions.ModifyContent, "Edit.Ocr.Applying",
            result.Pages.Where(p => !p.Skipped).Select(p => p.PageIndex).ToList());
        _ocr.AddOrUpdate(session, new OcrState { Result = final, Version = session.Current.Version });
    }

    private async Task OcrRegionAsync(IDocumentView view, int page, PdfRect rect)
    {
        var options = new OcrOptions { Languages = _ctx.Settings.OcrLanguages, Pages = new[] { page }, Region = rect, SkipPagesWithText = false, SuspectThreshold = _ctx.SettingsService?.Current.OcrSuspectThreshold ?? 70 };
        await RecognizeAndApplyAsync(view.Session, options, _ctx.Settings.OcrReviewSuspects);
    }

    private async Task SuspectAsync(IPdfDocumentSession session)
    {
        if (!_ocr.TryGetValue(session, out var state)) { _ctx.Message("Edit.Ocr.NoResults"); return; }
        if (state.Version != session.Current.Version) { _ctx.Message("Edit.Ocr.Outdated"); return; }
        if (!state.Result.SuspectWords.Any()) { _ctx.Message("Edit.Ocr.NoSuspects"); return; }
        var win = new OcrReviewWindow(L, state.Result, session.Current.GetSharedBytes(), _ctx.Renderer);
        if (_ctx.ShowDialog(win) != true) return;
        // Warstwa OCR strony jest zastępowana (znacznik), więc poprawki nie dublują tekstu.
        await ApplyLayerAsync(session, win.Corrected);
    }

    // ------------------------------------------------------------------ redakcja

    private async Task MarkAsync(IDocumentView view, IReadOnlyList<RedactionMark> marks)
    {
        var svc = _ctx.Get<IRedactionService>();
        await _ctx.ExecuteAsync(view.Session, "Edit.Undo.MarkRedaction", (pdf, _, ct) => svc.MarkAsync(pdf, marks, ct), DocumentChangeKind.Annotations,
            DocumentPermissions.ModifyContent, pages: marks.Select(m => m.PageIndex).Distinct().ToList());
        _ctx.Status(L["Edit.Redact.Marked"]);
    }

    private async Task SearchRedactAsync(IPdfDocumentSession session)
    {
        var svc = _ctx.Get<IRedactionService>();
        var pdf = session.Current.GetSharedBytes();
        var dlg = new SearchRedactDialog(L, s => svc.FindAsync(pdf, s));
        if (_ctx.ShowDialog(dlg) != true) return;
        var marks = dlg.Selected.Select(c => new RedactionMark(c.PageIndex, c.Quads)).ToList();
        await _ctx.ExecuteAsync(session, "Edit.Undo.MarkRedaction", (b, _, ct) => svc.MarkAsync(b, marks, ct), DocumentChangeKind.Annotations);
        _ctx.Status(L.Format("Edit.Redact.MarkedCount", marks.Count));
    }

    private async Task ApplyRedactionsAsync(IPdfDocumentSession session)
    {
        var marks = await Task.Run(() => RedactionService.ReadMarks(session.Current.GetSharedBytes()));
        if (marks.Count == 0) { _ctx.Message("Edit.Redact.NoMarks"); return; }
        var text = L.Format("Edit.Redact.Warning.Irreversible", marks.Values.Sum(v => v.Count), marks.Count);
        if (_ctx.Dialogs is { } d && !d.Confirm(text, L["Edit.Redact.Warning.Title"], MessageSeverity.Warning)) return;
        var svc = _ctx.Get<IRedactionService>();
        await _ctx.ExecuteAsync(session, "Edit.Undo.ApplyRedactions", (pdf, p, ct) => svc.ApplyAsync(pdf, new RedactionApplyOptions(), p, ct),
            DocumentChangeKind.PageContent | DocumentChangeKind.Annotations | DocumentChangeKind.FormFields | DocumentChangeKind.Metadata | DocumentChangeKind.Bookmarks,
            DocumentPermissions.ModifyContent, "Edit.Redact.Applying");
        _ctx.Status(L["Edit.Redact.Applied"]);
    }

    /// <summary>Polecenie niezależne od aktywnego dokumentu (Utwórz…).</summary>
    private sealed class SimpleCommand : System.Windows.Input.ICommand
    {
        private readonly Func<Task> _run;
        private readonly EditingContext _ctx;
        private bool _busy;

        public SimpleCommand(Func<Task> run, EditingContext ctx) { _run = run; _ctx = ctx; }

        public event EventHandler? CanExecuteChanged
        {
            add => System.Windows.Input.CommandManager.RequerySuggested += value;
            remove => System.Windows.Input.CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object? parameter) => !_busy;

        public async void Execute(object? parameter)
        {
            if (_busy) return;
            _busy = true;
            try { await _run(); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _ctx.Dialogs?.ShowError(ex); }
            finally { _busy = false; System.Windows.Input.CommandManager.InvalidateRequerySuggested(); }
        }
    }
}
