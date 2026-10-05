using System;
using System.IO;
using System.Linq;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// The backward-compatibility promise against a REAL file, not synthesized JSON: <c>fixtures/</c> holds
/// project files as older Cardinator versions actually wrote them, and every new build must still open
/// them completely. Inline fixtures only cover the shapes we remembered to write; a committed file catches
/// the ones we forgot. These files are evidence — never regenerate or "tidy" them.
/// </summary>
public class LegacyFixtureTests
{
    private static string Fixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", name);
        Assert.True(File.Exists(path), $"missing test fixture {path}");
        return path;
    }

    [Fact]
    public void A_1_0_EraProject_StillLoadsEveryField()
    {
        var project = CardProject.Load(Fixture("legacy-1.0-set.cardinator"));

        Assert.Equal("Legacy Demo Set", project.Name);
        Assert.Equal("Gold Multicolor", project.DefaultTemplate);
        Assert.Equal(@"C:\Users\someone\Pictures\cards", project.ArtBaseDir);
        Assert.Equal(3, project.Cards.Count);

        var aria = project.Cards[0];
        Assert.Equal("Aria Stormcaller", aria.Name);
        Assert.Equal("{2}{U}{R}", aria.ManaCost);
        Assert.Equal("Legendary Creature — Human Wizard", aria.TypeLine);
        Assert.Contains("deals 1 damage", aria.RulesText);
        Assert.Contains("\n", aria.RulesText);                  // multi-line rules survive
        Assert.Equal("The storm answers to no one.", aria.FlavorText);
        Assert.Equal("3", aria.Power);
        Assert.Equal("2", aria.Toughness);
        Assert.Equal(@"art\aria.png", aria.ArtPath);            // relative art path kept as written
        Assert.Equal(1.2, aria.ArtScale);
        Assert.Equal(-0.05, aria.ArtOffsetX);
        Assert.Equal(0.1, aria.ArtOffsetY);
        Assert.Equal("DEMO", aria.SetCode);
        Assert.Equal("001/003", aria.CollectorNumber);
        Assert.Equal("mythic", aria.Rarity);
        Assert.Equal("A. Painter", aria.Artist);
        Assert.Equal("™ & © 2024 Demo", aria.Copyright);

        // Fields that did not exist back then default cleanly rather than breaking the load.
        Assert.False(aria.IsDoubleFaced);
        Assert.Equal("", aria.DfcStyle);
        Assert.Equal("", aria.Subtitle);
        Assert.NotNull(project.Profile);
        Assert.Equal(0, project.FormatVersion);                 // pre-versioning
        Assert.False(project.IsFromNewerVersion);
    }

    [Fact]
    public void A_1_0_EraProject_SurvivesAResaveWithoutLosingAnything()
    {
        var original = CardProject.Load(Fixture("legacy-1.0-set.cardinator"));
        var path = Path.Combine(Path.GetTempPath(), "resaved_" + Guid.NewGuid().ToString("N") + ".cardinator");
        try
        {
            original.Save(path);
            var reloaded = CardProject.Load(path);

            Assert.Equal(CardProject.CurrentFormatVersion, reloaded.FormatVersion);   // stamped on save
            Assert.Equal(original.Cards.Count, reloaded.Cards.Count);
            foreach (var (a, b) in original.Cards.Zip(reloaded.Cards))
            {
                Assert.Equal(a.Name, b.Name);
                Assert.Equal(a.RulesText, b.RulesText);
                Assert.Equal(a.ArtPath, b.ArtPath);
                Assert.Equal(a.ArtScale, b.ArtScale);
                Assert.Equal(a.Copyright, b.Copyright);
                Assert.Equal(a.CollectorNumber, b.CollectorNumber);
            }
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void A_1_0_EraProject_RendersWithoutItsOriginalTemplate()
    {
        // "Slate Artifact" isn't an installed frame name any more; a missing frame must degrade to the
        // fallback rather than throw, or an old set would be unopenable.
        TestHelpers.RunSta(() =>
        {
            var project = CardProject.Load(Fixture("legacy-1.0-set.cardinator"));
            var templates = new TemplateService().LoadAll();
            var fallback = templates[0];
            var renderer = new CardRenderer(new SymbolService());

            foreach (var card in project.Cards)
            {
                var tpl = templates.FirstOrDefault(t => t.Name == card.TemplateName) ?? fallback;
                var bmp = renderer.RenderToBitmap(card, tpl, supersample: 1);
                Assert.True(TestHelpers.HasContent(bmp), $"{card.Name} rendered blank");
            }
        });
    }
}
