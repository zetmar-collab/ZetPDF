using System.Diagnostics;
using System.Runtime.InteropServices;
using ZetPDF.Core.Localization;
using ZetPDF.Editing.Interop;

namespace ZetPDF.Editing.Conversion;

/// <summary>Dokumenty pakietu Office → PDF; w testach atrapa.</summary>
public interface IOfficeConverter
{
    /// <summary>Czy zainstalowany Office obsłuży to rozszerzenie (np. ".docx").</summary>
    bool IsAvailableFor(string extension);
    Task ConvertToPdfAsync(string source, string outputPdf, CancellationToken ct);
}

public static class OfficeFormats
{
    public static bool IsOffice(string extension) => extension.ToLowerInvariant() is ".docx" or ".xlsx" or ".pptx";

    /// <summary>ProgID aplikacji dla rozszerzenia, null dla innych plików.</summary>
    public static string? ProgId(string extension) => extension.ToLowerInvariant() switch
    {
        ".docx" => "Word.Application",
        ".xlsx" => "Excel.Application",
        ".pptx" => "PowerPoint.Application",
        _ => null,
    };
}

/// <summary>
/// Automatyzacja COM zainstalowanego Office (ExportAsFixedFormat), typy dynamiczne, bez pakietów PIA. Aplikacja niewidoczna, makra wyłączone
/// (msoAutomationSecurityForceDisable), dokument tylko do odczytu, wątek STA poza UI, limit czasu. Proces jest zamykany w finally, a po limicie
/// kończony po numerze; dotyczy to tylko procesu uruchomionego tutaj, nigdy Office otwartego przez użytkownika.
/// </summary>
public sealed class OfficeComConverter : IOfficeConverter
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(3);

    private const int ForceDisableMacros = 3;      // msoAutomationSecurityForceDisable
    private const int WordAlertsNone = 0;          // wdAlertsNone
    private const int WordDoNotSave = 0;           // wdDoNotSaveChanges
    private const int WordFormatPdf = 17;          // wdExportFormatPDF
    private const int ExcelTypePdf = 0;            // xlTypePDF
    private const int PowerPointAlertsNone = 1;    // ppAlertsNone
    private const int PowerPointFormatPdf = 2;     // ppFixedFormatTypePDF
    private const int MsoTrue = -1, MsoFalse = 0;
    /// <summary>Hasło fikcyjne: plik chroniony hasłem kończy się błędem, a nie niewidocznym oknem pytania o hasło.</summary>
    private const string NoPassword = "zetpdf-no-password";

    private readonly TimeSpan _timeout;

    public OfficeComConverter() : this(DefaultTimeout) { }

    public OfficeComConverter(TimeSpan timeout) => _timeout = timeout;

    public bool IsAvailableFor(string extension) => OfficeFormats.ProgId(extension) is { } id && Type.GetTypeFromProgID(id) is not null;

    public async Task ConvertToPdfAsync(string source, string outputPdf, CancellationToken ct)
    {
        var extension = Path.GetExtension(source).ToLowerInvariant();
        var type = OfficeFormats.ProgId(extension) is { } id ? Type.GetTypeFromProgID(id) : null;
        if (type is null) throw new InvalidOperationException("Office application not found.").WithMessageKey("Edit.Office.Required", Path.GetFileName(source));
        var session = new OfficeSession(ProcessName(extension));
        var fullSource = Path.GetFullPath(source);
        var fullOutput = Path.GetFullPath(outputPdf);
        try
        {
            await StaThread.RunAsync<bool>(_ =>
            {
                session.Run(type, app =>
                {
                    switch (extension)
                    {
                        case ".docx": ConvertWord(app, fullSource, fullOutput); break;
                        case ".xlsx": ConvertExcel(app, fullSource, fullOutput); break;
                        default: ConvertPowerPoint(app, fullSource, fullOutput); break;
                    }
                });
                return true;
            }, _timeout, session.KillStarted, "Edit.Office.Timeout", ct).ConfigureAwait(false);
        }
        catch (COMException ex)
        {
            throw new InvalidOperationException(ex.Message, ex).WithMessageKey("Edit.Office.ConvertFailed", Path.GetFileName(source));
        }
        if (!File.Exists(fullOutput)) throw new InvalidOperationException("No output.").WithMessageKey("Edit.Office.ConvertFailed", Path.GetFileName(source));
    }

    private static string ProcessName(string extension) => extension switch { ".docx" => "WINWORD", ".xlsx" => "EXCEL", _ => "POWERPNT" };

    private static void ConvertWord(dynamic word, string source, string output)
    {
        word.Visible = false;
        word.DisplayAlerts = WordAlertsNone;
        word.AutomationSecurity = ForceDisableMacros;
        dynamic documents = word.Documents;
        dynamic? document = null;
        try
        {
            document = documents.Open(FileName: source, ConfirmConversions: false, ReadOnly: true, AddToRecentFiles: false, PasswordDocument: NoPassword, Visible: false, NoEncodingDialog: true);
            document.ExportAsFixedFormat(OutputFileName: output, ExportFormat: WordFormatPdf, OpenAfterExport: false);
        }
        finally
        {
            if (document is not null) { document.Close(SaveChanges: WordDoNotSave); Release(document); }
            Release(documents);
        }
    }

    private static void ConvertExcel(dynamic excel, string source, string output)
    {
        excel.Visible = false;
        excel.DisplayAlerts = false;
        excel.AskToUpdateLinks = false;
        excel.EnableEvents = false;
        excel.AutomationSecurity = ForceDisableMacros;
        dynamic workbooks = excel.Workbooks;
        dynamic? workbook = null;
        try
        {
            workbook = workbooks.Open(Filename: source, UpdateLinks: 0, ReadOnly: true, Password: NoPassword, IgnoreReadOnlyRecommended: true, Notify: false, AddToMru: false);
            workbook.ExportAsFixedFormat(Type: ExcelTypePdf, Filename: output, OpenAfterPublish: false);
        }
        finally
        {
            if (workbook is not null) { workbook.Close(SaveChanges: false); Release(workbook); }
            Release(workbooks);
        }
    }

    private static void ConvertPowerPoint(dynamic powerPoint, string source, string output)
    {
        powerPoint.DisplayAlerts = PowerPointAlertsNone;
        powerPoint.AutomationSecurity = ForceDisableMacros;
        dynamic presentations = powerPoint.Presentations;
        dynamic? presentation = null;
        try
        {
            presentation = presentations.Open(FileName: source, ReadOnly: MsoTrue, Untitled: MsoFalse, WithWindow: MsoFalse);
            presentation.ExportAsFixedFormat(Path: output, FixedFormatType: PowerPointFormatPdf);
        }
        finally
        {
            if (presentation is not null) { presentation.Close(); Release(presentation); }
            Release(presentations);
        }
    }

    private static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com)) Marshal.FinalReleaseComObject(com);
    }

    /// <summary>Uruchomienie aplikacji i zamknięcie w finally. Proces „nasz” to ten, którego nie było przed utworzeniem obiektu COM.</summary>
    private sealed class OfficeSession
    {
        private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(10);
        private readonly string _processName;
        private readonly object _gate = new();
        private readonly List<int> _started = new();

        public OfficeSession(string processName) => _processName = processName;

        public void Run(Type type, Action<dynamic> work)
        {
            var before = Ids();
            dynamic? app = null;
            try
            {
                app = Activator.CreateInstance(type)!;
                var mine = Ids().Except(before).ToList();
                lock (_gate) _started.AddRange(mine);
                work(app);
            }
            finally
            {
                // PowerPoint ma jedną instancję: gdy użytkownik już go używa, obiekt COM wskazuje jego program, więc Quit zamknąłby jego pracę.
                // Word i Excel przez COM zawsze dostają nowy proces, więc Quit dotyczy tylko naszej aplikacji, nawet gdy jej numeru nie udało się ustalić.
                bool shared = _processName == "POWERPNT" && before.Count > 0;
                if (app is not null)
                {
                    try { if (!shared) app.Quit(); }
                    catch (COMException) { } // aplikacja mogła już paść; kończy ją KillStarted niżej
                    Release(app);
                }
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                WaitOrKill();
            }
        }

        private void WaitOrKill()
        {
            int[] ids;
            lock (_gate) ids = _started.ToArray();
            var deadline = DateTime.UtcNow + ExitWait;
            foreach (var id in ids)
            {
                try
                {
                    using var p = Process.GetProcessById(id);
                    var left = deadline - DateTime.UtcNow;
                    if (left > TimeSpan.Zero && p.WaitForExit(left)) continue;
                    p.Kill();
                    p.WaitForExit(2000);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }

        /// <summary>Po limicie czasu: wątek wisi na wywołaniu COM, więc kończymy tylko proces uruchomiony tutaj. Wywołanie wtedy się przerywa.</summary>
        public void KillStarted()
        {
            int[] ids;
            lock (_gate) ids = _started.ToArray();
            foreach (var id in ids)
            {
                try
                {
                    using var p = Process.GetProcessById(id);
                    p.Kill();
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }

        private HashSet<int> Ids()
        {
            var processes = Process.GetProcessesByName(_processName);
            try { return processes.Select(p => p.Id).ToHashSet(); }
            finally { foreach (var p in processes) p.Dispose(); }
        }
    }
}
