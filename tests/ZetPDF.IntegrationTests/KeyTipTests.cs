using System.Globalization;
using System.Windows.Controls;
using Fluent;
using ZetPDF.App.Shell;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.IntegrationTests.Infrastructure;

namespace ZetPDF.IntegrationTests;

/// <summary>KeyTips wstążki (Alt + litera): każda karta i każdy przycisk karty ma literę, bez powtórzeń, w obu językach.</summary>
public class KeyTipTests
{
    [Fact]
    public void Assigner_uses_first_free_letter_without_diacritics_then_pairs_then_digits()
    {
        var keys = KeyTipAssigner.Assign(new[] { "Początek", "Plik", "Organizuj", "Ochrona", "Łącza", "O", "OO" }, new[] { "Q" });
        Assert.Equal(new[] { "P", "L", "O", "C", "A", "OC", "1" }.Take(4), keys.Take(4));
        Assert.Equal("A", keys[4]); // Ł → L zajęte, więc A
        Assert.Equal(keys.Length, keys.Distinct().Count());
        Assert.DoesNotContain("Q", KeyTipAssigner.Assign(new[] { "Q" }, new[] { "Q" }));
    }

    [Fact]
    public void Every_tab_and_tab_button_has_unique_key_tip_in_both_languages() => Sta.Run(() =>
    {
        using var host = new TestHost(initializeModules: true);
        var loc = host.Get<ILocalizationService>();
        loc.RegisterResources(new System.Resources.ResourceManager("ZetPDF.App.Resources.Strings", typeof(ZetPDF.App.ModuleCatalog).Assembly));
        var builder = new RibbonBuilder((CommandRegistry)host.Get<ICommandRegistry>(), loc);
        builder.Build(new Grid(), null);
        foreach (var culture in new[] { new CultureInfo("pl"), new CultureInfo("en") })
        {
            loc.SetCulture(culture);
            var tabKeys = builder.TabsById.Values.Select(t => KeyTip.GetKeys(t)).Append(KeyTip.GetKeys(builder.Backstage)).ToList();
            Assert.All(tabKeys, k => Assert.False(string.IsNullOrEmpty(k)));
            Assert.Equal(tabKeys.Count, tabKeys.Distinct().Count());
            Assert.DoesNotContain("Q", tabKeys);
            foreach (var tab in builder.TabsById.Values)
            {
                var keys = tab.Groups.SelectMany(g => g.Items.OfType<Control>()).Where(builder.IdsByElement.ContainsKey).Select(KeyTip.GetKeys).ToList();
                Assert.All(keys, k => Assert.False(string.IsNullOrEmpty(k)));
                Assert.Equal(keys.Count, keys.Distinct().Count());
            }
        }
        return Task.CompletedTask;
    });
}
