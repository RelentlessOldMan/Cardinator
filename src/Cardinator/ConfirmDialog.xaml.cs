using System.Windows;
using System.Windows.Controls;

namespace Cardinator;

public enum ConfirmResult { Affirmative, Negative, Cancel }

/// <summary>
/// A themed replacement for the native MessageBox for the app's own prompts (unsaved changes, print
/// options, errors), so confirmations match the dark theme. Buttons are built from the labels passed in;
/// the affirmative is the default (Enter) and Cancel is IsCancel (Esc). File pickers stay OS-native.
/// </summary>
public partial class ConfirmDialog : Window
{
    private ConfirmResult _result = ConfirmResult.Cancel;

    public ConfirmDialog(string title, string message, string affirmative, string? negative, string? cancel)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;

        // Laid out left→right: Cancel, Negative, Affirmative (primary, rightmost).
        if (cancel != null) AddButton(cancel, ConfirmResult.Cancel, primary: false, isCancel: true);
        if (negative != null) AddButton(negative, ConfirmResult.Negative, primary: false);
        AddButton(affirmative, ConfirmResult.Affirmative, primary: true, isDefault: true);
    }

    private void AddButton(string text, ConfirmResult result, bool primary,
        bool isDefault = false, bool isCancel = false)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 104,
            Margin = new Thickness(8, 0, 0, 0),
            IsDefault = isDefault,
            IsCancel = isCancel,
            Style = (Style)FindResource(primary ? "Primary" : "Ghost"),
        };
        button.Click += (_, _) => { _result = result; DialogResult = true; };
        Buttons.Children.Add(button);
    }

    /// <summary>Tests only: answers every prompt (given its title) instead of showing a modal window.</summary>
    internal static Func<string, ConfirmResult>? TestAnswer;

    /// <summary>Shows the dialog and returns which button was chosen (Cancel if dismissed via Esc/close).</summary>
    public static ConfirmResult Show(Window owner, string title, string message,
        string affirmative, string? negative = null, string? cancel = null)
    {
        if (TestAnswer != null) return TestAnswer(title);
        var dlg = new ConfirmDialog(title, message, affirmative, negative, cancel) { Owner = owner };
        dlg.ShowDialog();
        return dlg._result;
    }
}
