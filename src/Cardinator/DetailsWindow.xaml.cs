using System.Windows;
using Cardinator.Models;

namespace Cardinator;

/// <summary>
/// A spacious modal editor for the less-common card fields. Binds directly to the live CardModel,
/// so edits update the main preview in real time; closing just dismisses the dialog.
/// </summary>
public partial class DetailsWindow : Window
{
    public DetailsWindow(CardModel card)
    {
        InitializeComponent();
        DataContext = card;
    }

    private void OnDone(object sender, RoutedEventArgs e) => Close();
}
