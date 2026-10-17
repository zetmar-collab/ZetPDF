using System.Globalization;
using System.Reflection;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Localization;
using ZetPDF.Security.Actions;
using ZetPDF.Security.UI;

namespace ZetPDF.Security.Tests;

public class ModuleSmokeTests
{
    [Fact]
    public void Module_registers_its_contracts()
    {
        var services = new ServiceCollection();
        new SecurityModule().ConfigureServices(services);
        Assert.Contains(services, d => d.ServiceType == typeof(ISecurityService));
        Assert.Contains(services, d => d.ServiceType == typeof(ISignatureService));
        Assert.Contains(services, d => d.ServiceType == typeof(ICompareService));
        Assert.Contains(services, d => d.ServiceType == typeof(IActionWizard));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pl")]
    public void Dynamic_resource_keys_exist(string lang)
    {
        var c = new CultureInfo(lang);
        string Get(string k) => SecurityModule.Strings.GetString(k, c) ?? throw new Xunit.Sdk.XunitException("missing " + k);
        foreach (var m in Enum.GetValues<PdfEncryptionMethod>()) Get("Security.Method." + m);
        foreach (var s in Enum.GetValues<SignatureStatus>()) Get(SignaturesPanel.StatusKey(s));
        foreach (var p in Enum.GetValues<DocumentPermissions>()) Get("Security.Perm." + p);
        foreach (var v in Enum.GetNames<HorizontalPosition>().Concat(Enum.GetNames<VerticalPosition>())) Get("Security.Enum." + v);
        foreach (var t in ActionStepTypes.OptionTypes) { Get("Security.Action.Step." + t.Key); CheckParams(t.Value, Get); }
    }

    private static void CheckParams(Type type, Func<string, string> get)
    {
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanWrite || p.Name is "UserPassword" or "OwnerPassword" or "Pages" or "Region" or "ImagePath") continue;
            get("Security.Param." + p.Name);
            if (p.PropertyType == typeof(TextStyle)) CheckParams(typeof(TextStyle), get);
        }
    }

    [Fact]
    public void Dialogs_and_editors_can_be_built()
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                var loc = new LocalizationService(null, new CultureInfo("pl"));
                loc.RegisterResources(SecurityModule.Strings);
                var protect = new PasswordProtectDialog(loc, new PdfEncryptionSettings("a", "b", PdfEncryptionMethod.Aes128, DocumentPermissions.Print));
                Assert.NotNull(protect.Content);
                foreach (var type in ActionStepTypes.OptionTypes.Values)
                {
                    var editor = OptionsEditor.Build(Activator.CreateInstance(type)!, loc, () => { });
                    Assert.True(((Grid)editor).Children.Count > 0, type.Name);
                }
                // edycja przez edytor zmienia rekord
                var wm = new WatermarkOptions();
                var grid = (Grid)OptionsEditor.Build(wm, loc, () => { });
                var textBox = grid.Children.OfType<TextBox>().First();
                textBox.Text = "KOPIA";
                Assert.Equal("KOPIA", wm.Text);
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
    }
}
