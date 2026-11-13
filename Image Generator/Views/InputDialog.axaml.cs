using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Image_Generator.Views;

public partial class InputDialog : Window
{
    public string? Result { get; private set; }

    /// <summary>Parameterless constructor required by the XAML runtime loader.</summary>
    public InputDialog() : this("Input", "")
    {
    }

    public InputDialog(string title, string prompt, string defaultValue = "")
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        InputBox.Text = defaultValue;
        InputBox.SelectAll();
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        Result = InputBox.Text;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}