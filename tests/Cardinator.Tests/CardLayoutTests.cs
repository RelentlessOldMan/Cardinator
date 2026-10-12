using System.IO;
using System.Reflection;
using System.Windows;
using Cardinator.Models;
using Cardinator.Services;
using Layout = Cardinator.MainWindow.CardLayout;

namespace Cardinator.Tests;

/// <summary>
/// 1.7.0: one Card layout dropdown in place of the double-faced / meld / flip / split buttons, a Front | Back switch
/// above the preview (the back is the card's back face, or the set's card back), a half switch for flip and split
/// cards, and the editor following the side being shown.
/// </summary>
[Collection("STAWindows")]
public class CardLayoutTests
{
    private static Cardinator.MainWindow NewMain(out CardModel card)
    {
        var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
        card = main.SelectedCard!;
        return main;
    }

    [Theory]
    [InlineData(Layout.DoubleFaced)]
    [InlineData(Layout.Meld)]
    [InlineData(Layout.Flip)]
    [InlineData(Layout.Split)]
    [InlineData(Layout.Adventure)]
    public void EveryLayout_CanBeChosen_AndUndone(Layout layout)
        => OnAppThread(() =>
        {
            var main = NewMain(out var card);
            Assert.Equal(Layout.Normal, Cardinator.MainWindow.LayoutOf(card));
            Assert.True(main.ChangeLayout(card, layout));
            Assert.Equal(layout, Cardinator.MainWindow.LayoutOf(card));
            Assert.Equal(layout, main.ShownLayout);
            main.RenderPreview();
            Assert.NotNull(main.PreviewImage);

            main.Undo();
            Assert.Equal(Layout.Normal, Cardinator.MainWindow.LayoutOf(main.SelectedCard!));
            Assert.Equal(Layout.Normal, main.ShownLayout);
        });

    [Fact]
    public void DoubleFacedAndMeld_KeepTheBackFace_FlipAndSplit_KeepTheOtherHalf_WithoutAsking()
        => OnAppThread(() =>
        {
            var main = NewMain(out var card);
            ConfirmDialog.TestAnswer = _ => throw new Xunit.Sdk.XunitException("asked, though nothing is lost");
            try
            {
                main.ChangeLayout(card, Layout.DoubleFaced);
                card.BackFace!.Name = "The Back";
                main.ChangeLayout(card, Layout.Meld);
                Assert.Equal("The Back", card.BackFace!.Name);
                Assert.Equal("top", card.MeldHalf);
                main.ChangeLayout(card, Layout.DoubleFaced);
                Assert.Equal("The Back", card.BackFace!.Name);
                Assert.False(card.IsMeld);
                Assert.Equal("sunmoon", card.DfcStyle);
            }
            finally { ConfirmDialog.TestAnswer = null; }

            ConfirmDialog.TestAnswer = _ => ConfirmResult.Affirmative;
            try { main.ChangeLayout(card, Layout.Flip); }
            finally { ConfirmDialog.TestAnswer = null; }
            card.OtherHalf!.Name = "Other";
            ConfirmDialog.TestAnswer = _ => throw new Xunit.Sdk.XunitException("asked, though nothing is lost");
            try
            {
                main.ChangeLayout(card, Layout.Split);
                Assert.Equal("Other", card.OtherHalf!.Name);
                Assert.True(card.IsSplit);
            }
            finally { ConfirmDialog.TestAnswer = null; }
        });

    [Fact]
    public void LosingABackFaceWithContent_Asks_AndCancelKeepsEverything()
        => OnAppThread(() =>
        {
            var main = NewMain(out var card);
            main.ChangeLayout(card, Layout.DoubleFaced);
            card.BackFace!.Name = "Keep Me";
            main.CommitHistory();

            string? asked = null;
            ConfirmDialog.TestAnswer = t => { asked = t; return ConfirmResult.Cancel; };
            try { Assert.False(main.ChangeLayout(card, Layout.Split)); }
            finally { ConfirmDialog.TestAnswer = null; }
            Assert.NotNull(asked);
            Assert.Equal("Keep Me", card.BackFace!.Name);
            Assert.Null(card.OtherHalf);
            Assert.Equal(Layout.DoubleFaced, main.ShownLayout);
        });

    [Fact]
    public void AnEmptyBackFace_GoesWithoutAsking()
        => OnAppThread(() =>
        {
            var main = NewMain(out var card);
            main.ChangeLayout(card, Layout.DoubleFaced);
            ConfirmDialog.TestAnswer = _ => throw new Xunit.Sdk.XunitException("asked about an empty back face");
            try { Assert.True(main.ChangeLayout(card, Layout.Normal)); }
            finally { ConfirmDialog.TestAnswer = null; }
            Assert.Null(card.BackFace);
        });

    [Fact]
    public void LeavingAdventure_ClearsIt()
        => OnAppThread(() =>
        {
            var main = NewMain(out var card);
            main.ChangeLayout(card, Layout.Adventure);
            Assert.True(card.IsAdventure);
            card.AdventureName = "Quest";
            ConfirmDialog.TestAnswer = _ => ConfirmResult.Affirmative;
            try { main.ChangeLayout(card, Layout.Normal); }
            finally { ConfirmDialog.TestAnswer = null; }
            Assert.False(card.IsAdventure);
            Assert.Equal("", card.AdventureName);
        });

    [Fact]
    public void TheEditor_FollowsTheSideShown()
        => OnAppThread(() =>
        {
            var main = NewMain(out var card);
            Assert.Same(card, main.EditedFace);
            Assert.Equal("CARD NAME", main.NameLabel.Text);

            main.ChangeLayout(card, Layout.DoubleFaced);
            main.ShowSide(back: true);
            Assert.Same(card.BackFace, main.EditedFace);
            Assert.Equal("BACK FACE NAME", main.NameLabel.Text);
            Assert.True(main.BackSeg.IsChecked);
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
            Assert.Same(card.BackFace, main.NameBox.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)!.ResolvedSource);
            main.NameBox.Text = "Typed On The Back";   // the name box edits the back face
            Assert.Equal("Typed On The Back", card.BackFace!.Name);
            Assert.NotEqual("Typed On The Back", card.Name);

            main.ShowSide(back: false);
            Assert.Same(card, main.EditedFace);
            Assert.True(main.FrontSeg.IsChecked);
        });

    [Fact]
    public void FlipAndSplit_TheHalfSwitch_PicksTheHalfEdited()
        => OnAppThread(() =>
        {
            var main = NewMain(out var card);
            main.ChangeLayout(card, Layout.Flip);
            Assert.Equal(Visibility.Visible, main.HalfBar.Visibility);
            Assert.Equal("Flipped half", main.OtherHalfSeg.Content);
            main.OtherHalfSeg.IsChecked = true;
            Assert.Same(card.OtherHalf, main.EditedFace);
            Assert.Equal("FLIPPED HALF NAME", main.NameLabel.Text);

            ConfirmDialog.TestAnswer = _ => ConfirmResult.Affirmative;
            try { main.ChangeLayout(card, Layout.Split); }
            finally { ConfirmDialog.TestAnswer = null; }
            Assert.Same(card, main.EditedFace);   // a new layout starts on the first half
            Assert.Equal(Visibility.Visible, main.SidewaysBox.Visibility);
            main.OtherHalfSeg.IsChecked = true;
            Assert.Same(card.OtherHalf, main.EditedFace);
            Assert.Equal("SECOND HALF NAME", main.NameLabel.Text);

            main.ShowSide(back: true);   // a split card's back is the set's card back: no halves there
            Assert.Equal(Visibility.Collapsed, main.HalfBar.Visibility);
            Assert.Null(main.EditedFace);
        });

    [Fact]
    public void ANormalCardsBack_IsTheSetsCardBack()
        => OnAppThread(() =>
        {
            var main = NewMain(out var card);
            main.RenderPreview();
            var front = TestHelpers.Pixels(main.PreviewImage!);
            main.ShowSide(back: true);
            Assert.Null(main.EditedFace);
            Assert.Equal(Visibility.Visible, main.CardBackNote.Visibility);
            Assert.Equal(Visibility.Collapsed, main.FaceEditor.Visibility);
            Assert.Equal(Visibility.Collapsed, main.FaceFooter.Visibility);
            var back = main.PreviewImage!;
            Assert.NotEqual(front, TestHelpers.Pixels(back));

            // The set's own back shows once the set has one.
            main.ApplySetSettings(new SetProfile { CardBack = new CardBackDesign { Style = "designed", Color = "#7A1020" } }, "",
                SetSettingsMode.NewCardsOnly);
            main.RenderPreview();
            Assert.NotEqual(TestHelpers.Pixels(back), TestHelpers.Pixels(main.PreviewImage!));

            // Picking another card starts on its front.
            var other = new CardModel { Name = "Other", TemplateName = card.TemplateName };
            main.Cards.Add(other);
            main.SelectedCard = other;
            Assert.Same(other, main.EditedFace);
            Assert.True(main.FrontSeg.IsChecked);
        });

    [Fact]
    public void Meld_TheWholeMeldedCard_ShowsOnTheBack()
        => OnAppThread(() =>
        {
            var main = NewMain(out var card);
            main.ChangeLayout(card, Layout.Meld);
            Assert.Equal(Visibility.Collapsed, main.WholeMeldBox.Visibility);   // only on the back
            main.ShowSide(back: true);
            Assert.Equal(Visibility.Visible, main.WholeMeldBox.Visibility);
            Assert.Equal("MELDED CARD NAME", main.NameLabel.Text);
            main.MeldHalfBox.SelectedItem = "bottom half";
            Assert.Equal("bottom", card.MeldHalf);
        });

    [Fact]
    public void ImportCards_OpensAListFile_IntoTheBox_AndKeepsItsFolder()
        => OnAppThread(() =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"cardinator-importfile-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var file = Path.Combine(dir, "list.csv");
                File.WriteAllText(file, "name,art\nLightning Bolt,bolt.png\n");
                var w = new DeckListWindow();
                w.LoadFile(file);
                Assert.Contains("Lightning Bolt", w.DeckBox.Text);
                Assert.Equal(dir, w.BaseDir);
                Assert.Equal("list.csv", w.FileName);
                Assert.True(w.TryAccept());
                Assert.Contains("Lightning Bolt", w.DeckText);

                var empty = new DeckListWindow();
                Assert.False(empty.TryAccept());
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });

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
