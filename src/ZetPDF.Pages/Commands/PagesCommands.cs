using System.Globalization;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Pages.Engine;
using ZetPDF.Pages.Stamping;
using ZetPDF.Pages.Ui;
using PageRange = ZetPDF.Core.Utilities.PageRange;

namespace ZetPDF.Pages.Commands;

/// <summary>
/// Obsługa 40 poleceń wstążki modułu Pages. Każda zmiana dokumentu = DelegateDocumentCommand (Cofnij/Ponów)
/// wykonywany przez sesję; okna parametrów przez IDialogService.ShowDialog; dłuższe operacje z postępem i anulowaniem.
/// </summary>
internal sealed class PagesCommands
{
    private const DocumentPermissions Assemble = DocumentPermissions.AssembleDocument;
    private const DocumentPermissions Modify = DocumentPermissions.ModifyContent;

    private readonly IServiceProvider _sp;
    private readonly IDocumentManager _docs;
    private readonly IDialogService _dialogs;
    private readonly ILocalizationService _loc;
    private readonly IPageOperations _ops;
    private readonly IStampingService _stamp;
    private readonly IOptimizerService _opt;
    private readonly ISanitizeService _san;
    private readonly IPdfRenderer? _renderer;

    public PagesCommands(IServiceProvider services)
    {
        _sp = services;
        _docs = services.GetRequiredService<IDocumentManager>();
        _dialogs = services.GetRequiredService<IDialogService>();
        _loc = services.GetRequiredService<ILocalizationService>();
        _ops = services.GetRequiredService<IPageOperations>();
        _stamp = services.GetRequiredService<IStampingService>();
        _opt = services.GetRequiredService<IOptimizerService>();
        _san = services.GetRequiredService<ISanitizeService>();
        _renderer = services.GetService<IPdfRenderer>();
    }

    private IViewerService? Viewer => _sp.GetService<IViewerService>();

    private string T(string key) => _loc[key];

    public void Register(ICommandRegistry r)
    {
        void Reg(string id, Func<IPdfDocumentSession, Task> run, DocumentPermissions perm) =>
            r.Register(id, new ActiveDocumentCommand(_docs, _dialogs, run, perm));

        // Organizuj – strony
        Reg(CommandIds.Pages.InsertFromFile, InsertFromFileAsync, Assemble);
        Reg(CommandIds.Pages.InsertBlank, InsertBlankAsync, Assemble);
        Reg(CommandIds.Pages.Delete, DeleteAsync, Assemble);
        Reg(CommandIds.Pages.Extract, ExtractAsync, Assemble | DocumentPermissions.CopyContent);
        Reg(CommandIds.Pages.Replace, ReplaceAsync, Assemble);
        Reg(CommandIds.Pages.Duplicate, DuplicateAsync, Assemble);
        Reg(CommandIds.Pages.Move, MoveAsync, Assemble);
        Reg(CommandIds.Pages.Swap, SwapAsync, Assemble);
        Reg(CommandIds.Pages.Reverse, ReverseAsync, Assemble);
        Reg(CommandIds.Pages.Interleave, InterleaveAsync, Assemble);
        // Organizuj – podział i przekształcenia
        Reg(CommandIds.Pages.Split, SplitAsync, Assemble | DocumentPermissions.CopyContent);
        r.Register(CommandIds.Pages.Merge, new AsyncCommand(MergeAsync, _dialogs));
        Reg(CommandIds.Pages.RotateLeft, s => RotateSelectedAsync(s, -90), Assemble);
        Reg(CommandIds.Pages.RotateRight, s => RotateSelectedAsync(s, 90), Assemble);
        Reg(CommandIds.Pages.Rotate, RotateAsync, Assemble);
        Reg(CommandIds.Pages.Crop, CropAsync, Modify);
        Reg(CommandIds.Pages.Resize, ResizeAsync, Modify);
        Reg(CommandIds.Pages.Flatten, FlattenAsync, Modify);
        Reg(CommandIds.Pages.PageLabels, PageLabelsAsync, Assemble);
        // Oznaczenia stron
        Reg(CommandIds.Stamp.Watermark, s => WatermarkAsync(s, false), Modify);
        Reg(CommandIds.Stamp.WatermarkAdd, s => WatermarkAsync(s, false), Modify);
        Reg(CommandIds.Stamp.WatermarkUpdate, s => WatermarkAsync(s, true), Modify);
        Reg(CommandIds.Stamp.WatermarkRemoveAll, s => RemoveMarksAsync(s, "Pages.Undo.RemoveWatermarks", _stamp.RemoveWatermarksAsync), Modify);
        Reg(CommandIds.Stamp.Background, s => BackgroundAsync(s, false), Modify);
        Reg(CommandIds.Stamp.BackgroundAdd, s => BackgroundAsync(s, false), Modify);
        Reg(CommandIds.Stamp.BackgroundUpdate, s => BackgroundAsync(s, true), Modify);
        Reg(CommandIds.Stamp.BackgroundRemoveAll, s => RemoveMarksAsync(s, "Pages.Undo.RemoveBackgrounds", _stamp.RemoveBackgroundsAsync), Modify);
        Reg(CommandIds.Stamp.HeaderFooter, s => HeaderFooterAsync(s, false), Modify);
        Reg(CommandIds.Stamp.HeaderFooterAdd, s => HeaderFooterAsync(s, false), Modify);
        Reg(CommandIds.Stamp.HeaderFooterUpdate, s => HeaderFooterAsync(s, true), Modify);
        Reg(CommandIds.Stamp.HeaderFooterRemoveAll, s => RemoveMarksAsync(s, "Pages.Undo.RemoveHeaderFooter", _stamp.RemoveHeaderFooterAsync), Modify);
        Reg(CommandIds.Stamp.PageNumbers, PageNumbersAsync, Modify);
        Reg(CommandIds.Stamp.Bates, BatesAsync, Modify);
        Reg(CommandIds.Stamp.BatesAdd, BatesAsync, Modify);
        Reg(CommandIds.Stamp.BatesRemoveAll, s => RemoveMarksAsync(s, "Pages.Undo.RemoveBates", _stamp.RemoveBatesAsync), Modify);
        // Plik, Konwertuj, Ochrona
        r.Register(CommandIds.File.CreateBlank, new AsyncCommand(CreateBlankAsync, _dialogs));
        Reg(CommandIds.File.OptimizeReduceSize, s => OptimizeAsync(s, advanced: false), Modify);
        Reg(CommandIds.File.OptimizeAdvanced, s => OptimizeAsync(s, advanced: true), Modify);
        Reg(CommandIds.Convert.Grayscale, GrayscaleAsync, Modify);
        Reg(CommandIds.Sanitize.Document, SanitizeAsync, Modify);
    }

    // ------------------------------------------------------------------ pomocniki

    /// <summary>Wykonuje polecenie z Cofnij/Ponów; z oknem postępu, gdy podano tytuł.</summary>
    private async Task<bool> ExecAsync(IPdfDocumentSession s, string undoKey, Func<byte[], IProgress<OperationProgress>?, CancellationToken, Task<byte[]>> transform,
        DocumentChangeKind kind, DocumentPermissions perm, string? progressTitleKey = null)
    {
        var cmd = new DelegateDocumentCommand(undoKey, transform, kind, perm);
        if (progressTitleKey is null) return await s.ExecuteAsync(cmd);
        bool changed = false;
        bool ok = await _dialogs.RunWithProgressAsync(T(progressTitleKey), async (p, ct) => changed = await s.ExecuteAsync(cmd, p, ct));
        return ok && changed;
    }

    private IDocumentView? ViewOf(IPdfDocumentSession s) =>
        Viewer?.GetView(s) ?? (Viewer?.ActiveView is { } v && ReferenceEquals(v.Session, s) ? v : null);

    private async Task<DocContext> ContextAsync(IPdfDocumentSession s)
    {
        var view = ViewOf(s);
        var bytes = s.Current.GetSharedBytes();
        int current = view?.CurrentPageIndex ?? 0;
        var (count, size) = await Task.Run(() => PdfSharpEdit.Read(bytes, d =>
        {
            int i = Math.Clamp(current, 0, Math.Max(0, d.PageCount - 1));
            var f = d.PageCount > 0 ? PdfIo.Frame(d.Pages[i]) : default;
            return (d.PageCount, d.PageCount > 0 ? new PdfSize(f.Width, f.Height) : PdfSize.A4);
        }));
        current = Math.Clamp(current, 0, Math.Max(0, count - 1));
        var selected = view?.SelectedPages?.Where(p => p >= 0 && p < count).ToArray() ?? Array.Empty<int>();
        if (selected.Length == 0) selected = new[] { current };
        return new DocContext(count, current, selected, size, s.DisplayName);
    }

    private IReadOnlyList<int> SelectedPages(IPdfDocumentSession s)
    {
        var view = ViewOf(s);
        var sel = view?.SelectedPages;
        if (sel is { Count: > 0 }) return sel.ToArray();
        return new[] { view?.CurrentPageIndex ?? 0 };
    }

    private string? DocFolder(IPdfDocumentSession s) => s.FilePath is null ? null : Path.GetDirectoryName(s.FilePath);

    private string BaseName(IPdfDocumentSession s) => PdfIo.SafeFileName(Path.GetFileNameWithoutExtension(s.FilePath ?? s.DisplayName));

    /// <summary>Hasło pliku źródłowego (pętla okna hasła). Ok=false, gdy anulowano.</summary>
    private async Task<(bool Ok, string? Password, int PageCount)> SourceAsync(string path)
    {
        var bytes = await PdfFileProbes.ReadVerifiedAsync(path);
        bool encrypted = await Task.Run(() => PdfSecurityHelper.IsEncrypted(bytes));
        if (!encrypted || _renderer is null)
        {
            if (!encrypted)
                return (true, null, await Task.Run(() => PdfSharpEdit.Read(bytes, d => d.PageCount)));
        }
        if (_renderer is not null)
        {
            var probe = await _renderer.ProbeAsync(bytes);
            if (probe.Status == PdfProbeStatus.Ok) return (true, null, probe.PageCount);
            if (probe.Status == PdfProbeStatus.Corrupt) throw new PdfCorruptException(probe.ErrorMessage ?? Path.GetFileName(path));
        }
        bool retry = false;
        while (true)
        {
            var pw = _dialogs.AskPassword(Path.GetFileName(path), retry);
            if (pw is null) return (false, null, 0);
            await PdfFileProbes.EnsureSafeAsync(bytes, pw);
            if (_renderer is not null)
            {
                var probe = await _renderer.ProbeAsync(bytes, pw);
                if (probe.Status == PdfProbeStatus.Ok) return (true, pw, probe.PageCount);
            }
            else if (PdfSecurityHelper.IsOwnerPassword(bytes, pw))
                return (true, pw, await Task.Run(() => PdfSharpEdit.Read(PdfSecurityHelper.RemoveSecurity(bytes, pw), d => d.PageCount)));
            retry = true;
        }
    }

    private bool TryRange(string text, int count, out IReadOnlyList<int>? pages)
    {
        pages = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (PageRange.TryParse(text, count, out var p) && p.Count > 0) { pages = p; return true; }
        _dialogs.ShowMessage(_loc.Format("Pages.Error.InvalidRangeOf", count), T("Pages.Dialog.ErrorTitle"), MessageSeverity.Warning);
        return false;
    }

    private static string Size(long bytes) => bytes switch
    {
        < 1024 => bytes.ToString(CultureInfo.CurrentCulture) + " B",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0.#", CultureInfo.CurrentCulture) + " KB",
        _ => (bytes / 1024.0 / 1024.0).ToString("0.##", CultureInfo.CurrentCulture) + " MB",
    };


    // ------------------------------------------------------------------ strony

    private async Task InsertFromFileAsync(IPdfDocumentSession s)
    {
        var dlg = new InsertFromFileDialog(_loc, _dialogs, await ContextAsync(s));
        if (_dialogs.ShowDialog(dlg) != true) return;
        var (ok, pw, count) = await SourceAsync(dlg.FilePath);
        if (!ok || !TryRange(dlg.SourceRange, count, out var pages)) return;
        await ExecAsync(s, "Pages.Undo.Insert", (pdf, _, ct) => _ops.InsertFromFileAsync(pdf, dlg.FilePath, pw, pages, dlg.InsertAt, ct), DocumentChangeKind.PageStructure, Assemble, "Pages.Progress.Working");
    }

    private async Task InsertBlankAsync(IPdfDocumentSession s)
    {
        var dlg = new InsertBlankDialog(_loc, _dialogs, await ContextAsync(s));
        if (_dialogs.ShowDialog(dlg) != true) return;
        await ExecAsync(s, "Pages.Undo.InsertBlank", (pdf, _, ct) => _ops.InsertBlankAsync(pdf, dlg.InsertAt, dlg.Count, dlg.Size, ct), DocumentChangeKind.PageStructure, Assemble);
    }

    private async Task DeleteAsync(IPdfDocumentSession s)
    {
        var ctx = await ContextAsync(s);
        var dlg = new RangeDialog(_loc, _dialogs, ctx, "Pages.Delete.Title", "Pages.Delete.Hint", okKey: "Pages.Delete.Ok");
        if (_dialogs.ShowDialog(dlg) != true) return;
        if (dlg.Pages.Count >= ctx.PageCount) { _dialogs.ShowMessage(T("Pages.Error.CannotDeleteAll"), T("Pages.Dialog.ErrorTitle"), MessageSeverity.Warning); return; }
        var pages = dlg.Pages;
        if (ViewOf(s) is { } v) v.SelectedPages = Array.Empty<int>();
        await ExecAsync(s, "Pages.Undo.Delete", (pdf, _, ct) => _ops.DeleteAsync(pdf, pages, ct), DocumentChangeKind.PageStructure, Assemble);
    }

    private async Task ExtractAsync(IPdfDocumentSession s)
    {
        var ctx = await ContextAsync(s);
        var dlg = new ExtractDialog(_loc, _dialogs, ctx, DocFolder(s));
        if (_dialogs.ShowDialog(dlg) != true) return;
        var bytes = s.Current.GetSharedBytes();
        var pages = dlg.Pages;
        if (dlg.SeparateFiles)
        {
            IReadOnlyList<string> files = Array.Empty<string>();
            if (!await _dialogs.RunWithProgressAsync(T("Pages.Progress.Working"), async (_, ct) => files = await _ops.ExtractToFilesAsync(bytes, pages, dlg.Folder, dlg.Pattern, BaseName(s), ct))) return;
            _dialogs.ShowMessage(_loc.Format("Pages.Extract.Done", files.Count, dlg.Folder), T("Pages.Extract.Title"));
        }
        else
        {
            var extracted = await _ops.ExtractAsync(bytes, pages);
            await _docs.CreateNewAsync(extracted, _loc.Format("Pages.Extract.NewName", s.DisplayName, PageRange.Format(pages)));
        }
        if (dlg.DeleteAfter)
        {
            if (pages.Count >= ctx.PageCount) { _dialogs.ShowMessage(T("Pages.Error.CannotDeleteAll"), T("Pages.Dialog.ErrorTitle"), MessageSeverity.Warning); return; }
            await ExecAsync(s, "Pages.Undo.Delete", (pdf, _, ct) => _ops.DeleteAsync(pdf, pages, ct), DocumentChangeKind.PageStructure, Assemble);
        }
    }

    private async Task ReplaceAsync(IPdfDocumentSession s)
    {
        var dlg = new ReplaceDialog(_loc, _dialogs, await ContextAsync(s));
        if (_dialogs.ShowDialog(dlg) != true) return;
        var (ok, pw, count) = await SourceAsync(dlg.FilePath);
        if (!ok) return;
        if (dlg.SourceStart + dlg.Targets.Count > count)
        {
            _dialogs.ShowMessage(_loc.Format("Pages.Error.SourceTooShort", count, dlg.Targets.Count), T("Pages.Dialog.ErrorTitle"), MessageSeverity.Warning);
            return;
        }
        var src = Enumerable.Range(dlg.SourceStart, dlg.Targets.Count).ToArray();
        await ExecAsync(s, "Pages.Undo.Replace", (pdf, _, ct) => _ops.ReplaceAsync(pdf, dlg.Targets, dlg.FilePath, pw, src, ct), DocumentChangeKind.PageStructure, Assemble, "Pages.Progress.Working");
    }

    private Task DuplicateAsync(IPdfDocumentSession s)
    {
        var pages = SelectedPages(s);
        return ExecAsync(s, "Pages.Undo.Duplicate", (pdf, _, ct) => _ops.DuplicateAsync(pdf, pages, ct), DocumentChangeKind.PageStructure, Assemble);
    }

    private async Task MoveAsync(IPdfDocumentSession s)
    {
        var dlg = new MoveDialog(_loc, _dialogs, await ContextAsync(s));
        if (_dialogs.ShowDialog(dlg) != true) return;
        await ExecAsync(s, "Pages.Undo.Move", (pdf, _, ct) => _ops.MoveAsync(pdf, dlg.Pages, dlg.Target, ct), DocumentChangeKind.PageStructure, Assemble);
    }

    private async Task SwapAsync(IPdfDocumentSession s)
    {
        var dlg = new SwapDialog(_loc, _dialogs, await ContextAsync(s));
        if (_dialogs.ShowDialog(dlg) != true) return;
        await ExecAsync(s, "Pages.Undo.Swap", (pdf, _, ct) => _ops.SwapAsync(pdf, dlg.PageA, dlg.PageB, ct), DocumentChangeKind.PageStructure, Assemble);
    }

    private async Task ReverseAsync(IPdfDocumentSession s)
    {
        var dlg = new RangeDialog(_loc, _dialogs, await ContextAsync(s), "Pages.Reverse.Title", "Pages.Reverse.Hint", preferSelection: false);
        if (_dialogs.ShowDialog(dlg) != true) return;
        IReadOnlyList<int>? pages = dlg.IsAll ? null : dlg.Pages;
        await ExecAsync(s, "Pages.Undo.Reverse", (pdf, _, ct) => _ops.ReverseAsync(pdf, pages, ct), DocumentChangeKind.PageStructure, Assemble);
    }

    private async Task InterleaveAsync(IPdfDocumentSession s)
    {
        var dlg = new InterleaveDialog(_loc, _dialogs);
        if (_dialogs.ShowDialog(dlg) != true) return;
        var (ok, pw, _) = await SourceAsync(dlg.FilePath);
        if (!ok) return;
        await ExecAsync(s, "Pages.Undo.Interleave", (pdf, _, ct) => _ops.InterleaveAsync(pdf, dlg.FilePath, pw, dlg.ReverseSecond, ct), DocumentChangeKind.PageStructure, Assemble, "Pages.Progress.Working");
    }

    // ------------------------------------------------------------------ podział, scalanie, przekształcenia

    private async Task SplitAsync(IPdfDocumentSession s)
    {
        var dlg = new SplitDialog(_loc, _dialogs, DocFolder(s));
        if (_dialogs.ShowDialog(dlg) != true) return;
        var bytes = s.Current.GetSharedBytes();
        IReadOnlyList<string> files = Array.Empty<string>();
        if (!await _dialogs.RunWithProgressAsync(T("Pages.Split.Title"), async (p, ct) => files = await _ops.SplitAsync(bytes, dlg.Options, dlg.Folder, BaseName(s), p, ct))) return;
        _dialogs.ShowMessage(_loc.Format("Pages.Split.Done", files.Count, dlg.Folder), T("Pages.Split.Title"));
    }

    private async Task MergeAsync()
    {
        var active = _docs.ActiveDocument;
        MergeDialog.Entry? current = active is null || !active.Can(DocumentPermissions.CopyContent) ? null
            : new MergeDialog.Entry(active.FilePath ?? active.DisplayName, active.DisplayName) { Data = active.Current.GetSharedBytes() };
        var dlg = new MergeDialog(_loc, _dialogs, current);
        if (_dialogs.ShowDialog(dlg) != true) return;
        var sources = new List<MergeSource>();
        foreach (var e in dlg.Entries)
        {
            if (e.Data is not null)
            {
                int count = await Task.Run(() => PdfSharpEdit.Read(e.Data, d => d.PageCount));
                if (!TryRange(e.Range, count, out var pp)) return;
                sources.Add(new MergeSource(e.Path, null, pp, e.Data));
                continue;
            }
            var (ok, pw, n) = await SourceAsync(e.Path);
            if (!ok || !TryRange(e.Range, n, out var pages)) return;
            sources.Add(new MergeSource(e.Path, pw, pages));
        }
        byte[] result = Array.Empty<byte>();
        if (!await _dialogs.RunWithProgressAsync(T("Pages.Merge.Title"), async (p, ct) => result = await _ops.MergeAsync(sources, dlg.Options, p, ct))) return;
        await _docs.CreateNewAsync(result, T("Pages.Merge.NewName"));
    }

    private Task RotateSelectedAsync(IPdfDocumentSession s, int degrees)
    {
        var pages = SelectedPages(s);
        return ExecAsync(s, "Pages.Undo.Rotate", (pdf, _, ct) => _ops.RotateAsync(pdf, pages, degrees, ct), DocumentChangeKind.PageContent, Assemble);
    }

    private async Task RotateAsync(IPdfDocumentSession s)
    {
        var dlg = new RotateDialog(_loc, _dialogs, await ContextAsync(s));
        if (_dialogs.ShowDialog(dlg) != true) return;
        await ExecAsync(s, "Pages.Undo.Rotate", (pdf, _, ct) => _ops.RotateAsync(pdf, dlg.Pages, dlg.Degrees, ct), DocumentChangeKind.PageContent, Assemble);
    }

    private async Task CropAsync(IPdfDocumentSession s)
    {
        var dlg = new CropDialog(_loc, _dialogs, await ContextAsync(s));
        if (_dialogs.ShowDialog(dlg) != true) return;
        await ExecAsync(s, "Pages.Undo.Crop", (pdf, p, ct) => _ops.CropAsync(pdf, dlg.Pages, dlg.Options, p, ct), DocumentChangeKind.PageContent | DocumentChangeKind.PageStructure, Modify, "Pages.Crop.Title");
    }

    private async Task ResizeAsync(IPdfDocumentSession s)
    {
        var dlg = new ResizeDialog(_loc, _dialogs, await ContextAsync(s));
        if (_dialogs.ShowDialog(dlg) != true) return;
        await ExecAsync(s, "Pages.Undo.Resize", (pdf, _, ct) => _ops.ResizeAsync(pdf, dlg.Pages, dlg.Options, ct), DocumentChangeKind.All, Modify, "Pages.Resize.Title");
    }

    private async Task FlattenAsync(IPdfDocumentSession s)
    {
        var dlg = new FlattenDialog(_loc, _dialogs);
        if (_dialogs.ShowDialog(dlg) != true) return;
        await ExecAsync(s, "Pages.Undo.Flatten", (pdf, p, ct) => _ops.FlattenAsync(pdf, dlg.Options, p, ct), DocumentChangeKind.All, Modify, "Pages.Flatten.Title");
    }

    private async Task PageLabelsAsync(IPdfDocumentSession s)
    {
        var ctx = await ContextAsync(s);
        var current = await _ops.GetPageLabelsAsync(s.Current.GetSharedBytes());
        var dlg = new PageLabelsDialog(_loc, _dialogs, ctx.PageCount, current);
        if (_dialogs.ShowDialog(dlg) != true) return;
        var ranges = dlg.Ranges;
        await ExecAsync(s, "Pages.Undo.PageLabels", (pdf, _, ct) => _ops.SetPageLabelsAsync(pdf, ranges, ct), DocumentChangeKind.PageStructure, Assemble);
    }

    // ------------------------------------------------------------------ oznaczenia

    private async Task<T?> StoredAsync<T>(IPdfDocumentSession s, PageMarkKind kind) where T : class =>
        await _stamp.GetStoredOptionsAsync<T>(s.Current.GetSharedBytes(), kind);

    private async Task<bool> HasMarkAsync(IPdfDocumentSession s, PageMarkKind kind)
    {
        if ((await _stamp.DetectMarksAsync(s.Current.GetSharedBytes())).HasFlag(kind)) return true;
        _dialogs.ShowMessage(T("Pages.Marks.NoneFound"), T("Pages.Dialog.InfoTitle"));
        return false;
    }

    private async Task RemoveMarksAsync(IPdfDocumentSession s, string undoKey, Func<byte[], CancellationToken, Task<byte[]>> remove)
    {
        if (!await ExecAsync(s, undoKey, (pdf, _, ct) => remove(pdf, ct), DocumentChangeKind.PageContent, Modify))
            _dialogs.ShowMessage(T("Pages.Marks.NoneFound"), T("Pages.Dialog.InfoTitle"));
    }

    private async Task WatermarkAsync(IPdfDocumentSession s, bool update)
    {
        if (update && !await HasMarkAsync(s, PageMarkKind.Watermark)) return;
        var stored = update ? await StoredAsync<WatermarkOptions>(s, PageMarkKind.Watermark) : null;
        var dlg = new WatermarkDialog(_loc, _dialogs, await ContextAsync(s), stored, update);
        if (_dialogs.ShowDialog(dlg) != true) return;
        var o = dlg.Options;
        await ExecAsync(s, update ? "Pages.Undo.UpdateWatermark" : "Pages.Undo.Watermark", async (pdf, p, ct) =>
        {
            if (update) pdf = await _stamp.RemoveWatermarksAsync(pdf, ct);
            return await _stamp.AddWatermarkAsync(pdf, o, p, ct);
        }, DocumentChangeKind.PageContent, Modify, "Pages.Watermark.Title");
    }

    private async Task BackgroundAsync(IPdfDocumentSession s, bool update)
    {
        if (update && !await HasMarkAsync(s, PageMarkKind.Background)) return;
        var stored = update ? await StoredAsync<BackgroundOptions>(s, PageMarkKind.Background) : null;
        var dlg = new BackgroundDialog(_loc, _dialogs, await ContextAsync(s), stored, update);
        if (_dialogs.ShowDialog(dlg) != true) return;
        var o = dlg.Options;
        await ExecAsync(s, update ? "Pages.Undo.UpdateBackground" : "Pages.Undo.Background", async (pdf, p, ct) =>
        {
            if (update) pdf = await _stamp.RemoveBackgroundsAsync(pdf, ct);
            return await _stamp.AddBackgroundAsync(pdf, o, p, ct);
        }, DocumentChangeKind.PageContent, Modify, "Pages.Background.Title");
    }

    private async Task HeaderFooterAsync(IPdfDocumentSession s, bool update)
    {
        if (update && !await HasMarkAsync(s, PageMarkKind.HeaderFooter)) return;
        var stored = update ? await StoredAsync<HeaderFooterOptions>(s, PageMarkKind.HeaderFooter) : null;
        var dlg = new HeaderFooterDialog(_loc, _dialogs, await ContextAsync(s), stored, update);
        if (_dialogs.ShowDialog(dlg) != true) return;
        var file = s.DisplayName;
        var o = dlg.Options;
        // makro <<plik>> zna tylko warstwa poleceń; w opcjach zapisujemy wersję z makrem (Aktualizuj), do silnika – podstawioną
        var applied = o with
        {
            HeaderLeft = Macros.WithFile(o.HeaderLeft, file), HeaderCenter = Macros.WithFile(o.HeaderCenter, file), HeaderRight = Macros.WithFile(o.HeaderRight, file),
            FooterLeft = Macros.WithFile(o.FooterLeft, file), FooterCenter = Macros.WithFile(o.FooterCenter, file), FooterRight = Macros.WithFile(o.FooterRight, file),
        };
        await ExecAsync(s, update ? "Pages.Undo.UpdateHeaderFooter" : "Pages.Undo.HeaderFooter", async (pdf, p, ct) =>
        {
            if (update) pdf = await _stamp.RemoveHeaderFooterAsync(pdf, ct);
            return await _stamp.AddHeaderFooterAsync(pdf, applied, p, ct);
        }, DocumentChangeKind.PageContent, Modify, "Pages.HeaderFooter.Title");
    }

    private async Task PageNumbersAsync(IPdfDocumentSession s)
    {
        bool has = (await _stamp.DetectMarksAsync(s.Current.GetSharedBytes())).HasFlag(PageMarkKind.PageNumbers);
        var stored = has ? await StoredAsync<PageNumberOptions>(s, PageMarkKind.PageNumbers) : null;
        var dlg = new PageNumbersDialog(_loc, _dialogs, await ContextAsync(s), stored, has);
        if (_dialogs.ShowDialog(dlg) != true) return;
        if (dlg.RemoveRequested)
        {
            await ExecAsync(s, "Pages.Undo.RemovePageNumbers", (pdf, _, ct) => _stamp.RemovePageNumbersAsync(pdf, ct), DocumentChangeKind.PageContent, Modify);
            return;
        }
        var o = dlg.Options with { Format = Macros.WithFile(dlg.Options.Format, s.DisplayName) };
        bool replace = dlg.ReplaceExisting;
        await ExecAsync(s, "Pages.Undo.PageNumbers", async (pdf, _, ct) =>
        {
            if (replace) pdf = await _stamp.RemovePageNumbersAsync(pdf, ct);
            return await _stamp.AddPageNumbersAsync(pdf, o, ct);
        }, DocumentChangeKind.PageContent, Modify, "Pages.PageNumbers.Title");
    }

    private async Task BatesAsync(IPdfDocumentSession s)
    {
        var stored = await StoredAsync<BatesOptions>(s, PageMarkKind.Bates);
        var dlg = new BatesDialog(_loc, _dialogs, stored);
        if (_dialogs.ShowDialog(dlg) != true) return;
        long last = dlg.StartNumber - 1;
        var o = dlg.Options;
        bool ok = await ExecAsync(s, "Pages.Undo.Bates", async (pdf, _, ct) =>
        {
            var r = await _stamp.AddBatesAsync(pdf, o, dlg.StartNumber, ct);
            last = r.LastNumber;
            return r.Pdf;
        }, DocumentChangeKind.PageContent, Modify, "Pages.Bates.Title");
        if (!ok || dlg.MoreFiles.Count == 0) return;
        // kolejne pliki: hasła pytamy przed oknem postępu (na wątku UI)
        var inputs = new List<(string Path, string? Password)>();
        foreach (var f in dlg.MoreFiles)
        {
            var (fileOk, pw, _) = await SourceAsync(f);
            if (!fileOk) return;
            inputs.Add((f, pw));
        }
        var loader = new SourceLoader(_renderer);
        var written = new List<string>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await _dialogs.RunWithProgressAsync(T("Pages.Bates.Title"), async (p, ct) =>
        {
            for (int i = 0; i < inputs.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                p.Report(OperationProgress.Of(i, inputs.Count, Path.GetFileName(inputs[i].Path)));
                var bytes = await loader.LoadFileAsync(inputs[i].Path, inputs[i].Password, ct);
                var r = await _stamp.AddBatesAsync(bytes, o, last + 1, ct);
                last = r.LastNumber;
                var target = PdfIo.UniquePath(Path.Combine(dlg.OutputFolder, Path.GetFileName(inputs[i].Path)), taken);
                await Task.Run(() => PdfIo.WriteFileSafe(target, r.Pdf), ct);
                written.Add(target);
            }
        });
        _dialogs.ShowMessage(_loc.Format("Pages.Bates.Done", written.Count, dlg.OutputFolder, last), T("Pages.Bates.Title"));
    }

    // ------------------------------------------------------------------ plik, optymalizacja, szarość, oczyszczanie

    private async Task CreateBlankAsync()
    {
        var dlg = new InsertBlankDialog(_loc, _dialogs, null);
        if (_dialogs.ShowDialog(dlg) != true) return;
        var bytes = await _ops.CreateBlankDocumentAsync(dlg.Size, dlg.Count);
        await _docs.CreateNewAsync(bytes, T("Pages.Blank.NewName"));
    }

    private async Task OptimizeAsync(IPdfDocumentSession s, bool advanced)
    {
        var options = new OptimizeOptions();
        if (advanced)
        {
            var dlg = new OptimizeDialog(_loc, _dialogs, options);
            if (_dialogs.ShowDialog(dlg) != true) return;
            options = dlg.Options;
        }
        OptimizeResult? result = null;
        await ExecAsync(s, "Pages.Undo.Optimize", async (pdf, p, ct) =>
        {
            result = await _opt.OptimizeAsync(pdf, options, p, ct);
            return result.Pdf;
        }, DocumentChangeKind.All, Modify, "Pages.Optimize.Title");
        if (result is null) return;
        double pct = result.SizeBefore > 0 ? 100.0 * (result.SizeBefore - result.SizeAfter) / result.SizeBefore : 0;
        _dialogs.ShowMessage(_loc.Format("Pages.Optimize.Report", Size(result.SizeBefore), Size(result.SizeAfter), Math.Round(pct, 1), result.ImagesRecompressed), T("Pages.Optimize.Title"));
    }

    private async Task GrayscaleAsync(IPdfDocumentSession s)
    {
        var dlg = new GrayscaleDialog(_loc, _dialogs);
        if (_dialogs.ShowDialog(dlg) != true) return;
        bool raster = dlg.Rasterize;
        await ExecAsync(s, "Pages.Undo.Grayscale", (pdf, p, ct) => _opt.ConvertToGrayscaleAsync(pdf, raster, p, ct), DocumentChangeKind.All, Modify, "Pages.Gray.Title");
    }

    private async Task SanitizeAsync(IPdfDocumentSession s)
    {
        var dlg = new SanitizeDialog(_loc, _dialogs);
        if (_dialogs.ShowDialog(dlg) != true) return;
        SanitizeResult? result = null;
        await ExecAsync(s, "Pages.Undo.Sanitize", async (pdf, p, ct) =>
        {
            result = await _san.SanitizeAsync(pdf, dlg.Options, p, ct);
            return result.Pdf;
        }, DocumentChangeKind.All, Modify, "Pages.Sanitize.Title");
        if (result is null) return;
        _dialogs.ShowMessage(_loc.Format("Pages.Sanitize.Report", result.MetadataEntriesRemoved, result.CommentsRemoved, result.AttachmentsRemoved, result.ScriptsRemoved, result.LayersRemoved, result.LinksRemoved),
            T("Pages.Sanitize.Title"));
    }
}

/// <summary>Polecenie asynchroniczne niezależne od aktywnego dokumentu (Pusty dokument, Scal pliki PDF).</summary>
internal sealed class AsyncCommand : ICommand
{
    private readonly Func<Task> _run;
    private readonly IDialogService _dialogs;
    private bool _running;

    public AsyncCommand(Func<Task> run, IDialogService dialogs)
    {
        _run = run;
        _dialogs = dialogs;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => !_running;

    public async void Execute(object? parameter)
    {
        if (_running) return;
        _running = true;
        CommandManager.InvalidateRequerySuggested();
        try { await _run(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _dialogs.ShowError(ex); }
        finally
        {
            _running = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
