using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Annotations.UI.Panels;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Services;
using ZetPDF.TestData;

namespace ZetPDF.Annotations.Tests;

/// <summary>Zapis załącznika: oczyszczanie niezaufanej nazwy z PDF i ostrzeżenie o typach wykonywalnych.</summary>
public class AttachmentSaveGuardTests
{
    private static readonly IFileService Files = new FileService(new AppPaths(TestPaths.NewDirectory()));

    private sealed class Dialogs : IDialogService
    {
        public string? Suggested { get; private set; }
        public string? Result { get; set; }
        public bool Answer { get; set; } = true;
        public List<(string Text, MessageSeverity Severity)> Confirmations { get; } = new();
        public string? SaveFile(string title, IReadOnlyList<FileFilter> filters, string? defaultFileName = null, string? initialDirectory = null)
        {
            Suggested = defaultFileName;
            return Result ?? (defaultFileName is null ? null : Path.Combine(@"C:\wybrany", defaultFileName));
        }
        public bool Confirm(string text, string? title = null, MessageSeverity severity = MessageSeverity.Warning)
        {
            Confirmations.Add((text, severity));
            return Answer;
        }
        public IReadOnlyList<string>? OpenFiles(string title, IReadOnlyList<FileFilter> filters, bool multiselect = false, string? initialDirectory = null) => null;
        public string? SelectFolder(string title, string? initialDirectory = null) => null;
        public void ShowMessage(string text, string? title = null, MessageSeverity severity = MessageSeverity.Info) { }
        public YesNoCancel AskYesNoCancel(string text, string? title = null) => YesNoCancel.Cancel;
        public string? AskPassword(string documentName, bool isRetry) => null;
        public Task<bool> RunWithProgressAsync(string title, Func<IProgress<OperationProgress>, CancellationToken, Task> work, bool cancellable = true) => Task.FromResult(true);
        public bool? ShowDialog(Window dialog) => false;
        public void ShowModeless(Window window) { }
        public void ShowError(Exception exception, string? contextText = null) { }
    }

    private static ILocalizationService Loc()
    {
        var s = new ServiceCollection();
        s.AddZetPdfCore(new AppPaths(TestPaths.NewDirectory()));
        var loc = s.BuildServiceProvider().GetRequiredService<ILocalizationService>();
        loc.RegisterResources(AnnotationsModule.Strings);
        return loc;
    }

    [Theory]
    [InlineData("umowa.exe")]
    [InlineData("UMOWA.EXE")]
    [InlineData("faktura.pdf.exe")]
    [InlineData("a.bat")]
    [InlineData("a.cmd")]
    [InlineData("a.com")]
    [InlineData("a.scr")]
    [InlineData("a.ps1")]
    [InlineData("a.vbs")]
    [InlineData("a.js")]
    [InlineData("a.jse")]
    [InlineData("a.wsf")]
    [InlineData("a.msi")]
    [InlineData("a.dll")]
    [InlineData("a.lnk")]
    [InlineData("a.hta")]
    [InlineData("a.jar")]
    [InlineData("a.pif")]
    [InlineData("a.reg")]
    [InlineData("a.exe.")]
    [InlineData("a.exe . .")]
    [InlineData(@"C:\folder\a.exe")]
    public void Executable_types_are_detected(string name) => Assert.True(AttachmentSaveGuard.IsExecutable(name));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("umowa.pdf")]
    [InlineData("zdjęcie.jpg")]
    [InlineData("bez rozszerzenia")]
    [InlineData("exe")]
    [InlineData("a.exe.txt")]
    [InlineData("arkusz.xlsx")]
    public void Other_types_are_not_flagged(string? name) => Assert.False(AttachmentSaveGuard.IsExecutable(name));

    [Theory]
    [InlineData(@"..\..\Windows\evil.pdf", "evil.pdf")]
    [InlineData("../../etc/passwd.txt", "passwd.txt")]
    [InlineData(@"C:\Users\Public\a.pdf", "a.pdf")]
    [InlineData(@"\\serwer\udzial\a.pdf", "a.pdf")]
    [InlineData(@"..\", "_")]
    [InlineData("..", "_")]
    [InlineData(".", "_")]
    [InlineData("", "_")]
    [InlineData("   ", "_")]
    [InlineData("a<b>c|d?e*.pdf", "a_b_c_d_e_.pdf")]
    [InlineData("zażółć gęślą.pdf", "zażółć gęślą.pdf")]
    [InlineData("kropka.pdf...", "kropka.pdf")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("COM1.pdf", "_COM1.pdf")]
    [InlineData("CONSOLE.pdf", "CONSOLE.pdf")]
    public void Suggested_name_is_a_plain_safe_file_name(string raw, string expected)
    {
        var name = AttachmentSaveGuard.SuggestFileName(raw, Files);
        Assert.Equal(expected, name);
        Assert.Equal(name, Path.GetFileName(name));
        Assert.True(name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);
        Assert.Equal(Path.Combine(@"C:\wybrany", name), Path.GetFullPath(Path.Combine(@"C:\wybrany", name)));
    }

    [Fact]
    public void Control_and_direction_override_characters_cannot_hide_the_extension()
    {
        var spoofed = "faktura" + (char)0x202E + "fdp.exe";
        var name = AttachmentSaveGuard.SuggestFileName(spoofed, Files);
        Assert.Equal("fakturafdp.exe", name);
        Assert.True(AttachmentSaveGuard.IsExecutable(name));
        Assert.DoesNotContain((char)1, AttachmentSaveGuard.SuggestFileName("a" + (char)1 + ".pdf", Files));
    }

    [Fact]
    public void Very_long_name_keeps_its_extension()
    {
        var name = AttachmentSaveGuard.SuggestFileName(new string('a', 5000) + ".exe", Files);
        Assert.Equal(200, name.Length);
        Assert.EndsWith(".exe", name, StringComparison.Ordinal);
    }

    [Fact]
    public void Plain_attachment_is_saved_without_warning()
    {
        var d = new Dialogs();
        var target = AttachmentSaveGuard.ChooseTarget(@"..\umowa.pdf", d, Files, Loc());
        Assert.Equal("umowa.pdf", d.Suggested);
        Assert.Equal(@"C:\wybrany\umowa.pdf", target);
        Assert.Empty(d.Confirmations);
    }

    [Fact]
    public void Executable_attachment_warns_and_can_be_declined_or_accepted()
    {
        var d = new Dialogs { Answer = false };
        Assert.Null(AttachmentSaveGuard.ChooseTarget(@"..\..\program.exe", d, Files, Loc()));
        var (text, severity) = Assert.Single(d.Confirmations);
        Assert.Equal(MessageSeverity.Warning, severity);
        Assert.Contains("program.exe", text);
        Assert.Contains(".exe", text);
        Assert.DoesNotContain("..", text);

        d.Answer = true;
        Assert.Equal(@"C:\wybrany\program.exe", AttachmentSaveGuard.ChooseTarget("program.exe", d, Files, Loc()));
    }

    [Fact]
    public void Extension_typed_in_the_save_dialog_is_checked_in_both_directions()
    {
        var typedExe = new Dialogs { Result = @"C:\wybrany\zmieniony.exe" };
        Assert.Equal(typedExe.Result, AttachmentSaveGuard.ChooseTarget("dokument.txt", typedExe, Files, Loc()));
        Assert.Single(typedExe.Confirmations);

        var renamed = new Dialogs { Result = @"C:\wybrany\niewinny.txt", Answer = false };
        Assert.Null(AttachmentSaveGuard.ChooseTarget("program.exe", renamed, Files, Loc()));
        Assert.Single(renamed.Confirmations);
    }

    [Fact]
    public void Cancelled_save_dialog_does_not_warn()
    {
        var d = new CancelDialogs();
        Assert.Null(AttachmentSaveGuard.ChooseTarget("program.exe", d, Files, Loc()));
        Assert.Equal(0, d.Confirms);
    }

    private sealed class CancelDialogs : IDialogService
    {
        public int Confirms { get; private set; }
        public string? SaveFile(string title, IReadOnlyList<FileFilter> filters, string? defaultFileName = null, string? initialDirectory = null) => null;
        public bool Confirm(string text, string? title = null, MessageSeverity severity = MessageSeverity.Warning) { Confirms++; return true; }
        public IReadOnlyList<string>? OpenFiles(string title, IReadOnlyList<FileFilter> filters, bool multiselect = false, string? initialDirectory = null) => null;
        public string? SelectFolder(string title, string? initialDirectory = null) => null;
        public void ShowMessage(string text, string? title = null, MessageSeverity severity = MessageSeverity.Info) { }
        public YesNoCancel AskYesNoCancel(string text, string? title = null) => YesNoCancel.Cancel;
        public string? AskPassword(string documentName, bool isRetry) => null;
        public Task<bool> RunWithProgressAsync(string title, Func<IProgress<OperationProgress>, CancellationToken, Task> work, bool cancellable = true) => Task.FromResult(true);
        public bool? ShowDialog(Window dialog) => false;
        public void ShowModeless(Window window) { }
        public void ShowError(Exception exception, string? contextText = null) { }
    }
}
