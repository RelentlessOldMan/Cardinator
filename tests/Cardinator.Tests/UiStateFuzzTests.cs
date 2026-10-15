using System.Reflection;
using System.Windows;
using Cardinator.Models;
using Cardinator.Services;
using Layout = Cardinator.MainWindow.CardLayout;

namespace Cardinator.Tests;

/// <summary>
/// 1.7.0: hammers the main window's card-layout UI with random sequences — layout changes (answered yes or no),
/// Front | Back, the half switch, Read sideways, the whole melded card, meld halves and partners, new / deleted /
/// selected cards, set settings, art nudges, name edits, double and triple clicks, undo and redo — and checks after
/// every step that what the window shows matches the card, and that undo always lands on a state the set was in.
/// A failure names the seed and the steps that led to it.
/// </summary>
[Collection("STAWindows")]
public class UiStateFuzzTests
{
    private const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

    private static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, Any)!.GetValue(o)!;
    private static object? Call(object o, string name, params object[] args)
        => o.GetType().GetMethods(Any).First(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(o, args);

    /// <summary>Seeds 1–6; set CARDINATOR_FUZZ_SEEDS=200 to hammer harder.</summary>
    public static IEnumerable<object[]> Seeds()
    {
        int n = int.TryParse(Environment.GetEnvironmentVariable("CARDINATOR_FUZZ_SEEDS"), out var v) && v > 0 ? v : 6;
        return Enumerable.Range(1, n).Select(i => new object[] { i });
    }

    [Fact]
    public void AFreshWindow_HasNothingToUndo_AndIsSaved()
        => OnAppThread(() =>
        {
            // Filling the dropdowns after the first card was selected wrote "none" into its corner mark.
            var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            main.CommitHistory();
            Assert.False(main.CanUndo);
            Assert.False(main.Dirty);
            Assert.Equal("", main.SelectedCard!.DfcStyle);
        });

    [Theory]
    [MemberData(nameof(Seeds))]
    public void RandomClicking_NeverLeavesTheWindowOutOfStep(int seed)
        => OnAppThread(() =>
        {
            var rng = new Random(seed);
            var log = new List<string>();
            var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
            var seen = new HashSet<string>();
            string Snap() => (string)Call(main, "SnapshotJson")!;
            main.CommitHistory();
            seen.Add(Snap());

            ConfirmDialog.TestAnswer = title =>
            {
                var yes = rng.Next(3) > 0;
                log.Add($"   asked “{title}” → {(yes ? "yes" : "no")}");
                return yes ? ConfirmResult.Affirmative : ConfirmResult.Cancel;
            };
            try
            {
                var actions = Actions(main, rng);
                for (int step = 0; step < 160; step++)
                {
                    var (name, act) = actions[rng.Next(actions.Count)];
                    int repeat = rng.Next(10) switch { 0 => 3, < 3 => 2, _ => 1 };   // double and triple clicks
                    var before = Snap();
                    for (int r = 0; r < repeat; r++)
                    {
                        log.Add($"{step}: {name}{(repeat > 1 ? $" (click {r + 1} of {repeat})" : "")}");
                        act();
                        if (!name.StartsWith("undo") && !name.StartsWith("redo"))
                            seen.Add(Snap());   // each click can make its own undo step (a layout change does)
                    }
                    try
                    {
                        CheckInvariants(main);
                        if (name.StartsWith("undo") || name.StartsWith("redo"))
                            Assert.True(seen.Contains(Snap()), "undo/redo landed on a state the set was never in:\n" + Snap()
                                + "\nseen:\n" + string.Join("\n", seen));
                        if (name == "undo" && repeat == 1 && Snap() != before)
                        {
                            // One undo and one redo are a round trip: back exactly where it was, then the undo again.
                            var undone = Snap();
                            main.Redo();
                            Assert.True(Snap() == before, "redo after an undo didn't come back to where it was:\n" + Snap() + "\nwas:\n" + before);
                            main.Undo();
                            Assert.True(Snap() == undone, "undo, redo, undo landed somewhere else:\n" + Snap() + "\nnot:\n" + undone);
                        }
                        main.CommitHistory();
                        seen.Add(Snap());
                    }
                    catch (Exception ex)
                    {
                        throw new Xunit.Sdk.XunitException($"seed {seed}, after step {step}: {ex.Message}\n"
                            + string.Join("\n", log.TakeLast(25)));
                    }
                }
            }
            finally { ConfirmDialog.TestAnswer = null; }
        });

    private static List<(string, Action)> Actions(Cardinator.MainWindow main, Random rng)
    {
        var layouts = Enum.GetValues<Layout>();
        CardModel? Card() => main.SelectedCard;
        return new()
        {
            ("layout", () => { if (Card() is { } c) main.ChangeLayout(c, layouts[rng.Next(layouts.Length)]); }),
            ("layout via dropdown", () =>
            {
                var items = main.LayoutBox.Items;
                if (items.Count > 0) main.LayoutBox.SelectedIndex = rng.Next(items.Count);
            }),
            ("front", () => main.FrontSeg.IsChecked = true),
            ("back", () => main.BackSeg.IsChecked = true),
            ("show side", () => main.ShowSide(rng.Next(2) == 0)),
            ("first half", () => main.FirstHalfSeg.IsChecked = true),
            ("other half", () => main.OtherHalfSeg.IsChecked = true),
            ("read sideways", () => main.SidewaysBox.IsChecked = main.SidewaysBox.IsChecked != true),
            ("whole meld", () => main.WholeMeldBox.IsChecked = main.WholeMeldBox.IsChecked != true),
            ("meld half", () => main.SetMeldHalf(rng.Next(2) == 0)),
            ("meld half via dropdown", () => main.MeldHalfBox.SelectedIndex = rng.Next(2)),
            ("add meld partner", () => main.AddMeldPartner()),
            ("corner mark", () => main.DfcStyleBox.SelectedIndex = rng.Next(main.DfcStyleBox.Items.Count)),
            ("second half frame", () =>
            {
                if (main.HalfFrameBox.Items.Count > 0) main.HalfFrameBox.SelectedIndex = rng.Next(main.HalfFrameBox.Items.Count);
            }),
            ("type a name", () =>
            {
                if (main.EditedFace is { } f) f.Name = "N" + rng.Next(1000);
            }),
            ("nudge art", () => Call(main, "NudgeArt", 0.01 * (rng.NextDouble() - 0.5), 0.01 * (rng.NextDouble() - 0.5))),
            ("zoom art", () => Call(main, "ZoomArt", 0.1 * (rng.NextDouble() - 0.5))),
            ("new card", () => Call(main, "OnAddCard", null!, null!)),
            ("new token", () => Call(main, "OnAddToken", null!, null!)),
            ("duplicate", () => Call(main, "OnDuplicateCard", null!, null!)),
            ("delete", () => { if (main.Cards.Count > 1) Call(main, "OnDeleteCard", null!, null!); }),
            ("select", () => { if (main.Cards.Count > 0) main.SelectedCard = main.Cards[rng.Next(main.Cards.Count)]; }),
            ("set settings", () => main.ApplySetSettings(new SetProfile
                {
                    Artist = rng.Next(2) == 0 ? "" : "A" + rng.Next(9),
                    CardBack = rng.Next(2) == 0 ? null : new CardBackDesign { Style = "designed", Color = "#3A2050" },
                }, "", (SetSettingsMode)rng.Next(3))),
            ("undo", () => main.Undo()),
            ("undo", () => main.Undo()),
            ("redo", () => main.Redo()),
        };
    }

    private static void CheckInvariants(Cardinator.MainWindow main)
    {
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
        var card = main.SelectedCard;
        bool viewingBack = Field<bool>(main, "_viewingBack");
        bool viewingCardBack = Field<bool>(main, "_viewingCardBack");
        if (card == null)
        {
            Assert.Null(main.EditedFace);
            return;
        }
        Assert.Contains(card, main.Cards);
        var layout = Cardinator.MainWindow.LayoutOf(card);

        // The model is one layout at a time.
        Assert.False(card.BackFace != null && card.OtherHalf != null, "a card with both a back face and another half");

        // The dropdown and its options row show the card's layout.
        Assert.Equal(layout, main.ShownLayout);
        Assert.Equal(layout == Layout.DoubleFaced, main.DfcOptions.Visibility == Visibility.Visible);
        Assert.Equal(layout == Layout.Meld, main.MeldOptions.Visibility == Visibility.Visible);
        Assert.Equal(layout == Layout.Split, main.HalfFramePanel.Visibility == Visibility.Visible);
        if (layout == Layout.Meld)
            Assert.Equal(card.IsMeldBottom ? "bottom half" : "top half", main.MeldHalfBox.SelectedItem);

        // Front | Back says which side is shown, and the back is the back face when there is one.
        Assert.False(viewingBack && viewingCardBack, "showing the back face and the card back at once");
        Assert.False(viewingBack && card.BackFace == null, "showing a back face the card doesn't have");
        Assert.False(viewingCardBack && card.BackFace != null, "showing the set's card back on a card with a back face");
        bool back = viewingBack || viewingCardBack;
        Assert.Equal(!back, main.FrontSeg.IsChecked == true);
        Assert.Equal(back, main.BackSeg.IsChecked == true);

        // The editor edits what's shown.
        var face = main.EditedFace;
        if (viewingCardBack) Assert.Null(face);
        else if (viewingBack) Assert.Same(card.BackFace, face);
        else Assert.True(ReferenceEquals(face, card) || ReferenceEquals(face, card.OtherHalf), "the editor is on a face the card doesn't show");
        Assert.Equal(face != null, main.FaceEditor.Visibility == Visibility.Visible);
        Assert.Equal(face == null, main.CardBackNote.Visibility == Visibility.Visible);
        if (face != null) Assert.Equal(face.Name, main.NameBox.Text);

        // The half switch only on the front of a flip or split card, and it agrees with the half edited.
        bool halves = !back && layout is Layout.Flip or Layout.Split;
        Assert.Equal(halves, main.HalfBar.Visibility == Visibility.Visible);
        if (halves) Assert.Equal(ReferenceEquals(face, card.OtherHalf), main.OtherHalfSeg.IsChecked == true);
        Assert.Equal(halves && layout == Layout.Split, main.SidewaysBox.Visibility == Visibility.Visible);
        Assert.Equal(layout == Layout.Meld && viewingBack, main.WholeMeldBox.Visibility == Visibility.Visible);

        // A meld partner, when there is one, shows the same melded card.
        if (Field<CardModel?>(main, "_meldPartner") is { } partner)
        {
            Assert.Contains(partner, main.Cards);
            Assert.Equal(card.BackFace!.Name, partner.BackFace!.Name);
            Assert.NotEqual(card.IsMeldBottom, partner.IsMeldBottom);
        }

        // Something is always drawn.
        main.RenderPreview();
        Assert.NotNull(main.PreviewImage);
    }

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
