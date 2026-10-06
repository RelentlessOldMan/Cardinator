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
        Assert.Equal("", aria.Defense);                         // 1.5.0 fields default cleanly...
        Assert.Equal("", aria.Orientation);
        Assert.False(aria.WantsLandscape);                      // ...so an old creature stays upright
        Assert.Equal("", aria.HalfLayout);                      // 1.6.0: not a flip card
        Assert.Null(aria.OtherHalf);
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

    // ── A 1.6.8 set with every layout ────────────────────────────────────────────────────────────────────
    // Saved by 1.6.8's own ProjectWriter (a throwaway test in a worktree of commit 8b44477): the QA harness's
    // cards (each card's Subtitle names the layout it exercises), a double-faced card and a meld pair, with
    // its art copied into art\ the way a real save does. Split cards here predate per-half frames (1.6.9).

    private const string Layouts168 = "layouts-1.6.8/layouts-1.6.8.cardinator";

    [Fact]
    public void A_1_6_8_Set_LoadsEveryLayout()
    {
        var path = Fixture(Layouts168);
        var project = CardProject.Load(path);
        var cards = project.Cards;

        Assert.Equal("Layouts 1.6.8", project.Name);
        Assert.Equal("Crimson Red", project.DefaultTemplate);
        Assert.Equal(1, project.FormatVersion);
        Assert.False(project.IsFromNewerVersion);
        Assert.Equal("QAF", project.Profile.SetCode);
        Assert.Equal("™ & © 2026 Fixture", project.Profile.Copyright);
        Assert.Equal(29, cards.Count);
        Assert.Equal("01/29", cards[0].CollectorNumber);
        Assert.Equal(1.35, cards[0].ArtScale);

        CardModel Named(string n) => cards.Single(c => c.Name == n);

        // Every card's art came along in the set folder.
        var folder = Path.GetDirectoryName(path)!;
        foreach (var c in cards)
        {
            Assert.StartsWith(@"art\", c.ArtPath);
            Assert.True(File.Exists(Path.Combine(folder, c.ArtPath)), $"{c.Name}: art {c.ArtPath} missing");
        }

        Assert.Contains("LEVEL 2-6", Named("QA Leveler").RulesText);
        Assert.Contains("12+ | Flying", Named("QA Spacecraft").RulesText);
        Assert.Contains("To solve —", Named("QA Case").RulesText);
        Assert.True(Named("QA Giant").IsAdventure);
        Assert.Equal("QA Stomp", Named("QA Giant").AdventureName);
        Assert.Equal("5", Named("QA Siege").Defense);

        foreach (var n in new[] { "QA Apprentice", "QA Initiate", "QA Steelhand" })
        {
            Assert.True(Named(n).IsFlip, n);
            Assert.Equal(n + ", Ascended", Named(n).OtherHalf!.Name);
        }
        foreach (var n in new[] { "QA Wear", "QA Pool", "QA Fire" })
        {
            Assert.True(Named(n).IsSplit && !Named(n).IsAftermath, n);
            Assert.Equal(n + " Too", Named(n).OtherHalf!.Name);
        }
        foreach (var n in new[] { "QA Destined", "QA Fated" })
            Assert.True(Named(n).IsAftermath, n);
        Assert.Contains("Fuse", Named("QA Wear").OtherHalf!.RulesText);
        Assert.All(cards, c => Assert.Equal("", c.HalfTemplateName));   // 1.6.9: no frame of its own

        var dfc = Named("QA Daybound");
        Assert.True(dfc.IsDoubleFaced);
        Assert.Equal("sunmoon", dfc.DfcStyle);
        Assert.Equal("QA Nightbound", dfc.BackFace!.Name);
        Assert.Equal("Midnight", dfc.BackFace.TemplateName);

        var top = Named("QA Gisela");
        Assert.True(top.IsMeld);
        Assert.Equal("QA Bruna", top.MeldWith);
        Assert.Equal("QA Brisela", top.BackFace!.Name);
        Assert.True(Named("QA Bruna").IsMeldBottom);
    }

    [Fact]
    public void A_1_6_8_Set_KeepsEveryValueItWrote_ThroughAResave()
    {
        // Every value 1.6.8 wrote, at every depth (halves, backs, the profile), comes back unchanged after a
        // load + save; a newer version may only ADD fields.
        var path = Fixture(Layouts168);
        var original = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        var resaved = Path.Combine(Path.GetTempPath(), "resaved_" + Guid.NewGuid().ToString("N") + ".cardinator");
        try
        {
            CardProject.Load(path).Save(resaved);
            var after = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(resaved))!;
            AssertKept(original, after, "$");
        }
        finally { try { File.Delete(resaved); } catch { } }

        static void AssertKept(System.Text.Json.Nodes.JsonNode? was, System.Text.Json.Nodes.JsonNode? now, string at)
        {
            switch (was)
            {
                case System.Text.Json.Nodes.JsonObject o:
                    var n = Assert.IsType<System.Text.Json.Nodes.JsonObject>(now);
                    foreach (var (key, value) in o)
                    {
                        Assert.True(n.ContainsKey(key), $"{at}.{key} was dropped");
                        AssertKept(value, n[key], $"{at}.{key}");
                    }
                    break;
                case System.Text.Json.Nodes.JsonArray a:
                    var b = Assert.IsType<System.Text.Json.Nodes.JsonArray>(now);
                    Assert.True(a.Count == b.Count, $"{at}: {a.Count} items became {b.Count}");
                    for (int i = 0; i < a.Count; i++) AssertKept(a[i], b[i], $"{at}[{i}]");
                    break;
                default:
                    Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(was, now), $"{at}: {was?.ToJsonString()} became {now?.ToJsonString()}");
                    break;
            }
        }
    }

    [Fact]
    public void A_1_6_8_Set_RendersEveryCard_AndItsSplitCardsKeepTheCardsFrame()
    {
        TestHelpers.RunSta(() =>
        {
            var project = CardProject.Load(Fixture(Layouts168));
            var templates = new TemplateService().LoadAll();
            var names = templates.Select(t => t.Name).ToList();
            var renderer = new CardRenderer(new SymbolService());

            foreach (var card in project.Cards)
            {
                var tpl = templates.FirstOrDefault(t => t.Name == card.TemplateName);
                Assert.True(tpl != null, $"{card.Name}: frame {card.TemplateName} isn't a shipped frame");
                tpl = TemplateService.ResolveFor(card, tpl!);
                Assert.True(TestHelpers.HasContent(renderer.RenderToBitmap(card, tpl, supersample: 1)), $"{card.Name} rendered blank");
                if (card.BackFace is { } back)
                    Assert.True(TestHelpers.HasContent(renderer.RenderToBitmap(back, templates.First(t => t.Name == back.TemplateName), supersample: 1)),
                        $"{card.Name}: back rendered blank");

                if (card.IsSplit)
                {
                    Assert.Equal(tpl.Name, CardRenderer.SplitGeometry(card, tpl).Other.Template.Name);
                    Assert.DoesNotContain(CardValidator.Validate(card, tpl.Spec, project.Cards, names), i => i.Code == "half-frame-missing");
                }
            }
        });
    }
}
