using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Editing.Ui;

/// <summary>Ustawienia modułu (sekcja "Editing" w settings.json).</summary>
public sealed class EditingSettings
{
    public string FontFamily { get; set; } = "Arial";
    public double FontSize { get; set; } = 12;
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public uint TextColor { get; set; } = 0xFF000000;
    public uint ShapeStroke { get; set; } = 0xFFD01E1E;
    public uint ShapeFill { get; set; }
    public bool ShapeFilled { get; set; }
    public double ShapeLineWidth { get; set; } = 1.5;
    public int ExportDpi { get; set; } = 150;
    public string OcrLanguages { get; set; } = "pol+eng";
    public bool OcrReviewSuspects { get; set; } = true;

    public static PdfColor Color(uint argb) => new((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
    public static uint Pack(PdfColor c) => ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
}

/// <summary>Dostęp modułu do usług powłoki i wspólne operacje UI (wykonanie polecenia z postępem, aktywny widok).</summary>
internal sealed class EditingContext
{
    public const string SettingsKey = "Editing";

    public EditingContext(IServiceProvider services)
    {
        Services = services;
        Documents = services.GetRequiredService<IDocumentManager>();
        Loc = services.GetRequiredService<ILocalizationService>();
    }

    public IServiceProvider Services { get; }
    public IDocumentManager Documents { get; }
    public ILocalizationService Loc { get; }
    public IDialogService? Dialogs => Services.GetService<IDialogService>();
    public IViewerService? Viewer => Services.GetService<IViewerService>();
    public IToolService? Tools => Services.GetService<IToolService>();
    public IShellService? Shell => Services.GetService<IShellService>();
    public ISettingsService? SettingsService => Services.GetService<ISettingsService>();
    public IFileService? Files => Services.GetService<IFileService>();
    public IPdfRenderer Renderer => Services.GetRequiredService<IPdfRenderer>();

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    public EditingSettings Settings => SettingsService?.GetModuleSettings<EditingSettings>(SettingsKey) ?? new EditingSettings();

    public void SaveSettings(Action<EditingSettings> change)
    {
        var s = Settings;
        change(s);
        SettingsService?.SetModuleSettings(SettingsKey, s);
    }

    public IDocumentView? View => Viewer?.ActiveView;

    public bool? ShowDialog(Window w)
    {
        if (Dialogs is { } d) return d.ShowDialog(w);
        w.Owner = Application.Current?.MainWindow;
        return w.ShowDialog();
    }

    public void Message(string key, params object?[] args) =>
        Dialogs?.ShowMessage(args.Length == 0 ? Loc[key] : Loc.Format(key, args), Loc["Edit.ModuleName"]);

    public void Status(string text) => Shell?.SetStatus(text);

    /// <summary>Wykonuje polecenie dokumentu (Cofnij/Ponów) w oknie postępu. Zwraca false przy anulowaniu lub braku zmian.</summary>
    public async Task<bool> ExecuteAsync(IPdfDocumentSession session, string descriptionKey,
        Func<byte[], IProgress<OperationProgress>?, CancellationToken, Task<byte[]>> transform,
        DocumentChangeKind kind, DocumentPermissions permission = DocumentPermissions.ModifyContent, string? progressTitleKey = null, IReadOnlyList<int>? pages = null)
    {
        var cmd = new DelegateDocumentCommand(descriptionKey, transform, kind, permission, pages);
        bool changed = false;
        if (Dialogs is { } d)
        {
            var ok = await d.RunWithProgressAsync(Loc[progressTitleKey ?? descriptionKey], async (p, ct) => changed = await session.ExecuteAsync(cmd, p, ct));
            return ok && changed;
        }
        return await session.ExecuteAsync(cmd);
    }

    /// <summary>Praca w tle z postępem (bez zmiany dokumentu). Zwraca false przy anulowaniu.</summary>
    public Task<bool> RunAsync(string titleKey, Func<IProgress<OperationProgress>, CancellationToken, Task> work) =>
        Dialogs?.RunWithProgressAsync(Loc[titleKey], work) ?? RunDirect(work);

    private static async Task<bool> RunDirect(Func<IProgress<OperationProgress>, CancellationToken, Task> work)
    {
        await work(new Progress<OperationProgress>(), CancellationToken.None);
        return true;
    }
}
