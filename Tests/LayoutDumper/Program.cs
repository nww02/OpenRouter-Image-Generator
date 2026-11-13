using System;
using System.Globalization;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Image_Generator.ViewModels;
using Image_Generator.ViewModels.Documents;

namespace LayoutDumper;

/// <summary>
/// Layout regression harness for the MDI image window:
///   Phase 1 — dump a lot of text into the prompt textarea on load; its
///             width must not change (and must equal the panel width).
///   Phase 2 — preload a large image; the canvas must not resize.
///   Phase 3 — shrink the window; the textarea must never collapse below
///             one line tall.
/// Results are written to the path in the LAYOUT_DUMP_OUT env var
/// (default /tmp/layout_dumper_results.txt); nonzero exit if any check fails.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var outPath = Environment.GetEnvironmentVariable("LAYOUT_DUMP_OUT") ?? "/tmp/layout_dumper_results.txt";
        if (File.Exists(outPath)) File.Delete(outPath);

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(
            Array.Empty<string>(), ShutdownMode.OnExplicitShutdown);

        Console.Out.Flush();
        Environment.ExitCode = File.ReadAllText(outPath).Contains("CHECK(S) FAILED") ? 1 : 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<LayoutDumperApp>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

public class LayoutDumperApp : Application
{
    public override void Initialize()
    {
        var include = new Avalonia.Markup.Xaml.Styling.ResourceInclude(
            new Uri("avares://Dock.Avalonia.Themes.Fluent/Presets/Ide/Default.axaml"))
        {
            Source = new Uri("avares://Dock.Avalonia.Themes.Fluent/Presets/Ide/Default.axaml")
        };
        Resources.MergedDictionaries.Add(include);
        Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        var dockTheme = new Dock.Avalonia.Themes.Fluent.DockFluentTheme();
        Styles.Add(dockTheme);
        DataTemplates.Add(new Image_Generator.ViewLocator());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Dock.Settings.DockSettings.UseManagedWindows = true;

        var preferencesService = new Image_Generator.Services.PreferencesService();
        var preferences = preferencesService.Load();
        preferences.Theme = "Dark";
        var providerFactory = new Image_Generator.Services.ProviderFactory();
        var imageIOService = new Image_Generator.Services.ImageIOService();

        var mainViewModel = new MainWindowViewModel(preferencesService, preferences, providerFactory, imageIOService);

        var mainWindow = new Image_Generator.MainWindow
        {
            DataContext = mainViewModel,
            Width = 1100,
            Height = 700
        };
        mainWindow.Show();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = mainWindow;
            var outPath = Environment.GetEnvironmentVariable("LAYOUT_DUMP_OUT") ?? "/tmp/layout_dumper_results.txt";

            var run = RunTestAsync(mainWindow, mainViewModel, outPath);
            run.ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception is not null)
                    File.WriteAllText(outPath, "HARNESS EXCEPTION:\n" + t.Exception);
                Dispatcher.UIThread.Post(() => desktop.Shutdown(0));
            });
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task RunTestAsync(
        Window mainWindow,
        MainWindowViewModel mainViewModel,
        string outPath)
    {
        var sb = new StringBuilder();
        int failures = 0;

        void Check(string name, string detail, bool pass)
        {
            sb.AppendLine($"{(pass ? "PASS" : "FAIL")}  {name}: {detail}");
            if (!pass) failures++;
        }

        try
        {
            // Dock defers document content realization; force it so we measure
            // the real ImageDocumentView, not an empty presenter.
            for (int i = 0; i < 10; i++)
            {
                await Settle();
                var pending = mainWindow.GetVisualDescendants()
                    .Where(v => v.GetType().Name is "DeferredContentControl" or "DeferredContentPresenter")
                    .ToList();
                if (viewFound(mainWindow)) break;
                foreach (var v in pending)
                {
                    v.GetType().GetMethod("ApplyDeferredPresentation", Type.EmptyTypes)?
                        .Invoke(v, null);
                }
            }

            // ---- Phase 1: dump lots of text into the textarea on load ----
            sb.AppendLine("== Phase 1: dump lots of text into textarea on load ==");

            var doc = mainViewModel.DockFactory.DocumentDock?.VisibleDockables?
                .OfType<ImageDocumentViewModel>().FirstOrDefault();
            var view = mainWindow.GetVisualDescendants()
                .OfType<Image_Generator.Views.Documents.ImageDocumentView>()
                .FirstOrDefault();
            if (doc is null || view is null)
            {
                sb.AppendLine($"FAIL  document/view found: doc={doc is not null} view={view is not null}");
                sb.AppendLine("  visual tree types:");
                foreach (var v in mainWindow.GetVisualDescendants())
                    sb.AppendLine($"    {v.GetType().FullName}  '{(v as Avalonia.Controls.Control)?.Name}'");
                File.WriteAllText(outPath, sb.ToString());
                return;
            }

            var controlsGrid = view.GetVisualDescendants().OfType<Grid>()
                .FirstOrDefault(g => g.Name == "ControlsGrid");
            var canvasHost = view.GetVisualDescendants()
                .FirstOrDefault(v => v is Grid g && g.Name == "CanvasHost") as Grid;
            var promptBox = view.GetVisualDescendants().OfType<TextBox>()
                .FirstOrDefault(tb => tb.Name == "PromptTextBox");
            if (controlsGrid is null || canvasHost is null || promptBox is null)
            {
                sb.AppendLine($"FAIL  named controls found: grid={controlsGrid is not null} canvas={canvasHost is not null} prompt={promptBox is not null}");
                File.WriteAllText(outPath, sb.ToString());
                return;
            }

            var mdiWindow = view.GetVisualDescendants()
                .FirstOrDefault(v => v is Dock.Avalonia.Controls.MdiDocumentWindow) as Dock.Avalonia.Controls.MdiDocumentWindow
                ?? mainWindow.GetVisualDescendants().OfType<Dock.Avalonia.Controls.MdiDocumentWindow>().FirstOrDefault();
            var mdiPanel = mainWindow.GetVisualDescendants()
                .OfType<Dock.Avalonia.Controls.MdiLayoutPanel>().FirstOrDefault();

            void DumpMdi(string tag)
            {
                if (mdiWindow is null) { sb.AppendLine($"  {tag}: MDI window NOT FOUND"); return; }
                sb.AppendLine($"  {tag}: mdiWindow.Bounds={Fmt(mdiWindow.Bounds)} MdiBounds={doc!.MdiBounds.Width}x{doc.MdiBounds.Height}@({doc.MdiBounds.X},{doc.MdiBounds.Y}) state={doc.MdiState} panel={(mdiPanel is null ? "?" : Fmt(mdiPanel.Bounds))}");
            }

            // Measure empty state first.
            await Settle();
            var emptyPrompt = promptBox.Bounds;
            var emptyCanvas = canvasHost.Bounds;
            DumpMdi("initial");
            DumpChain("initial");

            void DumpChain(string tag)
            {
                sb.AppendLine($"  --- ancestor chain ({tag}) ---");
                var node = (Avalonia.Visual?)promptBox;
                while (node is not null)
                {
                    var c = node as Control;
                    sb.AppendLine($"    {node.GetType().Name,-32} bounds={Fmt(node.Bounds)} desired={c?.DesiredSize.Width.ToString("0") ?? "?"}x{c?.DesiredSize.Height.ToString("0") ?? "?"}");
                    node = node.GetVisualParent();
                }
            }

            const int paragraphCount = 60;
            var bigText = string.Join("\n", Enumerable.Range(1, paragraphCount).Select(i =>
                $"{i:0000} Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua. Ut enim ad minim veniam, quis nostrud exercitation ullamco laboris nisi ut aliquip ex ea commodo consequat."));
            doc.CurrentPrompt = bigText;
            await Settle();

            DumpMdi("after text dump");
            var bounds1 = promptBox.Bounds;
            var canvas1 = canvasHost.Bounds;
            DumpChain("after text dump");
            var grid1 = controlsGrid.Bounds;
            var win1 = mainWindow.Bounds;
            sb.AppendLine($"  empty textarea bounds           : {Fmt(emptyPrompt)}");
            sb.AppendLine($"  textarea bounds after text dump : {Fmt(bounds1)}");
            sb.AppendLine($"  canvas bounds after text dump  : {Fmt(canvas1)}");
            sb.AppendLine($"  controls grid bounds            : {Fmt(grid1)}");
            sb.AppendLine($"  main window bounds              : {Fmt(win1)}");

            Check("textarea width unchanged after text dump",
                $"{Fmt(bounds1)} vs empty {Fmt(emptyPrompt)}", bounds1.Width == emptyPrompt.Width);
            Check("textarea at least one line tall", $"height {bounds1.Height}", bounds1.Height >= 16);
            Check("textarea width == panel width",
                $"box {bounds1.Width} vs grid {grid1.Width}", Math.Abs(bounds1.Width - grid1.Width) < 1.5);
            Check("canvas unchanged after text dump",
                $"{Fmt(canvas1)} vs empty {Fmt(emptyCanvas)}", canvas1 == emptyCanvas);

            // Even more text — still nothing may change.
            var biggerText = bigText + "\n" + string.Join("\n", Enumerable.Range(0, 200).Select(i =>
                $"extra line {i} of long running text content for the prompt box, width pressure wwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwww"));
            doc.CurrentPrompt = biggerText;
            await Settle();

            var bounds1b = promptBox.Bounds;
            var canvas1b = canvasHost.Bounds;
            var win1b = mainWindow.Bounds;
            Check("textarea width unchanged after more text",
                $"{bounds1b.Width} vs {bounds1.Width}", bounds1b.Width == bounds1.Width);
            Check("textarea height unchanged after more text",
                $"{bounds1b.Height} vs {bounds1.Height}", bounds1b.Height == bounds1.Height);
            Check("canvas unchanged after more text",
                $"{Fmt(canvas1b)} vs {Fmt(canvas1)}", canvas1b == canvas1);
            Check("main window unchanged after more text",
                $"{Fmt(win1b)} vs {Fmt(win1)}", win1b == win1);

            // ---- Phase 2: preload a large image; canvas must not resize ----
            sb.AppendLine();
            sb.AppendLine("== Phase 2: preload image; canvas must not resize ==");
            var imageBytes = MakePng(2200, 1600);
            doc.LoadFromImage(imageBytes, "preloaded prompt");
            await Settle();

            DumpMdi("after image load");
            var live2 = GetLiveControls(mainWindow);
            canvasHost = live2.CanvasHost;
            controlsGrid = live2.ControlsGrid;
            promptBox = live2.PromptTextBox;
            mdiWindow = live2.MdiWindow;
            var canvas2 = canvasHost.Bounds;
            var bounds2 = promptBox.Bounds;
            var win2 = mainWindow.Bounds;
            var controls2 = controlsGrid.Bounds;
            sb.AppendLine($"  canvas bounds after image load  : {Fmt(canvas2)}");
            sb.AppendLine($"  textarea bounds after image     : {Fmt(bounds2)}");
            sb.AppendLine($"  controls grid bounds after image: {Fmt(controls2)}");
            sb.AppendLine($"  main window bounds after image   : {Fmt(win2)}");

            Check("canvas bounds unchanged after image load",
                $"{Fmt(canvas2)} vs {Fmt(canvas1b)}", canvas2 == canvas1b);
            Check("textarea bounds unchanged after image load",
                $"{Fmt(bounds2)} vs {Fmt(bounds1b)}", bounds2 == bounds1b);
            Check("main window unchanged after image load",
                $"{Fmt(win2)} vs {Fmt(win1b)}", win2 == win1b);
            Check("controls grid unchanged after image load",
                $"{Fmt(controls2)} vs {Fmt(grid1)}", controls2 == grid1);

            // The Image element must be letterboxed inside the unchanged canvas.
            var img = canvasHost.GetVisualDescendants().OfType<Image>().FirstOrDefault()
                      ?? canvasHost.Children.OfType<Image>().FirstOrDefault();
            if (img is not null)
            {
                sb.AppendLine($"  image control bounds            : {Fmt(img.Bounds)} (source 2200x1600)");
                Check("image fits inside canvas without resizing it",
                    $"img {Fmt(img.Bounds)} vs canvas {Fmt(canvas2)}",
                    img.Bounds.Width <= canvas2.Width + 0.5 && img.Bounds.Height <= canvas2.Height + 0.5);
            }
            else
            {
                sb.AppendLine("  (no Image control found inside canvasHost)");
            }

            // ---- Phase 3: MDI window must not collapse the textarea ----
            sb.AppendLine();
            sb.AppendLine("== Phase 3: window shrink must not collapse the textarea below one line ==");
            mainWindow.Width = 600;
            mainWindow.Height = 320;
            await Settle();

            var live3 = GetLiveControls(mainWindow);
            canvasHost = live3.CanvasHost;
            controlsGrid = live3.ControlsGrid;
            promptBox = live3.PromptTextBox;
            mdiWindow = live3.MdiWindow;
            var bounds3 = promptBox.Bounds;
            var canvas3 = canvasHost.Bounds;
            var grid3 = controlsGrid.Bounds;
            sb.AppendLine($"  textarea bounds @ 600x320      : {Fmt(bounds3)}");
            sb.AppendLine($"  canvas bounds  @ 600x320      : {Fmt(canvas3)}");
            sb.AppendLine($"  controls grid  @ 600x320      : {Fmt(grid3)}");
            Check("textarea still >= 1 line after shrinking window",
                $"height {bounds3.Height}", bounds3.Height >= 16);
            Check("textarea width still == panel width when narrow",
                $"box {bounds3.Width} vs grid {grid3.Width}", Math.Abs(bounds3.Width - grid3.Width) < 1.5);


            // ---- Phase 4: MDI window resize must not collapse the textarea ----
            sb.AppendLine();
            sb.AppendLine("== Phase 4: MDI window resized to minimum; textarea >= 1 line ==");
            // Restore the main window so the MDI panel has room for the min-size test.
            mainWindow.Width = 1100;
            mainWindow.Height = 700;
            await Settle();
            // Simulate dragging the resize handle down to the document's min size:
            // ClassicMdiLayoutManager.ApplySizeConstraints clamps to MinWidth/MinHeight.
            doc.MdiBounds = new Dock.Model.Core.DockRect(0, 0, doc.MinWidth, doc.MinHeight);
            await Settle();

            var live4 = GetLiveControls(mainWindow);
            canvasHost = live4.CanvasHost;
            controlsGrid = live4.ControlsGrid;
            promptBox = live4.PromptTextBox;
            mdiWindow = live4.MdiWindow;
            var bounds4 = promptBox.Bounds;
            var win4 = mdiWindow!.Bounds;
            sb.AppendLine($"  mdi window @ min size         : {Fmt(win4)} (min {doc.MinWidth}x{doc.MinHeight})");
            sb.AppendLine($"  textarea bounds @ min window  : {Fmt(bounds4)}");
            DumpChain("at min window");
            Check("textarea >= 1 line at minimum MDI window size", $"height {bounds4.Height}", bounds4.Height >= 16);
            Check("MDI window clamped to MinWidth", $"width {win4.Width} vs min {doc.MinWidth}", Math.Abs(win4.Width - doc.MinWidth) < 2.5);
            Check("MDI window clamped to MinHeight", $"height {win4.Height} vs min {doc.MinHeight}", Math.Abs(win4.Height - doc.MinHeight) < 2.5);
            Check("textarea width tracks window when resized", $"box {bounds4.Width}", bounds4.Width > 0);

            // Simulate the real drag-resize path: MdiDocumentWindow.ResizeMove calls
            // manager.GetResizeBounds, which applies ApplySizeConstraints (MinWidth/
            // MinHeight). Dragging far past the floor must clamp at the minimum.
            var panelSize = mdiPanel!.Bounds.Size;
            var entries = new List<Dock.Avalonia.Mdi.MdiLayoutEntry>
                { new(mdiWindow!, doc) };
            var manager = Dock.Avalonia.Mdi.ClassicMdiLayoutManager.Instance;
            var start = new Rect(0, 0, doc.MinWidth, doc.MinHeight);
            var dragged = manager.GetResizeBounds(
                doc, start, new Vector(-400, -400),
                Dock.Avalonia.Mdi.MdiResizeDirection.Right
                    | Dock.Avalonia.Mdi.MdiResizeDirection.Bottom,
                panelSize, entries);
            sb.AppendLine($"  drag below floor clamped to    : {Fmt(dragged)}");
            Check("drag resize clamps at MinWidth", $"width {dragged.Width} vs min {doc.MinWidth}", Math.Abs(dragged.Width - doc.MinWidth) < 0.5);
            Check("drag resize clamps at MinHeight", $"height {dragged.Height} vs min {doc.MinHeight}", Math.Abs(dragged.Height - doc.MinHeight) < 0.5);


            // ---- Phase 5: vertical squeeze must not squash the buttons row ----
            sb.AppendLine();
            sb.AppendLine("== Phase 5: vertical squeeze keeps buttons row full height ==");
            var liveA = GetLiveControls(mainWindow);
            canvasHost = liveA.CanvasHost;
            controlsGrid = liveA.ControlsGrid;
            promptBox = liveA.PromptTextBox;
            mdiWindow = liveA.MdiWindow;

            var buttonsRow = mainWindow.GetVisualDescendants()
                .OfType<StackPanel>().First(sp => sp.Name == "ButtonsRow");
            var fullButtonsH = buttonsRow.Bounds.Height;
            var ellipsis = buttonsRow.Children.OfType<Avalonia.Controls.Button>().FirstOrDefault();
            sb.AppendLine($"  full buttons row height: {fullButtonsH}");
            sb.AppendLine($"  first button in row    : {ellipsis?.Content} (should be \u2026, left of Build Prompt)");
            var ellipsisContent = ellipsis?.Content?.ToString() ?? "";
            var ellipsisCodepoints = string.Join(",", ellipsisContent.Select(c => ((int)c).ToString("X4")));
            Check("ellipsis button sits in buttons row",
                $"first=U+{ellipsisCodepoints} (expect U+22EF)", ellipsisContent == "\u22EF");

            // Squeeze the MDI window vertically, progressively (like a real drag),
            // down to the document's minimum height.
            var floor = (int)doc.MinHeight;
            foreach (var h in new[] { 500, 460, floor + 40, floor + 20, floor })
            {
                doc.MdiBounds = new Dock.Model.Core.DockRect(0, 0, 660, h);
                await Settle();
            }
            var liveB = GetLiveControls(mainWindow);
            promptBox = liveB.PromptTextBox;
            mdiWindow = liveB.MdiWindow;
            buttonsRow = mainWindow.GetVisualDescendants()
                .OfType<StackPanel>().First(sp => sp.Name == "ButtonsRow");
            var squeezeButtonsH = buttonsRow.Bounds.Height;
            var squeezePromptH = promptBox.Bounds.Height;
            var squeezeWin = mdiWindow.Bounds;
            sb.AppendLine($"  squeezed window          : {Fmt(squeezeWin)}");
            sb.AppendLine($"  buttons row @ squeezed   : {squeezeButtonsH} (was {fullButtonsH})");
            sb.AppendLine($"  prompt row @ squeezed    : {squeezePromptH}");
            Check("buttons row not squashed by vertical squeeze",
                $"{squeezeButtonsH} vs full {fullButtonsH}", Math.Abs(squeezeButtonsH - fullButtonsH) < 1.5);
            Check("prompt row stays >= 1 line under squeeze",
                $"height {squeezePromptH}", squeezePromptH >= 16);

            // Also squeeze via the main window (Phase 3 style) and re-check rows.
            doc.MdiBounds = new Dock.Model.Core.DockRect(0, 0, 660, 400.8);
            await Settle();
            mainWindow.Width = 600;
            mainWindow.Height = 240;
            await Settle();
            var liveC = GetLiveControls(mainWindow);
            promptBox = liveC.PromptTextBox;
            buttonsRow = mainWindow.GetVisualDescendants()
                .OfType<StackPanel>().First(sp => sp.Name == "ButtonsRow");
            Check("buttons row not squashed by main-window shrink",
                $"{buttonsRow.Bounds.Height} vs full {fullButtonsH}", Math.Abs(buttonsRow.Bounds.Height - fullButtonsH) < 1.5);
            Check("prompt row >= 1 line after main-window shrink",
                $"height {promptBox.Bounds.Height}", promptBox.Bounds.Height >= 16);


            // ---- Phase 7: model parameter descriptors & dialog flow ----
            sb.AppendLine();
            sb.AppendLine("== Phase 7: model parameters fetched, copied per window, validated ==");

            // Fetch descriptors for a known image model from the live API.
            var orProvider = new Image_Generator.Services.OpenRouterProvider();
            var descriptors = await orProvider.GetModelParameterDescriptorsAsync(
                "bytedance-seed/seedream-5-5", null, CancellationToken.None);
            var userFacing = descriptors.Where(Image_Generator.Models.ModelParameters.IsUserFacing).ToList();
            sb.AppendLine($"  descriptors fetched          : {descriptors.Count} total, {userFacing.Count} user-facing");
            Check("descriptor fetch returns parameters",
                $"{userFacing.Count} params", userFacing.Count >= 4);
            var resDesc = userFacing.FirstOrDefault(d => d.Name == "resolution");
            Check("resolution descriptor is enum",
                $"kind={resDesc?.Kind}", resDesc?.Kind == Image_Generator.Models.ModelParameterKind.Enum
                && resDesc.Values.Count >= 3);
            var nDesc = userFacing.FirstOrDefault(d => d.Name == "n");
            Check("n descriptor is range",
                $"kind={nDesc?.Kind} min={nDesc?.Min} max={nDesc?.Max}",
                nDesc?.Kind == Image_Generator.Models.ModelParameterKind.Range
                && nDesc.Min == 1 && nDesc.Max == 1);
            var seedDesc = userFacing.FirstOrDefault(d => d.Name == "seed");
            Check("seed descriptor is boolean",
                $"kind={seedDesc?.Kind}", seedDesc?.Kind == Image_Generator.Models.ModelParameterKind.Boolean);
            Check("input_references excluded from UI",
                $"present: {userFacing.Any(d => d.Name == "input_references")}",
                !userFacing.Any(d => d.Name == "input_references"));

            // Per-window copy: new document gets its own values, all unset.
            var prefs2 = new Image_Generator.Models.AppPreferences
            {
                OpenRouterGenerationModelParameters = userFacing
            };
            var vm2 = new MainWindowViewModel(
                new Image_Generator.Services.PreferencesService(),
                prefs2,
                new Image_Generator.Services.ProviderFactory(),
                new Image_Generator.Services.ImageIOService());
            var doc2 = vm2.DockFactory.DocumentDock?.VisibleDockables?
                .OfType<ImageDocumentViewModel>().FirstOrDefault();
            Check("new window gets its own parameter copy",
                $"{doc2?.ModelParameters.Count} values", doc2?.ModelParameters.Count == userFacing.Count);
            Check("all new values default to unset",
                $"set: {doc2?.ModelParameters.Count(p => p.Value is not null)}",
                doc2?.ModelParameters.All(p => p.Value is null) == true);

            // Setting a value and building the request dictionary.
            if (doc2 is not null)
            {
                doc2.ModelParameters.First(p => p.Descriptor.Name == "resolution").Value = "2K";
                doc2.ModelParameters.First(p => p.Descriptor.Name == "seed").Value = "12345";
                var reqParams = doc2.ModelParameters
                    .Where(p => p.Value is not null)
                    .ToDictionary(p => p.Descriptor.Name,
                        p => Image_Generator.Models.ModelParameters.ToRequestValue(p.Descriptor.Name, p.Value));
                Check("request dict contains set values",
                    $"resolution={reqParams.GetValueOrDefault("resolution")}, seed={reqParams.GetValueOrDefault("seed")}",
                    (string?)reqParams.GetValueOrDefault("resolution") == "2K"
                    && reqParams.GetValueOrDefault("seed") is 12345L);
                Check("unset values omitted from request",
                    $"contains aspect_ratio: {reqParams.ContainsKey("aspect_ratio")}",
                    !reqParams.ContainsKey("aspect_ratio"));

                // Dialog edit flow: copy -> validate -> commit, with checkboxes.
                var dlgVm = new Image_Generator.ViewModels.ModelParametersDialogViewModel(doc2);
                var seedRow0 = dlgVm.Rows.First(r => r.Descriptor.Name == "seed");
                var resRow0 = dlgVm.Rows.First(r => r.Descriptor.Name == "resolution");
                Check("mandatory params start checked",
                    $"resolution selected={resRow0.IsSelected}",
                    resRow0.IsSelected);
                Check("optional params start unchecked",
                    $"seed selected={seedRow0.IsSelected}, n selected={dlgVm.Rows.First(r => r.Descriptor.Name == "n").IsSelected}",
                    !seedRow0.IsSelected && !dlgVm.Rows.First(r => r.Descriptor.Name == "n").IsSelected);
                Check("mandatory checkbox locked",
                    $"checkboxEnabled={resRow0.IsCheckboxEnabled}",
                    !resRow0.IsCheckboxEnabled);
                Check("optional checkbox clickable",
                    $"checkboxEnabled={seedRow0.IsCheckboxEnabled}",
                    seedRow0.IsCheckboxEnabled);
                resRow0.IsSelected = false; // attempt to deselect a mandatory param
                Check("mandatory cannot be deselected",
                    $"resolution still selected={resRow0.IsSelected}",
                    resRow0.IsSelected);
                Check("mandatory control stays enabled",
                    $"controlEnabled={resRow0.IsControlEnabled}",
                    resRow0.IsControlEnabled);
                Check("optional control disabled when unchecked",
                    $"controlEnabled={seedRow0.IsControlEnabled}",
                    !seedRow0.IsControlEnabled);
                seedRow0.IsSelected = true;
                Check("optional control enabled when checked",
                    $"controlEnabled={seedRow0.IsControlEnabled}",
                    seedRow0.IsControlEnabled);
                Check("dialog rows mirror parameters",
                    $"{dlgVm.Rows.Count} rows", dlgVm.Rows.Count == doc2.ModelParameters.Count);
                var seedRow = dlgVm.Rows.First(r => r.Descriptor.Name == "seed");
                var resRow = dlgVm.Rows.First(r => r.Descriptor.Name == "resolution");
                Check("enum row shows (default) + values",
                    $"options={string.Join(",", resRow.EnumOptions)}",
                    resRow.EnumOptions[0] == "(default)" && resRow.EnumOptions.Contains("2K"));

                // Invalid range input must fail validation and abort commit.
                // Deselected rows skip validation entirely — select first.
                var rangeRow = dlgVm.Rows.First(r => r.Descriptor.Name == "n");
                rangeRow.IsSelected = true;
                rangeRow.TextValue = "5"; // n max is 1
                rangeRow.Validate();
                Check("out-of-range value rejected",
                    $"error='{rangeRow.ValidationError}'", rangeRow.HasValidationError);
                Check("commit aborted when invalid", $"allValid={dlgVm.AllValid}", !dlgVm.TryCommit());
                // Deselecting an invalid row makes the dialog valid again.
                rangeRow.IsSelected = false;
                rangeRow.Validate();
                Check("deselected invalid row ignored",
                    $"error='{rangeRow.ValidationError}'", !rangeRow.HasValidationError);
                rangeRow.IsSelected = true;
                rangeRow.TextValue = "1";
                rangeRow.Validate();
                Check("in-range value accepted",
                    $"error='{rangeRow.ValidationError}'", !rangeRow.HasValidationError);
                // Diagnose which rows are still invalid
                foreach (var r in dlgVm.Rows)
                {
                    r.Validate();
                    if (r.HasValidationError)
                        sb.AppendLine($"  still invalid: {r.Descriptor.Name} text='{r.TextValue}' err='{r.ValidationError}'");
                }
                Check("commit succeeds when valid", $"allValid={dlgVm.AllValid}", dlgVm.TryCommit());

                // Deselected parameters must not reach the payload even if their
                // text field still holds a value.
                var nRow2 = dlgVm.Rows.First(r => r.Descriptor.Name == "n");
                nRow2.TextValue = "1";
                nRow2.IsSelected = false;
                nRow2.Commit();
                Check("deselected param commits to null",
                    $"n value={nRow2.Source.Value ?? "null"}", nRow2.Source.Value is null);
                // Re-selecting restores editability but value stays unset until entered.
                nRow2.IsSelected = true;
                // Clearing the text and committing stores null ("use default").
                nRow2.TextValue = "";
                nRow2.Commit();
                Check("cleared selected param commits to default",
                    $"n value={nRow2.Source.Value ?? "null"}", nRow2.Source.Value is null);
                // Entering a valid value while selected commits it.
                nRow2.TextValue = "1";
                nRow2.Commit();
                Check("entered value commits when selected",
                    $"n value={nRow2.Source.Value}", nRow2.Source.Value == "1");
            }


            // ---- Phase 8: parameter persistence & model-change replacement ----
            sb.AppendLine();
            sb.AppendLine("== Phase 8: parameters persist to config and refresh on model change ==");

            // The harness constructs a MainWindowViewModel with empty params —
            // the startup fetch (EnsureModelParametersAsync) should have run
            // against the live API and backfilled the blank window.
            await Task.Delay(3000); // give the fire-and-forget fetch time
            var prefsPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ImageGenerator", "preferences.json");

            // Build a fresh VM with a config that has parameters already
            // (simulating a saved config) — verify they load from config
            // without any API call (ParametersFor matches).
            var savedPrefs = new Image_Generator.Models.AppPreferences
            {
                OpenRouterGenerationModel = "bytedance-seed/seedream-5-5",
                OpenRouterGenerationModelParameters = userFacing,
                OpenRouterGenerationModelParametersFor = "bytedance-seed/seedream-5-5"
            };
            // Round-trip through the real preferences service to test serialization.
            var tmpDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "IGParamTest");
            var tmpPath = System.IO.Path.Combine(tmpDir, "roundtrip.json");
            System.IO.Directory.CreateDirectory(tmpDir);
            await System.IO.File.WriteAllTextAsync(tmpPath,
                System.Text.Json.JsonSerializer.Serialize(savedPrefs));
            var loadedPrefs = System.Text.Json.JsonSerializer.Deserialize<Image_Generator.Models.AppPreferences>(
                await System.IO.File.ReadAllTextAsync(tmpPath));
            Check("parameters survive config round-trip",
                $"{loadedPrefs?.OpenRouterGenerationModelParameters.Count} params for '{loadedPrefs?.OpenRouterGenerationModelParametersFor}'",
                loadedPrefs?.OpenRouterGenerationModelParameters.Count == userFacing.Count
                && loadedPrefs.OpenRouterGenerationModelParametersFor == "bytedance-seed/seedream-5-5");

            // New window from a loaded config gets its copy from the config.
            var vm3 = new MainWindowViewModel(
                new Image_Generator.Services.PreferencesService(),
                savedPrefs,
                new Image_Generator.Services.ProviderFactory(),
                new Image_Generator.Services.ImageIOService());
            var doc3 = vm3.DockFactory.DocumentDock?.VisibleDockables?
                .OfType<ImageDocumentViewModel>().FirstOrDefault();
            Check("window gets parameters from loaded config",
                $"{doc3?.ModelParameters.Count} values", doc3?.ModelParameters.Count == userFacing.Count);

            // Stale-schema detection: model changed but ParametersFor is the
            // old model — the marker must mismatch.
            var stalePrefs = new Image_Generator.Models.AppPreferences
            {
                OpenRouterGenerationModel = "google/gemini-2.5-flash-image",
                OpenRouterGenerationModelParameters = userFacing, // seedream params!
                OpenRouterGenerationModelParametersFor = "bytedance-seed/seedream-5-5"
            };
            Check("stale schema detected by ParametersFor mismatch",
                $"model={stalePrefs.OpenRouterGenerationModel} paramsFor={stalePrefs.OpenRouterGenerationModelParametersFor}",
                stalePrefs.OpenRouterGenerationModelParametersFor != stalePrefs.OpenRouterGenerationModel);

            // Fetch the new model's real descriptors to prove replacement works.
            var geminiDescriptors = (await orProvider.GetModelParameterDescriptorsAsync(
                "google/gemini-2.5-flash-image", null, CancellationToken.None))
                .Where(Image_Generator.Models.ModelParameters.IsUserFacing).ToList();
            sb.AppendLine($"  gemini-2.5-flash-image params: {string.Join(", ", geminiDescriptors.Select(d => d.Name))}");
            Check("replacement model has different parameters",
                $"{geminiDescriptors.Count} params", geminiDescriptors.Count > 0);

            // Single provider listed.
            var factory = new Image_Generator.Services.ProviderFactory();
            Check("only OpenRouter provider listed",
                $"{factory.GetAvailableProviders().Count} provider(s)",
                factory.GetAvailableProviders().Count == 1
                && factory.GetAvailableProviders()[0].Id == "openrouter");
            Check("any provider id maps to OpenRouter",
                $"custom→{factory.CreateProvider("custom", savedPrefs).Name}",
                factory.CreateProvider("custom", savedPrefs) is Image_Generator.Services.OpenRouterProvider);


            // ---- Phase 9: .prompt metadata save/load round-trip ----
            sb.AppendLine();
            sb.AppendLine("== Phase 9: .prompt metadata carries model + parameters ==");

            // Save an image with metadata, then load it back.
            var io = new Image_Generator.Services.ImageIOService();
            var imgBytes = MakePng(64, 48);
            var tmpFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "IGMeta", "test.png");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(tmpFile)!);
            var savedParams = new Dictionary<string, string?> { ["resolution"] = "2K", ["seed"] = "12345" };
            await io.SaveImageAsync(tmpFile, imgBytes, "a test prompt",
                "bytedance-seed/seedream-5-5", savedParams);

            var promptRaw = await System.IO.File.ReadAllTextAsync(tmpFile + ".prompt");
            sb.AppendLine($"  .prompt file content: {promptRaw.Replace("\n", " ")}");
            Check(".prompt is JSON with model+params",
                $"has model={promptRaw.Contains("bytedance-seed/seedream-5-5")}, has resolution={promptRaw.Contains("\"2K\"")}",
                promptRaw.Contains("bytedance-seed/seedream-5-5") && promptRaw.Contains("\"2K\""));

            var (_, loadedMeta) = await io.LoadImageWithMetadataAsync(tmpFile);
            Check("metadata round-trips model",
                $"model={loadedMeta?.Model}", loadedMeta?.Model == "bytedance-seed/seedream-5-5");
            Check("metadata round-trips parameters",
                $"resolution={loadedMeta?.Parameters?.GetValueOrDefault("resolution")}, seed={loadedMeta?.Parameters?.GetValueOrDefault("seed")}",
                loadedMeta?.Parameters?.GetValueOrDefault("resolution") == "2K"
                && loadedMeta?.Parameters?.GetValueOrDefault("seed") == "12345");
            Check("metadata round-trips prompt",
                $"prompt={loadedMeta?.Prompt}", loadedMeta?.Prompt == "a test prompt");

            // Legacy plain-text .prompt still loads as prompt-only.
            await System.IO.File.WriteAllTextAsync(tmpFile + ".prompt", "legacy plain prompt");
            var (_, legacyMeta) = await io.LoadImageWithMetadataAsync(tmpFile);
            Check("legacy plain .prompt loads",
                $"prompt={legacyMeta?.Prompt}, model={legacyMeta?.Model}",
                legacyMeta?.Prompt == "legacy plain prompt" && string.IsNullOrEmpty(legacyMeta?.Model));

            // Same-model restore: loading a document whose .prompt model matches
            // the config model restores the parameter values into the window.
            var matchPrefs = new Image_Generator.Models.AppPreferences
            {
                OpenRouterGenerationModel = "bytedance-seed/seedream-5-5",
                OpenRouterGenerationModelParameters = userFacing
            };
            var vm4 = new MainWindowViewModel(
                new Image_Generator.Services.PreferencesService(),
                matchPrefs,
                new Image_Generator.Services.ProviderFactory(),
                new Image_Generator.Services.ImageIOService());
            var doc4 = vm4.DockFactory.DocumentDock?.VisibleDockables?
                .OfType<ImageDocumentViewModel>().FirstOrDefault();
            var restored = vm4.DockFactory.CreateLoadedImageDocument(
                imgBytes, loadedMeta?.Prompt ?? "a test prompt", "test.png",
                "bytedance-seed/seedream-5-5", savedParams);
            var resVal = restored?.ModelParameters.FirstOrDefault(p => p.Descriptor.Name == "resolution")?.Value;
            Check("same-model load restores parameter values",
                $"resolution={resVal}", resVal == "2K");

            // Different model: the sidecar's model is ADOPTED as the window's
            // model (per-window model support), its schema is fetched from the
            // API, and the saved values are restored onto it.
            var otherPrefs = new Image_Generator.Models.AppPreferences
            {
                OpenRouterGenerationModel = "google/gemini-2.5-flash-image",
                OpenRouterGenerationModelParameters = geminiDescriptors
            };
            var vm5 = new MainWindowViewModel(
                new Image_Generator.Services.PreferencesService(),
                otherPrefs,
                new Image_Generator.Services.ProviderFactory(),
                new Image_Generator.Services.ImageIOService());
            var adopted = vm5.DockFactory.CreateLoadedImageDocument(
                imgBytes, "a test prompt", "test.png",
                "bytedance-seed/seedream-5-5", savedParams);
            Check("different-model load adopts sidecar model",
                $"window model={adopted?.GenerationModel}",
                adopted?.GenerationModel == "bytedance-seed/seedream-5-5");
            Check("config model untouched by load",
                $"config={otherPrefs.OpenRouterGenerationModel}",
                otherPrefs.OpenRouterGenerationModel == "google/gemini-2.5-flash-image");
            // The schema fetch is async — wait for it, then the saved values
            // (resolution=2K, seed=12345) must be restored on seedream's schema.
            for (int i = 0; i < 40 && (adopted?.ModelParameters.Any(p => p.Value is not null) != true); i++)
                await Task.Delay(250);
            var adoptedRes = adopted?.ModelParameters.FirstOrDefault(p => p.Descriptor.Name == "resolution")?.Value;
            var adoptedSeed = adopted?.ModelParameters.FirstOrDefault(p => p.Descriptor.Name == "seed")?.Value;
            var adoptedParams = adopted?.ModelParameters.Select(p => p.Descriptor.Name).ToList();
            sb.AppendLine($"  adopted schema params: {string.Join(",", adoptedParams ?? new List<string>())}");
            Check("different-model load fetches sidecar model's schema",
                $"{adopted?.ModelParameters.Count} params",
                adoptedParams is not null && adoptedParams.Contains("resolution"));
            Check("different-model load restores saved values",
                $"resolution={adoptedRes}, seed={adoptedSeed}",
                adoptedRes == "2K" && adoptedSeed == "12345");


            // ---- Phase 10: per-window model override ----
            sb.AppendLine();
            sb.AppendLine("== Phase 10: per-window generation model override ==");

            // New windows copy the configured model.
            var modelPrefs = new Image_Generator.Models.AppPreferences
            {
                OpenRouterGenerationModel = "bytedance-seed/seedream-5-5",
                OpenRouterGenerationModelParameters = userFacing
            };
            var vm6 = new MainWindowViewModel(
                new Image_Generator.Services.PreferencesService(),
                modelPrefs,
                new Image_Generator.Services.ProviderFactory(),
                new Image_Generator.Services.ImageIOService());
            var doc6 = vm6.DockFactory.DocumentDock?.VisibleDockables?
                .OfType<ImageDocumentViewModel>().FirstOrDefault();
            Check("new window copies configured model",
                $"model={doc6?.GenerationModel}",
                doc6?.GenerationModel == "bytedance-seed/seedream-5-5");

            // Config untouched when window overrides.
            if (doc6 is not null)
            {
                var dlgVm2 = new Image_Generator.ViewModels.ModelParametersDialogViewModel(doc6);
                Check("dialog shows window's model",
                    $"dialog model={dlgVm2.GenerationModel}",
                    dlgVm2.GenerationModel == "bytedance-seed/seedream-5-5");
                Check("ModelChanged false initially",
                    $"changed={dlgVm2.ModelChanged}", !dlgVm2.ModelChanged);

                // Override to a different model and re-read its parameters.
                dlgVm2.GenerationModel = "google/gemini-2.5-flash-image";
                Check("ModelChanged true after override",
                    $"changed={dlgVm2.ModelChanged}", dlgVm2.ModelChanged);

                var reloadOk = await dlgVm2.LoadParametersForModelAsync("google/gemini-2.5-flash-image");
                Check("parameter re-read from API succeeds",
                    $"ok={reloadOk}, rows={dlgVm2.Rows.Count}", reloadOk && dlgVm2.Rows.Count == geminiDescriptors.Count);
                Check("old rows removed and new descriptors added",
                    $"rows={string.Join(",", dlgVm2.Rows.Select(r => r.Descriptor.Name))}",
                    dlgVm2.Rows.All(r => geminiDescriptors.Any(g => g.Name == r.Descriptor.Name))
                    && dlgVm2.Rows.Count == geminiDescriptors.Count);
                Check("reloaded values all unset",
                    $"set={dlgVm2.Rows.Count(r => r.Source.Value is not null)}",
                    dlgVm2.Rows.All(r => r.Source.Value is null));

                // Commit the override (simulating OK): window model updated,
                // parameters replaced, config untouched.
                dlgVm2.TryCommit();
                doc6.GenerationModel = dlgVm2.GenerationModel;
                doc6.ModelParameters.Clear();
                foreach (var row in dlgVm2.Rows)
                    doc6.ModelParameters.Add(row.Source.Clone());
                Check("window model overridden",
                    $"model={doc6.GenerationModel}",
                    doc6.GenerationModel == "google/gemini-2.5-flash-image");
                Check("window parameters replaced",
                    $"{doc6.ModelParameters.Count} values",
                    doc6.ModelParameters.Count == geminiDescriptors.Count);
                Check("config model untouched by window override",
                    $"config={modelPrefs.OpenRouterGenerationModel}",
                    modelPrefs.OpenRouterGenerationModel == "bytedance-seed/seedream-5-5");
                Check("config parameters untouched by window override",
                    $"config params={modelPrefs.OpenRouterGenerationModelParameters.Count}",
                    modelPrefs.OpenRouterGenerationModelParameters == userFacing);

                // Other windows keep the default model.
                var otherDoc = vm6.DockFactory.CreateNewImageDocument();
                Check("new window still gets config default model",
                    $"model={otherDoc?.GenerationModel}",
                    otherDoc?.GenerationModel == "bytedance-seed/seedream-5-5");
            }


            // ---- Phase 11: model browser lists the full image catalogue ----
            sb.AppendLine();
            sb.AppendLine("== Phase 11: model listing merges /images/models catalogue ==");

            var listed = await orProvider.ListModelsAsync(null, CancellationToken.None);
            var imageGenListed = listed.Where(m => m.Capabilities.SupportsImageGeneration).ToList();
            sb.AppendLine($"  models listed          : {listed.Count} total, {imageGenListed.Count} image-gen");
            Check("image catalogue included in listing",
                $"{imageGenListed.Count} image-gen models",
                imageGenListed.Count >= 60); // /images/models has 66
            Check("bytedance-seed/seedream-5-5 listed",
                $"present: {listed.Any(m => m.Id == "bytedance-seed/seedream-5-5")}",
                listed.Any(m => m.Id == "bytedance-seed/seedream-5-5"
                    && m.Capabilities.SupportsImageGeneration));
            Check("other catalogue models listed",
                $"flux={listed.Any(m => m.Id.Contains("flux-3-image"))}, qwen={listed.Any(m => m.Id.Contains("qwen-image"))}, seedream variants={listed.Count(m => m.Id.Contains("seedream"))}",
                listed.Any(m => m.Id.Contains("flux-3-image") && m.Capabilities.SupportsImageGeneration)
                && listed.Any(m => m.Id.Contains("qwen-image") && m.Capabilities.SupportsImageGeneration)
                && listed.Count(m => m.Id.Contains("seedream")) >= 4);
            Check("gemini image models keep pricing",
                $"pricing={listed.FirstOrDefault(m => m.Id == "google/gemini-3-pro-image")?.PricingText}",
                !string.IsNullOrEmpty(listed.FirstOrDefault(m => m.Id == "google/gemini-3-pro-image")?.PricingText)
                && listed.First(m => m.Id == "google/gemini-3-pro-image").PricingText != "Free / Unknown");
            Check("chat models still listed",
                $"gpt-5-chat={listed.Any(m => m.Id.StartsWith("openai/gpt-5") && !m.Capabilities.SupportsImageGeneration)}",
                listed.Any(m => m.Id.StartsWith("openai/gpt-5") && !m.Capabilities.SupportsImageGeneration));


            // ---- Phase 12: generation cost → history tooltip + .prompt metadata ----
            sb.AppendLine();
            sb.AppendLine("== Phase 12: cost persists and reaches the history tooltip ==");

            // Save with a cost, verify the .prompt file carries it.
            var io2 = new Image_Generator.Services.ImageIOService();
            var costFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "IGMeta", "cost.png");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(costFile)!);
            await io2.SaveImageAsync(costFile, imgBytes, "cost test prompt",
                "bytedance-seed/seedream-5-5",
                new Dictionary<string, string?> { ["resolution"] = "2K" },
                cost: 0.0400);
            var costRaw = await System.IO.File.ReadAllTextAsync(costFile + ".prompt");
            Check("cost written to .prompt file",
                $"has cost 0.04: {costRaw.Contains("\"cost\": 0.04")}",
                costRaw.Contains("\"cost\": 0.04"));

            // Load it back: metadata.Cost survives the round-trip.
            var (_, costMeta) = await io2.LoadImageWithMetadataAsync(costFile);
            Check("cost round-trips through metadata",
                $"cost={costMeta?.Cost}", Math.Abs((costMeta?.Cost ?? -1) - 0.04) < 0.0001);

            // Loading a document stamps the record → history tooltip shows it.
            var costDoc = vm4.DockFactory.CreateLoadedImageDocument(
                imgBytes, "cost test prompt", "cost.png",
                "bytedance-seed/seedream-5-5",
                new Dictionary<string, string?> { ["resolution"] = "2K" },
                cost: 0.0400);
            var costRecordVm = costDoc?.History.FirstOrDefault();
            Check("loaded record carries cost",
                $"cost={costRecordVm?.Record.Cost}",
                Math.Abs((costRecordVm?.Record.Cost ?? -1) - 0.04) < 0.0001);
            var tooltip = costRecordVm?.TooltipText ?? "";
            sb.AppendLine($"  tooltip: {tooltip.Replace("\n", " | ")}");
            Check("tooltip includes cost",
                $"contains 'Cost: $0.0400': {tooltip.Contains("Cost: $0.0400")}",
                tooltip.Contains("Cost: $0.0400"));
            Check("tooltip includes timestamp and model",
                $"has ts: {tooltip.Contains(":" )}, has model: {tooltip.Contains("bytedance-seed/seedream-5-5")}",
                tooltip.Contains(":") && tooltip.Contains("bytedance-seed/seedream-5-5"));

            // Cost-less legacy loads: tooltip still works, no Cost line.
            var noCostDoc = vm4.DockFactory.CreateLoadedImageDocument(
                imgBytes, "legacy prompt", "legacy.png",
                model: null, parameters: null, cost: null);
            var noCostTooltip = noCostDoc?.History.FirstOrDefault()?.TooltipText ?? "";
            Check("null cost omits tooltip line",
                $"contains 'Cost': {noCostTooltip.Contains("Cost")}",
                !noCostTooltip.Contains("Cost"));


            // ---- Phase 13: status bar (balance, windows, session spend) ----
            sb.AppendLine();
            sb.AppendLine("== Phase 13: status bar tracks balance, windows, session spend ==");

            // Session spend starts at zero on program load.
            Check("session spend starts at zero",
                $"spent={mainViewModel.SessionSpent}", mainViewModel.SessionSpent == 0.0);

            // Window count tracks open documents.
            var countBefore = mainViewModel.OpenWindowCount;
            var extraDoc = mainViewModel.DockFactory.CreateNewImageDocument();
            var countAfter = mainViewModel.OpenWindowCount;
            Check("window count increments on new document",
                $"{countBefore} -> {countAfter}", countAfter == countBefore + 1);

            // Costs reported by a document flow into the session total:
            // image generation, prompt build, image->prompt.
            var testDoc = extraDoc ?? doc;
            testDoc.CostReported += _ => { }; // ensure event wired (no-op)
            // Simulate the three cost reports (real paths invoke these events).
            mainViewModel.DockFactory.RaiseSessionCost(0.04);   // image generation
            mainViewModel.DockFactory.RaiseSessionCost(0.0002); // prompt build
            mainViewModel.DockFactory.RaiseSessionCost(0.0008); // image->prompt
            mainViewModel.DockFactory.RaiseSessionCost(null);   // unreported — ignored
            Check("session spend sums all three cost sources",
                $"spent={mainViewModel.SessionSpent:0.0000}",
                Math.Abs(mainViewModel.SessionSpent - 0.041) < 0.00001);

            // Formatted status text.
            Check("spent text formatted",
                $"'{mainViewModel.SpentText}'", mainViewModel.SpentText == "Spent: $0.041");
            Check("windows text plural",
                $"'{mainViewModel.WindowsText}'", mainViewModel.WindowsText.Contains("windows open"));
            Check("balance text placeholder before fetch",
                $"'{mainViewModel.BalanceText}'",
                mainViewModel.BalanceText == "Balance: —" || mainViewModel.CreditBalance is not null);

            // Balance text formats when a balance arrives.
            mainViewModel.CreditBalance = 12.5;
            Check("balance text formats",
                $"'{mainViewModel.BalanceText}'", mainViewModel.BalanceText == "Balance: $12.50");

            // Live credits endpoint: without an API key the balance stays null.
            var liveBalance = await orProvider.GetCreditBalanceAsync(null, CancellationToken.None);
            Check("balance fetch returns null without key",
                $"balance={(liveBalance is null ? "null" : liveBalance.ToString())}", liveBalance is null);


            // ---- Phase 14: model browser toolbar (dropdown, free, icon refresh) ----
            sb.AppendLine();
            sb.AppendLine("== Phase 14: model browser dropdown defaults per caller ==");

            System.Action<string, string> checkDefault = (filterType, expectedLabel) =>
            {
                var vm = new Image_Generator.ViewModels.ModelBrowserDialogViewModel(
                    orProvider, null, filterType);
                Check($"dropdown default for '{filterType}' is '{expectedLabel}'",
                    $"selected={vm.SelectedFilter}",
                    vm.SelectedFilter == expectedLabel && vm.CurrentFilterType == Image_Generator.ViewModels.ModelBrowserDialogViewModel.FilterTypeFromLabel(expectedLabel));
            };
            checkDefault("generation", "Image Gen");
            checkDefault("vision", "Vision");
            checkDefault("all", "All Models");
            checkDefault("promptgen", "All Models"); // prompt-gen models aren't a capability class

            Check("filter options are the three dropdown entries",
                $"{string.Join("|", Image_Generator.ViewModels.ModelBrowserDialogViewModel.FilterOptions)}",
                Image_Generator.ViewModels.ModelBrowserDialogViewModel.FilterOptions.Count == 3
                && Image_Generator.ViewModels.ModelBrowserDialogViewModel.FilterOptions.Contains("Image Gen")
                && Image_Generator.ViewModels.ModelBrowserDialogViewModel.FilterOptions.Contains("Vision")
                && Image_Generator.ViewModels.ModelBrowserDialogViewModel.FilterOptions.Contains("All Models"));

            // Changing the dropdown selection re-filters.
            var browserVm = new Image_Generator.ViewModels.ModelBrowserDialogViewModel(
                orProvider, null, "all");
            browserVm.Models.Clear();
            foreach (var m in listed) browserVm.Models.Add(m);
            // Simulate the filter applying on the loaded set: generation filter must keep only image-gen models.
            browserVm.SelectedFilter = "Image Gen";
            // ApplyClientSideFilter is private; verify indirectly via CurrentFilterType
            Check("dropdown change updates filter type",
                $"type={browserVm.CurrentFilterType}", browserVm.CurrentFilterType == "generation");


            // ---- Phase 15: unsaved-close dialog is non-blocking (deadlock regression) ----
            sb.AppendLine();
            sb.AppendLine("== Phase 15: close confirmation dialog renders and is dismissable ==");

            // The old bug: DockFactory.CloseDockable called
            // ShowDialog(...).GetAwaiter().GetResult() on the UI thread,
            // deadlocking — the dialog showed decoration-only (transparent)
            // and the app froze. Verify the new async path shows a real
            // dialog with content and closes normally.
            QuitConfirmTestResult testResult = new();
            var testDialog = new Image_Generator.Views.QuitConfirmDialog("test: close anyway?");
            testDialog.Opened += (_, _) =>
            {
                testResult.Opened = true;
                // Find the message TextBlock via the visual tree (x:Name is
                // internal to the app assembly, so search by content).
                var msgBlock = testDialog.GetVisualDescendants()
                    .OfType<Avalonia.Controls.TextBlock>()
                    .FirstOrDefault(t => t.Text == "test: close anyway?");
                testResult.HasContent = msgBlock is not null;
                // Auto-answer No without user input — proves the dialog is
                // responsive (the old deadlocked one could never run this).
                Avalonia.Threading.Dispatcher.UIThread.Post(() => testDialog.Close(false),
                    Avalonia.Threading.DispatcherPriority.Input);
            };
            var dlgResult = await testDialog.ShowDialog<bool>(mainWindow);
            testResult.ClosedWith = dlgResult;
            Check("confirmation dialog opened with content",
                $"opened={testResult.Opened}, content={testResult.HasContent}",
                testResult.Opened && testResult.HasContent);
            Check("confirmation dialog responsive (auto-closed, returned false)",
                $"result={testResult.ClosedWith}", testResult.ClosedWith == false);

            // Dirty-document close path uses the async continuation — calling
            // CloseDockable on a dirty doc must NOT block the UI thread and
            // must leave the doc open until confirmed. We auto-answer via the
            // Opened hook the same way.
            var dirtyDoc = mainViewModel.DockFactory.CreateNewImageDocument();
            dirtyDoc!.IsDirty = true;
            bool closeReturnedImmediately = false;
            var closeTask = System.Threading.Tasks.Task.Run(() =>
            {
                mainViewModel.DockFactory.CloseDockable(dirtyDoc);
                closeReturnedImmediately = true;
            });
            await System.Threading.Tasks.Task.Delay(1500);
            Check("CloseDockable returns without blocking",
                $"returned={closeReturnedImmediately}", closeReturnedImmediately);
            // The pending confirmation dialog gets auto-dismissed by the
            // harness teardown; the doc must still be open (unconfirmed).
            var stillOpen = mainViewModel.DockFactory.DocumentDock?.VisibleDockables?
                .OfType<ImageDocumentViewModel>().Contains(dirtyDoc) == true;
            Check("dirty doc stays open until confirmed",
                $"stillOpen={stillOpen}", stillOpen);

            doc.MdiBounds = new Dock.Model.Core.DockRect(0, 0, 660, 400.8);
            await Settle();
            mainWindow.Width = 1100;
            mainWindow.Height = 700;
            await Settle();

            // ---- Phase 6: prompt generator <skip> handling ----
            sb.AppendLine();
            sb.AppendLine("== Phase 6: <skip> topic omits its line from the LLM message ==");

            var promptVm = new Image_Generator.ViewModels.PromptGeneratorDialogViewModel(
                new Image_Generator.Models.AppPreferences(),
                new Image_Generator.Services.ProviderFactory());

            Check("all option lists start with <skip>",
                $"humour={Image_Generator.ViewModels.PromptGeneratorDialogViewModel.HumourOptions[0]}",
                Image_Generator.ViewModels.PromptGeneratorDialogViewModel.HumourOptions[0] == "<skip>"
                && Image_Generator.ViewModels.PromptGeneratorDialogViewModel.StyleOptions[0] == "<skip>"
                && Image_Generator.ViewModels.PromptGeneratorDialogViewModel.TopicOptions[0] == "<skip>"
                && Image_Generator.ViewModels.PromptGeneratorDialogViewModel.ColourOptions[0] == "<skip>"
                && Image_Generator.ViewModels.PromptGeneratorDialogViewModel.TimeOfDayOptions[0] == "<skip>"
                && Image_Generator.ViewModels.PromptGeneratorDialogViewModel.MoodOptions[0] == "<skip>");
            Check("defaults are not <skip>",
                $"humour={promptVm.Humour}, style={promptVm.Style}",
                promptVm.Humour != "<skip>" && promptVm.Style != "<skip>" && promptVm.Topic != "<skip>"
                && promptVm.Colour != "<skip>" && promptVm.TimeOfDay != "<skip>" && promptVm.Mood != "<skip>");

            var full = promptVm.BuildUserInput() ?? "";
            Check("all chosen: 6 lines present",
                $"{full.Split('\n').Length} lines", full.Split('\n').Length == 6);
            Check("full input has no skip",
                $"contains '<skip>': {full.Contains("<skip>")}", !full.Contains("<skip>"));

            promptVm.Topic = "<skip>";
            promptVm.Mood = "<skip>";
            var partly = promptVm.BuildUserInput() ?? "";
            sb.AppendLine($"  input with Topic+Mood skipped:\n{string.Join("\n", partly.Split('\n').Select(l => "    " + l))}");
            Check("skipped topics omitted from input",
                $"{partly.Split('\n').Length} lines", partly.Split('\n').Length == 4);
            Check("Topic line absent", $"present: {partly.Contains("Topic:")}", !partly.Contains("Topic:"));
            Check("Mood line absent", $"present: {partly.Contains("Mood:")}", !partly.Contains("Mood:"));
            Check("other topics still present",
                $"humour={partly.Contains("Humour:")}, style={partly.Contains("Style:")}",
                partly.Contains("Humour:") && partly.Contains("Style:") && partly.Contains("Colour:") && partly.Contains("Time of Day:"));

            // Randomize must never roll <skip>
            promptVm.RandomizeCommand.Execute(null);
            Check("Randomize never picks <skip>",
                $"topic={promptVm.Topic}, mood={promptVm.Mood}",
                promptVm.Humour != "<skip>" && promptVm.Style != "<skip>" && promptVm.Topic != "<skip>"
                && promptVm.Colour != "<skip>" && promptVm.TimeOfDay != "<skip>" && promptVm.Mood != "<skip>");

            // All skipped + no custom -> null (nothing to generate)
            promptVm.Humour = "<skip>";
            promptVm.Style = "<skip>";
            promptVm.Topic = "<skip>";
            promptVm.Colour = "<skip>";
            promptVm.TimeOfDay = "<skip>";
            promptVm.Mood = "<skip>";
            promptVm.CustomText = "";
            Check("all skipped + no custom -> null input",
                $"input={promptVm.BuildUserInput() ?? "null"}", promptVm.BuildUserInput() is null);

            // All skipped + custom text -> custom only
            promptVm.CustomText = "a lone lighthouse in a storm";
            var customOnly = promptVm.BuildUserInput() ?? "";
            Check("all skipped + custom -> custom only",
                $"input='{customOnly}'",
                customOnly == "Custom instructions: a lone lighthouse in a storm");

            // ToOptions maps skips to null
            var opts = promptVm.ToOptions();
            Check("ToOptions maps <skip> to null",
                $"humour={opts.Humour ?? "null"}",
                opts.Humour is null && opts.Style is null && opts.Topic is null
                && opts.Colour is null && opts.TimeOfDay is null && opts.Mood is null);

        }
        catch (Exception ex)
        {
            sb.AppendLine("HARNESS EXCEPTION: " + ex);
            failures++;
        }

        sb.AppendLine();
        sb.AppendLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
        File.WriteAllText(outPath, sb.ToString());
    }


    private sealed record LiveControls(
        Image_Generator.Views.Documents.ImageDocumentView View,
        Grid ControlsGrid,
        Grid CanvasHost,
        TextBox PromptTextBox,
        Dock.Avalonia.Controls.MdiDocumentWindow MdiWindow);

    private static LiveControls GetLiveControls(Window mainWindow)
    {
        var view = mainWindow.GetVisualDescendants()
            .OfType<Image_Generator.Views.Documents.ImageDocumentView>()
            .FirstOrDefault()
            ?? throw new InvalidOperationException("no live ImageDocumentView");
        var controlsGrid = view.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "ControlsGrid");
        var canvasHost = view.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "CanvasHost");
        var promptBox = view.GetVisualDescendants().OfType<TextBox>().First(tb => tb.Name == "PromptTextBox");
        var mdiWindow = view.GetVisualDescendants()
            .OfType<Dock.Avalonia.Controls.MdiDocumentWindow>()
            .Concat(mainWindow.GetVisualDescendants().OfType<Dock.Avalonia.Controls.MdiDocumentWindow>())
            .FirstOrDefault(w => view.GetVisualAncestors().Contains(w))
            ?? throw new InvalidOperationException("no live MdiDocumentWindow for view");
        return new LiveControls(view, controlsGrid, canvasHost, promptBox, mdiWindow);
    }


    private sealed class QuitConfirmTestResult
    {
        public bool Opened;
        public bool HasContent;
        public bool ClosedWith;
    }

    private static bool viewFound(Window mainWindow) =>
        mainWindow.GetVisualDescendants()
            .OfType<Image_Generator.Views.Documents.ImageDocumentView>()
            .Any();

    private static async Task Settle()
    {
        for (int i = 0; i < 20; i++)
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Task.Delay(200);
    }

    private static string Fmt(Rect r) =>
        $"{r.Width.ToString("0.##", CultureInfo.InvariantCulture)}x{r.Height.ToString("0.##", CultureInfo.InvariantCulture)} @({r.X.ToString("0.##", CultureInfo.InvariantCulture)},{r.Y.ToString("0.##", CultureInfo.InvariantCulture)})";

    /// <summary>Builds a PNG of the given size using Avalonia's own encoder.</summary>
    private static byte[] MakePng(int width, int height)
    {
        var wb = new WriteableBitmap(new PixelSize(width, height), new Avalonia.Vector(96, 96));
        using (var fb = wb.Lock())
        {
            unsafe
            {
                var basePtr = (byte*)fb.Address;
                for (int y = 0; y < height; y++)
                {
                    var row = basePtr + y * fb.RowBytes;
                    for (int x = 0; x < width; x++)
                    {
                        var px = row + x * 4;
                        px[0] = (byte)(x * 255 / width);
                        px[1] = (byte)(y * 255 / height);
                        px[2] = (byte)((x + y) & 0xFF);
                        px[3] = 0xFF;
                    }
                }
            }
        }
        using var ms = new MemoryStream();
        wb.Save(ms);
        return ms.ToArray();
    }
}
