using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Image_Generator.Models;
using Image_Generator.ViewModels;
using Image_Generator.ViewModels.Documents;

namespace Image_Generator.Views;

public partial class ModelParametersDialog : Window
{
    private ModelParametersDialogViewModel? _viewModel;
    private ImageDocumentViewModel? _owner;

    public ModelParametersDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Sets up the dialog for one image window: shows the window's generation
    /// model (overridable via the ellipsis browse) and its parameter rows.
    /// Works on copies; OK commits validated values back, Cancel discards.
    /// </summary>
    public void Initialize(ImageDocumentViewModel owner)
    {
        _owner = owner;
        _viewModel = new ModelParametersDialogViewModel(owner);
        DataContext = _viewModel;
    }

    /// <summary>
    /// Opens the model browser; picking a different model re-reads its
    /// parameters from the API and rebuilds the rows (old parameters removed,
    /// new ones added, values unset).
    /// </summary>
    private async void OnBrowseModel(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _owner is null)
            return;

        var dialog = new ModelBrowserDialog();
        dialog.Initialize(_owner.CreateProvider(), _owner.GetApiKey(), "generation");
        var result = await dialog.ShowDialog<string?>(this);

        if (result is null || result == _viewModel.GenerationModel)
            return;

        _viewModel.GenerationModel = result;

        // Re-read the new model's parameters from the API; old rows are
        // removed and the new descriptors added with values unset.
        try
        {
            var ok = await _viewModel.LoadParametersForModelAsync(result);
            if (!ok)
            {
                // Fetch failed — keep the rows we have rather than leaving the
                // dialog empty, and revert the model so OK stays consistent.
                _viewModel.GenerationModel = _owner.GenerationModel;
            }
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ModelParametersDialog] Parameter reload failed: {ex.Message}");
            _viewModel.GenerationModel = _owner.GenerationModel;
        }
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _owner is null)
            return;

        if (!_viewModel.TryCommit())
        {
            // Invalid range values — keep the dialog open; errors are shown inline.
            return;
        }

        // When the model changed, the window's parameter collection must be
        // replaced wholesale (different descriptors), not just value-copied.
        // Capture the verdict before overwriting the owner's model.
        var modelChanged = _viewModel.ModelChanged;

        if (modelChanged)
        {
            var source = _owner.ModelParameters;
            source.Clear();
            foreach (var row in _viewModel.Rows)
                source.Add(row.Source.Clone());

            _owner.GenerationModel = _viewModel.GenerationModel;
        }
        else
        {
            // Same model: value-copy the committed edits back.
            var source = _owner.ModelParameters;
            for (int i = 0; i < source.Count && i < _viewModel.Rows.Count; i++)
                source[i].Value = _viewModel.Rows[i].Source.Value;
        }

        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
