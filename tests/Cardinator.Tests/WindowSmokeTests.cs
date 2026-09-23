using System;
using System.Threading;
using System.Windows;
using Cardinator.Models;

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
