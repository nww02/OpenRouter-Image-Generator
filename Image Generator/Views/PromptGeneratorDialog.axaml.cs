using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Image_Generator.Models;
using Image_Generator.Services;
using Image_Generator.ViewModels;

namespace Image_Generator.Views;

public partial class PromptGeneratorDialog : Window
{
    public PromptGeneratorDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Sets up the dialog with a fresh ViewModel. Call before ShowDialog.
    /// </summary>
    public void Initialize(AppPreferences preferences, ProviderFactory providerFactory)
    {
        DataContext = new PromptGeneratorDialogViewModel(preferences, providerFactory);
    }

    /// <summary>
    /// Sets up the dialog with an existing ViewModel (preserves selections across opens).
    /// </summary>
    public void Initialize(PromptGeneratorDialogViewModel existingViewModel)
    {
        DataContext = existingViewModel;
    }

    protected override void OnInitialized()
    {
        base.OnInitialized();
        if (DataContext is null)
        {
            DataContext = new PromptGeneratorDialogViewModel(new AppPreferences(), new ProviderFactory());
        }
    }

    private void OnUseThisClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PromptGeneratorDialogViewModel vm && vm.GeneratedPrompt is not null)
        {
            Close(vm.GeneratedPrompt);
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
}