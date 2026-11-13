using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Image_Generator.Views;

public partial class QuitConfirmDialog : Window
{
    public QuitConfirmDialog()
    {
        InitializeComponent();
    }

    public QuitConfirmDialog(string message)
    {
        InitializeComponent();
        MessageText.Text = message;
    }

    private void OnYesClick(object? sender, RoutedEventArgs e)
    {
        Close(true);
    }

    private void OnNoClick(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}