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
