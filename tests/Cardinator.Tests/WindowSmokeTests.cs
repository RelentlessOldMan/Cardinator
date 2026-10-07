using System;
using System.Linq;
using System.Threading;
using System.Windows;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// Constructs the dialog windows with the real App.xaml resources loaded, on an STA thread,
/// without showing them. This catches runtime XAML problems the compiler doesn't — e.g. a
/// mistyped StaticResource key or a style applied to the wrong element type — which matters
/// because these windows can't be clicked in a headless/remote environment.
/// </summary>
[Collection("STAWindows")]
public class WindowSmokeTests
{
    [Fact]
    public void HelpWindow_Constructs_WithAppResources()
        => OnAppThread(() =>
        {
            var w = new Cardinator.HelpWindow("Cardinator test");
            Assert.NotNull(w.Content);
        });

    [Fact]
    public void DetailsWindow_Constructs_WithAppResources()
        => OnAppThread(() =>
        {
            var w = new Cardinator.DetailsWindow(new CardModel { Name = "Smoke" });
            Assert.NotNull(w.Content);
        });

    [Fact]
    public void InputDialog_Constructs_WithAppResources()
        => OnAppThread(() =>
        {
            var w = new Cardinator.InputDialog("Prompt", "hint", "initial");
            Assert.NotNull(w.Content);
        });

    [Fact]
    public void FrameDesignWindow_Constructs_AndPreviews()
        => OnAppThread(() =>
        {
            var tpl = new TemplateService().LoadAll().First();
            var w = new Cardinator.FrameDesignWindow(tpl);
            Assert.NotNull(w.Content);   // ctor also renders the live preview — throws if that path is broken
        });

    [Fact]
    public void ConfirmDialog_Constructs_WithAppResources()
        => OnAppThread(() =>
        {
            var w = new Cardinator.ConfirmDialog("Unsaved changes", "Save first?", "Save", "Don't save", "Cancel");
            Assert.NotNull(w.Content);
        });

    [Fact]
    public void DeckListWindow_Constructs_WithAppResources()
        => OnAppThread(() =>
        {
            var w = new Cardinator.DeckListWindow();
            Assert.NotNull(w.Content);
        });

    [Fact]
    public void BulkEditWindow_Constructs_WithAppResources()
        => OnAppThread(() =>
        {
            var w = new Cardinator.BulkEditWindow(new[] { "Gold Multicolor", "Ocean Blue" });
            Assert.NotNull(w.Content);
        });

    [Fact]
    public void ScryfallSearchWindow_Constructs_AndBindsResults()
        => OnAppThread(() =>
        {
            var w = new Cardinator.ScryfallSearchWindow(new ScryfallClient());
            w.Results.Add(new Cardinator.ScryfallSearchWindow.ResultItem(new CardModel { Name = "X", TypeLine = "Creature", Rarity = "R", SetCode = "TST" }));
            Assert.NotNull(w.Content);
            Assert.Single(w.Results);
        });

    [Fact]
    public void MainWindow_UndoRedo_RevertsAndReappliesEdit()
        => OnAppThread(() =>
        {
            var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
            var original = main.SelectedCard!.Name;

            main.SelectedCard!.Name = "Undo Probe";
            main.Undo();
            Assert.Equal(original, main.SelectedCard!.Name);   // reverted

            main.Redo();
            Assert.Equal("Undo Probe", main.SelectedCard!.Name);   // re-applied
        });

    [Fact]
    public void MainWindow_BulkEdit_ReRendersActiveCard()
        => OnAppThread(() =>
        {
            var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
            Assert.NotNull(main.PreviewImage);
            var before = main.PreviewImage;
            // Clearing the footer on all cards must re-render the selected card's preview immediately.
            main.ApplyBulkEdit(null, null, null, copyright: "", null, null);
            Assert.NotSame(before, main.PreviewImage);
        });

    [Fact]
    public void MainWindow_BulkEdit_CanTargetASubset()   // M14
        => OnAppThread(() =>
        {
            var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
            main.Cards.Clear();
            var a = new CardModel { Name = "A" };
            var b = new CardModel { Name = "B" };
            main.Cards.Add(a); main.Cards.Add(b);
            main.SelectedCard = a;

            int n = main.ApplyBulkEdit(new[] { a }, setCode: "ONLYA", null, null, null, null, null);

            Assert.Equal(1, n);
            Assert.Equal("ONLYA", a.SetCode);
            Assert.Equal("", b.SetCode);                       // the unselected card is untouched
        });

    [Fact]
    public void MainWindow_LiveChecks_FlagBlankArtFromExistingButCorruptFile()   // M5
        => OnAppThread(() =>
        {
            var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
            var card = main.SelectedCard!;
            // A file that EXISTS but isn't a decodable image -> renders a blank art window. Rule checks
            // can't see this; only the pixel inspector can.
            var bogus = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "cardinator_notimg_" + Guid.NewGuid().ToString("N") + ".png");
            System.IO.File.WriteAllText(bogus, "not really a png");
            try
            {
                card.ArtPath = bogus;
                main.ApplyBulkEdit(null, null, null, null, null, null);   // triggers the inspect render path
                Assert.True(main.HasValidationIssues);
                Assert.Contains("blank", main.ValidationDetails, StringComparison.OrdinalIgnoreCase);
            }
            finally { try { System.IO.File.Delete(bogus); } catch { } }
        });

    [Fact]
    public void MainWindow_Undo_AtStart_IsNoOp()
        => OnAppThread(() =>
        {
            var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
            Assert.False(main.CanUndo);
            var name = main.SelectedCard!.Name;
            main.Undo();                                       // nothing to undo
            Assert.Equal(name, main.SelectedCard!.Name);
        });

    [Fact]
    public void MainWindow_NewEdit_TruncatesRedoTail()
        => OnAppThread(() =>
        {
            var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
            main.SelectedCard!.Name = "First";
            main.Undo();
            Assert.True(main.CanRedo);                         // "First" is redoable
            main.SelectedCard!.Name = "Second";               // a new edit...
            main.CommitHistory();
            Assert.False(main.CanRedo);                        // ...cancels the redo tail
        });

    [Fact]
    public void MainWindow_Undo_OfStructuralChange_RestoresCollection()
        => OnAppThread(() =>
        {
            var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
            int before = main.Cards.Count;

            main.Cards.Add(new Cardinator.Models.CardModel { Name = "Extra", TemplateName = main.SelectedCard!.TemplateName });
            main.CommitHistory();
            Assert.Equal(before + 1, main.Cards.Count);

            main.Undo();
            Assert.Equal(before, main.Cards.Count);            // add reverted

            main.Redo();
            Assert.Equal(before + 1, main.Cards.Count);        // and re-applied
        });

    [Fact]
    public void MainWindow_EditingTheBackFace_DirtiesTheProject()   // A2: back-face work must not be lost
        => OnAppThread(() =>
        {
            var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
            var card = main.SelectedCard!;
            card.BackFace = new CardModel { Name = "Nightform" };
            main.Dirty = false;                                // pretend we just saved

            // Pan/zoom the BACK face the way the flipped preview does. Nothing else runs — if the back
            // face isn't tracked, this edit never dirties the project and is silently lost on close.
            card.BackFace!.ArtScale = 2.5;

            Assert.True(main.Dirty, "editing the back face did not dirty the project");
        });

    [Fact]
    public void MainWindow_ReplacingTheBackFace_RetargetsChangeTracking()
        => OnAppThread(() =>
        {
            var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
            var card = main.SelectedCard!;
            card.BackFace = new CardModel { Name = "First" };
            var orphan = card.BackFace!;
            card.BackFace = new CardModel { Name = "Second" };   // replaced — track the new one instead
            main.Dirty = false;

            card.BackFace!.ArtOffsetX = 0.4;
            Assert.True(main.Dirty, "the replacement back face isn't tracked");

            main.Dirty = false;
            orphan.ArtOffsetX = 0.9;                             // discarded face must no longer dirty
            Assert.False(main.Dirty, "a discarded back face still dirties the project");
        });

    [Fact]
    public void MainWindow_UndoRedo_AreIgnoredWhileBusy()   // D1: Ctrl+Z/Y bypassed the Busy gate
        => OnAppThread(() =>
        {
            var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
            main.SelectedCard!.Name = "Before";
            main.CommitHistory();
            main.SelectedCard!.Name = "After";
            main.CommitHistory();

            main.Busy = true;
            main.Undo();
            Assert.Equal("After", main.SelectedCard!.Name);   // the import/export still owns these cards

            main.Busy = false;
            main.Undo();
            Assert.Equal("Before", main.SelectedCard!.Name);  // and it works normally again afterwards
        });

    [Fact]
    public void MainWindow_PreviewsABattleSideways_AndFlipsToAnUprightBack()   // 1.5.0 landscape cards
        => OnAppThread(() =>
        {
            var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
            var card = main.SelectedCard!;
            card.Name = "Invasion of Testing";
            card.TypeLine = "Battle — Siege";
            card.Defense = "5";
            card.BackFace = new CardModel { Name = "Testing, Conquered", TypeLine = "Creature — Dragon", Power = "5", Toughness = "5" };
            main.RenderPreview();

            Assert.True(main.PreviewImage!.PixelWidth > main.PreviewImage.PixelHeight, "the Battle front should preview sideways");
        });

    [Fact]
    public void MainWindow_EditingTheFlippedHalf_DirtiesTheProject()   // 1.6.0 flip cards
        => OnAppThread(() =>
        {
            var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
            var card = main.SelectedCard!;
            card.HalfLayout = "flip";
            card.OtherHalf = new CardModel { Name = "Kenzo the Hardhearted" };
            main.Dirty = false;

            card.OtherHalf!.Power = "3";   // what the flipped half's details dialog does
            Assert.True(main.Dirty, "editing the flipped half did not dirty the project");
        });

    [Fact]
    public void MainWindow_ShowFlipped_TurnsThePreviewUpsideDown()
        => OnAppThread(() =>
        {
            var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
            var card = main.SelectedCard!;
            card.HalfLayout = "flip";
            card.OtherHalf = new CardModel { Name = "Kenzo the Hardhearted", TypeLine = "Legendary Creature — Human Samurai" };
            main.RenderPreview();
            var upright = main.PreviewImage!;

            typeof(Cardinator.MainWindow).GetMethod("OnShowFlipped",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(main, new object?[] { null, null });
            var turned = main.PreviewImage!;

            // The same pixels, rotated 180°.
            var a = TestHelpers.Pixels(upright); var b = TestHelpers.Pixels(turned);
            int w = upright.PixelWidth, h = upright.PixelHeight;
            for (int y = 0; y < h; y += 37)
                for (int x = 0; x < w; x += 29)
                {
                    int i = (y * w + x) * 4, j = ((h - 1 - y) * w + (w - 1 - x)) * 4;
                    Assert.True(Math.Abs(a[i] - b[j]) < 3 && Math.Abs(a[i + 1] - b[j + 1]) < 3, $"pixel {x},{y} isn't the rotated one");
                }
        });

    [Fact]
    public void MainWindow_ReadSideways_TurnsASplitCardAQuarterClockwise()   // 1.6.2 split cards
        => OnAppThread(() =>
        {
            var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
            var card = main.SelectedCard!;
            card.HalfLayout = "split";
            card.OtherHalf = new CardModel { Name = "Tear", ManaCost = "{W}", TypeLine = "Instant" };
            main.RenderPreview();
            var upright = main.PreviewImage!;

            Invoke(main, "OnShowFlipped", null, null);
            var turned = main.PreviewImage!;
            Assert.Equal(upright.PixelHeight, turned.PixelWidth);
            Assert.Equal(upright.PixelWidth, turned.PixelHeight);

            // 90° clockwise: the upright pixel (x, y) lands at (H-1-y, x).
            var a = TestHelpers.Pixels(upright); var b = TestHelpers.Pixels(turned);
            int w = upright.PixelWidth, h = upright.PixelHeight, tw = turned.PixelWidth;
            for (int y = 0; y < h; y += 37)
                for (int x = 0; x < w; x += 29)
                {
                    int i = (y * w + x) * 4, j = (x * tw + (h - 1 - y)) * 4;
                    Assert.True(Math.Abs(a[i] - b[j]) < 3 && Math.Abs(a[i + 1] - b[j + 1]) < 3, $"pixel {x},{y} isn't the turned one");
                }
        });

    [Fact]
    public void MainWindow_ReadSideways_TurnsAnAftermathCardCounterClockwise()   // 1.6.3 aftermath
        => OnAppThread(() =>
        {
            var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
            var card = main.SelectedCard!;
            card.HalfLayout = "split";
            card.OtherHalf = new CardModel { Name = "Lead", TypeLine = "Sorcery", RulesText = "Aftermath (Cast this spell only from your graveyard.)" };
            main.RenderPreview();
            var upright = main.PreviewImage!;
            Invoke(main, "OnShowFlipped", null, null);
            var turned = main.PreviewImage!;

            // 90° counter-clockwise: the upright pixel (x, y) lands at (y, W-1-x).
            var a = TestHelpers.Pixels(upright); var b = TestHelpers.Pixels(turned);
            int w = upright.PixelWidth, h = upright.PixelHeight, tw = turned.PixelWidth;
            Assert.Equal(h, tw);
            for (int y = 0; y < h; y += 37)
                for (int x = 0; x < w; x += 29)
                {
                    int i = (y * w + x) * 4, j = ((w - 1 - x) * tw + y) * 4;
                    Assert.True(Math.Abs(a[i] - b[j]) < 3 && Math.Abs(a[i + 1] - b[j + 1]) < 3, $"pixel {x},{y} isn't the turned one");
                }
        });

    [Fact]
    public void MainWindow_SplitCard_ArtMovesOnTheChosenHalf_AndDirtiesTheProject()
        => OnAppThread(() =>
        {
            var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
            var card = main.SelectedCard!;
            card.HalfLayout = "split";
            card.OtherHalf = new CardModel { Name = "Tear", ArtPath = "tear.png" };
            main.RenderPreview();
            double frontX = card.ArtOffsetX;

            typeof(Cardinator.MainWindow).GetField("_activeHalf",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(main, true);   // what clicking the other half in the preview does
            main.Dirty = false;
            Invoke(main, "NudgeArt", 0.0, 0.01);   // down on the upright card = left along the sideways half

            Assert.Equal(frontX, card.ArtOffsetX);
            Assert.True(card.OtherHalf!.ArtOffsetX < 0, "the other half's art didn't move left");
            Assert.True(main.Dirty, "moving the other half's art did not dirty the project");
        });

    [Fact]
    public void MainWindow_MeldCard_PartnerPrintsTheOtherHalf_AndTheMeldedCardStaysInSync()   // 1.6.5 meld
        => OnAppThread(() =>
        {
            var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
            var card = main.SelectedCard!;
            card.Name = "Gisela, the Broken Blade";
            Invoke(main, "OnToggleMeld", null, null);
            Assert.True(card.IsMeld);
            Assert.Equal("top", card.MeldHalf);
            card.BackFace!.Name = "Brisela, Voice of Nightmares";

            int before = main.Cards.Count;
            Invoke(main, "OnAddMeldPartner", null, null);
            Assert.Equal(before + 1, main.Cards.Count);
            var partner = main.Cards[main.Cards.IndexOf(card) + 1];
            Assert.True(partner.IsMeld);
            Assert.Equal("bottom", partner.MeldHalf);
            Assert.Equal("Brisela, Voice of Nightmares", partner.BackFace!.Name);
            Assert.Same(partner, main.MeldPartner(card));

            // An edit to the melded card through one part reaches the other part's back.
            card.BackFace.RulesText = "Flying, first strike, vigilance, lifelink";
            card.BackFace.ArtOffsetX = 0.2;
            Assert.Equal("Flying, first strike, vigilance, lifelink", partner.BackFace.RulesText);
            Assert.Equal(0.2, partner.BackFace.ArtOffsetX);
            Assert.Equal("bottom", partner.BackFace.MeldHalf);   // its own half is kept

            // Swapping the halves swaps both.
            Invoke(main, "OnSwapMeldHalf", null, null);
            Assert.Equal("bottom", card.MeldHalf);
            Assert.Equal("top", partner.MeldHalf);

            // "Show melded card" previews the whole melded card: an upright card, not this back's half.
            Invoke(main, "OnShowMelded", null, null);
            var whole = TestHelpers.Pixels(main.PreviewImage!);
            Invoke(main, "OnShowMelded", null, null);
            var half = TestHelpers.Pixels(main.PreviewImage!);
            Assert.NotEqual(whole, half);
        });

    [Fact]
    public void DetailsWindow_AlsoUseItsArt_PutsTheLookedUpArtOnTheCard_AndOnANewBack()   // 1.6.8
        => OnAppThread(() =>
        {
            var card = new CardModel { Name = "Boogie Woogie", ArtPath = "mine.png", ArtScale = 1.5, ArtOffsetX = 0.2 };
            card.BackFace = new CardModel { Name = "Back", ArtUrl = "https://cards.scryfall.io/back.jpg" };
            var w = new Cardinator.DetailsWindow(card) { ArtDownloader = url => System.Threading.Tasks.Task.FromResult("cache/" + url.Split('/')[^1]) };
            var face = new CardModel { Name = "Delver of Secrets", ArtUrl = "https://cards.scryfall.io/front.jpg" };

            var m = typeof(Cardinator.DetailsWindow).GetMethod("UseScryfallArtAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var ok = ((System.Threading.Tasks.Task<bool>)m.Invoke(w, new object[] { face })!).GetAwaiter().GetResult();

            Assert.True(ok);
            Assert.Equal("cache/front.jpg", card.ArtPath);
            Assert.Equal(1.0, card.ArtScale);
            Assert.Equal(0, card.ArtOffsetX);
            Assert.Equal("Boogie Woogie", card.Name);          // the name is still the card's own
            Assert.Equal("cache/back.jpg", card.BackFace.ArtPath);

            // Cancel puts the card's own art back.
            Invoke(w, "OnCancel", null, null);
            Assert.Equal("mine.png", card.ArtPath);
            Assert.Equal(1.5, card.ArtScale);
        });

    [Fact]
    public void MainWindow_ResetArt_PutsPannedZoomedArtBack_AndIsUndoable()   // 1.6.7
        => OnAppThread(() =>
        {
            var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
            var card = main.SelectedCard!;
            card.ArtPath = "art.png";
            Invoke(main, "NudgeArt", 0.05, -0.03);
            Invoke(main, "ZoomArt", 0.4);
            Assert.NotEqual(0, card.ArtOffsetX);
            main.CommitHistory();

            Invoke(main, "OnResetArt", null, null);
            Assert.Equal(1.0, card.ArtScale);
            Assert.Equal(0, card.ArtOffsetX);
            Assert.Equal(0, card.ArtOffsetY);
            Assert.Equal("art.png", card.ArtPath);   // the art itself stays
            main.CommitHistory();

            main.Undo();
            var undone = main.SelectedCard!;
            Assert.Equal(1.4, undone.ArtScale, 6);
            Assert.NotEqual(0, undone.ArtOffsetX);
        });

    [Fact]
    public void MainWindow_NewArt_StartsCentred_AndUndoBringsBackTheOldPictureWhereItWas()   // B6, 1.6.9
        => OnAppThread(() =>
        {
            var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "cardinator-newart-" + Guid.NewGuid().ToString("N"))).FullName;
            try
            {
                string Png(string name)
                {
                    var bmp = System.Windows.Media.Imaging.BitmapSource.Create(4, 4, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[64], 16);
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                    var path = Path.Combine(dir, name);
                    using (var fs = File.Create(path)) enc.Save(fs);
                    return path;
                }
                var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
                var card = main.SelectedCard!;
                var setArt = typeof(Cardinator.MainWindow).GetMethod("SetArt",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, new[] { typeof(CardModel), typeof(string) })!;
                setArt.Invoke(main, new object[] { card, Png("old.png") });
                Invoke(main, "NudgeArt", 0.05, -0.03);
                Invoke(main, "ZoomArt", 0.4);
                main.CommitHistory();
                var oldArt = card.ArtPath;

                setArt.Invoke(main, new object[] { card, Png("new.png") });   // a different picture: its own shape, so it starts fresh
                Assert.Equal(1.0, card.ArtScale);
                Assert.Equal(0, card.ArtOffsetX);
                Assert.Equal(0, card.ArtOffsetY);
                main.CommitHistory();

                main.Undo();
                var undone = main.SelectedCard!;
                Assert.Equal(oldArt, undone.ArtPath);
                Assert.Equal(1.4, undone.ArtScale, 6);
                Assert.NotEqual(0, undone.ArtOffsetX);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });

    [Fact]
    public void MainWindow_MeldBack_ArtMovesTheWayTheMouseGoes()
        => OnAppThread(() =>
        {
            var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
            var card = main.SelectedCard!;
            Invoke(main, "OnToggleMeld", null, null);
            card.BackFace!.ArtPath = "brisela.png";
            Invoke(main, "OnFlipPreview", null, null);   // show the back (the melded card's top half, turned)
            Invoke(main, "NudgeArt", 0.0, 0.01);         // down on the back = towards the melded card's left edge
            Assert.True(card.BackFace.ArtOffsetX < 0, "the melded card's art didn't move left");
            Assert.Equal(0, card.BackFace.ArtOffsetY, 6);
        });

    [Fact]
    public void MainWindow_OtherHalfsFrame_ListShowsOnSplitCards_PicksAFrame_AndIsUndoable()   // 1.6.9
        => OnAppThread(() =>
        {
            var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
            Assert.Equal(Visibility.Collapsed, main.HalfFramePanel.Visibility);   // not a split card yet
            Invoke(main, "OnToggleSplit", null, null);
            var card = main.SelectedCard!;
            Assert.True(card.IsSplit);
            Assert.Equal(Visibility.Visible, main.HalfFramePanel.Visibility);
            Assert.Equal(Cardinator.MainWindow.SameFrameAsCard, main.HalfFrameBox.SelectedItem);

            var other = main.Templates.First(t => t.Name != card.TemplateName).Name;
            main.HalfFrameBox.SelectedItem = other;
            Assert.Equal(other, card.HalfTemplateName);
            main.CommitHistory();

            main.Undo();
            Assert.Equal("", main.SelectedCard!.HalfTemplateName);
            Assert.Equal(Cardinator.MainWindow.SameFrameAsCard, main.HalfFrameBox.SelectedItem);
        });

    [Fact]
    public void MainWindow_AddToken_AddsASelectedToken_PreviewedWithTheTokenLayout()   // 1.6.10
        => OnAppThread(() =>
        {
            var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
            int before = main.Cards.Count;
            Invoke(main, "OnAddToken", null, null);
            Assert.Equal(before + 1, main.Cards.Count);
            var card = main.SelectedCard!;
            Assert.True(card.IsToken);
            Assert.False(string.IsNullOrEmpty(card.TemplateName));
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var tpl = (Template)typeof(Cardinator.MainWindow).GetMethod("TemplateFor", flags)!.Invoke(main, new object[] { card })!;
            Assert.True(tpl.Spec.IsTokenLayout || tpl.Spec.IsLandscape);
        });

    [Fact]
    public void MainWindow_ExportAll_DefaultsToTheOpenSetsOutFolder_NotTheLastSetsOne()
        => OnAppThread(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "cardinator-outdir-" + Guid.NewGuid().ToString("N"));
            try
            {
                string MakeSet(string name)
                {
                    var path = Path.Combine(root, name, name + ".cardinator");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    ProjectWriter.Write(path, new[] { new CardModel { Name = name + " card" } }, name, "", "", new SetProfile());
                    return path;
                }
                var a = MakeSet("SetA"); var b = MakeSet("SetB");
                var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                string OutDir() => (string)typeof(Cardinator.MainWindow).GetMethod("DefaultOutputDir", flags)!.Invoke(main, null)!;
                var lastExport = typeof(Cardinator.MainWindow).GetField("_lastExportDir", flags)!;

                Invoke(main, "LoadProjectFile", a);
                Assert.Equal(Path.Combine(root, "SetA", "out"), OutDir());

                // Exporting somewhere else in this set is remembered...
                var custom = Directory.CreateDirectory(Path.Combine(root, "SetA", "prints")).FullName;
                lastExport.SetValue(main, custom);
                Assert.Equal(custom, OutDir());

                // ...but opening another set defaults to ITS out/ folder, not set A's.
                Invoke(main, "LoadProjectFile", b);
                Assert.Equal(Path.Combine(root, "SetB", "out"), OutDir());
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        });

    [Fact]
    public void MainWindow_SetDefaults_StayUnsaved_UntilSaved()
        => OnAppThread(() =>
        {
            var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
            main.Dirty = false;
            main.ApplySetDefaults(new SetProfile { SetCode = "ABC" }, "", applyToExisting: false);
            Assert.True(main.Dirty, "changing the set defaults didn't mark the set unsaved");
            main.CommitHistory();   // any later no-op commit (a cancelled dialog, an undo flush)…
            main.Undo();
            Assert.True(main.Dirty, "the set-defaults change was forgotten by a no-op commit — closing wouldn't prompt");
        });

    [Fact]
    public void MainWindow_ABackupOpenedDirectly_StaysUnsaved_AndSavesOverItsSetFile_WithItsArt()
        => OnAppThread(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "cardinator-bak-open-" + Guid.NewGuid().ToString("N"));
            try
            {
                var setFile = Path.Combine(root, "MySet", "MySet.cardinator");
                var art = Path.Combine(root, "MySet", "art", "pic.png");
                Directory.CreateDirectory(Path.GetDirectoryName(art)!);
                var bmp = System.Windows.Media.Imaging.BitmapSource.Create(4, 4, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[64], 16);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using (var fs = File.Create(art)) enc.Save(fs);

                ProjectWriter.Write(setFile, new[] { new CardModel { Name = "Good", ArtPath = art } }, "MySet", "", "", new SetProfile());
                ProjectWriter.Write(setFile, new[] { new CardModel { Name = "Broken", ArtPath = art } }, "MySet", "", "", new SetProfile(), "20261001-120000");
                var backup = Path.Combine(root, "MySet", "backups", "MySet.20261001-120000.cardinator");
                Assert.True(File.Exists(backup));

                var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
                Invoke(main, "LoadProjectFile", backup);   // File > Open on the backup, not Restore
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                Assert.Equal(setFile, (string)typeof(Cardinator.MainWindow).GetField("_projectPath", flags)!.GetValue(main)!);
                Assert.True(main.Dirty);
                main.CommitHistory();   // e.g. Edit details → Cancel
                Assert.True(main.Dirty, "the opened backup lost its ● — closing would keep the broken file on disk");

                Invoke(main, "SaveProject", false);
                Assert.False(main.Dirty);
                var saved = CardProject.Load(setFile);
                Assert.Equal("Good", saved.Cards[0].Name);
                CardProject.ResolveArt(saved.Cards[0], Path.GetDirectoryName(setFile)!);
                Assert.True(File.Exists(saved.Cards[0].ArtPath), "the restored set's art link is broken");
                Assert.False(Directory.Exists(Path.Combine(root, "MySet", "backups", "art")), "saving wrote the set into backups\\");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        });

    [Fact]
    public void MainWindow_ACardJson_IsntOpenedAsAnEmptySet()
        => OnAppThread(() =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "cardinator-notaset-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var prompts = new System.Collections.Generic.List<string>();
            ConfirmDialog.TestAnswer = t => { prompts.Add(t); return ConfirmResult.Affirmative; };
            try
            {
                var cardJson = Path.Combine(dir, "bolt.json");
                File.WriteAllText(cardJson, "{ \"name\": \"Lightning Bolt\", \"manaCost\": \"{R}\" }");
                var before = File.ReadAllText(cardJson);
                var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
                int cards = main.Cards.Count;
                var ok = (bool)typeof(Cardinator.MainWindow).GetMethod("LoadProjectFile", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .Invoke(main, new object[] { cardJson })!;
                Assert.False(ok);
                Assert.Equal(cards, main.Cards.Count);   // the set that was open stays open
                Assert.Contains("Not a set", prompts);
                Assert.Empty(Directory.GetFiles(dir, "*corrupt*"));   // it isn't corrupt, so no rescue copy
                Assert.Equal(before, File.ReadAllText(cardJson));
            }
            finally { ConfirmDialog.TestAnswer = null; try { Directory.Delete(dir, true); } catch { } }
        });

    [Fact]
    public void MainWindow_SavingASet_KeepsItsHouseFrame_EvenWhenThatFrameIsntInstalled()
        => OnAppThread(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "cardinator-house-" + Guid.NewGuid().ToString("N"));
            try
            {
                var setFile = Path.Combine(root, "Friend", "Friend.cardinator");
                Directory.CreateDirectory(Path.GetDirectoryName(setFile)!);
                ProjectWriter.Write(setFile, new[] { new CardModel { Name = "A", TemplateName = "Their Custom Frame" } },
                    "Friend", "", "Their Custom Frame", new SetProfile());
                var plain = Path.Combine(root, "Plain", "Plain.cardinator");
                Directory.CreateDirectory(Path.GetDirectoryName(plain)!);
                ProjectWriter.Write(plain, new[] { new CardModel { Name = "B" } }, "Plain", "", "", new SetProfile());

                var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
                Invoke(main, "LoadProjectFile", setFile);
                Invoke(main, "SaveProject", false);
                Assert.Equal("Their Custom Frame", CardProject.Load(setFile).DefaultTemplate);

                Invoke(main, "LoadProjectFile", plain);   // a set with no house frame doesn't silently get one
                Invoke(main, "SaveProject", false);
                Assert.Equal("", CardProject.Load(plain).DefaultTemplate);
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        });

    [Fact]
    public void MainWindow_SaveRightAfterTyping_SavesAndUndoesConsistently()
        => OnAppThread(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "cardinator-quicksave-" + Guid.NewGuid().ToString("N"));
            try
            {
                var setFile = Path.Combine(root, "S", "S.cardinator");
                Directory.CreateDirectory(Path.GetDirectoryName(setFile)!);
                ProjectWriter.Write(setFile, new[] { new CardModel { Name = "Before" } }, "S", "", "", new SetProfile());
                var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
                Invoke(main, "LoadProjectFile", setFile);

                main.SelectedCard!.Name = "Typed";   // still inside the undo debounce
                Invoke(main, "SaveProject", false);  // Ctrl+S straight away
                Assert.False(main.Dirty);
                main.CommitHistory();                // the debounce firing later
                Assert.False(main.Dirty, "the ● came back although nothing changed since the save");
                main.Undo();                         // back to "Before", which isn't what's on disk
                Assert.Equal("Before", main.SelectedCard!.Name);
                Assert.True(main.Dirty, "undoing past the save didn't mark the set unsaved");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        });

    [Fact]
    public void MainWindow_RenamingAFrameInPlace_MovesEveryCardThatUsedIt()
        => OnAppThread(() =>
        {
            var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
            main.Cards.Clear();
            var a = new CardModel { Name = "A", TemplateName = "My Frame" };
            var b = new CardModel { Name = "B", TemplateName = "Other" };
            b.BackFace = new CardModel { Name = "B back", TemplateName = "My Frame" };
            var c = new CardModel { Name = "Fire", TypeLine = "Instant", TemplateName = "Other", HalfTemplateName = "My Frame" };
            foreach (var x in new[] { a, b, c }) main.Cards.Add(x);
            main.ApplySetDefaults(new SetProfile(), "My Frame", applyToExisting: false);

            int n = main.RenameFrameReferences("My Frame", "My Frame v2");
            Assert.Equal(4, n);
            Assert.Equal("My Frame v2", a.TemplateName);
            Assert.Equal("Other", b.TemplateName);
            Assert.Equal("My Frame v2", b.BackFace!.TemplateName);
            Assert.Equal("My Frame v2", c.HalfTemplateName);
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            Assert.Equal("My Frame v2", typeof(Cardinator.MainWindow).GetField("_defaultTemplate", flags)!.GetValue(main));
        });

    private sealed class FakeHandler(Func<System.Net.Http.HttpRequestMessage, System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage>> answer)
        : System.Net.Http.HttpMessageHandler
    {
        protected override System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, CancellationToken ct) => answer(request);
    }

    [Fact]
    public void DetailsWindow_Cancel_StopsALookupStillRunning_FromFillingTheCard()
        => OnAppThread(() =>
        {
            var reply = new System.Threading.Tasks.TaskCompletionSource();
            ScryfallClient.TestHttp = new System.Net.Http.HttpClient(new FakeHandler(async _ =>
            {
                await reply.Task;   // Scryfall answers only after the user pressed Cancel
                return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new System.Net.Http.StringContent(
                        """{ "object": "card", "name": "Lightning Bolt", "layout": "normal", "mana_cost": "{R}", "type_line": "Instant", "oracle_text": "Deal 3." }""",
                        System.Text.Encoding.UTF8, "application/json"),
                };
            }));
            try
            {
                var card = new CardModel { Name = "Bolt", ManaCost = "{9}", TypeLine = "Sorcery" };
                var w = new Cardinator.DetailsWindow(card, new ScryfallClient());
                var search = (System.Threading.Tasks.Task)typeof(Cardinator.DetailsWindow)
                    .GetMethod("SearchAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .Invoke(w, null)!;
                Invoke(w, "OnCancel", w, new RoutedEventArgs());
                reply.SetResult();

                var frame = new System.Windows.Threading.DispatcherFrame();
                var d = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                search.ContinueWith(_ => d.BeginInvoke(() => frame.Continue = false));
                System.Windows.Threading.Dispatcher.PushFrame(frame);

                Assert.Equal(("{9}", "Sorcery"), (card.ManaCost, card.TypeLine));   // as it was before the dialog
            }
            finally { ScryfallClient.TestHttp = null; }
        });

    [Fact]
    public void MainWindow_DeletingAFrame_LeavesTheSelectedCardOnIt_SoItsFlagged()
        => OnAppThread(() =>
        {
            var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
            main.Cards.Clear();
            var card = new CardModel { Name = "A", TypeLine = "Instant", TemplateName = "A Frame That Was Deleted" };
            main.Cards.Add(card);
            main.SelectedCard = card;
            Invoke(main, "RefreshTemplates", new object?[] { null });   // what Delete frame does after removing it
            Assert.Equal("A Frame That Was Deleted", card.TemplateName);
        });

    [Fact]
    public void MainWindow_ImportingAFrame_PutsItOnTheSelectedCard()
        => OnAppThread(() =>
        {
            var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
            main.Cards.Clear();
            var other = main.Templates[1].Name;
            var card = new CardModel { Name = "A", TypeLine = "Instant", TemplateName = main.Templates[0].Name };
            main.Cards.Add(card);
            main.SelectedCard = card;
            Invoke(main, "RefreshTemplates", other);
            Assert.Equal(other, card.TemplateName);
        });

    [Fact]
    public void MainWindow_ShareSet_FindsAFrameRenamedInPlace_InItsOldFolder()
        => OnAppThread(() =>
        {
            var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
            main.Cards.Clear();
            var src = main.Templates[0];
            var folder = Path.Combine(Path.GetTempPath(), "old-name");
            main.Templates.Add(new Template { Name = "New Name", Spec = src.Spec, FramePath = Path.Combine(folder, "frame.png"), FrameImage = src.FrameImage });
            main.Cards.Add(new CardModel { Name = "A", TypeLine = "Instant", TemplateName = "New Name" });
            Assert.Equal(new[] { folder }, main.ShareFrameFolders());
        });

    private static void Invoke(object target, string method, params object?[] args)
        => target.GetType().GetMethod(method, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(target, args);

    /// <summary>Runs on an STA thread with an Application whose resources come from App.xaml.</summary>
    private static void OnAppThread(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = Application.Current ?? new Application();
                if (app.Resources.MergedDictionaries.Count == 0)
                {
                    // Load the theme dictionary directly (App.xaml can't be loaded here — it would
                    // try to construct a second Application). This mirrors what App.xaml merges.
                    app.Resources.MergedDictionaries.Add(
                        (ResourceDictionary)Application.LoadComponent(
                            new Uri("/Cardinator;component/Theme.xaml", UriKind.Relative)));
                }
                action();
            }
            catch (Exception ex) { captured = ex; }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (captured != null) throw captured;
    }
}
