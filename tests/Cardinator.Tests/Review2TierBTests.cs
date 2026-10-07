using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// Tier B of the second independent review (1.6.15): things that show up wrong — frames, card shapes after a
/// lookup, rules-text layout, set handling and imports. Each test fails with its fix taken out. In the STA-window
/// collection because some swap <see cref="ScryfallClient.TestHttp"/> or <see cref="ConfirmDialog.TestAnswer"/>.
/// </summary>
[Collection("STAWindows")]
public class Review2TierBTests
{
    // --- frames: two with the same name -------------------------------------------------------------

    private static string FrameFolder(string root, string slug, string name, string frameColor, Action<TemplateSpec>? tweak = null)
    {
        var dir = Path.Combine(root, slug);
        Directory.CreateDirectory(dir);
        var spec = new TemplateSpec { Name = name };
        spec.Colors.Frame = frameColor;
        tweak?.Invoke(spec);
        spec.Save(Path.Combine(dir, "template.json"));
        return dir;
    }

    private static string TempRoot(string tag) => Path.Combine(Path.GetTempPath(), $"cardinator-{tag}-{Guid.NewGuid():N}");

    [Fact]
    public void TwoFramesWithOneName_EveryLookupTakesTheSameOne()
        => TestHelpers.RunSta(() =>
        {
            var root = TempRoot("dupframes");
            var name = "Dup " + Guid.NewGuid().ToString("N")[..6];
            try
            {
                FrameFolder(root, "a-red", name, "#D01010");
                FrameFolder(root, "b-blue", name, "#1010D0");
                var templates = new TemplateService().LoadFrom(root);
                Assert.Equal(2, templates.Count(t => t.Name == name));
                var first = templates.First(t => t.Name == name);
                Assert.Same(first, TemplateService.Named(name));   // the split-half / validator lookup

                // Export all draws the card on that same first frame (it used to take the last).
                var card = new CardModel { Name = "Dup test", TemplateName = name };
                var outDir = Path.Combine(root, "out");
                var symbols = new SymbolService();
                var result = BatchService.ExportAll(new[] { card }, templates, outDir, symbols);
                Assert.Equal(1, result.Exported);
                var exported = CustomFrameComposer.LoadBitmap(Directory.GetFiles(outDir, "*.png").Single());
                var renderer = new CardRenderer(symbols);
                var asFirst = CardExporter.AtCardSize(renderer.RenderToBitmap(card, first, supersample: 2));
                var asSecond = CardExporter.AtCardSize(renderer.RenderToBitmap(card, templates.Last(t => t.Name == name), supersample: 2));
                Assert.True(Distance(exported, asFirst) < Distance(exported, asSecond), "Export all drew the card on the other copy");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        });

    private static double Distance(BitmapSource a, BitmapSource b)
    {
        var (pa, pb) = (TestHelpers.Pixels(Bgra(a)), TestHelpers.Pixels(Bgra(b)));
        double d = 0;
        for (int i = 0; i < Math.Min(pa.Length, pb.Length); i += 16) d += Math.Abs(pa[i] - pb[i]) + Math.Abs(pa[i + 2] - pb[i + 2]);
        return d;
    }

    private static BitmapSource Bgra(BitmapSource s) => s.Format == PixelFormats.Bgra32 ? s : new FormatConvertedBitmap(s, PixelFormats.Bgra32, null, 0);

    // --- frames: back faces -------------------------------------------------------------------------

    [Fact]
    public void ChangingTheFrontsFrame_TakesAlongABackFaceOnTheSameFrame_ButNotOneWithItsOwn()
    {
        var follows = new CardModel { Name = "F", TemplateName = "Crimson Red", BackFace = new CardModel { Name = "B", TemplateName = "Crimson Red" } };
        Cardinator.MainWindow.SetFrame(follows, "Ocean Blue");
        Assert.Equal(("Ocean Blue", "Ocean Blue"), (follows.TemplateName, follows.BackFace!.TemplateName));

        var own = new CardModel { Name = "F", TemplateName = "Crimson Red", BackFace = new CardModel { Name = "B", TemplateName = "Azure Modern" } };
        Cardinator.MainWindow.SetFrame(own, "Ocean Blue");
        Assert.Equal("Azure Modern", own.BackFace!.TemplateName);
    }

    [Fact]
    public void ABackFacesOwnFrame_CountsAsInUse()
    {
        var card = new CardModel { Name = "F", TemplateName = "Crimson Red", BackFace = new CardModel { Name = "B", TemplateName = "Azure Modern" } };
        Assert.Contains("Azure Modern", TemplateService.FramesUsed(card));
        Assert.Equal(1, TemplateService.CountReferencing(new[] { card }, "Azure Modern"));   // the delete warning
    }

    [Fact]
    public void WithTheBackShown_ThePickerShowsAndChangesTheBacksFrame()
        => OnAppThread(() =>
        {
            var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
            var names = main.Templates.Select(t => t.Name).Distinct().Take(3).ToList();
            var card = new CardModel { Name = "F", TemplateName = names[0], BackFace = new CardModel { Name = "B", TemplateName = names[1] } };
            main.Cards.Add(card);
            main.SelectedCard = card;
            Assert.Equal(names[0], main.SelectedTemplate?.Name);
            Invoke(main, "OnFlipPreview", null, null);   // Show back
            Assert.Equal(names[1], main.SelectedTemplate?.Name);
            main.SelectedTemplate = main.Templates.First(t => t.Name == names[2]);
            Assert.Equal((names[0], names[2]), (card.TemplateName, card.BackFace!.TemplateName));
        });

    // --- frames: a card on a frame that isn't installed ---------------------------------------------

    [Fact]
    public void ACardOnAMissingFrame_ShowsNoFrame_AndLooksTheSameHoweverYouReachIt()
        => OnAppThread(() =>
        {
            var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
            var names = main.Templates.Select(t => t.Name).Distinct().Take(3).ToList();
            var a = new CardModel { Name = "A", TemplateName = names[0] };
            var b = new CardModel { Name = "B", TemplateName = names[1] };
            main.GetType().GetField("_defaultTemplate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(main, names[2]);   // the set's house frame
            var lost = new CardModel { Name = "Lost", TemplateName = "Not Installed " + Guid.NewGuid().ToString("N")[..6] };
            foreach (var c in new[] { a, b, lost }) main.Cards.Add(c);

            main.SelectedCard = a;
            main.SelectedCard = lost;
            Assert.Null(main.SelectedTemplate);   // not "whatever the last card used"
            var drawnAfterA = TemplateForName(main, lost);
            main.SelectedCard = b;
            main.SelectedCard = lost;
            Assert.Equal(drawnAfterA, TemplateForName(main, lost));
            Assert.Equal(names[2], drawnAfterA);   // drawn on the house frame meanwhile

            // Delete / Design / Export act on the picked frame — there's none, so they say so instead.
            ConfirmDialog.TestAnswer = _ => throw new InvalidOperationException("nothing should be asked");
            try { Invoke(main, "OnDeleteFrame", null, null); }
            finally { ConfirmDialog.TestAnswer = null; }
            Assert.Contains("isn't installed", main.Status);
        });

    private static string? TemplateForName(Cardinator.MainWindow main, CardModel card)
        => ((Template?)main.GetType().GetMethod("TemplateFor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(main, new object[] { card }))?.Name;

    [Theory]
    [InlineData("Crimson Red", "Crimson Red", null, null)]           // changed in place: cards stay put
    [InlineData("Crimson Red", "Crimson Red copy", null, "Crimson Red copy")]   // Save as new: onto the card
    [InlineData("Crimson Red", "Crimson Renamed", "Crimson Red", null)]          // renamed: its cards follow by name
    public void FrameDesign_PutsOnlyANewFrameOnTheCard(string opened, string applied, string? renamedFrom, string? expected)
        => Assert.Equal(expected, Cardinator.MainWindow.FrameToPutOnCard(opened, applied, renamedFrom));

    // --- frames: Frame Design saves -----------------------------------------------------------------

    [Fact]
    public void RenamingABuiltInFrame_SavesACopy_AndKeepsTheBuiltIn()
        => OnAppThread(() =>
        {
            var builtIn = new TemplateService().LoadAll().First(t => t.Name == "Ocean Blue");
            var builtInDir = Path.GetDirectoryName(builtIn.FramePath)!;
            var newName = "Ocean Renamed " + Guid.NewGuid().ToString("N")[..6];
            string? copyDir = null;
            ConfirmDialog.TestAnswer = title => title == "Built-in frame" ? ConfirmResult.Negative : ConfirmResult.Affirmative;
            try
            {
                var win = new FrameDesignWindow(builtIn) { IsBuiltIn = true, Standalone = true };
                win.NameBox.Text = newName;
                Invoke(win, "OnApply", null, null);   // "Overwrite built-in", with a new name
                Assert.Equal("Ocean Blue", TemplateSpec.Load(Path.Combine(builtInDir, "template.json")).Name);
                copyDir = Path.Combine(AppPaths.TemplatesDir, TextUtil.Slug(newName));
                Assert.Equal(newName, TemplateSpec.Load(Path.Combine(copyDir, "template.json")).Name);
                win.Close();
            }
            finally
            {
                ConfirmDialog.TestAnswer = null;
                if (copyDir != null) try { Directory.Delete(copyDir, true); } catch { }
                // Should the built-in have been renamed after all, let it heal on the next load.
                if (TemplateSpec.Load(Path.Combine(builtInDir, "template.json")).Name != "Ocean Blue")
                    File.Delete(Path.Combine(builtInDir, "template.json"));
            }
        });

    [Fact]
    public void SaveAsNew_KeepsTheFramesHandMadeLayouts()
        => OnAppThread(() =>
        {
            var root = TempRoot("saveasnew");
            var name = "Picture " + Guid.NewGuid().ToString("N")[..6];
            string? copyDir = null;
            try
            {
                var dir = FrameFolder(root, "picture", name, "#808080", s => s.CustomFrame = true);
                File.WriteAllBytes(Path.Combine(dir, "frame.png"), TestHelpers.PngBytes(750, 1050, 0x00000000));
                foreach (var v in new[] { "flip", "token" })
                {
                    Directory.CreateDirectory(Path.Combine(dir, v));
                    File.WriteAllBytes(Path.Combine(dir, v, "frame.png"), TestHelpers.PngBytes(750, 1050, 0x00000000));
                    File.Copy(Path.Combine(dir, "template.json"), Path.Combine(dir, v, "template.json"));
                }
                var t = new TemplateService().LoadFrom(root).Single();
                var win = new FrameDesignWindow(t) { Standalone = true };
                var copyName = name + " copy";
                Invoke(win, "SaveCopy", copyName);
                copyDir = Path.Combine(AppPaths.TemplatesDir, TextUtil.Slug(copyName));
                Assert.True(File.Exists(Path.Combine(copyDir, "flip", "frame.png")), "the flip layout was left behind");
                Assert.True(File.Exists(Path.Combine(copyDir, "token", "template.json")), "the token layout was left behind");
                win.Close();
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
                if (copyDir != null) try { Directory.Delete(copyDir, true); } catch { }
            }
        });

    // --- frames: bundles ----------------------------------------------------------------------------

    [Fact]
    public void ABundle_CarriesTheTokenLayout()
        => TestHelpers.RunSta(() =>
        {
            var root = TempRoot("tokenbundle");
            try
            {
                var dir = FrameFolder(root, "f", "Token Bundle", "#808080", s => s.CustomFrame = true);
                File.WriteAllBytes(Path.Combine(dir, "frame.png"), TestHelpers.PngBytes(8, 8));
                Directory.CreateDirectory(Path.Combine(dir, "token"));
                File.WriteAllBytes(Path.Combine(dir, "token", "frame.png"), TestHelpers.PngBytes(8, 8));
                File.Copy(Path.Combine(dir, "template.json"), Path.Combine(dir, "token", "template.json"));
                var bundle = Path.Combine(root, "f.cardframe");
                TemplateImporter.ExportBundle(dir, bundle);
                using var zip = ZipFile.OpenRead(bundle);
                Assert.Contains(zip.Entries, e => e.FullName == "token/frame.png");
                Assert.Contains(zip.Entries, e => e.FullName == "token/template.json");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        });

    [Fact]
    public void AFrameThatIsCalledFlip_IsImportedAsAFrame()
        => TestHelpers.RunSta(() =>
        {
            var root = TempRoot("flipnamed");
            Directory.CreateDirectory(root);
            var name = "Flip " + Guid.NewGuid().ToString("N")[..6];
            var installed = new List<string>();
            try
            {
                var zipPath = Path.Combine(root, "share.zip");
                var specFile = Path.Combine(root, "spec.json");
                new TemplateSpec { Name = name, CustomFrame = true }.Save(specFile);
                using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                {
                    // A "Share set + frames" zip whose frame happens to be slugged "flip".
                    using (var w = new StreamWriter(zip.CreateEntry("frames/flip/template.json").Open())) w.Write(File.ReadAllText(specFile));
                    using (var b = new BinaryWriter(zip.CreateEntry("frames/flip/frame.png").Open())) b.Write(TestHelpers.PngBytes(8, 8));
                }
                var names = TemplateImporter.ImportBundles(zipPath);
                installed.AddRange(names.Select(n => Path.Combine(AppPaths.TemplatesDir, TextUtil.Slug(n))));
                Assert.Equal(new[] { name }, names);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
                foreach (var d in installed) try { Directory.Delete(d, true); } catch { }
            }
        });

    [Fact]
    public void AFrameImageThatCantBeRead_FailsTheImport_AndInstallsNothing()
        => TestHelpers.RunSta(() =>
        {
            var name = "Unreadable " + Guid.NewGuid().ToString("N")[..6];
            var notAPicture = new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3, 4 };
            var ex = Assert.Throws<InvalidOperationException>(() => TemplateImporter.CreateFromFrame(name, notAPicture));
            Assert.Contains("can't be read", ex.Message);
            Assert.False(Directory.Exists(Path.Combine(AppPaths.TemplatesDir, TextUtil.Slug(name))));
        });

    // --- card editing -------------------------------------------------------------------------------

    [Fact]
    public void AfterALookupMakesASplitCard_TheButtonsSaySo()
        => OnAppThread(() =>
        {
            ScryfallClient.TestHttp = new HttpClient(new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                { "object": "card", "name": "Fire // Ice", "layout": "split", "card_faces": [
                    { "name": "Fire", "mana_cost": "{1}{R}", "type_line": "Instant", "oracle_text": "Fire deals 2 damage divided as you choose." },
                    { "name": "Ice", "mana_cost": "{1}{U}", "type_line": "Instant", "oracle_text": "Tap target permanent. Draw a card." } ] }
                """, Encoding.UTF8, "application/json"),
            })));
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try
            {
                var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
                var card = new CardModel { Name = "Fire", TemplateName = main.Templates[0].Name };
                main.Cards.Add(card);
                main.SelectedCard = card;
                Assert.Equal("Make split card", main.SplitToggleBtn.Content);
                Invoke(main, "OnLookup", null, null);
                PumpUntil(() => !main.Busy);
                Assert.True(card.IsSplit);
                Assert.Equal("Remove other half", main.SplitToggleBtn.Content);
                Assert.Equal("Edit other half…", main.FlipEditBtn.Content);
            }
            finally { ScryfallClient.TestHttp = null; }
        });

    [Fact]
    public void EveryFillPath_KeepsTheAdventure()
    {
        CardModel Giant() => new()
        {
            Name = "Bonecrusher Giant", ManaCost = "{2}{R}", TypeLine = "Creature — Giant", Power = "4", Toughness = "3",
            AdventureName = "Stomp", AdventureCost = "{1}{R}", AdventureType = "Instant — Adventure", AdventureText = "Damage can't be prevented this turn.",
        };
        var lookedUp = new CardModel { Name = "Bonecrusher Giant" };
        CardDetailsFill.MergeNonEmpty(lookedUp, Giant());   // Ctrl+L
        var details = new CardModel { Name = "My Giant" };
        CardDetailsFill.ApplyScryfall(details, Giant());     // Edit details → Fill from Scryfall
        var imported = new CardModel { Name = "Bonecrusher Giant" };
        BatchService.FillBlanks(imported, Giant(), canonicalName: true);   // import / Fill blanks
        foreach (var c in new[] { lookedUp, details, imported })
        {
            Assert.Equal(("Stomp", "{1}{R}", "Instant — Adventure"), (c.AdventureName, c.AdventureCost, c.AdventureType));
            Assert.True(c.IsAdventure);
        }
    }

    [Fact]
    public void Checks_ShowNotesToo()
        => OnAppThread(() =>
        {
            var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
            var card = new CardModel { Name = "No Art Yet", TypeLine = "Instant", RulesText = "Draw a card.", TemplateName = main.Templates[0].Name };
            main.Cards.Add(card);
            main.SelectedCard = card;
            main.RenderPreview();
            Assert.Contains("note", main.ValidationSummary);
            Assert.Contains("No artwork", main.ValidationDetails);
        });

    [Fact]
    public void SetFields_SaysWhichCardsItChanges()
        => OnAppThread(() =>
        {
            Assert.Contains("every card", new BulkEditWindow(new[] { "A" }).ScopeText.Text);
            var some = new BulkEditWindow(new[] { "A" }, selectedCount: 3);
            Assert.Contains("3 selected", some.HeadingText.Text);
            Assert.DoesNotContain("every card", some.ScopeText.Text);
        });

    // --- rendering ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("−X: Chandra deals X damage to target creature.", "−X")]
    [InlineData("-x: Draw X cards.", "−X")]
    [InlineData("+X: You gain X life.", "+X")]
    [InlineData("-3: Destroy target creature.", "−3")]
    public void LoyaltyAbilities_WithX_GetABadge(string line, string cost)
        => Assert.Equal(cost, CardRenderer.ParseAbilities(line).Single().cost);

    [Theory]
    [InlineData("2{G}", "{2}{G}")]
    [InlineData("W/U", "{W/U}")]
    [InlineData("2/W 2/W", "{2/W}{2/W}")]
    [InlineData("g/p", "{G/P}")]
    [InlineData("1 w/u w/u", "{1}{W/U}{W/U}")]
    [InlineData("2RW", "{2}{R}{W}")]
    [InlineData("{2}{R}", "{2}{R}")]
    [InlineData("{W/U}", "{W/U}")]
    public void LooselyTypedCosts_KeepEverySymbol(string typed, string expected)
        => Assert.Equal(expected, ManaText.NormalizeCost(typed));

    [Fact]
    public void ASplitCostColumn_IsReadLikeTheManaColumn()
    {
        var card = ImportService.Parse("name,mana,split_name,split_cost\nFire,1R,Ice,1U", "", "Crimson Red").Single().Card;
        Assert.Equal("{1}{U}", card.OtherHalf!.ManaCost);
    }

    private static (CardRenderer renderer, FontSpec font) Layout()
        => (new CardRenderer(new SymbolService()), new FontSpec { Family = "Georgia", Size = 24 });

    [Fact]
    public void SymbolsWrittenTogether_SitTogether()
        => TestHelpers.RunSta(() =>
        {
            var (r, font) = Layout();
            var items = r.InspectTextLayout("{2}{R}, {T}: Draw a card.", new Rect(0, 0, 600, 300), font, 24, 22);
            var syms = items.Where(i => i.Symbol).Select(i => i.Rect).ToList();
            Assert.Equal(3, syms.Count);
            Assert.True(syms[1].X - syms[0].Right < 4, $"{{2}} and {{R}} are {syms[1].X - syms[0].Right:F1}px apart — a word gap");
            var colon = items.First(i => !i.Symbol && i.Rect.X > syms[2].X).Rect;
            Assert.True(colon.X - syms[2].Right < 1, "the colon after {T} got a word gap");
        });

    [Fact]
    public void ACost_NeverWrapsInTheMiddle()
        => TestHelpers.RunSta(() =>
        {
            var (r, font) = Layout();
            // A width where the line runs out between {2} and {R}: the whole "{2}{R}{R}:" moves down.
            for (double w = 150; w <= 420; w += 6)
            {
                var items = r.InspectTextLayout("Pay the price now {2}{R}{R}: Draw a card.", new Rect(0, 0, w, 600), font, 24, 22);
                var syms = items.Where(i => i.Symbol).Select(i => i.Rect).ToList();
                Assert.True(syms.Select(s => Math.Round(s.Y)).Distinct().Count() == 1, $"the cost split across lines at width {w}");
            }
        });

    [Fact]
    public void AWordLongerThanTheLine_StaysInsideTheBox()
        => TestHelpers.RunSta(() =>
        {
            var (r, font) = Layout();
            var box = new Rect(10, 0, 200, 600);
            var items = r.InspectTextLayout("Abracadabracadabracadabracadabracadabra!", box, font, 24, 22);
            Assert.True(items.Count > 1, "the long word wasn't broken");
            Assert.All(items, i => Assert.True(i.Rect.Right <= box.Right + 0.5, $"a piece runs to {i.Rect.Right:F0}, past the box's {box.Right}"));
        });

    [Fact]
    public void TextTooLongForTheBox_IsFlaggedInChecks()
        => TestHelpers.RunSta(() =>
        {
            var template = new TemplateService().LoadAll().First(t => t.Name == "Crimson Red");
            var r = new CardRenderer(new SymbolService());
            var normal = new CardModel { Name = "Short", TypeLine = "Instant", RulesText = "Draw a card." };
            var wall = new CardModel { Name = "Wall", TypeLine = "Instant", RulesText = string.Join("\n", Enumerable.Repeat("Whenever a creature you control attacks, draw a card, then discard a card.", 25)) };
            Assert.Empty(RenderInspector.Overflow(r, normal, template));
            Assert.Equal("text-overflow", Assert.Single(RenderInspector.Overflow(r, wall, template)).Code);
            Assert.Contains(RenderInspector.InspectCard(r, wall, template), i => i.Code == "text-overflow");
        });

    [Fact]
    public void AWideSetSymbol_KeepsItsShape()
        => TestHelpers.RunSta(() =>
        {
            var root = TempRoot("setsym");
            Directory.CreateDirectory(root);
            try
            {
                var sym = Path.Combine(root, "wide.png");
                File.WriteAllBytes(sym, TestHelpers.PngBytes(200, 50, 0xFFFF0000));   // 4:1, pure red
                var template = new TemplateService().LoadAll().First(t => t.Name == "Crimson Red");
                var card = new CardModel { Name = "Sym", TypeLine = "Instant", Rarity = "R", SetSymbolPath = sym };
                var bmp = Bgra(new CardRenderer(new SymbolService()).RenderToBitmap(card, template));
                var px = TestHelpers.Pixels(bmp);
                var bar = template.Spec.TypeBar;
                int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;
                for (int y = (int)bar.Y; y < bar.Y + bar.H; y++)
                    for (int x = (int)(bar.X + bar.W / 2); x < bar.X + bar.W; x++)
                    {
                        int i = (y * bmp.PixelWidth + x) * 4;
                        if (px[i + 2] > 230 && px[i + 1] < 30 && px[i] < 30)
                        { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
                    }
                Assert.True(maxX > 0, "no set symbol drawn");
                double aspect = (maxX - minX + 1.0) / (maxY - minY + 1.0);
                Assert.True(aspect > 2.5, $"the 4:1 symbol was drawn {aspect:F1}:1");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        });

    // --- set handling -------------------------------------------------------------------------------

    [Fact]
    public void TheSetDefaultsSymbol_IsCopiedIntoTheSetFolder()
    {
        var root = TempRoot("profilesym");
        try
        {
            Directory.CreateDirectory(root);
            var outside = Path.Combine(root, "symbol.png");
            File.WriteAllBytes(outside, new byte[] { 1, 2, 3 });
            var setFile = Path.Combine(root, "Set", "Set.cardinator");
            Directory.CreateDirectory(Path.GetDirectoryName(setFile)!);
            var profile = new SetProfile { SetSymbolPath = outside };
            ProjectWriter.Write(setFile, new[] { new CardModel { Name = "A" } }, "Set", "", "", profile);
            var artDir = Path.Combine(root, "Set", "art");
            Assert.StartsWith(artDir, profile.SetSymbolPath);
            Assert.True(File.Exists(profile.SetSymbolPath));
            var saved = CardProject.Load(setFile).Profile!.SetSymbolPath;
            Assert.StartsWith("art", saved);   // written relative to the set, so it travels with it
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void ClosingDuringAnExport_AsksFirst()
        => OnAppThread(() =>
        {
            var main = new Cardinator.MainWindow();
            main.Dirty = false;
            main.IsExporting = true;
            string? asked = null;
            ConfirmDialog.TestAnswer = title => { asked = title; return ConfirmResult.Affirmative; };   // "Keep exporting"
            try
            {
                var e = new System.ComponentModel.CancelEventArgs();
                Invoke(main, "OnWindowClosing", null, e);
                Assert.Equal("Export still running", asked);
                Assert.True(e.Cancel);

                ConfirmDialog.TestAnswer = _ => ConfirmResult.Negative;   // "Close anyway"
                e = new System.ComponentModel.CancelEventArgs();
                Invoke(main, "OnWindowClosing", null, e);
                Assert.False(e.Cancel);
            }
            finally { ConfirmDialog.TestAnswer = null; main.IsExporting = false; main.SuppressClosePrompt = true; }
        });

    // --- imports ------------------------------------------------------------------------------------

    [Fact]
    public void TheQuickstartCsvExample_Imports()
    {
        var doc = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "QUICKSTART.md"));
        var section = doc[doc.IndexOf("## Make a whole set at once", StringComparison.Ordinal)..];
        int open = section.IndexOf("```", StringComparison.Ordinal) + 3;
        var example = section[open..section.IndexOf("```", open, StringComparison.Ordinal)];
        var cards = ImportService.Parse(example, "", "Default").Select(c => c.Card).ToList();
        Assert.Equal(new[] { "Lightning Bolt", "Counterspell" }, cards.Select(c => c.Name));
        Assert.Equal(new[] { "Crimson Red", "Ocean Blue" }, cards.Select(c => c.TemplateName));
    }

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "docs", "QUICKSTART.md"))) return d.FullName;
        throw new DirectoryNotFoundException("repo root");
    }

    [Fact]
    public void AQuantityColumn_MakesThatManyCopies()
    {
        var cards = ImportService.Parse("name,qty\nLightning Bolt,4\nShock,2x\nOpt,", "", "Default").Select(c => c.Card).ToList();
        Assert.Equal(4, cards.Count(c => c.Name == "Lightning Bolt"));
        Assert.Equal(2, cards.Count(c => c.Name == "Shock"));
        Assert.Single(cards, c => c.Name == "Opt");
        Assert.Equal(cards.Count, cards.Distinct().Count());   // separate cards, not one object four times
    }

    [Fact]
    public async Task AWrongPrintingHint_IsReplacedByThePrintingFound()
    {
        var bodies = new List<string>();
        ScryfallClient.TestHttp = new HttpClient(new FakeHandler(async req =>
        {
            if (req.Content != null) { var text = await req.Content.ReadAsStringAsync(); lock (bodies) bodies.Add(text); }
            var json = req.RequestUri!.AbsolutePath.EndsWith("/collection")
                ? """{ "object": "list", "data": [], "not_found": [ {} ] }"""
                : """{ "object": "card", "name": "Lightning Bolt", "type_line": "Instant", "oracle_text": "Lightning Bolt deals 3 damage to any target.", "set": "m10", "collector_number": "146", "rarity": "common" }""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }));
        try
        {
            var items = ImportService.Parse("Lightning Bolt (XYZ) 999", "", "Default");
            Assert.True(items.Single().PrintingHint);
            await BatchService.FillFromScryfallAsync(items, downloadArt: false);
            var card = items.Single().Card;
            Assert.Equal(("M10", "146", "C"), (card.SetCode, card.CollectorNumber, card.Rarity));
        }
        finally { ScryfallClient.TestHttp = null; }
    }

    [Fact]
    public async Task ASetOnlyHint_AsksForThatSetsPrinting()
    {
        string? body = null;
        ScryfallClient.TestHttp = new HttpClient(new FakeHandler(async req =>
        {
            body ??= req.Content == null ? null : await req.Content.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("""{ "object": "list", "data": [] }""", Encoding.UTF8, "application/json") };
        }));
        try
        {
            await BatchService.FillFromScryfallAsync(ImportService.Parse("Lightning Bolt (M10)", "", "Default"), downloadArt: false);
            Assert.Contains("\"set\":\"m10\"", body);
            Assert.Contains("\"name\":\"Lightning Bolt\"", body);
        }
        finally { ScryfallClient.TestHttp = null; }
    }

    [Fact]
    public async Task ACustomSetsOwnCode_IsNotAPrintingHint()
    {
        // A CSV's set column is the card's own data (a custom set), never swapped for a real printing.
        var item = ImportService.Parse("name,set,number\nLightning Bolt,FMA,12", "", "Default").Single();
        Assert.False(item.PrintingHint);
        ScryfallClient.TestHttp = new HttpClient(new FakeHandler(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(req.RequestUri!.AbsolutePath.EndsWith("/collection")
                ? """{ "object": "list", "data": [] }"""
                : """{ "object": "card", "name": "Lightning Bolt", "type_line": "Instant", "set": "m10", "collector_number": "146" }""", Encoding.UTF8, "application/json"),
        })));
        try
        {
            await BatchService.FillFromScryfallAsync(new[] { item with { NeedsLookup = true } }, downloadArt: false);
            Assert.Equal(("FMA", "12"), (item.Card.SetCode, item.Card.CollectorNumber));
        }
        finally { ScryfallClient.TestHttp = null; }
    }

    [Theory]
    [InlineData("binary/octet-stream", true, ".png")]
    [InlineData("application/octet-stream", true, ".png")]
    [InlineData("binary/octet-stream", false, null)]
    public async Task ADownloadWithABinaryType_IsKeptIfItIsAPicture(string contentType, bool picture, string? ext)
    {
        var body = picture ? TestHelpers.PngBytes(4, 4) : Encoding.UTF8.GetBytes("<html>not a picture</html>");
        using var server = new OneShotServer(contentType, body);
        var url = $"http://127.0.0.1:{server.Port}/download?id=7";   // no image extension in the link
        if (picture)
        {
            var path = await ImageIntake.DownloadAsync(url);
            try { Assert.Equal(ext, Path.GetExtension(path)); Assert.True(File.Exists(path)); }
            finally { File.Delete(path); }
        }
        else
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ImageIntake.DownloadAsync(url));
            Assert.Contains("didn't return an image", ex.Message);
        }
    }

    // --- helpers ----------------------------------------------------------------------------------

    /// <summary>A tiny HTTP server that answers one request with the given type and bytes.</summary>
    private sealed class OneShotServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public int Port { get; }
        public OneShotServer(string contentType, byte[] body)
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                try
                {
                    using var client = await _listener.AcceptTcpClientAsync();
                    using var stream = client.GetStream();
                    var buf = new byte[4096];
                    var req = new StringBuilder();
                    while (!req.ToString().Contains("\r\n\r\n"))
                    {
                        int n = await stream.ReadAsync(buf);
                        if (n == 0) break;
                        req.Append(Encoding.ASCII.GetString(buf, 0, n));
                    }
                    var head = $"HTTP/1.1 200 OK\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                    await stream.WriteAsync(body);
                }
                catch { /* the test failed elsewhere */ }
            });
        }
        public void Dispose() => _listener.Stop();
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => answer(request);
    }

    private static void PumpUntil(Func<bool> done, int ms = 15000)
    {
        var sw = Stopwatch.StartNew();
        while (!done() && sw.ElapsedMilliseconds < ms)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
        Assert.True(done(), "timed out");
    }

    private static void Invoke(object target, string method, params object?[] args)
        => target.GetType().GetMethods(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .First(m => m.Name == method && m.GetParameters().Length == args.Length)
            .Invoke(target, args);

    private static void OnAppThread(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = Application.Current ?? new Application();
                if (app.Resources.MergedDictionaries.Count == 0)
                    app.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
                        new Uri("/Cardinator;component/Theme.xaml", UriKind.Relative)));
                action();
            }
            catch (System.Reflection.TargetInvocationException ex) { captured = ex.InnerException ?? ex; }
            catch (Exception ex) { captured = ex; }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (captured != null) throw captured;
    }
}
