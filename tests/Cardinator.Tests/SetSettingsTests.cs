using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media.Imaging;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// 1.7.0: one "Set settings…" window in place of "Set defaults…" and "Set fields on all…", and the set's card back
/// (Cardinator's, one designed for the set, or the user's own picture) that travels with the set.
/// </summary>
[Collection("STAWindows")]
public class SetSettingsTests
{
    private static string TempRoot(string tag) => Path.Combine(Path.GetTempPath(), $"cardinator-{tag}-{Guid.NewGuid():N}");

    private static SetProfile ProfileOf(Cardinator.MainWindow main)
        => (SetProfile)typeof(Cardinator.MainWindow).GetField("_setProfile", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;

    // --- the window -------------------------------------------------------------------------------------

    [Fact]
    public void TheWindow_ShowsTheSetsSettings_AndHandsThemBack()
        => OnAppThread(() =>
        {
            var current = new SetProfile
            {
                SetCode = "CDN", Rarity = "R", Artist = "Sam", Copyright = "© Me",
                CardBack = new CardBackDesign { Style = CardBackDesign.DesignedStyle, Wordmark = "MY SET", Color = "navy" },
            };
            var w = new SetSettingsWindow(current, "Ocean Blue", new[] { "Gold Multicolor", "Ocean Blue" }, "My Set");
            Assert.Equal("CDN", w.SetBox.Text);
            Assert.Equal("R", w.RarityBox.SelectedItem);
            Assert.Equal("Ocean Blue", w.FrameBox.SelectedItem);
            Assert.True(w.BackDesigned.IsChecked);
            Assert.Equal(Visibility.Visible, w.DesignedPanel.Visibility);
            Assert.NotNull(w.BackPreview.Source);
            Assert.Equal(Visibility.Collapsed, w.OnlySelectedBox.Visibility);   // no selection to stick to

            w.ArtistBox.Text = "Alex";
            w.ModeReplace.IsChecked = true;
            Assert.Equal(Visibility.Visible, w.ReplacePanel.Visibility);
            w.ClearCopyrightBox.IsChecked = true;
            w.Collect();

            Assert.Equal("Alex", w.Profile.Artist);
            Assert.Equal("MY SET", w.Profile.CardBack!.Wordmark);
            Assert.Equal("Ocean Blue", w.DefaultFrame);
            Assert.Equal(SetSettingsMode.Replace, w.Mode);
            Assert.True(w.ClearCopyright);
            Assert.False(w.ClearArtist);
            Assert.False(w.OnlySelected);
        });

    [Fact]
    public void TheWindow_OffersTheSelection_WhenSomeCardsAreSelected()
        => OnAppThread(() =>
        {
            var w = new SetSettingsWindow(new SetProfile(), "", new[] { "A" }, "S", selectedCount: 3);
            Assert.Equal(Visibility.Visible, w.OnlySelectedBox.Visibility);
            Assert.Contains("3 selected", (string)w.OnlySelectedBox.Content);
            w.ModeReplace.IsChecked = true;
            w.Collect();
            Assert.True(w.OnlySelected);
        });

    [Fact]
    public void TheDefaultBack_IsNotStored_AndClearsOnlyOutsideReplace()
        => OnAppThread(() =>
        {
            var w = new SetSettingsWindow(new SetProfile(), "", new[] { "A" }, "S");
            w.ClearArtistBox.IsChecked = true;   // ticked, then Replace not chosen: it must not clear anything
            w.Collect();
            Assert.Null(w.Profile.CardBack);
            Assert.True(w.Profile.IsEmpty);
            Assert.False(w.ClearArtist);
            Assert.Equal(SetSettingsMode.NewCardsOnly, w.Mode);
        });

    // --- applying them ----------------------------------------------------------------------------------

    [Fact]
    public void Replace_PutsTheFilledInFieldsOnEveryCard_KeepsBlanks_ClearsTheTicked_AsOneUndo()
        => OnAppThread(() =>
        {
            var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
            main.Cards.Clear();
            var a = new CardModel { Name = "A", Artist = "Old", Copyright = "© Old", SetCode = "OLD" };
            var b = new CardModel { Name = "B", Artist = "Old", Copyright = "© Old", SetCode = "OLD" };
            main.Cards.Add(a); main.Cards.Add(b);
            main.SelectedCard = a;
            main.CommitHistory();

            main.ApplySetSettings(new SetProfile { Artist = "New", SetCode = "" }, "", SetSettingsMode.Replace,
                clearCopyright: true);
            Assert.All(main.Cards, c => Assert.Equal("New", c.Artist));
            Assert.All(main.Cards, c => Assert.Equal("OLD", c.SetCode));   // blank: left as it was
            Assert.All(main.Cards, c => Assert.Equal("", c.Copyright));    // ticked: emptied
            Assert.Equal("New", ProfileOf(main).Artist);
            Assert.True(main.Dirty);

            main.Undo();
            Assert.All(main.Cards, c => Assert.Equal("Old", c.Artist));
            Assert.All(main.Cards, c => Assert.Equal("© Old", c.Copyright));
        });

    [Fact]
    public void Replace_OnlyTheSelected_LeavesTheOthers()
        => OnAppThread(() =>
        {
            var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
            main.Cards.Clear();
            var a = new CardModel { Name = "A" };
            var b = new CardModel { Name = "B" };
            main.Cards.Add(a); main.Cards.Add(b);
            main.ApplySetSettings(new SetProfile { SetCode = "ONLYA" }, "", SetSettingsMode.Replace, targets: new[] { a });
            Assert.Equal("ONLYA", a.SetCode);
            Assert.Equal("", b.SetCode);
        });

    [Fact]
    public void FillBlanks_FillsOnlyBlanks_AndNewCardsOnly_TouchesNoCard()
        => OnAppThread(() =>
        {
            var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
            main.Cards.Clear();
            var a = new CardModel { Name = "A", Artist = "Mine" };
            var b = new CardModel { Name = "B" };
            main.Cards.Add(a); main.Cards.Add(b);

            main.ApplySetSettings(new SetProfile { Artist = "House" }, "", SetSettingsMode.NewCardsOnly);
            Assert.Equal("", b.Artist);

            main.ApplySetSettings(new SetProfile { Artist = "House" }, "", SetSettingsMode.FillBlanks);
            Assert.Equal("Mine", a.Artist);
            Assert.Equal("House", b.Artist);
        });

    // --- the card back ----------------------------------------------------------------------------------

    [Fact]
    public void TheCardBack_RoundTrips_AndItsPictureTravelsWithTheSet()
    {
        var root = TempRoot("cardback");
        try
        {
            Directory.CreateDirectory(root);
            var picture = Path.Combine(root, "back.png");
            File.WriteAllBytes(picture, TestHelpers.PngBytes(10, 14));
            var setFile = Path.Combine(root, "Set", "Set.cardinator");
            Directory.CreateDirectory(Path.GetDirectoryName(setFile)!);
            var profile = new SetProfile { CardBack = new CardBackDesign { Style = CardBackDesign.PictureStyle, ImagePath = picture } };
            ProjectWriter.Write(setFile, new[] { new CardModel { Name = "A" } }, "Set", "", "", profile);

            var raw = CardProject.Load(setFile).Profile!.CardBack!;
            Assert.Equal(CardBackDesign.PictureStyle, raw.Style);
            Assert.StartsWith("art", raw.ImagePath);   // written relative to the set, copied into it

            var resolved = CardProject.LoadResolved(setFile, out _).Profile!.CardBack!;
            Assert.True(File.Exists(resolved.ImagePath));
            Assert.StartsWith(Path.Combine(root, "Set"), resolved.ImagePath);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void AnOlderSet_WithNoCardBack_LoadsWithTheDefaultBack()
    {
        var json = """{ "name": "Old", "profile": { "setCode": "OLD" }, "cards": [ { "name": "A" } ] }""";
        var path = Path.Combine(Path.GetTempPath(), $"cardinator-oldback-{Guid.NewGuid():N}.cardinator");
        try
        {
            File.WriteAllText(path, json);
            var p = CardProject.Load(path);
            Assert.Equal("OLD", p.Profile!.SetCode);
            Assert.Null(p.Profile.CardBack);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void IsDefault_AndIsEmpty_FollowTheBack()
    {
        Assert.True(new CardBackDesign().IsDefault);
        Assert.True(new CardBackDesign { Style = CardBackDesign.PictureStyle }.IsDefault);   // a picture back with no picture
        Assert.False(new CardBackDesign { Style = CardBackDesign.DesignedStyle }.IsDefault);
        Assert.True(new SetProfile { CardBack = new CardBackDesign() }.IsEmpty);
        Assert.False(new SetProfile { CardBack = new CardBackDesign { Style = "designed" } }.IsEmpty);
        var p = new SetProfile { CardBack = new CardBackDesign { Style = "designed", Wordmark = "W" } };
        var c = p.Clone();
        c.CardBack!.Wordmark = "X";
        Assert.Equal("W", p.CardBack!.Wordmark);   // the clone owns its own back
    }

    [Fact]
    public void TheBacks_LookDifferent_AndABadPictureFallsBack()
        => OnAppThread(() =>
        {
            var root = TempRoot("backrender");
            Directory.CreateDirectory(root);
            try
            {
                var standard = BackRenderer.Render(null, "S", null, 1);
                var designed = BackRenderer.Render(new CardBackDesign { Style = "designed", Color = "#7A1020", Wordmark = "RED" }, "S", null, 1);
                var bad = BackRenderer.Render(new CardBackDesign { Style = "picture", ImagePath = Path.Combine(root, "missing.png") }, "S", null, 1);
                Assert.Equal(750, standard.PixelWidth);
                Assert.Equal(1050, standard.PixelHeight);
                Assert.NotEqual(Pixel(standard, 375, 120), Pixel(designed, 375, 120));
                Assert.Equal(Pixel(standard, 375, 120), Pixel(bad, 375, 120));   // missing picture: Cardinator's back

                var red = Path.Combine(root, "red.png");
                SolidPng(red, 0xFF, 0x00, 0x00);
                var pic = BackRenderer.Render(new CardBackDesign { Style = "picture", ImagePath = red }, "S", null, 1);
                var mid = Pixel(pic, 375, 525);
                Assert.True(mid.r > 240 && mid.g < 20 && mid.b < 20, $"the picture didn't fill the back: {mid}");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        });

    private static (byte r, byte g, byte b) Pixel(BitmapSource bmp, int x, int y)
    {
        var px = new byte[4];
        new FormatConvertedBitmap(bmp, System.Windows.Media.PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
        return (px[2], px[1], px[0]);
    }

    private static void SolidPng(string path, byte r, byte g, byte b)
    {
        const int w = 30, h = 42;
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255; }
        var bmp = BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, px, w * 4);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static object? Invoke(object target, string method, params object[] args)
        => target.GetType().GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
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
                try { action(); }
                finally { TestHelpers.EndUiThread(); }
            }
            catch (TargetInvocationException ex) { captured = ex.InnerException ?? ex; }
            catch (Exception ex) { captured = ex; }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (captured != null) throw captured;
    }
}
