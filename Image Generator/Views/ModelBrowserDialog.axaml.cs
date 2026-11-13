using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Image_Generator.Models;
using Image_Generator.ViewModels;

namespace Image_Generator.Views;

public partial class ModelBrowserDialog : Window
{
    private ModelBrowserDialogViewModel? _viewModel;

    public ModelBrowserDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Sets up the dialog with the provider and filter. Call before ShowDialog.
    /// Triggers the initial model load immediately so the list is populated
    /// when the window opens.
    /// </summary>
    public void Initialize(IImageGenerationProvider provider, string? apiKey, string filterType)
    {
        _viewModel = new ModelBrowserDialogViewModel(provider, apiKey, filterType);
        DataContext = _viewModel;
        // Kick off the first load right away, before the window is shown.
        _viewModel.LoadModelsCommand.Execute(null);
    }

    private void OnSelectClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedModel is not null)
        {
            Close(_viewModel.SelectedModel.Id);
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
}