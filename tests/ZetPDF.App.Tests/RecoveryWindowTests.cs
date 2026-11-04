using System.Globalization;
using System.Resources;
using System.Threading;
using System.Windows.Controls;
using ZetPDF.App.Dialogs;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Core.Localization;
using ZetPDF.Core.Services;
using ZetPDF.TestData;

namespace ZetPDF.App.Tests;

public class RecoveryWindowTests
{
    private static LocalizationService Loc()
    {
        var loc = new LocalizationService(null, new CultureInfo("pl"));
        loc.RegisterResources(new ResourceManager("ZetPDF.App.Resources.Strings", typeof(ZetPDF.App.App).Assembly));
        return loc;
    }

    private static void RunSta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() => { try { body(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
    }

    private static IEnumerable<T> Find<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
    {
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root).OfType<System.Windows.DependencyObject>())
        {
            if (child is T t) yield return t;
            foreach (var n in Find<T>(child)) yield return n;
        }
    }

    [Fact]
    public void Window_lists_entries_and_buttons_remove_rows() => RunSta(() =>
    {
        var discarded = new List<string>();
        var entries = new[]
        {
            new RecoveryInfo { Id = "1", DisplayName = "zażółć.pdf", OriginalPath = @"C:\a\zażółć.pdf", SavedUtc = DateTime.UtcNow, IsProtected = true },
            new RecoveryInfo { Id = "2", DisplayName = "nowy.pdf", SavedUtc = DateTime.UtcNow, State = RecoveryDocumentState.NeverSaved },
        };
        var loc = Loc();
        var w = new RecoveryWindow(loc, entries, _ => Task.FromResult(true), e => { discarded.Add(e.Id); return true; });
        Assert.Equal(2, w.RemainingCount);
        var texts = Find<TextBlock>(w).Select(t => t.Text).ToList();
        Assert.Contains("zażółć.pdf", texts);
        Assert.Contains(@"C:\a\zażółć.pdf", texts);
        Assert.Contains(loc["Shell.Recovery.Protected"], texts);

        var discard = Find<Button>(w).First(b => (string)b.Content == loc["Shell.Recovery.Discard"]);
        discard.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Assert.Equal(new[] { "1" }, discarded);
        Assert.Equal(1, w.RemainingCount);
        w.Close();
    });

    [Fact]
    public void Settings_model_validates_and_saves_autosave_interval() => RunSta(() =>
    {
        var home = TestPaths.NewDirectory();
        var settings = new SettingsService(new AppPaths(home));
        var vm = new SettingsViewModel(settings, Loc(), new NoTheme());
        Assert.True(vm.AutoSaveEnabled);
        Assert.Equal("5", vm.AutoSaveMinutes);

        vm.AutoSaveMinutes = "0";
        Assert.NotNull(vm.AutoSaveError);
        Assert.False(vm.IsValid);
        vm.AutoSaveEnabled = false;
        Assert.Null(vm.AutoSaveError); // wyłączony autozapis nie blokuje okna
        vm.AutoSaveEnabled = true;
        vm.AutoSaveMinutes = "12";
        Assert.True(vm.Apply());
        Assert.Equal(12, settings.Current.AutoSaveMinutes);

        vm.AutoSaveEnabled = false;
        vm.AutoSaveMinutes = "abc";
        Assert.True(vm.Apply());
        Assert.False(settings.Current.AutoSaveEnabled);
        Assert.Equal(12, settings.Current.AutoSaveMinutes);
        vm.Detach();
    });

    private sealed class NoTheme : IThemeService
    {
        public AppTheme Theme => AppTheme.Light;
        public AppTheme EffectiveTheme => AppTheme.Light;
        public void SetTheme(AppTheme theme) { }
        public event EventHandler? ThemeChanged { add { } remove { } }
    }
}
