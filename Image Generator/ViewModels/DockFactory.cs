using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using Dock.Settings;
using Image_Generator.Models;
using Image_Generator.Services;
using Image_Generator.ViewModels.Documents;
using Image_Generator.Views;

namespace Image_Generator.ViewModels;

/// <summary>
/// Dock factory that builds the MDI layout: a single DocumentDock
/// whose documents are free-floating image windows.
/// </summary>
public class DockFactory : Factory
{
    private readonly IPreferencesService _preferencesService;
    private readonly AppPreferences _preferences;
    private readonly ProviderFactory _providerFactory;
    private readonly IImageIOService _imageIOService;

    private IRootDock? _rootDock;
    private IDocumentDock? _documentDock;

    /// <summary>Raised when a document reports an API cost (image, prompt, vision).</summary>
    public event Action<double?>? SessionCostAdded;

    /// <summary>Raised when the number of open documents changes.</summary>
    public event Action<int>? WindowCountChanged;

    public DockFactory(
        IPreferencesService preferencesService,
        AppPreferences preferences,
        ProviderFactory providerFactory,
        IImageIOService imageIOService)
    {
        _preferencesService = preferencesService;
        _preferences = preferences;
        _providerFactory = providerFactory;
        _imageIOService = imageIOService;
    }

    /// <summary>Forwards a document's reported API cost to subscribers.</summary>
    public void RaiseSessionCost(double? cost) => SessionCostAdded?.Invoke(cost);

    private void RaiseWindowCount()
    {
        WindowCountChanged?.Invoke(_documentDock?.VisibleDockables?.OfType<ImageDocumentViewModel>().Count() ?? 0);
    }

    public override IRootDock CreateLayout()
    {
        var documentDock = new DocumentDock
        {
            Id = "Images",
            Title = "Images",
            IsCollapsable = false,
            CanCreateDocument = false,
            EnableWindowDrag = false,
            LayoutMode = DocumentLayoutMode.Mdi,
            ActiveDockable = null,
            VisibleDockables = CreateList<IDockable>()
        };

        var rootDock = CreateRootDock();
        rootDock.IsCollapsable = false;
        rootDock.ActiveDockable = documentDock;
        rootDock.DefaultDockable = documentDock;
        rootDock.VisibleDockables = CreateList<IDockable>(documentDock);

        rootDock.LeftPinnedDockables = CreateList<IDockable>();
        rootDock.RightPinnedDockables = CreateList<IDockable>();
        rootDock.TopPinnedDockables = CreateList<IDockable>();
        rootDock.BottomPinnedDockables = CreateList<IDockable>();
        rootDock.PinnedDock = null;

        _documentDock = documentDock;
        _rootDock = rootDock;

        return rootDock;
    }

    /// <summary>
    /// Creates a new blank image document and adds it to the MDI dock.
    /// </summary>
    public ImageDocumentViewModel? CreateNewImageDocument()
    {
        if (_documentDock is null)
            return null;

        var document = new ImageDocumentViewModel(
            _preferencesService, _preferences, _providerFactory, _imageIOService)
        {
            Id = $"Image_{Guid.NewGuid():N}",
            Title = "Untitled"
        };

        AddDocument(document);
        return document;
    }

    /// <summary>
    /// Creates an image document pre-loaded with an image (and optional prompt)
    /// and adds it to the MDI dock. Used by File &gt; Load.
    /// </summary>
    public ImageDocumentViewModel? CreateLoadedImageDocument(
        byte[] imageData, string? prompt, string fileName,
        string? model = null, Dictionary<string, string?>? parameters = null, double? cost = null)
    {
        if (_documentDock is null)
            return null;

        var document = new ImageDocumentViewModel(
            _preferencesService, _preferences, _providerFactory, _imageIOService)
        {
            Id = $"Image_{Guid.NewGuid():N}",
            Title = fileName
        };

        document.LoadFromImage(imageData, prompt, model, parameters, cost);
        AddDocument(document);
        return document;
    }

    /// <summary>
    /// Adds a document to the document dock and activates it.
    /// </summary>
    public void AddDocument(ImageDocumentViewModel document)
    {
        if (_documentDock is null)
            return;

        document.Factory = this;

        // Copy the configured default generation model into the window, so the
        // window has its own model that the parameters dialog can override
        // without affecting other windows or the config. A loaded image has
        // already adopted its sidecar's model (LoadFromImage), and that wins.
        if (string.IsNullOrEmpty(document.GenerationModel))
            document.GenerationModel = _preferences.OpenRouterGenerationModel;

        // Give the new window its own copy of the generation model's parameter
        // schema, with all values unset (the API applies its own defaults).
        // When the window's model differs from the config model (a loaded
        // image made with another model), fetch THAT model's schema from the
        // API before restoring the sidecar's saved parameter values.
        var schemaModel = _preferences.OpenRouterGenerationModel;
        var schemasMatch = string.Equals(document.GenerationModel, schemaModel, StringComparison.OrdinalIgnoreCase);
        if (schemasMatch)
        {
            foreach (var descriptor in _preferences.OpenRouterGenerationModelParameters)
                document.ModelParameters.Add(new ModelParameterValue(descriptor));
            document.ApplyPendingParameterRestore();
        }
        else
        {
            _ = LoadSchemaAndRestoreAsync(document);
        }

        // Floor the MDI window resize to the document's real minimum: 180px
        // history column + canvas column (MinWidth 320) + margins wide, and tall
        // enough for the title bar, canvas row (MinHeight 208), splitter and
        // controls panel (MinHeight 144) so nothing can be squeezed out.
        document.MinWidth = 560;
        document.MinHeight = 400;

        _documentDock.VisibleDockables?.Add(document);
        _documentDock.ActiveDockable = document;

        // Report costs from this document upward (session total on the status
        // bar) and notify the window count.
        document.CostReported += c => RaiseSessionCost(c);
        RaiseWindowCount();
    }

    /// <summary>
    /// Fetches the schema for a window whose model differs from the config
    /// model (a loaded image's sidecar model), populates its parameters, then
    /// applies the sidecar's saved values. Non-fatal on fetch failure — the
    /// window just runs with defaults.
    /// </summary>
    private async Task LoadSchemaAndRestoreAsync(ImageDocumentViewModel document)
    {
        try
        {
            var model = document.GenerationModel;
            if (string.IsNullOrWhiteSpace(model))
                return;

            var provider = _providerFactory.CreateProvider(_preferences.SelectedProviderId, _preferences);
            var descriptors = await provider.GetModelParameterDescriptorsAsync(
                model, _preferences.OpenRouterApiKey, CancellationToken.None);
            var userFacing = descriptors.Where(Image_Generator.Models.ModelParameters.IsUserFacing).ToList();

            document.ModelParameters.Clear();
            foreach (var d in userFacing)
                document.ModelParameters.Add(new ModelParameterValue(d));

            // Now the descriptors match the sidecar's model — safe to restore.
            document.ApplyPendingParameterRestore();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[DockFactory] Schema fetch for loaded window's model failed: {ex.Message}");
            // Leave the window with whatever it has; the pending values stay
            // queued harmlessly and are dropped with the window.
        }
    }

    public IDocumentDock? DocumentDock => _documentDock;

    /// <summary>
    /// Override to handle document close requests — confirm if dirty.
    /// Dock's close pipeline is synchronous, so the dialog can't be awaited
    /// inline (blocking the UI thread on ShowDialog deadlocks the app — the
    /// dialog opens transparent and unusable, and the app must be killed).
    /// Instead: kick off the async dialog and only perform the actual close
    /// from its continuation when the user confirms.
    /// </summary>
    public override void CloseDockable(IDockable? dockable)
    {
        if (dockable is ImageDocumentViewModel docVm && docVm.IsDirty)
        {
            var mainWindow = GetMainWindow();
            if (mainWindow is not null)
            {
                // Return immediately (don't close yet); ask asynchronously.
                _ = ConfirmAndCloseAsync(docVm, mainWindow);
                return;
            }
        }

        base.CloseDockable(dockable);
        RaiseWindowCount();
    }

    /// <summary>
    /// Shows the unsaved-changes confirmation without blocking the UI thread,
    /// and completes the close (the real removal) only on confirmation.
    /// </summary>
    private async Task ConfirmAndCloseAsync(ImageDocumentViewModel docVm, Window mainWindow)
    {
        var dialog = new QuitConfirmDialog("This document has unsaved changes. Close anyway?");
        var confirmed = await dialog.ShowDialog<bool>(mainWindow);
        if (!confirmed)
            return; // Cancel the close

        base.CloseDockable(docVm);
        RaiseWindowCount();
    }

    private static Window? GetMainWindow()
    {
        return Avalonia.Application.Current?.ApplicationLifetime
            is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;
    }

    public override void InitLayout(IDockable layout)
    {
        ContextLocator = new Dictionary<string, Func<object?>>();

        DockableLocator = new Dictionary<string, Func<IDockable?>>
        {
            ["Root"] = () => _rootDock,
            ["Images"] = () => _documentDock
        };

        HostWindowLocator = new Dictionary<string, Func<IHostWindow?>>
        {
            [nameof(IDockWindow)] = () => DockSettings.UseManagedWindows
                ? new ManagedHostWindow()
                : new HostWindow()
        };

        base.InitLayout(layout);
    }
}