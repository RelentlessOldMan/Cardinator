using System.Windows;

namespace Cardinator;

/// <summary>Built-in cheat sheet: a quick reference for the main flow, shortcuts and batch tools.</summary>
public partial class HelpWindow : Window
{
    public HelpWindow(string version)
    {
        InitializeComponent();
        VersionText.Text = version;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
