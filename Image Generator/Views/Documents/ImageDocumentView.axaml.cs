using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Image_Generator.ViewModels.Documents;

namespace Image_Generator.Views.Documents;

public partial class ImageDocumentView : UserControl
{
    private IDisposable? _parentBoundsSubscription;

    public ImageDocumentView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Dock's MdiLayoutPanel measures MDI windows with the full panel size but
    /// arranges them at their (smaller) MdiBounds. Star-sized rows/columns
    /// resolve their pixel lengths from the measure constraint, so an
    /// over-large constraint makes the textarea overflow the window (and the
    /// canvas row resolve to the image's aspect-fit size instead of the
    /// window's). Clamping the constraint to the parent slot — the size this
    /// view was actually arranged into last layout pass — keeps measure and
    /// arrange in agreement: the view always sizes itself to the MDI window,
    /// never to its content.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        if (Parent is Control { } parent)
        {
            var slot = parent.Bounds;
            if (slot.Width > 0 && slot.Height > 0)
            {
                availableSize = new Size(
                    Math.Min(availableSize.Width, slot.Width),
                    Math.Min(availableSize.Height, slot.Height));
            }
        }

        return base.MeasureOverride(availableSize);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Re-measure whenever the slot changes (MDI window moved/resized),
        // so star rows/columns re-resolve from the new window size.
        if (Parent is Control parent)
        {
            _parentBoundsSubscription?.Dispose();
            _parentBoundsSubscription = parent.GetObservable(BoundsProperty)
                .Subscribe(new Avalonia.Reactive.AnonymousObserver<Rect>(_ => InvalidateMeasure()));
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _parentBoundsSubscription?.Dispose();
        _parentBoundsSubscription = null;
        base.OnDetachedFromVisualTree(e);
    }

    private async void OnHistoryMenuClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string action, DataContext: GenerationRecordViewModel record })
            return;
        if (DataContext is not ImageDocumentViewModel vm)
            return;

        switch (action)
        {
            case "Save":
                await vm.SaveRecordAsync(record);
                break;
            case "Remove":
                vm.RemoveRecord(record);
                break;
            case "CopyPrompt":
                await vm.CopyPromptAsync(record);
                break;
            case "CopyImage":
                await vm.CopyImageAsync(record);
                break;
        }
    }

    private async void OnHistorySaveAllClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ImageDocumentViewModel vm)
            await vm.SaveAllRecordsAsync();
    }
}