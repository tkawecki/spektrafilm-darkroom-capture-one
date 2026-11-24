using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

internal sealed partial class SpatialPreview : IDisposable
{
    private const int TransferSteps = 8192;
    private const int InitialPreviewLongEdge = 1200;
    // How often an idle panel looks for another image or an adjustment made in Capture One (the read takes
    // about 0.04 ms). 100 ms until 2026-11-21; half of it comes off the first frame after such a change.
    private const int SelectionCheckIntervalMs = 50;
    private const int DragSettleDelay = 220;
    private const long ProgressivePreviewPixels = 8_000_000;
    // Viewer resolution "Auto": the final frame has the size the image has on screen and becomes a full-size
    // frame when the Viewer is zoomed in. Capture One still develops the full source once, so zooming needs no
    // new develop.
    private const string AutoSize = "auto";
    private const int AutoZoomSettleMs = 300;
    // After a quick frame for an adjustment made in Capture One, the full develop waits until the adjustment has
    // rested this long: it cannot be stopped once started and would hold up the next quick frame for about a second.
    private const int AdjustmentSettleMs = 120;
    // A slow adjustment (steps further apart than the wait above) would start that develop between two steps.
    // The wait therefore grows to about two of the recent steps, and while the mouse button is held (a slider
    // being dragged in Capture One) the develop does not start at all, unless the slider is kept still.
    private const int AdjustmentSettleMaxMs = 600;
    private const int AdjustmentBurstMs = 1200;
    private const int AdjustmentHoldMaxMs = 1500;
    // Replaced by the copy-only test hook, which cannot press a real mouse button.
    internal static Func<bool> AdjustmentPointerHeld = () =>
        (System.Windows.Forms.Control.MouseButtons & System.Windows.Forms.MouseButtons.Left) != 0;
    // Long edges for the frame that follows a change while the full-size frame renders (ViewerDraftSource caps at 2560).
    private static readonly int[] SettleLongEdges = { 1600, 2048, 2560 };
    private static readonly double[] SrgbDecode = BuildTransfer(DecodeSrgb);
    private static readonly double[] SrgbEncode = BuildTransfer(EncodeSrgb);
    private static readonly double[] Rec709Gamma24Decode = BuildTransfer(value => Math.Pow(value, 2.4));
    private static readonly double[] AdobeDecode = BuildTransfer(value => Math.Pow(value, 563.0 / 256));
    private static readonly double[] ProPhotoDecode = BuildTransfer(value => value <= 1.0 / 32 ? value / 16 : Math.Pow(value, 1.8));
    private static readonly InputChoice[] InputSpaces =
    {
        new(PreviewInputSpace.Srgb, "srgb", "sRGB"),
        new(PreviewInputSpace.AdobeRgb, "adobe_rgb", "Adobe RGB (1998)"),
        new(PreviewInputSpace.ProPhotoRgb, "prophoto", "ProPhoto RGB"),
        new(PreviewInputSpace.LinearRec2020, "linear_rec2020", "Linear Rec.2020"),
    };
    private static readonly Choice[] OutputSpaces =
    {
        new("srgb", "sRGB"), new("rec709_gamma24", "Rec.709 gamma 2.4"), new("linear_rec709", "Linear Rec.709"),
    };
    private static readonly Choice[] ExportSpaces = ExportColorSpace.Choices.Select(x => new Choice(x.Id, x.Label)).ToArray();
    private static readonly Choice[] ExportFormats = {
        new("recipe", "Use C1 recipe format (TIFF if unavailable)"), new("tif", "TIFF · 16-bit"),
        new("png", "PNG · 16-bit"), new("jpg", "JPEG · 8-bit"), new("dng", "Linear DNG · 16-bit Rec.2020"),
    };
    private readonly ComboBox exportFormat = new() { DisplayMemberPath = nameof(Choice.Label), ItemsSource = ExportFormats, SelectedIndex = 0 };
    private static readonly Choice[] PreviewSizes =
    {
        new(AutoSize, "Auto (follows Viewer zoom)"), new("native", "Native resolution"), new("4096", "4096 px"), new("3072", "3072 px"), new("2560", "2560 px"),
        new("2048", "2048 px"), new("1600", "1600 px"), new("1200", "1200 px"), new("900", "900 px"), new("720", "720 px"),
    };
    private static readonly DragSize[] DragSizes =
    {
        new(0, "Off"), new(720, "720 px"), new(1200, "1200 px"), new(1600, "1600 px"),
        new(2048, "2048 px"), new(2560, "2560 px"),
    };
    private static readonly DragSize DefaultDragSize = DragSizes[3]; // 1600 px
    private static readonly Choice[] DiffusionFamilies =
    {
        new("off", "Off"), new("black_pro_mist", "Black Pro-Mist"), new("glimmerglass", "Glimmerglass"),
        new("pro_mist", "Pro-Mist"), new("cinebloom", "CineBloom"),
    };
    private readonly object window;
    private readonly Func<LookSettings?> getSettings;
    private readonly Func<Guid, bool> useSelectedLook;
    private readonly Dictionary<string, NativeSlider> values = new();
    private readonly Dictionary<string, ComboBox> choices = new();
    private readonly Dictionary<string, CheckBox> toggles = new();
    private readonly Dictionary<string, string> curveSelections = new();
    private readonly List<Action> capabilityUpdates = new();
    private bool controlsReady;
    private bool printMode = true;
    private readonly TextBlock scannerHint = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3) };
    private readonly TextBlock synthesisHint = new()
    {
        Text = "Experimental model: Grain Synthesis can change brightness and color as well as texture. Use Production for more stable tones. Full-resolution preview/export supports up to 32 MP with Synthesis; saved quality settings are retained.",
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3), Visibility = Visibility.Collapsed,
    };
    private readonly Settings defaults;
    private readonly TextBlock status = new() { Margin = new Thickness(0, 5, 0, 3), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock renderErrorText = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White };
    private readonly Border renderError = new()
    {
        Background = new SolidColorBrush(Color.FromRgb(0xA3, 0x2E, 0x2E)), Padding = new Thickness(6, 4, 6, 4),
        Margin = new Thickness(0, 4, 0, 2), Visibility = Visibility.Collapsed,
    };
    private readonly TextBlock inputAdviceText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel inputAdvice = new() { Margin = new Thickness(0, 4, 0, 2), Visibility = Visibility.Collapsed };
    private readonly TextBlock grainHint = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3) };
    private readonly TextBlock halationHint = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3) };
    private readonly TextBlock halationStockHint = new()
    {
        Text = "These three values are the renderer's \"use film stock\" marker, so the stock's own halation colour is still used. Move any slider to override it.",
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3), Visibility = Visibility.Collapsed,
    };
    private StackPanel? particleGrain;
    private StackPanel? synthesisGrain;
    private StackPanel? halationChannels;
    private StackPanel? couplerChannels;
    private readonly TextBlock couplerHint = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3) };
    /// <summary>Raised when the user asks to switch this look to the recommended scene-linear input.</summary>
    internal event Action? RecommendedInputRequested;
    // Options parsed by the renderer as integers. Sliders can report fractional values while dragging.
    private static readonly HashSet<string> IntegerOptions = new(StringComparer.Ordinal)
    {
        "grain-seed", "grain-sublayer-count", "grain-synthesis-samples", "grain-synthesis-cell-cap",
    };
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly ViewerPreview viewer;
    private OpticalRenderer? renderer;
    private bool rendererUsesHighDepthPreview;
    private CancellationTokenSource? pending;
    private CancellationTokenSource? pendingDraft;
    private CancellationTokenSource? pendingInitial;
    private OpticalRenderer? draftRenderer;
    // Renderer start-up started while Capture One develops the source (see StartRendererWarmup).
    private Task? rendererWarmup;
    private Task? draftWarmup;
    private HostImageSource.Frame? draftSource;
    private byte[]? draftPixels;
    private string[]? draftArguments;
    private int draftPixelsEdge;
    // Screen-resolution copy of the source for the frame shown between a change and the native-resolution frame.
    private HostImageSource.Frame? settleSource;
    private int settleEdge;
    // The frame being rendered from Capture One's small preview while the full source is developed, if any.
    private Task? quickSourceWork;
    // Quick frames are numbered per source job; one is shown only while a new source is awaited, when it is
    // newer than the last one shown and no full source has arrived since its job started.
    private long quickSequence;
    private long shownQuickSequence;
    private int sourceGeneration;
    private bool awaitingSource;
    // The Capture One adjustment state the last quick-only frame was made for, and when.
    private HostImageSource.Revision? quickRevision;
    private long quickRevisionAt;
    // Time between the last two adjustments of one run of adjustments; 0 for a single adjustment.
    private long adjustmentIntervalMs;
    // What the cached final render was made from (the full source, or its screen-size copy in Auto).
    private HostImageSource.Frame? renderedInput;
    private long autoZoomNeededSince;
    // Whether the image painted last was shown whole (Capture One's Fit): its crop inside the Viewer and
    // filling it in one direction. A newly selected image is then expected at Fit too.
    private bool viewerAtFit;
    private bool draftWarmupAttempted;
    private int lastDraftRevision = -1;
    private bool dragging;
    private long lastDragChange;
    private long lastInputChange;
    private HostImageSource.Frame? source;
    private HostImageSource.Revision? sourceRevision;
    private HostImageSource.Revision? failedRevision;
    private byte[]? renderedPixels;
    private string[]? renderedArguments;
    private BitmapSource? sourceDisplay;
    private BitmapSource? renderedDisplay;
    private double renderedStrength;
    private FinishingSettings? renderedFinishing;
    private ScanGeometrySettings? renderedGeometry;
    private ViewerFrameLayout? viewerLayout;
    private FinishingSettings? finishingSnapshot;
    private Dictionary<string, double>? finishingSnapshotValues;
    private Dictionary<string, string>? finishingSnapshotCurves;
    private PreviewInputSpace? sourceSpace;
    private PreviewInputSpace? failedSpace;
    private SourcePreparation? sourcePreparation;
    private SourcePreparation? failedPreparation;
    private static readonly Choice[] PreparationModes =
    {
        new("neutral-film", "Neutral for film"), new("capture-one", "Capture One development"),
    };
    private readonly ComboBox inputPreparation = new()
    {
        DisplayMemberPath = nameof(Choice.Label), ItemsSource = PreparationModes, SelectedIndex = 0,
        ToolTip = "Neutral for film uses Linear Response and bypasses C1 sharpening, Clarity and Structure, including layers. Other C1 corrections remain active. Saved C1 settings are unchanged.",
    };
    private SourcePreparation Preparation => ((Choice)inputPreparation.SelectedItem).Id == "neutral-film"
        ? SourcePreparation.NeutralFilm : SourcePreparation.CaptureOne;
    // The film model is saved in each look, so an update of the model never changes a saved look. A look without the
    // key was made with model 1; new looks and Defaults use the latest model.
    internal const string LatestFilmModel = "4";
    private static readonly Choice[] FilmModels =
    {
        new("4", "Model 4 (current)"), new("3", "Model 3 (looks saved before model 4)"),
        new("2", "Model 2 (looks saved on 23 November 2026)"), new("1", "Model 1 (looks saved before model 2)"),
    };
    private readonly ComboBox filmModel = new()
    {
        DisplayMemberPath = nameof(Choice.Label), ItemsSource = FilmModels, SelectedIndex = 0,
        ToolTip = "The version of the film simulation this look uses; each look keeps its model. Model 4 (new looks) is model 3 with three more defaults of the Python spektrafilm reference: out-of-gamut colours are brought into the output colour space in CAM16-UCS instead of OkLch (saturated colours keep their hue and lightness as in the reference), auto exposure measures the photo as the reference does, and strong DIR coupler settings can no longer fold a density curve back on itself. Model 3 is model 2 with the current film and paper profiles of the Python spektrafilm reference and neutral print filters fitted to them. Model 2 follows the reference more closely than model 1: it adapts the input colours to the film's light with CAT16 (most visible with tungsten films and deep blues), counts grain particles exactly where few develop, keeps the full grain of nearly developed emulsion layers, and makes a grain RMS setting hold for the finished, blurred grain. Model 1 keeps looks saved before model 2 existed exactly as they were.",
    };
    private readonly ComboBox inputSpace = new()
    {
        DisplayMemberPath = nameof(InputChoice.Label),
        ItemsSource = InputSpaces,
        SelectedIndex = 2,
    };
    private readonly ComboBox outputSpace = new() { DisplayMemberPath = nameof(Choice.Label), ItemsSource = OutputSpaces, SelectedIndex = 0 };
    private readonly ComboBox exportSpace = new() { DisplayMemberPath = nameof(Choice.Label), ItemsSource = ExportSpaces, SelectedIndex = 0 };
    private ExportRgbSpace SelectedExportSpace => ExportColorSpace.Parse(((Choice)exportSpace.SelectedItem).Id);
    internal string ExportExtensionFor(string? recipeFormat) => ExportFileOptions.ExtensionFor(
        exportFormat.SelectedItem is Choice { Id: not "recipe" } selected ? selected.Id : recipeFormat);
    private readonly ComboBox previewSize = new() { DisplayMemberPath = nameof(Choice.Label), ItemsSource = PreviewSizes, SelectedIndex = 0 };
    private readonly ComboBox dragPreviewSize = new()
    {
        DisplayMemberPath = nameof(DragSize.Label),
        ItemsSource = DragSizes,
        SelectedItem = DefaultDragSize,
        ToolTip = "Temporary frame while dragging, and during other adjustments to large Viewer previews. Off disables temporary frames. Applies to all photos.",
    };
    private readonly CheckBox showSource = new() { Content = "Show source", Margin = new Thickness(0, 4, 0, 1), IsEnabled = false };
    private readonly Button savePreview = new() { Content = "Save preview PNG…", Margin = new Thickness(0, 4, 0, 1), HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = false };
    private readonly CheckBox fastSynthesis = new()
    {
        Content = "Draft synthesis preview",
        ToolTip = "Uses up to two samples. Draft grain can look substantially noisier. Uncheck to see your selected sampling quality.",
        Margin = new Thickness(0, 4, 0, 1),
    };
    private bool queued;
    private bool loading;
    private bool enabled;
    private Guid? activeVariant;
    private BitmapSource? viewerFrame;
    private ImageScopes.ScopeWindow? scopesWindow;
    private ExposureComparison.ComparisonWindow? comparisonWindow;
    private long nextViewerCheck;
    private int inputRevision;
    private string? sourceSize;
    private string? failedSize;
    // The original file was offline when the last develop failed: develop again once it is connected
    // (after upstream a4e59ba, Spektrafilm for Capture One, Luka Kauzlaric, GPL-3.0).
    private bool failedWhileOffline;
    private bool frameCurrent;
    private bool saveInProgress;
    private CancellationTokenSource? exportWork;
    private bool selectionUnavailable;
    private string? previewDetail;

    private SpatialPreview(object window, Func<LookSettings?> getSettings, Func<Guid, bool> useSelectedLook)
    {
        this.window = window;
        this.getSettings = getSettings;
        this.useSelectedLook = useSelectedLook;
        viewer = new ViewerPreview(window);
        viewer.StatusChanged += UpdateViewerStatus;
        var liveControls = new StackPanel();
        liveControls.Children.Add(status);
        renderError.Child = renderErrorText;
        liveControls.Children.Add(renderError);
        BuildInputAdvice();
        liveControls.Children.Add(inputAdvice);
        var actions = new WrapPanel();
        actions.Children.Add(showSource);
        savePreview.Margin = new Thickness(8, 4, 0, 1);
        actions.Children.Add(savePreview);
        var scopes = new Button { Content = "Scopes…", Margin = new Thickness(8, 4, 0, 1) };
        scopes.Click += (_, _) => {
            if (viewerFrame is not { } frame) { status.Text = "Wait for a Viewer frame before opening scopes."; return; }
            if (scopesWindow is { IsClosed: false }) { scopesWindow.Activate(); scopesWindow.Update(frame); return; }
            scopesWindow = ImageScopes.Show(Window.GetWindow(LiveControls), frame, "Spektrafilm Viewer scopes");
            scopesWindow.Closed += (_, _) => scopesWindow = null;
        };
        actions.Children.Add(scopes);
        foreach (var (label, action) in new (string, Action)[] { ("Pin reference", PinReference), ("Compare reference…", ShowReferenceComparison), ("Clear reference", ClearReference) }) {
            var button = new Button { Content = label, Margin = new Thickness(8, 4, 0, 1) };
            button.Click += (_, _) => { try { action(); } catch (Exception error) { status.Text = error.Message; } };
            actions.Children.Add(button);
        }
        liveControls.Children.Add(actions);
        AddResolutionControls(liveControls);
        LiveControls = liveControls;
        MeteringControls = BuildMetering();
        Element = Build();
        ApplyViewerPreferences();
        defaults = CaptureSettings();
        controlsReady = true;
        UpdateCapabilities();
        refresh.Tick += OnRefreshTick;
        showSource.Checked += (_, _) => OnSettingsChanged();
        showSource.Unchecked += (_, _) => OnSettingsChanged();
        savePreview.Click += async (_, _) => await SavePreviewAsync();
    }

    private bool disposed;
    private async void OnRefreshTick(object? sender, EventArgs args) => await RefreshAsync();
    public void Dispose()
    {
        if (disposed) return;
        exportWork?.Cancel();
        SetEnabled(false);
        ClearReference();
        disposed = true;
        refresh.Tick -= OnRefreshTick;
        viewer.StatusChanged -= UpdateViewerStatus;
        viewer.Dispose();
        SettingsChanged = null;
    }

    internal FrameworkElement Element { get; }
    internal FrameworkElement LiveControls { get; }
    // Automatic film exposure and its metering method, shown on one row under Defaults (not in a section).
    internal FrameworkElement MeteringControls { get; }
    internal TextBlock ExportStatus => status;
    internal int SettingsRevision => inputRevision;
    internal void CancelExport() => exportWork?.Cancel();
    internal bool IsExporting => exportWork is not null;
    internal event Action? SettingsChanged;
    // Film model 4 limits strong DIR settings in the renderer (dirStrengthGuard), so the panel's DIR warning changes.
    internal event Action? FilmModelChanged;
    internal bool FilmModelGuardsDir =>
        int.TryParse(((Choice)filmModel.SelectedItem).Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var model) && model >= 4;
    internal bool CompareSource { get => showSource.IsChecked == true; set => showSource.IsChecked = value; }
    private int DragLongEdge => ((DragSize)dragPreviewSize.SelectedItem).LongEdge;

    // Looks saved with a second processing engine (removed 2026-11-17) cannot be rendered by
    // Native. Refuse them instead of silently reinterpreting their parameters; callers
    // keep the saved file unchanged (VariantLookStore.ProtectUnloadable).
    internal const string RemovedEngineId = "dyeworks-2.2.3";
    internal static void RejectRemovedEngine(Settings? optics)
    {
        if (optics?.Selections?.GetValueOrDefault("processing-engine") == RemovedEngineId)
            throw new InvalidDataException("This look was made with a processing engine that is no longer part of Spektrafilm. Its saved settings are kept unchanged; use Defaults or load a Native preset to edit this photo.");
    }

    // A look saved by a newer panel with a film model this version does not know is refused, never reinterpreted.
    internal static void RejectUnknownFilmModel(Settings? optics)
    {
        if (optics?.Selections?.GetValueOrDefault("film-model") is { } model && !FilmModels.Any(item => item.Id == model))
            throw new InvalidDataException("This look uses film model " + model + ", which this version of the panel does not know. Update the panel to edit it.");
    }

    // A look with Spektrafilm turned off does not use the film model, so an unknown one must not stop its export.
    internal static Settings? WithoutUnknownFilmModel(Settings? optics)
    {
        if (optics?.Selections?.GetValueOrDefault("film-model") is not { } model || FilmModels.Any(item => item.Id == model))
            return optics;
        var selections = new Dictionary<string, string>(optics.Selections);
        selections.Remove("film-model");
        return optics with { Selections = selections };
    }

    internal static SpatialPreview BuildForPanel(object window, Func<LookSettings?> getSettings, Func<Guid, bool> useSelectedLook) =>
        new(window, getSettings, useSelectedLook);

    internal Settings CaptureSettings()
    {
        var numbers = new Dictionary<string, double>();
        var selections = new Dictionary<string, string>();
        var switches = new Dictionary<string, bool>();
        foreach (var (key, value) in curveSelections) selections.Add(key, value);
        PaperDecorationStorage.Capture(paperDecoration, selections);
        foreach (var (key, slider) in values)
            numbers.Add(key, slider.Value);
        foreach (var (key, chooser) in choices)
            selections.Add(key, ((Choice)chooser.SelectedItem).Id);
        selections.Add("input-color", ((InputChoice)inputSpace.SelectedItem).Id);
        selections.Add("input-preparation", ((Choice)inputPreparation.SelectedItem).Id);
        selections.Add("film-model", ((Choice)filmModel.SelectedItem).Id);
        selections.Add("quality-defaults", "2");
        selections.Add("output-color", ((Choice)outputSpace.SelectedItem).Id);
        selections.Add("export-color", ((Choice)exportSpace.SelectedItem).Id);
        selections.Add("export-format", ((Choice)exportFormat.SelectedItem).Id);
        selections.Add("preview-size", ((Choice)previewSize.SelectedItem).Id);
        selections.Add("drag-preview-size", DragLongEdge.ToString(CultureInfo.InvariantCulture));
        foreach (var (key, item) in toggles)
            switches.Add(key, item.IsChecked == true);
        switches.Add("draft-synthesis", fastSynthesis.IsChecked == true);
        switches.Add("show-source", CompareSource);
        return new Settings(numbers, selections, switches);
    }

    internal void LoadSettings(Settings? settings)
    {
        loading = true;
        try
        {
            LoadControlValues(settings ?? defaults);
        }
        finally
        {
            loading = false;
        }
        RequestRefresh();
    }

    private void LoadControlValues(Settings settings)
    {
        // A look without the key was saved before film models existed (model 1). Checked before any control changes.
        var modelId = settings.Selections?.GetValueOrDefault("film-model") ?? "1";
        var savedModel = FilmModels.FirstOrDefault(item => item.Id == modelId)
            ?? throw new InvalidDataException("This look uses film model " + modelId + ", which this version of the panel does not know. Update the panel to edit it.");
        var savedPaper = PaperDecorationStorage.FromSettings(settings.Selections);
        // Validate the complete curve snapshot before changing any controls.
        var savedCurves = new Dictionary<string, string>();
        foreach (var channel in new[] { "master", "red", "green", "blue" })
        {
            var key = "finishing-curve-" + channel + "-points";
            var text = settings.Selections?.GetValueOrDefault(key);
            if (!string.IsNullOrWhiteSpace(text))
                savedCurves.Add(key, FinishingCurveStorage.Serialize(FinishingCurveStorage.Parse(text)));
        }
        curveSelections.Clear();
        foreach (var pair in savedCurves) curveSelections.Add(pair.Key, pair.Value);
        paperDecoration = savedPaper; UpdatePaperSummary();
        var currentDefaults = settings.Selections?.GetValueOrDefault("quality-defaults") == "2";
        foreach (var (key, slider) in values)
        {
            var baseline = QualityDefaults.NumberFallback(key, defaults.Numbers[key], currentDefaults);
            var value = settings.Numbers is { } numbers && numbers.TryGetValue(key, out var saved)
                ? saved : baseline;
            slider.Value = double.IsFinite(value) && value >= slider.Minimum && value <= slider.Maximum
                ? value : baseline;
        }
        foreach (var (key, chooser) in choices)
        {
            var baseline = defaults.Selections[key];
            var id = settings.Selections?.GetValueOrDefault(key) ?? baseline;
            var options = (Choice[])chooser.ItemsSource;
            if (key is "camera-diffusion-family" or "print-diffusion-family" && !options.Any(item => item.Id == id))
            {
                options = options.Append(new Choice(id, "Unavailable filter: " + id)).ToArray();
                chooser.ItemsSource = options;
            }
            chooser.SelectedItem = Array.Find(options, item => item.Id == id) ?? Array.Find(options, item => item.Id == baseline);
        }
        var colorId = settings.Selections?.GetValueOrDefault("input-color");
        inputSpace.SelectedItem = InputSpaces.FirstOrDefault(space => space.Id == colorId) ?? InputSpaces[currentDefaults ? 2 : 0];
        filmModel.SelectedItem = savedModel;
        var preparationId = settings.Selections?.GetValueOrDefault("input-preparation");
        inputPreparation.SelectedItem = PreparationModes.FirstOrDefault(item => item.Id == preparationId)
            ?? PreparationModes[currentDefaults ? 0 : 1];
        outputSpace.SelectedItem = OutputSpaces.FirstOrDefault(item => item.Id == settings.Selections?.GetValueOrDefault("output-color")) ?? OutputSpaces[0];
        exportSpace.SelectedItem = ExportSpaces.FirstOrDefault(item => item.Id == settings.Selections?.GetValueOrDefault("export-color")) ?? ExportSpaces[0];
        exportFormat.SelectedItem = ExportFormats.FirstOrDefault(item => item.Id == settings.Selections?.GetValueOrDefault("export-format")) ?? ExportFormats[0];
        foreach (var (key, item) in toggles)
            item.IsChecked = settings.Switches is { } switches && switches.TryGetValue(key, out var saved)
                ? saved : QualityDefaults.SwitchFallback(key, defaults.Switches[key], currentDefaults);
        // Viewer resolution, drag size, draft synthesis and Show source are global (PanelPreferences);
        // values saved in older looks are only used once, to seed the preferences.
        if (!ReferenceEquals(settings, defaults)) MigrateViewerPreferences(settings);
    }

    private static bool viewerPreferencesMigrated;

    private void MigrateViewerPreferences(Settings settings)
    {
        if (viewerPreferencesMigrated || PanelPreferences.Exists || settings.Selections?.ContainsKey("preview-size") != true)
            return;
        viewerPreferencesMigrated = true;
        var dragEdge = settings.Selections.GetValueOrDefault("drag-preview-size");
        var drag = int.TryParse(dragEdge, NumberStyles.None, CultureInfo.InvariantCulture, out var longEdge)
            && DragSizes.Any(size => size.LongEdge == longEdge) ? longEdge
            : settings.Switches?.GetValueOrDefault("quick-drag-preview", true) == false ? 0 : DefaultDragSize.LongEdge;
        var viewer = PreviewSizes.FirstOrDefault(item => item.Id == settings.Selections["preview-size"])?.Id ?? PreviewSizes[0].Id;
        PanelPreferences.Update(_ => new PanelPreferenceData(ViewerSize: viewer, DragSize: drag,
            DraftSynthesis: settings.Switches?.GetValueOrDefault("draft-synthesis") == true));
        ApplyViewerPreferences();
    }

    private void ApplyViewerPreferences()
    {
        var preferences = PanelPreferences.Current;
        var wasLoading = loading; loading = true;
        try
        {
            previewSize.SelectedItem = PreviewSizes.FirstOrDefault(item => item.Id == preferences.ViewerSize) ?? PreviewSizes[0];
            dragPreviewSize.SelectedItem = DragSizes.FirstOrDefault(size => size.LongEdge == preferences.DragSize) ?? DefaultDragSize;
            fastSynthesis.IsChecked = preferences.DraftSynthesis;
        }
        finally { loading = wasLoading; }
    }

    private void SaveViewerPreferences()
    {
        if (loading) return;
        PanelPreferences.Update(current => current with
        {
            ViewerSize = ((Choice)previewSize.SelectedItem).Id, DragSize = DragLongEdge, DraftSynthesis = fastSynthesis.IsChecked == true,
        });
    }

    private FrameworkElement Build()
    {
        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = "Input preparation applies to the Viewer and 16-bit export. C1 corrections act before the film simulation.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 1),
        });

        var color = new StackPanel();
        AddPreviewChoice(color, filmModel, "Film model");
        filmModel.SelectionChanged += (_, _) => FilmModelChanged?.Invoke();
        AddPreviewChoice(color, inputPreparation, "Input preparation");
        color.Children.Add(new TextBlock { Text = "Input color space", Margin = new Thickness(0, 4, 0, 1) });
        AutomationProperties.SetName(inputSpace, "Input color space");
        inputSpace.SelectionChanged += (_, _) => OnSettingsChanged();
        color.Children.Add(inputSpace);
        AddPreviewChoice(color, outputSpace, "Renderer transport encoding (technical)");
        outputSpace.ToolTip = "Only changes how pixel values travel from the renderer to the panel. The panel decodes them back before display and export, so the look does not change. Leave it at sRGB.";
        AddPreviewChoice(color, exportSpace, "Export profile / compression target");
        exportSpace.ToolTip = "Sets the exported profile. Viewer pixels remain sRGB before monitor correction; this is not a full wide-gamut soft proof. Output compression keeps sRGB; disable it to retain wider export colors. DaVinci Intermediate and V-Log exports carry no embedded profile.";
        AddPreviewChoice(color, exportFormat, "Export file format");
        AddSlider(color, "export-jpeg-quality", "JPEG quality", 60, 100, 92, 1);
        color.Children.Add(new TextBlock { Text = "TIFF and PNG retain 16-bit precision. JPEG is lossy 8-bit. Linear DNG uses Rec.2020 regardless of the selected profile.", TextWrapping = TextWrapping.Wrap });
        body.Children.Add(Section("Color management", color));

        var halation = new StackPanel();
        halation.Children.Add(halationHint);
        AddSlider(halation, "halation-amount", "Halation amount", 0, 4, 0, 0.05);
        AddSlider(halation, "halation-scale", "Halation spread", 0, 4, 1, 0.05);
        AddSlider(halation, "scatter-amount", "Emulsion scatter", 0, 2, 0, 0.05);
        AddSlider(halation, "scatter-scale", "Emulsion scatter spread", 0, 4, 1, 0.05);
        AddSlider(halation, "halation-boost", "Highlight boost (EV)", 0, 20, 0, 0.1).ToolTip =
            "Capture One delivers highlights clipped at white, so halation has little light to spread. Boost re-expands the brightest highlights before halation and scatter.";
        AddSlider(halation, "halation-boost-range", "Highlight boost range", 0, 1, 0.3, 0.01);
        AddSlider(halation, "halation-protect", "Highlight protection (EV)", 0, 10, 2, 0.1);
        AddToggle(halation, "halation-protect-relative", "Measure protection down from the brightest highlight", true).ToolTip =
            "On: Highlight boost acts on the top part of the image's brightness range; protection is how many EV below the brightest highlight it starts. "
            + "Off: the original Spektrafilm rule, protection is EV above mid grey. Capture One images end at sensor clipping, usually only 2–3 EV above mid grey, "
            + "so with the original rule and 4 EV protection the boost changes nothing. Native engine only; saved looks keep the original rule.";
        halationChannels = new StackPanel();
        AddToggle(halation, "custom-halation-rgb", "Override stock halation channels");
        AddSlider(halationChannels, "halation-red", "Red strength", 0, 1, 0.05, 0.005);
        AddSlider(halationChannels, "halation-green", "Green strength", 0, 1, 0.015, 0.005);
        AddSlider(halationChannels, "halation-blue", "Blue strength", 0, 1, 0, 0.005);
        halationChannels.Children.Add(halationStockHint);
        halation.Children.Add(halationChannels);
        body.Children.Add(Section("Halation and scatter", halation));
        body.Children.Add(MakeDiffusion("camera", "Camera diffusion"));
        body.Children.Add(MakeDiffusion("print", "Print diffusion"));

        var grain = new StackPanel();
        grain.Children.Add(grainHint);
        AddChoice(grain, "grain-model", "Grain model", new[]
        {
            new Choice("off", "Off"), new Choice("preview", "Preview"), new Choice("production", "Production"),
            new Choice("synthesis", "Grain synthesis (experimental)"),
            new Choice("rms", "RMS calibrated"),
        });
        grain.Children.Add(synthesisHint);
        AddToggle(grain, "grain-mean-restore", "Keep average colour under grain", true).ToolTip =
            "Grain drawn per pixel shifts the average density and, through the print, the average colour; more so at higher resolutions (about 3–8% darker and cooler at 24 MP). "
            + "On: the local average is restored to the grain-free image, so preview and export match and the colour no longer depends on resolution. Grain texture is kept; only its coarsest variation is slightly reduced. "
            + "Off: the original Spektrafilm behaviour. Native engine only; saved looks keep their previous setting.";
        var rms = new StackPanel();
        AddToggle(rms, "grain-rms-stock", "Use stock RMS granularity", true);
        AddSlider(rms, "grain-rms-strength", "RMS strength", .001, 4, 1, .01);
        var rmsChannels = new StackPanel(); rms.Children.Add(rmsChannels);
        AddSlider(rmsChannels, "grain-rms-r", "Red RMS × 1000", .1, 100, 7, .1);
        AddSlider(rmsChannels, "grain-rms-g", "Green RMS × 1000", .1, 100, 7, .1);
        AddSlider(rmsChannels, "grain-rms-b", "Blue RMS × 1000", .1, 100, 12, .1);
        void UpdateRms()
        {
            rms.IsEnabled = choices["grain-model"].SelectedItem is Choice { Id: "rms" };
            rmsChannels.IsEnabled = toggles["grain-rms-stock"].IsChecked != true;
        }
        capabilityUpdates.Add(UpdateRms); UpdateRms();
        grain.Children.Add(Section("RMS granularity", rms));
        AddSlider(grain, "grain-amount", "Grain amount", 0, 2, 1, 0.05);
        AddSlider(grain, "grain-saturation", "Grain saturation", 0, 1, 1, 0.05);
        AddSlider(grain, "grain-final-blur", "Final blur (µm)", 0, 25, 7.17, 0.01);
        AddChoice(grain, "film-format", "Film format", new[]
        {
            new Choice("8", "8 mm"), new Choice("super8", "Super 8"), new Choice("16", "16 mm"),
            new Choice("super16", "Super 16"), new Choice("35", "35 mm"), new Choice("super35", "Super 35"),
            new Choice("65", "65 mm"), new Choice("70", "IMAX 70"),
        }, 4);
        choices["film-format"].ToolTip = "Physical frame size. Besides grain it scales halation, scatter, diffusion filters, DIR coupler spread and scanner sharpness, so it changes the look even with Grain model Off.";
        AddSlider(grain, "grain-seed", "Seed", 0, 2147483647, 1, 1);
        var particle = new StackPanel();
        particleGrain = particle;
        AddToggle(particle, "grain-sublayers", "Use emulsion sublayers", true).ToolTip =
            "On: three fixed emulsion sublayers. Off: the Sublayers slider sets the count.";
        AddSlider(particle, "grain-sublayer-count", "Sublayers", 1, 8, 1, 1);
        AddSlider(particle, "grain-particle-area", "Particle area (µm²)", 0.01, 5, 0.1, 0.01);
        AddSlider(particle, "grain-dye-cloud-blur", "Dye cloud blur (µm)", 0, 10, 1, 0.1);
        const string microstructureNote = "Microstructure is only rendered when its size reaches a fraction of a pixel. At 35 mm and typical camera resolutions the defaults are below that threshold, so the effect is usually invisible; it appears with smaller film formats or larger values.";
        AddSlider(particle, "grain-microstructure", "Microstructure scale", 0, 4, 0.2, 0.05).ToolTip = microstructureNote;
        AddSlider(particle, "grain-microstructure-sigma", "Microstructure width (nm)", 0, 500, 30, 1).ToolTip = microstructureNote;
        AddChannelSliders(particle, "grain-particle-scale", "Particle scale", 0.01, 20, (1.2, 1, 2.5), 0.01);
        AddChannelSliders(particle, "grain-density-min", "Minimum density", 0, 1, (0.04, 0.05, 0.06), 0.01);
        AddChannelSliders(particle, "grain-uniformity", "Uniformity", 0, 1, (0.99, 0.97, 0.98), 0.01);
        AddLayerSliders(particle, "grain-layer-scale", (6, 1, 0.4));
        grain.Children.Add(Section("Particle grain", particle));
        var synthesis = new StackPanel();
        synthesisGrain = synthesis;
        fastSynthesis.Checked += (_, _) => { SaveViewerPreferences(); OnSettingsChanged(); };
        fastSynthesis.Unchecked += (_, _) => { SaveViewerPreferences(); OnSettingsChanged(); };
        synthesis.Children.Add(fastSynthesis);
        AddToggle(synthesis, "grain-synthesis-layered", "Layered grain", true);
        AddToggle(synthesis, "grain-synthesis-aperture", "Match grain to output size", true).ToolTip =
            "On: each pixel averages the grain over its own area on the film, so a smaller preview or a downsized export keeps the average colour and grain of a downscaled full-size render. "
            + "Full size from 24 MP and larger 35 mm files is unchanged. Off: the original behaviour, where smaller renders get darker, colour-shifted and much grainier. "
            + "Native engine only; saved looks keep their previous setting.";
        AddToggle(synthesis, "grain-synthesis-noise-match", "Remove sampling noise", true).ToolTip =
            "On: when “Match grain to output size” enlarges the pixels (smaller previews, downsized exports, Draft synthesis preview), each pixel gets the grain strength of a render with unlimited samples, at the same render time. "
            + "Full size from 24 MP and larger 35 mm files is unchanged. Off: the original behaviour, where fewer samples give stronger, noisier grain. "
            + "Saved looks keep their previous setting.";
        AddToggle(synthesis, "grain-synthesis-transmission-blur", "Blur grain as light", true).ToolTip =
            "On: the final grain blur averages the light passing through the film, as a lens or scanner does, instead of averaging density. "
            + "Colours under grain stay correct even without “Keep average colour under grain”, and the grain keeps the strength of the grain model; with that option on, grain is about a third weaker than with this one off. "
            + "With “Match grain to output size” and “Remove sampling noise”, smaller previews and downsized exports get the grain of a downscaled full-size render. "
            + "Off: the original density blur. Saved looks keep their previous setting.";
        AddSlider(synthesis, "grain-synthesis-size", "Size", 0.25, 4, 1, 0.05);
        AddSlider(synthesis, "grain-synthesis-amount", "Amount", 0, 3, 1, 0.05);
        AddSlider(synthesis, "grain-synthesis-sharpness", "Sharpness", 0.25, 4, 1, 0.05);
        AddSlider(synthesis, "grain-synthesis-quality", "Quality", 0.25, 4, 1, 0.05);
        AddSlider(synthesis, "grain-synthesis-samples", "Samples", 1, 2048, 128, 1).ToolTip =
            "The renderer limits Samples × Quality to 1024. Settings whose work per pixel would outlast the GPU watchdog (many samples with a wide radius variation, high quantile or large radius scales) are refused with a message instead of resetting the graphics driver.";
        AddSlider(synthesis, "grain-synthesis-radius", "Mean radius (µm)", 0.05, 10, 0.25, 0.01);
        AddSlider(synthesis, "grain-synthesis-radius-sd", "Radius variation", 0, 1, 0, 0.01);
        AddSlider(synthesis, "grain-synthesis-observation", "Observation width (µm)", 0, 20, 1, 0.1);
        AddSlider(synthesis, "grain-synthesis-cell", "Cell size ratio", 0.25, 2, 1, 0.05);
        AddSlider(synthesis, "grain-synthesis-radius-quantile", "Radius quantile", 0.95, 0.9999, 0.999, 0.0001);
        AddSlider(synthesis, "grain-synthesis-epsilon", "Coverage tolerance", 0.000001, 0.01, 0.0001, 0.000001);
        AddSlider(synthesis, "grain-synthesis-cell-cap", "Max grains per cell", 1, 128, 32, 1);
        AddChannelSliders(synthesis, "grain-synthesis-radius-scale", "Radius scale", 0.01, 20, (1.2, 1, 2.5), 0.01);
        AddLayerSliders(synthesis, "grain-synthesis-layer-scale", (6, 1, 0.4));
        grain.Children.Add(Section("Grain synthesis", synthesis));
        body.Children.Add(Section("Spektrafilm grain", grain));

        var scanner = new StackPanel();
        AddToggle(scanner, "scanner-enabled", "Enable scanner controls");
        scanner.Children.Add(scannerHint);
        AddToggle(scanner, "scanner-white", "Correct white level");
        AddSlider(scanner, "scanner-white-level", "White point", 0, 1, 0.98, 0.01).ToolTip =
            "sRGB-coded level for the brightest print this film can make on this paper (Python Spektrafilm semantics). Mid-grey is kept by re-timing the print, so mainly the highlights move. In an inverted negative scan it applies to the film's clearest density.";
        AddToggle(scanner, "scanner-black", "Correct black level");
        AddSlider(scanner, "scanner-black-level", "Black point", 0, 1, 0.01, 0.01).ToolTip =
            "sRGB-coded level for the deepest print black this film can make on this paper (Python Spektrafilm semantics; 0.01 ≈ 3/255). Mid-grey is kept by re-timing the print, so mainly the shadows move. In an inverted negative scan it applies to the film's densest area.";
        AddSlider(scanner, "scanner-glare", "Scanner glare", 0, 0.2, 0.03, 0.001);
        AddSlider(scanner, "scanner-glare-roughness", "Glare roughness", 0, 4, 0.7, 0.05);
        AddSlider(scanner, "scanner-glare-blur", "Glare blur (px)", 0, 32, 0.5, 0.1).ToolTip =
            "Measured in output pixels, not on the film, so the same value looks wider on a small preview than on a full-resolution export.";
        AddSlider(scanner, "scanner-mtf", "Scanner MTF50 (lp/mm)", 0, 300, 60, 1);
        AddSlider(scanner, "scanner-unsharp-radius", "Unsharp radius (µm)", 0, 100, 5, 0.5);
        AddSlider(scanner, "scanner-unsharp-amount", "Unsharp amount", 0, 4, 0.7, 0.05);
        body.Children.Add(Section("Scanning", scanner));

        var geometry = new StackPanel();
        var scale = AddSlider(geometry, "enlarger-scale", "Enlarger scale", 1, 32, 1, 0.1);
        scale.ToolTip =
            "1.0 keeps the original framing. Higher values magnify the image inside the frame.";
        AddSlider(geometry, "enlarger-x", "Horizontal offset (%)", -100, 100, 0, 1);
        AddSlider(geometry, "enlarger-y", "Vertical offset (%)", -100, 100, 0, 1);
        geometry.PreviewMouseWheel += ScrollPastFramingControls;
        body.Children.Add(Section("Enlarger positioning", geometry));

        var couplers = new StackPanel();
        couplers.Children.Add(couplerHint);
        AddSlider(couplers, "dir-diffusion", "Diffusion width (µm)", 0, 100, 0, 0.5);
        AddSlider(couplers, "dir-tail-size", "Tail width (µm)", 0, 1000, 200, 1);
        AddSlider(couplers, "dir-tail-weight", "Tail weight", 0, 1, 0.06, 0.01);
        couplerChannels = new StackPanel();
        AddToggle(couplers, "custom-dir-rgb", "Override stock coupler channels").ToolTip =
            "Off: each film uses its own measured coupler values. On: the sliders below replace them for every film; their defaults are generic, not the selected stock's values.";
        AddChannelSliders(couplerChannels, "dir-gamma", "Same-layer gamma", 0, 1, (0.341, 0.319, 0.273), 0.001);
        foreach (var (from, to, baseline) in new[]
        {
            ("r", "g", 0.353), ("r", "b", 0.302), ("g", "r", 0.154),
            ("g", "b", 0.353), ("b", "r", 0.168), ("b", "g", 0.226),
        })
            AddSlider(couplerChannels, $"dir-{from}-to-{to}", $"{from.ToUpperInvariant()} → {to.ToUpperInvariant()} coupling", 0, 1, baseline, 0.001);
        couplers.Children.Add(couplerChannels);
        body.Children.Add(Section("Coupler diffusion", couplers));

        var adaptation = new StackPanel();
        AddToggle(adaptation, "color-adaptation", "Color adaptation", true);
        AddToggle(adaptation, "color-input-compression", "Input spectral gamut compression", true);
        AddToggle(adaptation, "color-curve-smoothing", "Density curve smoothing", true);
        AddToggle(adaptation, "color-output-lightness-compression", "Output lightness compression", true);
        AddToggle(adaptation, "color-output-chroma-compression", "Output chroma compression", true);
        body.Children.Add(Section("Color adaptation", adaptation));

        var sections = new StackPanel();
        sections.Children.Add(Section("Spektrafilm optics", body));
        sections.Children.Add(BuildBasesAndAging());
        sections.Children.Add(BuildFinishing());
        sections.Children.Add(BuildPaper());
        return sections;
    }

    private FinishingSettings CaptureFinishingSettings()
    {
        var numbers = values.Where(p => p.Key.StartsWith("finishing", StringComparison.Ordinal))
            .ToDictionary(p => p.Key, p => p.Value.Value);
        numbers["finishingActive"] = toggles["finishing-enabled"].IsChecked == true ? 1 : 0;
        if (finishingSnapshot is not null && finishingSnapshotValues is { } previous
            && numbers.Count == previous.Count && numbers.All(pair => previous.TryGetValue(pair.Key, out var value) && pair.Value == value)
            && finishingSnapshotCurves is { } previousCurves && curveSelections.Count == previousCurves.Count
            && curveSelections.All(pair => previousCurves.GetValueOrDefault(pair.Key) == pair.Value))
            return finishingSnapshot;
        finishingSnapshotValues = numbers;
        finishingSnapshotCurves = new(curveSelections);
        var settings = FinishingSettings.FromDictionary(numbers);
        System.Collections.Immutable.ImmutableArray<FinishingPoint> Curve(string channel,
            System.Collections.Immutable.ImmutableArray<FinishingPoint> fallback) =>
            curveSelections.TryGetValue("finishing-curve-" + channel + "-points", out var text)
                ? FinishingCurveStorage.Parse(text) : fallback;
        return finishingSnapshot = settings with {
            Curve = Curve("master", settings.Curve), CurveRed = Curve("red", settings.CurveRed),
            CurveGreen = Curve("green", settings.CurveGreen), CurveBlue = Curve("blue", settings.CurveBlue),
        };
    }

    private FrameworkElement BuildBasesAndAging()
    {
        var root = new StackPanel();
        foreach (var stage in new[] { "film", "print" })
        {
            var section = new StackPanel();
            AddToggle(section, stage + "-base-enabled", "Enable base tuning", true);
            var controls = new StackPanel(); section.Children.Add(controls);
            AddSlider(controls, stage + "-base-scale", "Base density scale", 0, 5, 1, .05);
            if (stage == "film") AddSlider(controls, "film-base-tilt", "Spectral tilt", -2, 2, 0, .01);
            foreach (var color in new[] { "cyan", "magenta", "yellow" })
                AddSlider(controls, stage + "-base-" + color, color + " base", 0, 5, 1, .05);
            void Update()
            {
                section.IsEnabled = stage == "film" || printMode;
                controls.IsEnabled = toggles[stage + "-base-enabled"].IsChecked == true;
            }
            capabilityUpdates.Add(Update); Update();
            root.Children.Add(Section(stage == "film" ? "Film base" : "Print base", section));
        }
        var aging = new StackPanel();
        AddToggle(aging, "film-aging-enabled", "Enable emulsion aging", false);
        aging.Children.Add(new TextBlock { Text = "Creative aging model. Age and storage are empirical estimates, not measurements of a particular roll.", TextWrapping = TextWrapping.Wrap });
        var settings = new StackPanel(); aging.Children.Add(settings);
        AddSlider(settings, "film-age-years", "Age (years)", 0, 100, 0, 1);
        AddChoice(settings, "film-age-storage", "Storage", new[] {new Choice("room", "Room temperature"), new Choice("cold", "Refrigerated"), new Choice("frozen", "Frozen"), new Choice("hot_humid", "Hot and humid")});
        AddSlider(settings, "film-age-gamma", "Gamma scale", .05, 2, 1, .01);
        AddSlider(settings, "film-age-density", "Dye density scale", .05, 2, 1, .01);
        AddSlider(settings, "film-age-grain", "Grain gain", .05, 8, 1, .05);
        AddSlider(settings, "film-age-speed-ev", "Speed loss (EV)", -3, 6, 0, .05);
        foreach (var color in new[] { "r", "g", "b" })
        {
            AddSlider(settings, "film-age-fog-" + color, color.ToUpperInvariant() + " fog density", 0, 1, 0, .01);
            AddSlider(settings, "film-age-gamma-" + color, color.ToUpperInvariant() + " gamma scale", .05, 2, 1, .01);
        }
        void UpdateAging() => settings.IsEnabled = toggles["film-aging-enabled"].IsChecked == true;
        capabilityUpdates.Add(UpdateAging); UpdateAging();
        root.Children.Add(Section("Emulsion aging", aging));
        return root;
    }

    private FrameworkElement BuildFinishing()
    {
        var body = new StackPanel();
        AddToggle(body, "finishing-enabled", "Enable post-scan finishing", true);
        body.Children.Add(new TextBlock { Text = "After film/print simulation, before Look strength. Shared by Viewer and 16-bit export. Color-only LUTs include color and tone; spatial micro contrast is excluded.", TextWrapping = TextWrapping.Wrap });
        var controls = new StackPanel();
        body.Children.Add(controls);
        void UpdateEnabled() => controls.IsEnabled = toggles["finishing-enabled"].IsChecked == true;
        capabilityUpdates.Add(UpdateEnabled);
        UpdateEnabled();
        void Slider(Panel parent, string key, string label, double min, double max, double neutral, double step = .01) =>
            AddSlider(parent, "finishing" + key, label, min, max, neutral, step);
        Slider(controls, "ExposureEv", "Finishing exposure (EV)", -4, 4, 0, .05);
        Slider(controls, "Contrast", "Contrast", -1, 1, 0);
        Slider(controls, "MicroContrast", "Micro contrast", -1, 1, 0);
        values["finishingMicroContrast"].ToolTip = "Uses a fixed radius in output pixels, so its visible strength depends on resolution. Judge it at 100% or on the export.";
        Slider(controls, "Saturation", "Saturation", 0, 2, 1);
        Slider(controls, "Vibrance", "Vibrance", 0, 2, 1);
        var levels = new StackPanel();
        Slider(levels, "BlackPoint", "Black point", -.25, .5, 0);
        Slider(levels, "WhitePoint", "White point", .5, 1.5, 1);
        Slider(levels, "Gamma", "Gamma", .2, 3, 1);
        Slider(levels, "Toe", "Toe", 0, 1, 0);
        Slider(levels, "Shoulder", "Shoulder", 0, 1, 0);
        foreach (var channel in new[] { "Red", "Green", "Blue" })
        {
            Slider(levels, "Gain" + channel + "Ev", channel + " gain (EV)", -2, 2, 0, .05);
            Slider(levels, "Black" + channel, channel + " black offset", -.25, .25, 0);
            Slider(levels, "White" + channel, channel + " white offset", -.25, .25, 0);
        }
        Slider(levels, "ColorSeparation", "Color separation", 0, 1, 0);
        controls.Children.Add(Section("Levels and channel balance", levels));
        var hues = new StackPanel();
        foreach (var hue in new[] { "Red", "Yellow", "Green", "Cyan", "Blue", "Magenta" })
            Slider(hues, "Hue" + hue, hue + " saturation", 0, 2, 1);
        controls.Children.Add(Section("Hue saturation", hues));
        var toning = new StackPanel();
        foreach (var (range, hue) in new[] { ("Shadow", 210), ("Midtone", 120), ("Highlight", 45) })
        {
            Slider(toning, range + "Hue", range + " hue", 0, 360, hue, 1);
            Slider(toning, range + "Strength", range + " strength", 0, 1, 0);
        }
        controls.Children.Add(Section("Split toning", toning));
        var curves = new StackPanel();
        foreach (var channel in new[] { "", "Red", "Green", "Blue" })
        {
            var section = new StackPanel();
            var points = new StackPanel();
            var key = "finishing-curve-" + (channel.Length == 0 ? "master" : channel.ToLowerInvariant()) + "-points";
            var title = channel.Length == 0 ? "Master curve" : channel + " curve";
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var edit = new Button { Content = "Edit curve…", Margin = new Thickness(0, 2, 5, 4) };
            var reset = new Button { Content = "Use five-point controls", Margin = new Thickness(0, 2, 0, 4) };
            var hint = new TextBlock { TextWrapping = TextWrapping.Wrap };
            buttons.Children.Add(edit); buttons.Children.Add(reset);
            section.Children.Add(buttons); section.Children.Add(hint); section.Children.Add(points);
            void UpdateCurve()
            {
                var custom = curveSelections.ContainsKey(key);
                points.IsEnabled = !custom; reset.IsEnabled = custom;
                hint.Text = custom ? "Custom curve active. Edit its points or restore the saved five-point controls." : "Five-point curve active. Edit curve to place points freely.";
            }
            edit.Click += (_, _) => {
                var settings = CaptureFinishingSettings();
                var selected = channel switch { "Red" => settings.CurveRed, "Green" => settings.CurveGreen,
                    "Blue" => settings.CurveBlue, _ => settings.Curve };
                var result = FinishingCurveEditor.Edit(Window.GetWindow(section), title, selected);
                if (result is { } curve) { curveSelections[key] = FinishingCurveStorage.Serialize(curve); OnSettingsChanged(); }
            };
            reset.Click += (_, _) => { curveSelections.Remove(key); OnSettingsChanged(); };
            capabilityUpdates.Add(UpdateCurve); UpdateCurve();
            var names = new[] { "Black", "Shadows", "Midtones", "Highlights", "White" };
            for (var i = 0; i < names.Length; i++)
                Slider(points, "Curve" + channel + names[i], names[i] + " output", 0, 1, i / 4d);
            curves.Children.Add(Section(title, section));
        }
        controls.Children.Add(Section("Point curves", curves));
        var geometry = new StackPanel();
        geometry.Children.Add(new TextBlock { Text = "After finishing and the Capture One crop. The Viewer fits the result inside the C1 image area; export keeps the resulting pixel dimensions. Color LUTs exclude geometry.", TextWrapping = TextWrapping.Wrap });
        AddToggle(geometry, "finishing-crop", "Enable scan crop", false);
        Slider(geometry, "CropCenterX", "Crop center X", 0, 1, .5);
        Slider(geometry, "CropCenterY", "Crop center Y", 0, 1, .5);
        Slider(geometry, "CropSizeX", "Crop width / long side", .005, 1, 1, .005);
        Slider(geometry, "CropSizeY", "Crop height / long side", .005, 1, 1, .005);
        Slider(geometry, "SkewDeg", "Scan rotation (degrees)", -45, 45, 0, .1);
        Slider(geometry, "Desqueeze", "Desqueeze (+width / −height)", -2, 2, 1);
        // Only |v| >= 1 desqueezes; 0 and ±1 mean none. Values in between would squeeze the
        // image towards a few pixels, so the slider skips them (nearest of −1, 0, 1).
        var desqueeze = values["finishingDesqueeze"];
        desqueeze.ToolTip = "1 (or 0) = no desqueeze. 1.33–2 widens the image (anamorphic lenses), −1.33…−2 stretches its height. Values between −1 and 1 are skipped.";
        desqueeze.ValueChanged += (_, _) =>
        {
            var snapped = SnapDesqueeze(desqueeze.Value);
            if (snapped != desqueeze.Value) desqueeze.Value = snapped;
        };
        void CropEnabled() { foreach (var name in new[]{"CropCenterX","CropCenterY","CropSizeX","CropSizeY"}) values["finishing"+name].IsEnabled = toggles["finishing-crop"].IsChecked == true; }
        capabilityUpdates.Add(CropEnabled); CropEnabled();
        controls.Children.Add(Section("Scan geometry", geometry));
        return Section("Post-scan finishing", body);
    }

    private Expander MakeDiffusion(string stage, string name)
    {
        var section = new StackPanel();
        AddChoice(section, stage + "-diffusion-family", "Filter", DiffusionFamilies);
        var filter = choices[stage + "-diffusion-family"];
        var hint = new TextBlock
        {
            Text = "Select a filter to enable diffusion. Saved strength is retained while Off.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3),
        };
        section.Children.Add(hint);
        var parameters = new StackPanel();
        section.Children.Add(parameters);
        void UpdateEnabled()
        {
            var available = stage != "print" || printMode;
            var active = filter.SelectedItem is Choice { Id: not "off" };
            filter.IsEnabled = available;
            parameters.IsEnabled = available && active;
            hint.Text = available
                ? "Select a filter to enable diffusion. Saved strength is retained while Off."
                : "Print diffusion requires a printing stage. Saved filter settings are retained.";
            hint.Visibility = available && active ? Visibility.Collapsed : Visibility.Visible;
        }
        capabilityUpdates.Add(UpdateEnabled);
        filter.SelectionChanged += (_, _) => UpdateEnabled();
        UpdateEnabled();
        AddSlider(parameters, stage + "-diffusion-strength", "Filter strength", 0, 2, 0.5, 0.125);
        AddSlider(parameters, stage + "-diffusion-scale", "Spatial scale", 0, 4, 1, 0.05);
        AddSlider(parameters, stage + "-diffusion-warmth", "Halo warmth", -1.5, 1.5, 0, 0.05);
        foreach (var (part, label) in new[] { ("core", "Core"), ("halo", "Halo"), ("bloom", "Bloom") })
        {
            AddSlider(parameters, $"{stage}-diffusion-{part}-intensity", label + " intensity", 0, 4, 1, 0.05);
            AddSlider(parameters, $"{stage}-diffusion-{part}-size", label + " size", 0.1, 4, 1, 0.05);
        }
        return Section(name, section);
    }

    private static Expander Section(string name, UIElement content) => new()
    {
        Header = name, Content = content, Margin = new Thickness(0, 5, 0, 0),
    };

    private void AddChannelSliders(Panel panel, string key, string label, double min, double max,
        (double Red, double Green, double Blue) initial, double step)
    {
        AddSlider(panel, key + "-r", label + " · R", min, max, initial.Red, step);
        AddSlider(panel, key + "-g", label + " · G", min, max, initial.Green, step);
        AddSlider(panel, key + "-b", label + " · B", min, max, initial.Blue, step);
    }

    private void AddLayerSliders(Panel panel, string key, (double Layer0, double Layer1, double Layer2) initial)
    {
        AddSlider(panel, key + "-0", "Layer 1 scale", 0.01, 20, initial.Layer0, 0.01);
        AddSlider(panel, key + "-1", "Layer 2 scale", 0.01, 20, initial.Layer1, 0.01);
        AddSlider(panel, key + "-2", "Layer 3 scale", 0.01, 20, initial.Layer2, 0.01);
    }

    private CheckBox AddToggle(Panel panel, string key, string label, bool initial = false)
    {
        var item = new CheckBox { Content = label, IsChecked = initial, Margin = new Thickness(0, 3, 0, 2) };
        item.Checked += (_, _) => OnSettingsChanged();
        item.Unchecked += (_, _) => OnSettingsChanged();
        toggles.Add(key, item);
        panel.Children.Add(item);
        return item;
    }

    private static void ScrollPastFramingControls(object sender, MouseWheelEventArgs args)
    {
        // Pass wheel input to the enclosing scroll area before it reaches a slider.
        args.Handled = true;
        ((UIElement)sender).RaiseEvent(new MouseWheelEventArgs(args.MouseDevice, args.Timestamp, args.Delta)
        {
            RoutedEvent = Mouse.MouseWheelEvent,
        });
    }

    private NativeSlider AddSlider(Panel panel, string key, string title, double lower, double upper, double value, double step)
    {
        var control = new NativeSlider(title, lower, upper, value, step);
        TrackSliderDrag(control);
        control.ValueChanged += (_, _) => OnSettingsChanged();
        values.Add(key, control);
        panel.Children.Add(control);
        return control;
    }

    private void AddChoice(Panel panel, string key, string title, Choice[] items, int selected = 0)
    {
        var chooser = MakeChoice(key, title, items, selected);
        panel.Children.Add(new TextBlock { Text = title, Margin = new Thickness(0, 4, 0, 1) });
        panel.Children.Add(chooser);
    }

    private ComboBox MakeChoice(string key, string title, Choice[] items, int selected = 0)
    {
        var chooser = new ComboBox { DisplayMemberPath = nameof(Choice.Label), ItemsSource = items, SelectedIndex = selected };
        AutomationProperties.SetName(chooser, title);
        chooser.SelectionChanged += (_, _) => OnSettingsChanged();
        choices.Add(key, chooser);
        return chooser;
    }

    private FrameworkElement BuildMetering()
    {
        var row = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var toggle = AddToggle(row, "auto-exposure", "Automatic film exposure", true);
        toggle.ToolTip = "Meters the source to mid grey before exposing the film. Film exposure then acts as an offset, and brightness changes made in Capture One are largely metered away. Turn it off to expose the film directly from the Capture One image.";
        toggle.VerticalAlignment = VerticalAlignment.Center;
        var meter = MakeChoice("auto-exposure-meter", "Metering", new[]
        {
            new Choice("center_weighted", "Center weighted"), new Choice("median", "Median"),
            new Choice("average", "Average"), new Choice("partial", "Partial (center circle)"),
            new Choice("matrix", "Matrix (5 × 5 zones)"), new Choice("multi_zone", "Multi-zone (center rings)"),
            new Choice("highlight_weighted", "Highlight weighted"),
        });
        meter.ToolTip = "How the source is metered to mid grey, as in the Python spektrafilm reference: Center weighted favours the middle of the frame, Median ignores very bright and very dark areas, Average weighs the whole frame evenly, Partial meters a circle in the center, Matrix weighs 5 × 5 zones less towards the corners, Multi-zone weighs a center spot and two rings, and Highlight weighted favours bright areas so they keep detail. With film model 4 the source is reduced and metered exactly as in the reference.";
        var label = new TextBlock { Text = "Metering", Margin = new Thickness(14, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1);
        Grid.SetColumn(meter, 2);
        row.Children.Add(label);
        row.Children.Add(meter);
        // The metering method only matters while the film exposure is metered.
        void UpdateMeter() => label.IsEnabled = meter.IsEnabled = toggle.IsChecked == true;
        toggle.Checked += (_, _) => UpdateMeter();
        toggle.Unchecked += (_, _) => UpdateMeter();
        return row;
    }

    private void AddPreviewChoice(Panel panel, ComboBox chooser, string title)
    {
        panel.Children.Add(new TextBlock { Text = title, Margin = new Thickness(0, 4, 0, 1) });
        AutomationProperties.SetName(chooser, title);
        chooser.SelectionChanged += (_, _) => OnSettingsChanged();
        panel.Children.Add(chooser);
    }

    private void AddResolutionControls(Panel panel)
    {
        var resolutions = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        resolutions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        resolutions.ColumnDefinitions.Add(new ColumnDefinition());
        AddRow(0, "Viewer resolution", previewSize);
        AddRow(1, "Drag preview resolution", dragPreviewSize);
        previewSize.SelectionChanged += (_, _) => { SaveViewerPreferences(); OnSettingsChanged(); };
        previewSize.ToolTip = "Resolution of the final Viewer frame. Auto renders the size the image has on screen (1600–2560 px) and full resolution when you zoom in (at high zoom only the visible part, which is faster); judge grain and sharpness zoomed in or at Native resolution. Save preview PNG saves the frame as shown. "
            + "Viewer settings apply to all photos; they are not part of a look. Export does not use this setting.";
        dragPreviewSize.SelectionChanged += (_, _) =>
        {
            if (DragLongEdge == 0)
                EndSliderDrag();
            ResetDraft(keepRenderer: DragLongEdge > 0);
            SaveViewerPreferences();
            OnSettingsChanged();
        };
        panel.Children.Add(resolutions);

        void AddRow(int row, string title, ComboBox chooser)
        {
            resolutions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            AutomationProperties.SetLabeledBy(chooser, label);
            AutomationProperties.SetName(chooser, title);
            chooser.Margin = new Thickness(0, 2, 0, 2);
            Grid.SetRow(label, row);
            Grid.SetRow(chooser, row);
            Grid.SetColumn(chooser, 1);
            resolutions.Children.Add(label);
            resolutions.Children.Add(chooser);
        }
    }

    private void OnSettingsChanged()
    {
        if (loading)
            return;
        SettingsChanged?.Invoke();
        RequestRefresh();
    }

    internal void RequestRefresh()
    {
        if (loading)
            return;
        UpdateCapabilities();
        if (!enabled)
            return;
        lastInputChange = Environment.TickCount64;
        if (dragging)
            lastDragChange = lastInputChange;
        queued = true;
        frameCurrent = false;
        selectionUnavailable = false;
        savePreview.IsEnabled = false;
        inputRevision++;
        TraceJob("settings.changed", $"revision={inputRevision} dragging={dragging}");
        // Initial feedback is obsolete immediately on a settings change. Unlike
        // interactive drag work, it must not hold the next final render hostage.
        pendingInitial?.Cancel();
        refresh.Start();
    }

    private void UpdateCapabilities()
    {
        if (!controlsReady)
            return;
        var look = getSettings();
        printMode = look is null || look.Mode == "print";
        foreach (var update in capabilityUpdates)
            update();
        synthesisHint.Visibility = choices["grain-model"].SelectedItem is Choice { Id: "synthesis" }
            ? Visibility.Visible : Visibility.Collapsed;
        if (values.TryGetValue("film-age-grain", out var agingGrain))
        {
            agingGrain.IsEnabled = choices["grain-model"].SelectedItem is Choice { Id: "preview" or "production" or "rms" };
            agingGrain.ToolTip = "Aging grain gain changes Preview, Production and RMS particle areas. Synthesis has separate radius controls.";
            ToolTipService.SetShowOnDisabled(agingGrain, true);
        }

        // Noise matching acts only on the enlarged window of aperture integration (SpektraGrain.comp).
        var noiseMatch = toggles["grain-synthesis-noise-match"];
        noiseMatch.IsEnabled = toggles["grain-synthesis-aperture"].IsChecked == true;
        ToolTipService.SetShowOnDisabled(noiseMatch, true);

        var scanner = toggles["scanner-enabled"].IsChecked == true;
        var levels = printMode || look?.ScanInvert == true;
        toggles["scanner-white"].IsEnabled = toggles["scanner-black"].IsEnabled = scanner && levels;
        values["scanner-white-level"].IsEnabled = scanner && levels && toggles["scanner-white"].IsChecked == true;
        values["scanner-black-level"].IsEnabled = scanner && levels && toggles["scanner-black"].IsChecked == true;
        foreach (var key in new[] { "scanner-glare", "scanner-glare-roughness", "scanner-glare-blur" })
            values[key].IsEnabled = scanner && printMode;
        foreach (var key in new[] { "scanner-mtf", "scanner-unsharp-radius", "scanner-unsharp-amount" })
            values[key].IsEnabled = scanner;
        scannerHint.Text = !scanner ? "Enable scanner controls to adjust scanning. Saved values are retained while disabled."
            : printMode ? "White and black points apply when their correction is enabled."
            : levels ? "Inverted Scan supports level correction and sharpness. Scanner glare applies only in Print."
            : "Scan supports scanner sharpness. White/black correction requires Print or an inverted negative scan; glare requires Print.";
        UpdateGrainCapabilities();
        UpdateHalationCapabilities();
        UpdateCouplerCapabilities(look);
        UpdateInputAdvice();
    }

    // DIR couplers are off at amount 0; the long tail is only part of the diffusion blur
    // (SpektraVulkanRenderer: dirTailPath requires dirBlurPath).
    private void UpdateCouplerCapabilities(LookSettings? look)
    {
        var amountOn = look?.Development is not { } development || development.GetValueOrDefault("dir_amount", 1) > 0;
        var diffusionOn = amountOn && values["dir-diffusion"].Value > 0;
        values["dir-diffusion"].IsEnabled = amountOn;
        values["dir-tail-size"].IsEnabled = values["dir-tail-weight"].IsEnabled = diffusionOn;
        toggles["custom-dir-rgb"].IsEnabled = amountOn;
        couplerChannels!.IsEnabled = amountOn && toggles["custom-dir-rgb"].IsChecked == true;
        couplerHint.Text = !amountOn ? "DIR coupler amount is 0, so coupler diffusion and channel overrides have no effect. Saved values are retained."
            : !diffusionOn ? "Diffusion width is 0: couplers act without spatial spread, so Tail width and Tail weight have no effect."
            : "Diffusion adds edge contrast; the tail keeps a weak long-range effect.";
    }

    // Grain controls only affect the renderer for the model that reads them. Disable the rest
    // so that a slider never looks active while it cannot change the image. Values are kept.
    private void UpdateGrainCapabilities()
    {
        var model = (choices["grain-model"].SelectedItem as Choice)?.Id ?? "off";
        var grainOn = model != "off";
        foreach (var key in new[] { "grain-final-blur", "grain-seed" })
            values[key].IsEnabled = grainOn;
        // Synthesis copies its own density (SpektraVulkanRenderer: kGrainOpCopyDensity) and never
        // reads grain amount or saturation.
        foreach (var key in new[] { "grain-amount", "grain-saturation" })
        {
            values[key].IsEnabled = grainOn && model != "synthesis";
            values[key].ToolTip = model == "synthesis" ? "Not used by Grain synthesis; use Synthesis amount. The saved value is retained." : null;
            ToolTipService.SetShowOnDisabled(values[key], true);
        }
        toggles["grain-mean-restore"].IsEnabled = grainOn;
        particleGrain!.IsEnabled = grainOn && model is "preview" or "production" or "rms";
        synthesisGrain!.IsEnabled = grainOn && model == "synthesis";
        var sublayers = toggles["grain-sublayers"].IsChecked == true;
        values["grain-sublayer-count"].IsEnabled = !sublayers;
        // RMS replaces particle area and per-channel scales with calibrated areas; dye clouds and
        // microstructure are only rendered with emulsion sublayers (SpektraGrain.comp).
        foreach (var key in new[] { "grain-particle-area", "grain-particle-scale-r", "grain-particle-scale-g", "grain-particle-scale-b" })
        {
            values[key].IsEnabled = model != "rms";
            values[key].ToolTip = model == "rms" ? "RMS granularity sets the particle areas from its calibration. The saved value is retained." : null;
            ToolTipService.SetShowOnDisabled(values[key], true);
        }
        foreach (var key in new[] { "grain-dye-cloud-blur", "grain-microstructure", "grain-microstructure-sigma" })
            values[key].IsEnabled = sublayers;
        grainHint.Text = model == "off" ? "Grain model is Off: the grain controls below have no effect until you choose a model. Film format still scales optical effects."
            : model == "synthesis" ? "Grain synthesis uses only its own section; Particle grain controls are inactive."
            : "Particle grain controls apply; Grain synthesis controls are inactive.";
    }

    // Halation, scatter and highlight boost each need a non-zero amount before their detail
    // controls are read by the renderer (SpektraVulkanRenderer: halationBounce/Scatter/BoostEnabled).
    private void UpdateHalationCapabilities()
    {
        bool Active(string target) => values[target].Value > 0;
        var halationOn = Active("halation-amount");
        var scatterOn = Active("scatter-amount");
        var boostOn = (halationOn || scatterOn) && Active("halation-boost");
        values["halation-scale"].IsEnabled = halationOn;
        values["scatter-scale"].IsEnabled = scatterOn;
        values["halation-boost"].IsEnabled = halationOn || scatterOn;
        values["halation-boost-range"].IsEnabled = values["halation-protect"].IsEnabled = boostOn;
        var relativeProtect = toggles["halation-protect-relative"].IsChecked == true;
        toggles["halation-protect-relative"].IsEnabled = boostOn;
        values["halation-protect"].ToolTip = relativeProtect
            ? "EV below the brightest highlight where Highlight boost starts. 1–2 EV boosts only the brightest areas."
            : "EV above mid grey where Highlight boost starts (original Spektrafilm rule).";
        var deadBoost = boostOn && !relativeProtect && values["halation-protect"].Value >= 3;
        var custom = toggles["custom-halation-rgb"];
        custom.IsEnabled = halationOn;
        halationChannels!.IsEnabled = halationOn && custom.IsChecked == true;
        halationStockHint.Visibility = custom.IsChecked == true
            && Math.Abs(values["halation-red"].Value - 0.05) <= 1e-6
            && Math.Abs(values["halation-green"].Value - 0.015) <= 1e-6
            && Math.Abs(values["halation-blue"].Value) <= 1e-6 ? Visibility.Visible : Visibility.Collapsed;
        halationHint.Text = !(halationOn || scatterOn) ? "Halation and scatter are off (amount 0). Raise an amount to enable the related controls."
            : deadBoost ? "Highlight protection is measured from mid grey and set high: Capture One highlights usually end 2–3 EV above mid grey, so the boost probably changes nothing. Lower protection or measure it from the brightest highlight."
            : "Halation needs bright highlights. Capture One clips at sensor white, so use Highlight boost if the glow is too weak.";
    }

    private void BuildInputAdvice()
    {
        var apply = new Button { Content = "Use recommended input", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 3, 0, 0) };
        apply.ToolTip = "Sets Input preparation to Neutral for film and Input color space to ProPhoto RGB for this look. The current look is saved as a version first, so you can restore it.";
        apply.Click += (_, _) => {
            try { RecommendedInputRequested?.Invoke(); }
            catch (Exception error) { status.Text = error.GetBaseException().Message; StartupHook.Log(error.ToString()); }
        };
        inputAdvice.Children.Add(inputAdviceText);
        inputAdvice.Children.Add(apply);
    }

    // Spektrafilm expects scene-linear input without a tone curve (upstream README: "linear
    // scene-referred files"; darktable manual: only one tone mapper per image).
    private void UpdateInputAdvice()
    {
        var curve = Preparation == SourcePreparation.CaptureOne;
        var narrow = inputSpace.SelectedItem is InputChoice { Space: PreviewInputSpace.Srgb };
        inputAdvice.Visibility = curve || narrow ? Visibility.Visible : Visibility.Collapsed;
        inputAdviceText.Text = curve
            ? "This look uses Capture One development as input, including its tone curve. Spektrafilm applies its own film and print curves, so contrast is doubled and film stocks look more alike."
            : "This look uses sRGB input, which clips saturated colours before the film simulation. ProPhoto RGB keeps them.";
    }

    internal void ApplyRecommendedInput()
    {
        inputPreparation.SelectedItem = PreparationModes[0];
        inputSpace.SelectedItem = InputSpaces.First(x => x.Space == PreviewInputSpace.ProPhotoRgb);
    }

    internal void TrackSliderDrag(NativeSlider slider)
    {
        slider.DragStarted += (_, _) => BeginSliderDrag();
        slider.DragCompleted += (_, _) => EndSliderDrag();
        slider.LostMouseCapture += (_, _) => EndSliderDrag();
        slider.Unloaded += (_, _) => EndSliderDrag();
    }

    internal void BeginSliderDrag()
    {
        dragging = true;
        lastDragChange = 0;
    }

    internal void EndSliderDrag()
    {
        if (!dragging)
            return;
        dragging = false;
        if (lastDragChange != 0)
            RequestRefresh();
    }

    // Keep feedback independent of the expensive final render. Small final frames
    // are already fast; only drags or large sources need an intermediate frame.
    private bool WantsQuickPreview => DragLongEdge > 0 && lastInputChange != 0 && !CompareSource && !frameCurrent
        && (dragging || source is { } frame && FinalPixels(frame) > ProgressivePreviewPixels);

    // How long an adjustment made in Capture One has to rest before its full develop starts.
    private long AdjustmentSettle() => Math.Clamp(2 * adjustmentIntervalMs, AdjustmentSettleMs, AdjustmentSettleMaxMs);

    private bool AutoViewerSize => ((Choice)previewSize.SelectedItem).Id == AutoSize;

    // Auto: long edge of the final frame for the Viewer's current zoom; 0 when the full-size frame is needed
    // (zoomed in beyond 2560 px across the image, or the source is not much larger than the screen size).
    // Before the first paint the zoom is unknown and the largest step is used.
    private int AutoLongEdge(HostImageSource.Frame full)
    {
        var screen = OnScreenLongEdge(full);
        var edge = screen <= 0 ? SettleLongEdges[^1] : SettleLongEdges.FirstOrDefault(step => step >= screen * 0.95);
        return edge > 0 && (long)edge * 4 <= (long)Math.Max(full.Width, full.Height) * 3 ? edge : 0;
    }

    // Long edge of the whole image on screen, in physical pixels; 0 when unknown. Measured by the Viewer once it
    // has painted this image. Before that (right after another image was selected) it is predicted for Fit from
    // the Viewer's area and the image's crop, because Capture One fits the crop; without a prediction the size
    // of the previous image is used, as before, and a too small frame is re-rendered after the first paint.
    private double OnScreenLongEdge(HostImageSource.Frame image)
    {
        if (viewer.PaintedVariant == activeVariant || !viewerAtFit || viewer.ViewportWidth <= 0 || viewer.ViewportHeight <= 0
            || image.Width <= 0 || image.Height <= 0)
            return viewer.ScreenLongEdge;
        var crop = image.PreviewCrop is { Width: > 0, Height: > 0 } visible ? visible
            : new System.Drawing.Rectangle(0, 0, image.Width, image.Height);
        var fittedCropWidth = Math.Min(viewer.ViewportWidth, viewer.ViewportHeight * crop.Width / crop.Height);
        var predicted = Math.Max(image.Width, image.Height) * fittedCropWidth / crop.Width;
        return double.IsFinite(predicted) && predicted > 0 ? predicted : viewer.ScreenLongEdge;
    }

    // Remembers whether the image on screen is shown whole; called while it is still the active image.
    private void UpdateViewerFit()
    {
        if (viewerLayout is not null)
        {
            // Scan geometry or a paper border changes the canvas; nothing is predicted from such a frame.
            viewerAtFit = false;
            return;
        }
        if (source is not { Width: > 0, Height: > 0 } shown || viewer.PaintedVariant != activeVariant || viewer.ScreenLongEdge <= 0
            || viewer.ViewportWidth <= 0)
            return;
        var crop = shown.PreviewCrop is { Width: > 0, Height: > 0 } visible ? visible
            : new System.Drawing.Rectangle(0, 0, shown.Width, shown.Height);
        var scale = viewer.ScreenLongEdge / Math.Max(shown.Width, shown.Height);
        double width = crop.Width * scale, height = crop.Height * scale;
        const double tolerance = 2;
        viewerAtFit = width <= viewer.ViewportWidth + tolerance && height <= viewer.ViewportHeight + tolerance
            && (Math.Abs(width - viewer.ViewportWidth) <= tolerance || Math.Abs(height - viewer.ViewportHeight) <= tolerance);
    }

    private long FinalPixels(HostImageSource.Frame full)
    {
        if (!AutoViewerSize || AutoLongEdge(full) is not (> 0 and var edge))
            return (long)full.Width * full.Height;
        var scale = (double)edge / Math.Max(full.Width, full.Height);
        return (long)(full.Width * scale * (full.Height * scale));
    }

    // Auto: the frame on screen is current but too small for the zoom the Viewer has now.
    private bool AutoFrameTooSmall(HostImageSource.Frame full)
    {
        if (renderedInput is not { } shown || ReferenceEquals(shown, full))
            return false;
        var wanted = AutoLongEdge(full);
        return wanted == 0 || wanted > Math.Max(shown.Width, shown.Height);
    }

    // Size of the quick frame. During a drag: the drag size, for speed. After a change or on release: the
    // smallest step that fills the Viewer's own pixels, so the image is sharp at the current zoom long before
    // the full-size frame arrives. Falls back to the drag size when that is enough or the source is not much larger.
    private int QuickLongEdge(HostImageSource.Frame live, int dragEdge)
    {
        // In Auto a drag frame larger than the final frame would only be slower.
        if (dragging)
            return AutoViewerSize && AutoLongEdge(live) is > 0 and var final ? Math.Min(dragEdge, final) : dragEdge;
        var screen = OnScreenLongEdge(live);
        if (screen <= dragEdge * 1.05)
            return dragEdge;
        var edge = SettleLongEdges.FirstOrDefault(step => step >= screen * 0.95, SettleLongEdges[^1]);
        return edge > dragEdge && (long)edge * 4 <= (long)Math.Max(live.Width, live.Height) * 3 ? edge : dragEdge;
    }

    private bool DefersFinalPreview => WantsQuickPreview
        && (dragging || Environment.TickCount64 - lastInputChange < DragSettleDelay);

    internal void SetEnabled(bool value)
    {
        if (disposed) return;
        enabled = value;
        if (enabled)
        {
            RequestRefresh();
        }
        else
        {
            // An accepted export owns its own snapshot of the photo and settings. Selecting
            // another photo or one with Spektrafilm off must not cancel it (upstream FIX-03);
            // the export dialog's Cancel and Dispose still do.
            ClearPreview();
        }
    }

    internal void ShowExposureComparison(Action<double> apply)
    {
        if (comparisonWindow is { IsVisible: true }) { comparisonWindow.Activate(); return; }
        if (!enabled || !FrameShowsCurrentPhoto() || source is not { } captured || sourceSpace is not { } input || getSettings() is not { } look)
            throw new InvalidOperationException("Wait for the full Viewer frame before comparing exposures.");
        var id = activeVariant; var revision = inputRevision; var adjustment = sourceRevision;
        var encoding = ((Choice)outputSpace.SelectedItem).Id;
        var args = RendererOptions(look, InputSpaces.First(x => x.Space == input).Id, encoding, export: true);
        var finishing = new FinishingTransform(CaptureFinishingSettings());
        var geometry = CaptureGeometrySettings();
        var paper = paperDecoration;
        bool Current()
        {
            if (disposed || !enabled || activeVariant != id || inputRevision != revision || sourceRevision != adjustment) return false;
            try { var selected = HostImageSource.GetSelectedRevision(window); return selected.VariantId == id && selected.AdjustmentKey == adjustment; }
            catch { return false; }
        }
        var title = $"Spektrafilm · {captured.Width} × {captured.Height} Viewer resolution";
        comparisonWindow = ExposureComparison.Show(Window.GetWindow(LiveControls), title, look.Exposure, -8, 8,
            async (ev, token) => await Task.Run(async () => {
                var options = (string[])args.Clone();
                OverrideOption(options, "--film-exposure", ev.ToString("0.######", CultureInfo.InvariantCulture));
                using var worker = new OpticalRenderer(true, false);
                var pixels = look.Strength > 0 ? await worker.RenderAsync(captured.Width, captured.Height, captured.FloatBytes, options, token).ConfigureAwait(false) : null;
                return DecodeDecoratedPreview(captured, pixels, look.Strength, input, ResultEncoding(encoding), token, finishing, geometry, paper);
            }, token),
            ev => {
                if (!Current()) return false;
                apply(ev);
                return true;
            }, Current);
        comparisonWindow.Closed += (_, _) => comparisonWindow = null;
    }

    internal async Task<bool> ExportAsync(bool useRecipeSize, Window? owner = null)
    {
        if (!enabled || exportWork is not null)
            return false;
        try
        {
            var look = getSettings() ?? throw new InvalidOperationException("Wait for the Spektrafilm controls to finish loading.");
            var target = HostProfile.CaptureTarget(window);
            var settingsRevision = inputRevision;
            var sourceRevisionAtDialog = HostImageSource.GetSelectedRevision(window);
            var input = (InputChoice)inputSpace.SelectedItem;
            var finishing = new FinishingTransform(CaptureFinishingSettings());
            var geometry = CaptureGeometrySettings();
            var paper = paperDecoration;
            var snapshot = HostImageSource.Capture(window, target,
                input.Space, captureRecipe: useRecipeSize, preparation: Preparation);
            var recipe = snapshot.Recipe;
            var extension = ExportExtensionFor(recipe?.FormatId);

            var picker = new SaveFileDialog
            {
                Title = "Save Spektrafilm export",
                Filter = ExportFileOptions.Filter,
                FilterIndex = ExportFileOptions.FilterIndex(extension),
                FileName = Path.GetFileNameWithoutExtension(target.SourcePath) + "-Spektrafilm",
                DefaultExt = extension,
                AddExtension = true,
                OverwritePrompt = true,
            };
            if (recipe is { RootFolderMode: "CustomPath" } && Directory.Exists(recipe.CustomRootFolder))
                picker.InitialDirectory = recipe.CustomRootFolder;
            if (recipe is { RootFolderMode: "ImageFolder" })
                picker.InitialDirectory = Path.GetDirectoryName(target.SourcePath)!;
            if ((owner is { IsVisible: true } ? picker.ShowDialog(owner) : picker.ShowDialog()) != true)
                return false;

            if (!enabled || HostProfile.CaptureTarget(window) != target)
                return false;
            if (inputRevision != settingsRevision || HostImageSource.GetSelectedRevision(window) != sourceRevisionAtDialog)
                throw new InvalidOperationException("The photo or Spektrafilm settings changed while the save dialog was open. Start the export again to use one consistent set of settings.");
            var path = ProtectOriginal(picker.FileName, target.SourcePath);
            var exportColor = ExportFileOptions.IsDng(path) ? ExportRgbSpace.LinearRec2020 : SelectedExportSpace;
            var profile = ExportColorSpace.ResolveProfileOrNull(exportColor);
            var encoding = exportColor == ExportRgbSpace.Srgb ? ((Choice)outputSpace.SelectedItem).Id : "linear_rec709";
            var args = look.Strength > 0 ? RendererOptions(look, input.Id, encoding, export: true) : Array.Empty<string>();
            var jpegQuality = (int)Math.Round(values["export-jpeg-quality"].Value);

            using var work = new CancellationTokenSource();
            exportWork = work;
            status.Text = "Rendering Spektrafilm export…";
            try
            {
                // Capture One's settings read before the save dialog can be minutes old (and hold native pointers
                // Capture One may have freed since): read them again once our Viewer development has ended.
                // After upstream 4370ebe and 3c90b88 (Spektrafilm for Capture One, Luka Kauzlaric, GPL-3.0).
                await HostImageSource.WaitForNativeDevelopmentAsync(work.Token);
                if (!enabled || HostProfile.CaptureTarget(window) != target)
                {
                    status.Text = "Export not started: the selected photo changed.";
                    return false;
                }
                snapshot = HostImageSource.Capture(window, target, input.Space, captureRecipe: useRecipeSize, preparation: Preparation);
                if (snapshot.AdjustmentKey.Counters != sourceRevisionAtDialog.AdjustmentKey.Counters)
                    throw new InvalidOperationException("The photo changed while the export was starting. Start the export again to use one consistent set of settings.");
                using var progress = ExportProgressWindow.Show(owner ?? Window.GetWindow(LiveControls), status, work.Cancel);
                var result = await Task.Run(async () =>
                {
                    var rendered = snapshot.RenderExport(null, work.Token, useRecipeSize);
                    var source = rendered.Image;
                    if (source.BitDepth < 16)
                        throw new InvalidDataException("Capture One did not produce high-bit-depth image data for this export.");
                    StartupHook.Log($"Capture One developed {target.SourceFormat} export input at {source.Width} × {source.Height}, {source.BitDepth}-bit {input.Space}.");
                    using var exporter = new OpticalRenderer(true, false);
                    var pixels = look.Strength > 0
                        ? await exporter.RenderAsync(source.Width, source.Height, source.FloatBytes, args, work.Token).ConfigureAwait(false)
                        : null;
                    work.Token.ThrowIfCancellationRequested();
                    // Re-check just before writing: the folder may have changed during a long render.
                    ProtectOriginal(path, target.SourcePath);
                    // Files are written atomically (PanelFiles.WriteAtomically).
                    return WritePaperExportImage(path, source, pixels, look.Strength, input.Space, ResultEncoding(encoding),
                        rendered.Crop, work.Token, finishing, exportColor, profile, jpegQuality, geometry, paper);
                }, work.Token);
                status.Text = $"Saved Spektrafilm export · {result.PixelWidth} × {result.PixelHeight}" + result.Detail;
                StartupHook.Log($"Spektrafilm export saved at {result.PixelWidth}x{result.PixelHeight}: {path}{result.Detail}");
                return true;
            }
            finally
            {
                exportWork = null;
            }
        }
        catch (OperationCanceledException)
        {
            status.Text = "Spektrafilm export canceled.";
            return false;
        }
        catch (Exception error)
        {
            status.Text = error.GetBaseException().Message;
            StartupHook.Log("Spektrafilm export failed: " + error);
            if (owner is { IsVisible: true })
                MessageBox.Show(owner, status.Text, "Could not export with Spektrafilm", MessageBoxButton.OK, MessageBoxImage.Error);
            else
                MessageBox.Show(status.Text, "Could not export with Spektrafilm", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private sealed record BatchRender(HostImageSource.Snapshot Source, PreviewInputSpace Input, string Encoding,
        double Strength, string[] Arguments, FinishingTransform Finishing, ExportRgbSpace Color, string? Profile, int JpegQuality, ScanGeometrySettings Geometry, PaperDecorationSettings Paper);
    // Copy-only integration diagnostics; null in normal use and no buffer copy.
    internal Action<Guid, byte[]>? BatchSourceObserved { get; set; }

    internal async Task<BatchExportSupport.Result> ExportBatchAsync(IReadOnlyList<BatchExportSupport.Entry> entries,
        string outputDirectory, string extension, bool useRecipeSize, CancellationToken token = default)
    {
        if (exportWork is not null) throw new InvalidOperationException("Wait for the current export to finish.");
        var plan = BatchExportSupport.CreatePlan(entries, outputDirectory, extension);
        var renders = new Dictionary<Guid, BatchRender>();
        var captures = new Dictionary<Guid, Func<HostImageSource.Snapshot>>();
        LookOptions? catalog = null;
        // Use a detached controls context to normalize each persisted version in
        // exactly the same way as the Viewer. It never attaches to the Viewer,
        // selects a variant, queues a render or writes its settings.
        LookSettings? look = null;
        using (var context = BuildForPanel(window, () => look, _ => false))
        {
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                var saved = entry.Settings; look = saved.Look;
                if (saved.Enabled) { RejectRemovedEngine(saved.Optics); RejectUnknownFilmModel(saved.Optics); }
                context.LoadSettings(saved.Enabled ? saved.Optics : WithoutUnknownFilmModel(saved.Optics));
                catalog ??= System.Text.Json.JsonSerializer.Deserialize<LookOptions>(File.ReadAllText(Path.Combine(
                    Environment.GetEnvironmentVariable("SPEKTRAFILM_ROOT")!, "stocks.json")))!;
                look = saved.Enabled && look.Strength > 0 ? BatchExportSupport.RenderingLook(look, catalog) : look;
                var exportColor = ExportFileOptions.IsDng("output" + extension) ? ExportRgbSpace.LinearRec2020 : context.SelectedExportSpace;
                var profile = ExportColorSpace.ResolveProfileOrNull(exportColor);
                var input = saved.Enabled ? (InputChoice)context.inputSpace.SelectedItem
                    : InputSpaces.First(x => (int)x.Space == ((int)exportColor < 4 ? (int)exportColor : (int)PreviewInputSpace.LinearRec2020));
                var preparation = saved.Enabled ? context.Preparation : SourcePreparation.CaptureOne;
                var source = entry.CaptureSource(window, input.Space, useRecipeSize, preparation);
                var space = input.Space;
                captures.Add(entry.Target.VariantId, () => entry.CaptureSource(window, space, useRecipeSize, preparation));
                var strength = saved.Enabled ? look.Strength : 0;
                var encoding = exportColor == ExportRgbSpace.Srgb ? ((Choice)context.outputSpace.SelectedItem).Id : "linear_rec709";
                var args = strength > 0 ? context.RendererOptions(look, input.Id, encoding, export: true) : Array.Empty<string>();
                renders.Add(entry.Target.VariantId, new(source, input.Space, encoding, strength, args,
                    new FinishingTransform(context.CaptureFinishingSettings()), exportColor, profile, (int)Math.Round(context.values["export-jpeg-quality"].Value),
                    saved.Enabled ? context.CaptureGeometrySettings() : new(Active: false),
                    saved.Enabled ? context.paperDecoration : PaperDecorationSettings.Default));
            }
        }
        using var work = CancellationTokenSource.CreateLinkedTokenSource(token);
        exportWork = work;
        var ui = status.Dispatcher;
        var progress = new Progress<BatchExportSupport.Progress>(p =>
            status.Text = $"Exporting selected variants · {p.Completed}/{p.Total}" +
                (p.Current is null ? "" : " · " + Path.GetFileName(p.Current.Target.SourcePath)));
        try
        {
            var result = await BatchExportSupport.RunAsync(plan, async (entry, temporary, stop) =>
            {
                // Settings read when the batch started can be minutes old by the time this image is reached: read
                // them again just before it develops (after upstream 3c90b88, Luka Kauzlaric, GPL-3.0).
                await HostImageSource.WaitForNativeDevelopmentAsync(stop).ConfigureAwait(false);
                if (ui.HasShutdownStarted) throw new OperationCanceledException("Capture One is closing.");
                var fresh = await ui.InvokeAsync(captures[entry.Target.VariantId], DispatcherPriority.Normal, stop).Task.ConfigureAwait(false)
                    ?? throw new OperationCanceledException("Capture One is closing.");
                var captured = renders[entry.Target.VariantId] with { Source = fresh };
                await Task.Run(async () =>
                {
                    var developed = captured.Source.RenderExport(null, stop, useRecipeSize);
                    var source = developed.Image;
                    BatchSourceObserved?.Invoke(entry.Target.VariantId, source.FloatBytes);
                    if (source.BitDepth < 16) throw new InvalidDataException("Capture One did not produce high-bit-depth export data.");
                    using var worker = new OpticalRenderer(true, false);
                    var pixels = captured.Strength > 0
                        ? await worker.RenderAsync(source.Width, source.Height, source.FloatBytes, captured.Arguments, stop).ConfigureAwait(false) : null;
                    var saved = WritePaperExportImage(temporary, source, pixels, captured.Strength, captured.Input, ResultEncoding(captured.Encoding),
                        developed.Crop, stop, captured.Finishing, captured.Color, captured.Profile, captured.JpegQuality, captured.Geometry, captured.Paper);
                    if (saved.Detail.Length > 0) StartupHook.Log("Batch export " + entry.Target.VariantId + saved.Detail);
                }, stop).ConfigureAwait(false);
            }, work.Token, progress);
            status.Text = result.Cancelled ? $"Batch canceled · {result.Completed.Count} completed files retained."
                : $"Saved {result.Completed.Count} selected variants · {extension.TrimStart('.').ToUpperInvariant()}";
            return result;
        }
        finally { exportWork = null; }
    }

    private static void OverrideOption(string[] args, string key, string value)
    {
        for (var i = 0; i + 1 < args.Length; i += 2) if (args[i] == key) { args[i + 1] = value; return; }
    }

    private static (int PixelWidth, int PixelHeight, string Detail) WriteExportImage(string path, HostImageSource.Frame source,
        byte[]? pixels, double strength, PreviewInputSpace input, string encoding, System.Drawing.Rectangle crop,
        CancellationToken token, FinishingTransform finishing, ExportRgbSpace color, string? profile, int jpegQuality,
        bool intermediate = false)
    {
        if (ExportFileOptions.IsDng(path))
        {
            var rgba = DecodeLinearDng(source, pixels, strength, input, encoding, crop, token, finishing);
            var info = SpektraExport.SaveLinearDng(path, rgba, crop.Width, crop.Height, token);
            return (crop.Width, crop.Height, $" · linear Rec.2020 DNG; headroom {info.HeadroomStops} EV; {info.NegativeSamples} negative and {info.OverRangeSamples} over-range samples clipped");
        }
        var output = DecodeExportColorManaged(source, pixels, strength, input, encoding, ExportFileOptions.WorkingFormat(path), crop, token, finishing, color);
        if (ExportFileOptions.IsJpeg(path)) SpektraExport.SaveJpegWithProfile(path, output, token, profile, jpegQuality);
        else SpektraExport.SaveWithProfile(path, output, token, profile, intermediate);
        return (output.PixelWidth, output.PixelHeight, ExportFileOptions.IsJpeg(path) ? $" · JPEG quality {jpegQuality}" : " · 16-bit");
    }

    internal async Task<string> ExportColorLutAsync(LookSettings look, int resolution, CancellationToken token)
    {
        if (exportWork is not null) throw new InvalidOperationException("Wait for the current export to finish.");
        var args = NativeColorLut.ColorOnlyOptions(RendererOptions(look, "srgb", "srgb", export: true));
        var finishing = new FinishingTransform(CaptureFinishingSettings() with { MicroContrast = 0 });
        using var work = CancellationTokenSource.CreateLinkedTokenSource(token);
        exportWork = work;
        try
        {
            return await Task.Run(async () =>
            {
                var source = NativeColorLut.CreateGrid(resolution);
                using var worker = new OpticalRenderer(true, false);
                var rendered = look.Strength > 0
                    ? await worker.RenderAsync(resolution * resolution, resolution, source, args, work.Token).ConfigureAwait(false)
                    : null;
                return EncodeColorLut(resolution, source, rendered, look.Strength, finishing, work.Token, ResultEncoding("srgb"));
            }, work.Token);
        }
        finally { exportWork = null; }
    }

    private static string EncodeColorLut(int resolution, byte[] source, byte[]? rendered, double strength,
        FinishingTransform finishing, CancellationToken token, string outputEncoding)
    {
        var input = MemoryMarshal.Cast<byte, float>(source.AsSpan());
        var output = MemoryMarshal.Cast<byte, float>(rendered.AsSpan());
        var transform = new SrgbTransform(PreviewInputSpace.Srgb, outputEncoding, strength, finishing);
        var colors = new double[checked(resolution * resolution * resolution * 3)];
        for (var i = 0; i < colors.Length / 3; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            var rgb = transform.GetSrgb(input, output, i * 4);
            colors[i * 3] = rgb.Red; colors[i * 3 + 1] = rgb.Green; colors[i * 3 + 2] = rgb.Blue;
        }
        return NativeColorLut.Encode(resolution, colors);
    }

    private void ClearPreview()
    {
        referenceComparison?.Close(); referenceComparison = null;
        comparisonWindow?.Close(); comparisonWindow = null;
        scopesWindow?.Close();
        scopesWindow = null;
        refresh.Stop();
        queued = false;
        inputRevision++;
        pending?.Cancel();
        dragging = false;
        viewer.Clear();
        activeVariant = null;
        viewerAtFit = false;
        ResetSource();
        viewerFrame = null;
        previewDetail = null;
        frameCurrent = false;
        showSource.IsEnabled = false;
        savePreview.IsEnabled = false;
        if (pending is null)
            ReleaseRenderer();
        status.Text = string.Empty;
        HideRenderError();
    }

    private void ResetSource(bool keepDraftRenderer = false)
    {
        ForgetWindow();
        baseSource = null;
        ResetDraft(keepRenderer: keepDraftRenderer);
        source = null;
        sourceGeneration++;
        awaitingSource = false;
        quickRevision = null;
        quickRevisionAt = 0;
        adjustmentIntervalMs = 0;
        renderedInput = null;
        autoZoomNeededSince = 0;
        sourceRevision = null;
        sourceSpace = null;
        sourcePreparation = null;
        sourceSize = null;
        failedRevision = null;
        failedWhileOffline = false;
        failedSpace = null;
        failedPreparation = null;
        failedSize = null;
        sourceDisplay = null;
        renderedPixels = null;
        renderedArguments = null;
        renderedDisplay = null;
    }

    // Preview job lifecycle for SPEKTRAFILM_PERF_LOG diagnostics (no effect otherwise).
    private static void TraceJob(string stage, string detail) => PreviewPerformance.End(PreviewPerformance.Start(), stage, detail);

    private async Task RefreshAsync()
    {
        if (!enabled)
        {
            ClearPreview();
            return;
        }
        if (exportWork is not null)
            return;
        if ((!queued || selectionUnavailable || pending is not null && !WantsQuickPreview)
            && Environment.TickCount64 < nextViewerCheck)
            return;
        // Reading the current selection/revision is cheap; avoid adding up to
        // 350 ms of idle polling latency before starting a new photo.
        nextViewerCheck = Environment.TickCount64 + SelectionCheckIntervalMs;

        Guid selectedVariant;
        HostImageSource.Revision revision;
        try
        {
            (selectedVariant, revision) = HostImageSource.GetSelectedRevision(window);
        }
        catch (InvalidOperationException error)
        {
            if (!selectionUnavailable) TraceJob("job.selection-unavailable", error.Message);
            pendingDraft?.Cancel();
            selectionUnavailable = true;
            viewer.Suspend(error.Message);
            UpdateViewerStatus();
            return;
        }
        selectionUnavailable = false;
        if (!useSelectedLook(selectedVariant))
            return;
        var newSelection = activeVariant != selectedVariant;
        if (!newSelection)
            UpdateViewerFit();
        if (newSelection)
        {
            PreviewPerformance.End(PreviewPerformance.Start(), "preview.selection", $"variant={selectedVariant} pending={pending is not null}");
            viewer.Clear();
            if (pending is not null)
            {
                pending.Cancel();
                pendingDraft?.Cancel();
                return;
            }
            activeVariant = selectedVariant;
            ResetSource(keepDraftRenderer: true);
            viewerFrame = null;
            frameCurrent = false;
            showSource.IsEnabled = false;
            queued = true;
        }
        var selectedSpace = (InputChoice)inputSpace.SelectedItem;
        var space = selectedSpace.Space;
        var preparation = Preparation;
        var encoding = (Choice)outputSpace.SelectedItem;
        var resultEncoding = ResultEncoding(encoding.Id);
        var size = ((Choice)previewSize.SelectedItem).Id;
        var autoSize = size == AutoSize;
        var nativeSize = size == "native" || autoSize;
        // Auto works on the native source, so switching between Native and Auto needs no new develop.
        var sourceKey = autoSize ? "native" : size;
        var longEdge = nativeSize ? 0 : int.Parse(size, CultureInfo.InvariantCulture);
        var highDepthPreview = nativeSize || longEdge > InitialPreviewLongEdge;
        var dragEdge = DragLongEdge;
        var needsSource = source is null || sourceRevision != revision || sourceSpace != space
            || sourcePreparation != preparation || sourceSize != sourceKey;
        if (highDepthPreview && !needsSource && WantsQuickPreview
            && source is { } liveSource && Math.Max(liveSource.Width, liveSource.Height) > dragEdge)
        {
            if (pendingDraft is null && lastDraftRevision != inputRevision && getSettings() is { } draftLook)
            {
                // Zoomed in: the window itself is the interactive preview, sharp and about as fast as a draft.
                if (nativeSize && draftLook.Strength > 0 && encoding.Id == "srgb" && viewerFrame is not null
                    && WindowDraftReady(liveSource) && CaptureFinishingSettings() is var draftFinishing
                    && new FinishingTransform(draftFinishing).IsIdentity && CaptureGeometrySettings().IsIdentity && paperDecoration.IsIdentity
                    && WantedWindow(liveSource) is { } draftWindow)
                {
                    await RefreshWindowDraftAsync(selectedVariant, revision, liveSource, selectedSpace, encoding, draftLook, size, draftWindow);
                    return;
                }
                await RefreshDraftAsync(selectedVariant, revision, liveSource, selectedSpace, encoding, draftLook, size, dragEdge,
                    QuickLongEdge(liveSource, dragEdge));
                // Re-read selection and controls on the next tick after the await.
                return;
            }
            if (DefersFinalPreview || pendingDraft is not null)
                return;
        }
        if (pending is not null)
            return;
        if (needsSource && (failedRevision != revision || failedSpace != space || failedPreparation != preparation || failedSize != sourceKey
                || failedWhileOffline && activeVariant == selectedVariant && OfflineSourceReturned()))
            queued = true;

        if (autoSize && !queued && frameCurrent && !needsSource && source is { } zoomSource && AutoFrameTooSmall(zoomSource)
            && !WindowCovers(zoomSource))
        {
            // Zoomed in on an Auto frame: render the larger frame once the zoom has rested for a moment.
            var now = Environment.TickCount64;
            if (autoZoomNeededSince == 0)
                autoZoomNeededSince = now;
            else if (now - autoZoomNeededSince >= AutoZoomSettleMs)
            {
                autoZoomNeededSince = 0;
                queued = true;
                TraceJob("job.zoom", $"screen={viewer.ScreenLongEdge:F0}");
            }
        }
        else
            autoZoomNeededSince = 0;
        if (!queued && frameCurrent && !needsSource && source is { } panSource && WindowPanSettled(panSource))
        {
            queued = true;
            TraceJob("job.pan", $"window={windowRect}");
        }
        if (!queued || getSettings() is not { } look)
        {
            if (viewerFrame is not null)
                ShowFrame(selectedVariant, viewerFrame);
            if (highDepthPreview && !needsSource && frameCurrent && !dragging && dragEdge > 0
                && !draftWarmupAttempted && pendingDraft is null
                && source is { } fullSource && Math.Max(fullSource.Width, fullSource.Height) > dragEdge
                && getSettings() is { } warmLook)
                await RefreshDraftAsync(selectedVariant, revision, fullSource, selectedSpace, encoding, warmLook, size, dragEdge, dragEdge, warmup: true);
            return;
        }
        // An adjustment is being made in Capture One and its quick frame is on screen: let it rest first.
        if (needsSource && quickRevision == revision)
        {
            var rested = Environment.TickCount64 - quickRevisionAt;
            if (rested < AdjustmentSettle() || rested < AdjustmentHoldMaxMs && AdjustmentPointerHeld?.Invoke() == true)
                return;
        }
        queued = false;
        failedRevision = null;
        failedWhileOffline = false;
        var settingsVersion = inputRevision;
        var started = Stopwatch.GetTimestamp();
        using var job = new CancellationTokenSource();
        pending = job;
        TraceJob("job.start", $"variant={selectedVariant} needsSource={needsSource} newSelection={newSelection} size={size}");
        try
        {
            status.Text = nativeSize && !(autoSize && source is { } known && AutoLongEdge(known) > 0)
                ? "Rendering at native resolution…" : "Rendering Viewer preview…";
            var comparing = CompareSource;
            var strength = comparing ? 0 : look.Strength;
            var finishingSettings = CaptureFinishingSettings();
            var finishing = new FinishingTransform(finishingSettings);
            var geometry = comparing ? new ScanGeometrySettings(Active: false) : CaptureGeometrySettings();
            var paper = comparing ? PaperDecorationSettings.Default : paperDecoration;
            var args = strength > 0 ? RendererOptions(look, selectedSpace.Id, encoding.Id) : Array.Empty<string>();
            if (needsSource)
            {
                if (renderer is not null && rendererUsesHighDepthPreview != highDepthPreview)
                    ReleaseRenderer();
                var target = HostProfile.CaptureTarget(window);
                var snapshot = HostImageSource.Capture(window, target, space, preparation: preparation);
                if (strength > 0)
                    StartRendererWarmup(args, highDepthPreview, newSelection && highDepthPreview && dragEdge > 0
                        ? RendererOptions(look, selectedSpace.Id, encoding.Id, draft: true) : null);
                // The image content changes (another image, or an adjustment made in Capture One): Capture One's
                // own small preview arrives within tens of milliseconds of its scheduler starting our job, long
                // before the full develop. Show the film look on it at once. While a Capture One slider is being
                // dragged the full develop is abandoned again and again; these frames are then the only feedback.
                var contentChanged = source is null || sourceRevision != revision || sourceSpace != space || sourcePreparation != preparation;
                Action<HostImageSource.Frame>? quick = null;
                quickSourceWork = null;
                if (contentChanged)
                    awaitingSource = true;
                var quickFrames = highDepthPreview && contentChanged && dragEdge > 0 && strength > 0;
                // An adjustment made in Capture One on the image already shown (not another image, input or size).
                var adjusting = quickFrames && !newSelection && source is not null && sourceSpace == space
                    && sourcePreparation == preparation && sourceSize == sourceKey;
                if (adjusting && quickRevision != snapshot.AdjustmentKey)
                {
                    // First only the quick frame, in a short scheduler visit of its own. The full develop follows
                    // in a later job, once the adjustment has rested (see AdjustmentSettleMs); while a Capture One
                    // slider is dragged this repeats and gives several frames a second.
                    var small = await Task.Run(() => snapshot.RenderQuickPreview(job.Token), job.Token);
                    job.Token.ThrowIfCancellationRequested();
                    var now = Environment.TickCount64;
                    adjustmentIntervalMs = quickRevisionAt != 0 && now - quickRevisionAt < AdjustmentBurstMs ? now - quickRevisionAt : 0;
                    quickRevision = snapshot.AdjustmentKey;
                    quickRevisionAt = now;
                    if (small is not null && settingsVersion == inputRevision)
                        await ShowQuickSourceAsync(small, ++quickSequence, sourceGeneration, started, selectedVariant,
                            selectedSpace, encoding, look, dragEdge, settingsVersion, job.Token);
                    TraceJob("job.quick", small is null ? "no Capture One preview" : $"{small.Width}x{small.Height}");
                    queued = true;
                    // Look again at once (after pending input) instead of at the next timer tick: during a drag
                    // in Capture One the next adjustment is usually already there.
                    _ = refresh.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _ = RefreshAsync()));
                    return;
                }
                if (quickFrames && !adjusting)
                {
                    var sequence = ++quickSequence;
                    var generation = sourceGeneration;
                    var quickToken = job.Token; // read now: the Token getter throws once the job is disposed
                    quick = small => refresh.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                    {
                        // One quick frame at a time; a later job brings the next one.
                        if (pendingDraft is null)
                            quickSourceWork = ShowQuickSourceAsync(small, sequence, generation, started, selectedVariant,
                                selectedSpace, encoding, look, dragEdge, settingsVersion, quickToken);
                    }));
                }
                var developed = await Task.Run(() =>
                {
                    var developing = PreviewPerformance.Start();
                    var frame = nativeSize ? snapshot.RenderNativePreview(job.Token, quick)
                        : highDepthPreview ? snapshot.RenderScaledPreview(longEdge, job.Token, quick)
                        : snapshot.Render(longEdge, job.Token);
                    PreviewPerformance.End(developing, "source.full", $"variant={selectedVariant} {frame.Width}x{frame.Height}");
                    return frame;
                }, job.Token);
                job.Token.ThrowIfCancellationRequested();
                if (HostImageSource.GetRevision(window, selectedVariant) != snapshot.AdjustmentKey)
                {
                    TraceJob("job.abort", "source revision changed while developing");
                    queued = true;
                    return;
                }
                ResetDraft(keepRenderer: highDepthPreview);
                source = developed;
                sourceGeneration++;
                quickRevision = null;
                // The run of adjustments is over; the next one starts with the short wait again.
                quickRevisionAt = 0;
                adjustmentIntervalMs = 0;
                sourceRevision = snapshot.AdjustmentKey;
                sourceSpace = space;
                sourcePreparation = preparation;
                sourceSize = sourceKey;
                renderedInput = null; // do not keep the previous full-size source alive
                sourceDisplay = null;
                renderedPixels = null;
                renderedArguments = null;
                renderedDisplay = null;
                StartupHook.Log($"Capture One developed {target.SourceFormat} input at {developed.Width} × {developed.Height}, {developed.BitDepth}-bit {space} in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms.");
                if (highDepthPreview && contentChanged && dragEdge > 0 && strength > 0 && settingsVersion == inputRevision
                    // In Auto the final frame itself is screen-sized and follows at once.
                    && !(autoSize && AutoLongEdge(developed) > 0)
                    && (pendingDraft is null || quickSourceWork is not null && ReferenceEquals(pendingDraft, pendingInitial)))
                {
                    // Derive temporary feedback from the full source. Its worker must
                    // never delay the final frame or replace it after completion.
                    _ = TryShowInitialPreviewAsync(developed, started, snapshot.AdjustmentKey,
                        selectedVariant, selectedSpace, encoding, look, dragEdge, settingsVersion, job.Token, after: quickSourceWork);
                }
            }
            if (settingsVersion != inputRevision)
            {
                TraceJob("job.abort", "settings changed while developing");
                return;
            }

            var input = source!;
            // Zoomed in: render only the visible part (SpatialPreview.ViewerWindow.cs) over the frame on screen.
            var windowEligible = nativeSize && highDepthPreview && strength > 0 && finishing.IsIdentity && encoding.Id == "srgb"
                && geometry.IsIdentity && paper.IsIdentity && WindowModelAllowed();
            var refreshWindowBase = false;
            if (windowEligible && viewerFrame is not null && WantedWindow(input) is { } wantedWindow)
            {
                var windowCurrent = WindowCovers(input) && windowArguments is { } heldArguments && heldArguments.SequenceEqual(args)
                    && windowStrength == strength;
                if (!windowCurrent && await RenderViewerWindowAsync(input, wantedWindow, args, strength, job.Token) is { } windowBitmap)
                {
                    if (settingsVersion != inputRevision)
                    {
                        TraceJob("job.abort", "settings changed while rendering the window");
                        return;
                    }
                    if (HostImageSource.GetRevision(window, selectedVariant) != sourceRevision)
                    {
                        TraceJob("job.abort", "source revision changed while rendering the window");
                        queued = true;
                        return;
                    }
                    PresentViewerWindow(selectedVariant, input, wantedWindow, windowBitmap, args, strength, started);
                    {
                        frameCurrent = true;
                        awaitingSource = false;
                        showSource.IsEnabled = true;
                        TraceJob("job.window", $"{wantedWindow}");
                        // The frame around the window still shows the previous settings: refresh it next.
                        if (!WindowBaseCurrent(args, strength, input))
                            queued = true;
                        return;
                    }
                }
                else if (windowCurrent && WindowBaseCurrent(args, strength, input))
                {
                    frameCurrent = true;
                    awaitingSource = false;
                    return;
                }
                refreshWindowBase = windowCurrent;
            }
            if (!refreshWindowBase)
                ForgetWindow(); // a whole frame follows; no window may stay over it
            var reduceEdge = refreshWindowBase ? WindowBaseLongEdge(input) : autoSize ? AutoLongEdge(input) : 0;
            if (reduceEdge > 0 && reduceEdge is var autoEdge)
            {
                if (settleSource is null || settleEdge != autoEdge)
                {
                    var full = input;
                    var reducing = PreviewPerformance.Start();
                    var reduced = await Task.Run(() => ViewerDraftSource.Downsample(full, autoEdge, job.Token), job.Token);
                    PreviewPerformance.End(reducing, "source.reduce", $"{reduced.Width}x{reduced.Height}");
                    job.Token.ThrowIfCancellationRequested();
                    if (settingsVersion != inputRevision || !ReferenceEquals(source, full))
                    {
                        TraceJob("job.abort", "settings changed while reducing the source");
                        queued = true;
                        return;
                    }
                    settleSource = reduced;
                    settleEdge = autoEdge;
                }
                input = settleSource;
            }
            if (!ReferenceEquals(renderedInput, input))
            {
                // The cached render and display belong to another input (full size against screen size).
                renderedInput = input;
                renderedPixels = null;
                renderedDisplay = null;
                sourceDisplay = null;
            }
            BitmapSource output;
            if (strength <= 0)
            {
                if (!geometry.IsIdentity || !paper.IsIdentity)
                    output = await Task.Run(() => ForViewer(DecodeDecoratedPreview(input, null, 0, space, resultEncoding, job.Token, finishing, geometry, paper), job.Token), job.Token);
                else
                {
                    if (sourceDisplay is null)
                    {
                        var display = await Task.Run(() => ForViewer(DecodeResult(input, null, 0, space, encoding.Id, job.Token), job.Token), job.Token);
                        job.Token.ThrowIfCancellationRequested();
                        sourceDisplay = display;
                    }
                    output = sourceDisplay;
                }
            }
            else
            {
                if (renderedArguments is null || !args.SequenceEqual(renderedArguments))
                {
                    renderedArguments = args;
                    renderedPixels = null;
                    renderedDisplay = null;
                }

                if (renderedDisplay is null || renderedStrength != strength || renderedFinishing != finishingSettings || renderedGeometry != geometry || renderedPaper != paper)
                {
                    var displayPixels = finishing.IsIdentity && highDepthPreview && strength > 0 && encoding.Id == "srgb"
                        && renderedPixels is null && geometry.IsIdentity && paper.IsIdentity;
                    byte[]? packed = null;
                    if (renderedPixels is null)
                    {
                        if (renderer is null || rendererUsesHighDepthPreview != highDepthPreview)
                        {
                            ReleaseRenderer();
                            renderer = new OpticalRenderer(false, highDepthPreview);
                            rendererUsesHighDepthPreview = highDepthPreview;
                        }
                        var active = renderer;
                        if (rendererWarmup is { } warming)
                        {
                            rendererWarmup = null;
                            await warming;
                            job.Token.ThrowIfCancellationRequested();
                            if (!ReferenceEquals(active, renderer))
                            {
                                queued = true; // released while it was starting; render again
                                return;
                            }
                        }
                        var reply = await active.RenderAsync(input.Width, input.Height, input.FloatBytes,
                            displayPixels ? WithDisplayStrength(args, strength) : args, job.Token, displayPixels);
                        job.Token.ThrowIfCancellationRequested();
                        if (displayPixels)
                            packed = reply;
                        else
                            renderedPixels = reply;
                    }
                    if (settingsVersion != inputRevision)
                    {
                        TraceJob("job.abort", "settings changed during the renderer call");
                        return;
                    }

                    var completedPixels = renderedPixels;
                    var display = await Task.Run(() => ForViewer(packed is not null
                        ? CreateDisplayBitmap(input, packed)
                        : DecodeDecoratedPreview(input, completedPixels, strength, space, resultEncoding, job.Token, finishing, geometry, paper), job.Token), job.Token);
                    job.Token.ThrowIfCancellationRequested();
                    renderedDisplay = display;
                    renderedStrength = strength;
                    renderedFinishing = finishingSettings;
                    renderedGeometry = geometry;
                    renderedPaper = paper;
                }
                output = renderedDisplay;
            }
            job.Token.ThrowIfCancellationRequested();
            if (settingsVersion != inputRevision)
            {
                TraceJob("job.abort", "settings changed while rendering");
                return;
            }
            if (HostImageSource.GetRevision(window, selectedVariant) != sourceRevision)
            {
                TraceJob("job.abort", "source revision changed while rendering");
                queued = true;
                return;
            }
            viewerFrame = output;
            viewerLayout = geometry.IsIdentity && paper.IsIdentity ? null : GeometryLayout(input);
            frameCurrent = true;
            awaitingSource = false;
            pendingInitial?.Cancel();
            showSource.IsEnabled = true;
            previewDetail = comparing ? "Source preview" : "Live Viewer";
            ShowFrame(selectedVariant, output, keepWindow: refreshWindowBase);
            RememberBase(args, strength, source!);
            // Start the window renderer and give it this source now, so the first zoom-in waits neither for its
            // Vulkan start (about 4 s) nor for the upload of the whole source (about 1.5 s for 18 MP).
            if (nativeSize && highDepthPreview && strength > 0 && WindowModelAllowed()
                && (long)source!.Width * source.Height <= WindowSourcePixelsMax)
                PrefetchWindowSource(source, args);
            TraceJob("job.shown", viewer.UnavailableReason ?? "visible");
            StartupHook.Log($"Live Viewer frame {input.Width} × {input.Height} in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms. {DescribeRender(args)}");
        }
        catch (OperationCanceledException)
        {
            TraceJob("job.abort", job.IsCancellationRequested ? "cancelled" : "operation cancelled; queued again");
            if (!job.IsCancellationRequested)
                queued = true;
        }
        catch (HostImageSource.ProxyPendingException) when (!job.IsCancellationRequested)
        {
            TraceJob("job.abort", "Capture One proxy pending");
            status.Text = "Waiting for Capture One to build the source preview…";
            nextViewerCheck = Environment.TickCount64 + 1500;
        }
        catch (Exception error) when (settingsVersion == inputRevision)
        {
            failedRevision = revision;
            failedSpace = space;
            failedPreparation = preparation;
            failedSize = sourceKey;
            failedWhileOffline = !SelectedFileAvailable();
            ClearFrame(error);
        }
        catch (Exception) when (settingsVersion != inputRevision) { }
        finally
        {
            pending = null;
            if (!enabled)
                ReleaseRenderer();
        }
    }

    // Checked at most every two seconds while a develop failed with the original offline (it may touch the disk).
    private long nextOfflineCheck;
    private bool OfflineSourceReturned()
    {
        var now = Environment.TickCount64;
        if (now < nextOfflineCheck) return false;
        nextOfflineCheck = now + 2000;
        return SelectedFileAvailable();
    }

    private bool SelectedFileAvailable()
    {
        try { return HostProfile.ReadSelection(window).Variant.CoreImage?.IsFileAvailable == true; }
        catch (Exception error)
        {
            StartupHook.Log("Spektrafilm could not check the original file: " + error.GetBaseException().Message);
            return false;
        }
    }

    // The frame on screen still shows the photo as Capture One has it: no edit in Capture One is being developed.
    // Save preview, Pin reference and the comparisons wait otherwise (after upstream fc97f9d, Luka Kauzlaric, GPL-3.0).
    private bool FrameShowsCurrentPhoto()
    {
        if (!frameCurrent || sourceRevision is not { } shown || activeVariant is not { } variant)
            return false;
        try { return HostImageSource.GetRevision(window, variant) == shown; }
        catch (InvalidOperationException) { return false; }
    }

    private void ReleaseRenderer()
    {
        ReleaseWindowRenderer();
        renderer?.Dispose();
        renderer = null;
        rendererWarmup = null;
    }

    // Start renderer processes, Vulkan and the stock's tables while Capture One develops the
    // source, instead of after it. Pixels are unchanged: the real frame is rendered afterwards
    // by the same process, which only waits for the warm-up to finish.
    private void StartRendererWarmup(string[] args, bool highDepthPreview, string[]? draftArgs)
    {
        if (renderer is null || rendererUsesHighDepthPreview != highDepthPreview)
        {
            ReleaseRenderer();
            renderer = new OpticalRenderer(false, highDepthPreview);
            rendererUsesHighDepthPreview = highDepthPreview;
        }
        if (!renderer.Started && rendererWarmup is null)
            rendererWarmup = renderer.WarmUpAsync(args);
        if (draftArgs is null)
            return;
        draftRenderer ??= new OpticalRenderer(false, true);
        if (!draftRenderer.Started && draftWarmup is null)
            draftWarmup = draftRenderer.WarmUpAsync(draftArgs);
    }

    private async Task AwaitDraftWarmupAsync()
    {
        if (draftWarmup is { } warming)
        {
            draftWarmup = null;
            await warming;
        }
    }

    private void ResetDraft(bool keepRenderer = false)
    {
        pendingDraft?.Cancel();
        if (pendingDraft is null && !keepRenderer)
            ReleaseDraftRenderer();
        draftSource = null;
        settleSource = null;
        draftPixels = null;
        draftArguments = null;
        draftWarmupAttempted = false;
        lastDraftRevision = -1;
    }

    private async Task TryShowInitialPreviewAsync(HostImageSource.Frame input, long started,
        HostImageSource.Revision revision, Guid variant, InputChoice inputColor, Choice encoding, LookSettings look,
        int dragEdge, int version, CancellationToken token, Task? after = null)
    {
        // A quick frame still in flight (after) finishes first, so the two never use the draft renderer at the
        // same time.
        if (after is not null)
        {
            // The quick frame's worker reports its own errors and does not fault; this only waits for it to end.
            try { await after; }
            catch (Exception) { }
        }
        if (version != inputRevision || DragLongEdge != dragEdge || token.IsCancellationRequested
            || !awaitingSource && frameCurrent || activeVariant != variant || !ReferenceEquals(source, input))
            return;
        using var work = CancellationTokenSource.CreateLinkedTokenSource(token);
        var stop = work.Token;
        var resultEncoding = ResultEncoding(encoding.Id);
        pendingDraft = work;
        pendingInitial = work;
        draftWarmupAttempted = true;
        try
        {
            var quickEdge = QuickLongEdge(input, dragEdge);
            // The first frame of an image should come quickly: at most 2048 px here; later quick frames may use 2560.
            quickEdge = quickEdge == dragEdge ? dragEdge : Math.Min(quickEdge, 2048);
            var initialEdge = quickEdge == dragEdge ? Math.Min(dragEdge, InitialPreviewLongEdge) : quickEdge;
            var frame = await Task.Run(() => ViewerDraftSource.Downsample(input, initialEdge, stop), stop);
            stop.ThrowIfCancellationRequested();
            if (version != inputRevision || !awaitingSource && frameCurrent || activeVariant != variant || !ReferenceEquals(source, input))
                return;
            if (quickEdge != dragEdge)
            {
                settleSource = frame;
                settleEdge = quickEdge;
            }
            else if (dragEdge <= InitialPreviewLongEdge)
                draftSource = frame;
            var finishing = new FinishingTransform(CaptureFinishingSettings());
            var geometry = CaptureGeometrySettings();
            var paper = paperDecoration;
            var args = RendererOptions(look, inputColor.Id, encoding.Id, draft: true);
            var draft = draftRenderer ??= new OpticalRenderer(false, true);
            await AwaitDraftWarmupAsync();
            stop.ThrowIfCancellationRequested();
            if (!ReferenceEquals(draft, draftRenderer))
                return; // released while it was starting
            var packed = finishing.IsIdentity && geometry.IsIdentity && paper.IsIdentity && look.Strength > 0 && encoding.Id == "srgb";
            var pixels = await draft.RenderAsync(frame.Width, frame.Height, frame.FloatBytes,
                packed ? WithDisplayStrength(args, look.Strength) : args, stop, packed);
            var display = await Task.Run(() => ForViewer(packed ? CreateDisplayBitmap(frame, pixels)
                : DecodeDecoratedPreview(frame, pixels, look.Strength, inputColor.Space, resultEncoding, stop, finishing, geometry, paper), stop), stop);
            stop.ThrowIfCancellationRequested();
            if (version != inputRevision || !awaitingSource && frameCurrent || activeVariant != variant
                || !ReferenceEquals(source, input)
                || DragLongEdge != dragEdge || exportWork is not null
                || HostImageSource.GetRevision(window, variant) != revision)
                return;
            lastDraftRevision = version;
            viewerFrame = display;
            viewerLayout = geometry.IsIdentity && paper.IsIdentity ? null : GeometryLayout(frame);
            frameCurrent = false;
            previewDetail = "Loading native resolution";
            ShowFrame(variant, display);
            StartupHook.Log($"Initial Viewer preview {frame.Width} × {frame.Height} in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms.");
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!token.IsCancellationRequested)
                StartupHook.Log("Spektrafilm initial preview: " + error.GetBaseException().Message);
        }
        finally
        {
            if (ReferenceEquals(pendingInitial, work))
                pendingInitial = null;
            if (ReferenceEquals(pendingDraft, work))
            {
                pendingDraft = null;
                if (stop.IsCancellationRequested)
                    ReleaseDraftRenderer();
            }
        }
    }

    // Renders the film look on Capture One's small preview of the image content that is being developed and shows
    // it until the frame from the full source arrives. During a drag of a Capture One slider the preview may be a
    // step behind the slider; like a drag draft it is still the newest picture available.
    private async Task ShowQuickSourceAsync(HostImageSource.Frame frame, long sequence, int generation, long started, Guid variant,
        InputChoice inputColor, Choice encoding, LookSettings look, int dragEdge, int version, CancellationToken jobToken)
    {
        // jobToken: the source job that asked for the frame; cancelled when another image is selected.
        bool Obsolete() => !enabled || !awaitingSource || version != inputRevision || activeVariant != variant
            || jobToken.IsCancellationRequested
            || generation != sourceGeneration || sequence <= shownQuickSequence || exportWork is not null || DragLongEdge != dragEdge;
        if (Obsolete() || pendingDraft is not null)
            return;
        using var work = CancellationTokenSource.CreateLinkedTokenSource(jobToken);
        var stop = work.Token;
        var resultEncoding = ResultEncoding(encoding.Id);
        pendingDraft = work;
        pendingInitial = work;
        draftWarmupAttempted = true;
        try
        {
            var finishing = new FinishingTransform(CaptureFinishingSettings());
            var geometry = CaptureGeometrySettings();
            var paper = paperDecoration;
            var args = RendererOptions(look, inputColor.Id, encoding.Id, draft: true);
            var draft = draftRenderer ??= new OpticalRenderer(false, true);
            await AwaitDraftWarmupAsync();
            stop.ThrowIfCancellationRequested();
            if (!ReferenceEquals(draft, draftRenderer))
                return; // released while it was starting
            var packed = finishing.IsIdentity && geometry.IsIdentity && paper.IsIdentity && look.Strength > 0 && encoding.Id == "srgb";
            var pixels = await draft.RenderAsync(frame.Width, frame.Height, frame.FloatBytes,
                packed ? WithDisplayStrength(args, look.Strength) : args, stop, packed);
            var display = await Task.Run(() => ForViewer(packed ? CreateDisplayBitmap(frame, pixels)
                : DecodeDecoratedPreview(frame, pixels, look.Strength, inputColor.Space, resultEncoding, stop, finishing, geometry, paper), stop), stop);
            stop.ThrowIfCancellationRequested();
            if (Obsolete())
                return;
            shownQuickSequence = sequence;
            // The draft renderer now holds this small source, not the drag copy of the previous one.
            draftPixels = null;
            draftArguments = null;
            viewerFrame = display;
            viewerLayout = geometry.IsIdentity && paper.IsIdentity ? null : GeometryLayout(frame);
            frameCurrent = false;
            previewDetail = "Loading native resolution";
            ShowFrame(variant, display);
            StartupHook.Log($"Quick Viewer preview {frame.Width} × {frame.Height} in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms.");
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            StartupHook.Log("Spektrafilm quick preview: " + error.GetBaseException().Message);
        }
        finally
        {
            if (ReferenceEquals(pendingInitial, work))
                pendingInitial = null;
            if (ReferenceEquals(pendingDraft, work))
            {
                pendingDraft = null;
                if (stop.IsCancellationRequested)
                    ReleaseDraftRenderer();
            }
        }
    }

    private async Task RefreshDraftAsync(Guid selectedVariant, HostImageSource.Revision revision,
        HostImageSource.Frame input, InputChoice inputColor, Choice encoding, LookSettings look, string viewerSize, int dragEdge,
        int quickEdge, bool warmup = false)
    {
        draftWarmupAttempted = true;
        var settingsVersion = inputRevision;
        if (!warmup)
            lastDraftRevision = settingsVersion;
        var resultEncoding = ResultEncoding(encoding.Id);
        var started = Stopwatch.GetTimestamp();
        using var job = new CancellationTokenSource();
        pendingDraft = job;
        try
        {
            var finishing = new FinishingTransform(CaptureFinishingSettings());
            var geometry = CaptureGeometrySettings();
            var paper = paperDecoration;
            var args = look.Strength > 0 ? RendererOptions(look, inputColor.Id, encoding.Id, draft: true) : Array.Empty<string>();
            HostImageSource.Frame frame;
            if (quickEdge == dragEdge)
            {
                if (draftSource is null)
                {
                    var reduced = await Task.Run(() => ViewerDraftSource.Downsample(input, dragEdge, job.Token), job.Token);
                    job.Token.ThrowIfCancellationRequested();
                    draftSource = reduced;
                }
                frame = draftSource;
            }
            else
            {
                if (settleSource is null || settleEdge != quickEdge)
                {
                    var reduced = await Task.Run(() => ViewerDraftSource.Downsample(input, quickEdge, job.Token), job.Token);
                    job.Token.ThrowIfCancellationRequested();
                    settleSource = reduced;
                    settleEdge = quickEdge;
                }
                frame = settleSource;
            }
            byte[]? packed = null;
            if (look.Strength > 0 && (draftPixels is null || draftPixelsEdge != quickEdge || !args.SequenceEqual(draftArguments!)))
            {
                var draft = draftRenderer ??= new OpticalRenderer(false, true);
                await AwaitDraftWarmupAsync();
                job.Token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(draft, draftRenderer))
                    return; // released while it was starting
                var pack = finishing.IsIdentity && geometry.IsIdentity && paper.IsIdentity && !warmup && look.Strength > 0 && encoding.Id == "srgb";
                var rendered = await draft.RenderAsync(frame.Width, frame.Height, frame.FloatBytes,
                    pack ? WithDisplayStrength(args, look.Strength) : args, job.Token, pack);
                job.Token.ThrowIfCancellationRequested();
                packed = pack ? rendered : null;
                draftPixels = pack ? null : rendered;
                draftPixelsEdge = quickEdge;
                draftArguments = args;
            }
            if (warmup)
                return;
            var pixels = draftPixels;
            var output = await Task.Run(() => ForViewer(packed is null
                ? DecodeDecoratedPreview(frame, pixels, look.Strength, inputColor.Space, resultEncoding, job.Token, finishing, geometry, paper)
                : CreateDisplayBitmap(frame, packed), job.Token), job.Token);
            job.Token.ThrowIfCancellationRequested();
            // A drag changes the settings faster than a draft renders. The newest finished draft is still the
            // best feedback available, so it is shown even when a later change has already arrived; the next
            // draft follows at once (see finally). Dropping it left the Viewer frozen until the slider paused.
            if (frameCurrent || !WantsQuickPreview
                || exportWork is not null || activeVariant != selectedVariant || !ReferenceEquals(source, input)
                || !ReferenceEquals(inputSpace.SelectedItem, inputColor) || !ReferenceEquals(outputSpace.SelectedItem, encoding)
                || ((Choice)previewSize.SelectedItem).Id != viewerSize || DragLongEdge != dragEdge
                || HostImageSource.GetRevision(window, selectedVariant) != revision)
                return;

            viewerFrame = output;
            viewerLayout = geometry.IsIdentity && paper.IsIdentity ? null : GeometryLayout(frame);
            frameCurrent = false;
            previewDetail = "Interactive preview";
            ShowFrame(selectedVariant, output);
            StartupHook.Log($"Interactive preview frame {frame.Width} × {frame.Height} in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms.");
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) when (!WantsQuickPreview || selectionUnavailable) { }
        catch (Exception error)
        {
            StartupHook.Log("Spektrafilm drag preview: " + error.GetBaseException().Message);
        }
        finally
        {
            pendingDraft = null;
            if (job.IsCancellationRequested)
                ReleaseDraftRenderer();
            else if (!warmup && enabled && lastDraftRevision != inputRevision && WantsQuickPreview)
                // Input is handled first (higher priority), then the draft for the newest settings starts
                // without waiting for the next timer tick.
                _ = refresh.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _ = RefreshAsync()));
        }
    }

    // Frames are converted for the monitor here, on the worker thread, not in the Viewer's Paint handler.
    private BitmapSource ForViewer(BitmapSource frame, CancellationToken token)
    {
        viewer.PrepareAhead(frame, token);
        return frame;
    }

    private void ReleaseDraftRenderer()
    {
        draftRenderer?.Dispose();
        draftRenderer = null;
        draftWarmup = null;
    }

    private void ClearFrame(Exception error)
    {
        viewer.Clear();
        viewerFrame = null;
        frameCurrent = false;
        savePreview.IsEnabled = false;
        nextViewerCheck = Environment.TickCount64 + 350;
        var message = error.GetBaseException().Message;
        if (status.Text != message)
            StartupHook.Log(error.ToString());
        status.Text = message;
        renderErrorText.Text = "Spektrafilm could not render this frame. The Viewer shows the plain Capture One image, not the film look. Reason: " + message;
        renderError.Visibility = Visibility.Visible;
    }

    private void HideRenderError() => renderError.Visibility = Visibility.Collapsed;

    private BitmapSource? shownFrame;

    private void ShowFrame(Guid variant, BitmapSource frame, bool keepWindow = false)
    {
        // A new frame (not the refreshed base under a window) replaces the Viewer window.
        if (!keepWindow && !ReferenceEquals(frame, shownFrame))
            ForgetWindow();
        shownFrame = frame;
        referenceComparison?.Update(frame);
        if (scopesWindow is { IsClosed: false })
        {
            scopesWindow.Title = $"Spektrafilm scopes · {previewDetail} · {frame.PixelWidth} × {frame.PixelHeight}";
            scopesWindow.Update(frame);
        }
        nextViewerCheck = Environment.TickCount64 + SelectionCheckIntervalMs;
        HideRenderError();
        viewer.Show(variant, frame, viewerLayout, keepWindow);
        UpdateViewerStatus();
    }

    private void UpdateViewerStatus()
    {
        if (!enabled || exportWork is not null)
            return;
        var message = viewer.UnavailableReason ?? viewer.ColorManagementWarning;
        if (message is null && viewerFrame is { } frame)
            message = $"{previewDetail} · {frame.PixelWidth} × {frame.PixelHeight}";
        if (message is not null && status.Text != message)
            status.Text = message;
        savePreview.IsEnabled = viewerFrame is not null && frameCurrent && !saveInProgress && viewer.UnavailableReason is null;
    }

    private async Task SavePreviewAsync()
    {
        if (!frameCurrent || viewerFrame is not { } frame || saveInProgress)
            return;
        if (!FrameShowsCurrentPhoto())
        {
            status.Text = "Wait for the Viewer to show your latest Capture One adjustments, then save the preview.";
            return;
        }
        try
        {
            var target = HostProfile.CaptureTarget(window);
            if (target.VariantId != activeVariant)
                return;
            var picker = new SaveFileDialog
            {
                Filter = "PNG image|*.png",
                FileName = "Spektrafilm preview.png",
                AddExtension = true,
                DefaultExt = ".png",
            };
            if (picker.ShowDialog() != true)
                return;

            var path = ProtectOriginal(picker.FileName, target.SourcePath);
            saveInProgress = true;
            savePreview.IsEnabled = false;
            await Task.Run(() => PanelFiles.WriteAtomically(path, stream =>
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(frame, null, null,
                    new ReadOnlyCollection<ColorContext>(new[] { SrgbProfile.Context() })));
                encoder.Save(stream);
            }));
            StartupHook.Log("Saved the sRGB Viewer preview as PNG.");
        }
        catch (Exception error)
        {
            MessageBox.Show(error.GetBaseException().Message, "Could not save Spektrafilm preview", MessageBoxButton.OK, MessageBoxImage.Error);
            StartupHook.Log(error.ToString());
        }
        finally
        {
            saveInProgress = false;
            UpdateViewerStatus();
        }
    }

    // Look strength below 100% is blended by the renderer when it packs Viewer pixels, with the
    // same math and rounding as SrgbTransform (ofx-still-renderer DisplayBlend). "R" keeps the
    // exact double. The renderer excludes it from its argument cache, so a strength change only
    // re-packs the last frame.
    private static string[] WithDisplayStrength(string[] args, double strength) => strength >= 1 ? args
        : [.. args, "--display-strength", strength.ToString("R", CultureInfo.InvariantCulture)];

    private static string ProtectOriginal(string destination, string source) => OutputPathGuard.Validate(destination, source);

    private static string DescribeRender(string[] arguments)
    {
        var settings = new List<string>();
        for (var index = 0; index < arguments.Length; index += 2)
        {
            var option = arguments[index];
            if (option is "--stock" or "--paper" or "--mode" or "--film-model" or "--input-color" or "--output-color" or "--grain-model" or "--camera-diffusion-family"
                or "--auto-exposure" or "--auto-exposure-meter" or "--film-exposure" or "--print-exposure"
                or "--print-diffusion-family" or "--halation-amount" or "--scatter-amount" or "--scanner-enabled"
                or "--enlarger-scale")
                settings.Add($"{option[2..]}={arguments[index + 1]}");
        }
        return string.Join(' ', settings);
    }

    private string[] RendererOptions(LookSettings look, string inputColor, string outputColor, bool export = false, bool draft = false)
    {
        if (look.Mode.StartsWith("process_negative", StringComparison.Ordinal))
            throw new InvalidOperationException("Process Negative requires an encoded negative scan as input.");

        var args = new List<string>();
        Choice Selected(string key) => (Choice)choices[key].SelectedItem;
        void Set(string key, string value) { args.Add("--" + key); args.Add(value); }
        // Integer options must be sent as integers; a dragged slider can report e.g. 20881.93,
        // which the renderer rejects ("Invalid integer") and the frame disappears.
        void Numeric(string key, double value) => Set(key, IntegerOptions.Contains(key)
            ? Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("0.######", CultureInfo.InvariantCulture));

        Set("stock", look.Film);
        Set("paper", look.Paper);
        Set("input-color", inputColor);
        Set("output-color", outputColor);
        Set("mode", look.Mode.Contains("scan", StringComparison.Ordinal) ? (look.ScanInvert ? "scan_inverted" : "scan") : "print");
        Set("spectral-method", look.SpectralMethod);
        // Model 1 sends no option, so its arguments (and renders) stay exactly as before model 2.
        if (((Choice)filmModel.SelectedItem).Id is var model && model != "1") Set("film-model", model);
        Set("film-push-pull-mode", look.FilmPushPullMode);
        Numeric("film-exposure", look.Exposure);
        Numeric("print-exposure", look.PrintExposure);
        if (!look.Mode.Contains("scan", StringComparison.Ordinal)) Numeric("print-exhaustion", look.Development.GetValueOrDefault("print_exhaustion", 0));
        foreach (var (sourceKey, rendererKey) in new[]
        {
            ("film_gamma", "film-gamma"), ("film_push_pull", "film-push-pull"), ("print_gamma", "print-gamma"),
            ("print_push_pull", "print-push-pull"), ("negative_bleach_bypass", "negative-bleach-bypass"),
            ("print_bleach_bypass", "print-bleach-bypass"), ("negative_leuco_cyan", "negative-leuco-cyan"),
            ("print_shadows", "print-shadow-shape"), ("print_highlights", "print-highlight-shape"),
            ("cyan_filter", "cyan-filter"), ("magenta_filter", "magenta-filter"), ("yellow_filter", "yellow-filter"),
            ("preflash_exposure", "preflash"), ("preflash_magenta", "preflash-magenta"), ("preflash_yellow", "preflash-yellow"),
            ("uv_cutoff", "uv-cutoff"), ("ir_cutoff", "ir-cutoff"), ("dir_amount", "dir-amount"),
            ("dir_same_layer", "dir-same-layer"), ("dir_interlayer", "dir-interlayer"),
        })
        {
            if (look.Development.TryGetValue(sourceKey, out var value))
                Numeric(rendererKey, value);
        }
        Set("uv-enabled", look.Development.GetValueOrDefault("uv_cutoff", 380) > 380 ? "true" : "false");
        Set("ir-enabled", look.Development.GetValueOrDefault("ir_cutoff", 780) < 780 ? "true" : "false");

        foreach (var key in choices.Keys)
            Set(key, Selected(key).Id);
        var fastGrain = !export && (draft || fastSynthesis.IsChecked == true) && Selected("grain-model").Id == "synthesis";
        var grainQuality = Math.Min(values["grain-synthesis-quality"].Value, 2);
        foreach (var (key, slider) in values)
        {
            if (key.StartsWith("finishing", StringComparison.Ordinal) || key.StartsWith("export-", StringComparison.Ordinal)) continue;
            var halationChannel = key is "halation-red" or "halation-green" or "halation-blue";
            var couplerChannel = key.StartsWith("dir-gamma-", StringComparison.Ordinal) || key.Contains("-to-", StringComparison.Ordinal);
            if ((halationChannel && toggles["custom-halation-rgb"].IsChecked != true)
                || (couplerChannel && toggles["custom-dir-rgb"].IsChecked != true))
                continue;
            var value = slider.Value;
            if (fastGrain && key == "grain-synthesis-samples")
                value = Math.Min(value, Math.Floor(2 / grainQuality));
            if (fastGrain && key == "grain-synthesis-quality")
                value = grainQuality;
            Numeric(key, value);
        }
        foreach (var (key, toggle) in toggles)
            if (!key.StartsWith("custom-", StringComparison.Ordinal) && !key.StartsWith("finishing", StringComparison.Ordinal)
                && !key.StartsWith("export-", StringComparison.Ordinal))
                Set(key, toggle.IsChecked == true ? "true" : "false");
        return args.ToArray();
    }

    private static BitmapSource DecodeResult(HostImageSource.Frame frame, byte[]? bytes, double strength,
        PreviewInputSpace space, string outputEncoding, CancellationToken token)
    {
        return DecodeResultWithFinishing(frame, bytes, strength, space, outputEncoding, token, null);
    }

    private static BitmapSource DecodeResultWithFinishing(HostImageSource.Frame frame, byte[]? bytes, double strength,
        PreviewInputSpace space, string outputEncoding, CancellationToken token, FinishingTransform? finishing)
    {
        var pixels = strength <= 0 && space == PreviewInputSpace.Srgb && frame.Codes.Length > 0
            ? frame.Codes : ToDisplayPixelsWithFinishing(frame, bytes, strength, space, outputEncoding, token, finishing);
        return CreateDisplayBitmap(frame, pixels);
    }

    private static BitmapSource CreateDisplayBitmap(HostImageSource.Frame frame, byte[] pixels)
    {
        var started = PreviewPerformance.Start();
        var image = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, pixels, frame.Width * 4);
        image.Freeze();
        PreviewPerformance.End(started, "display.bitmap", $"{frame.Width}x{frame.Height}");
        return image;
    }

    private static byte[] ToDisplayPixels(HostImageSource.Frame original, byte[]? rendered, double strength,
        PreviewInputSpace space, string outputEncoding, CancellationToken token)
    {
        return ToDisplayPixelsWithFinishing(original, rendered, strength, space, outputEncoding, token, null);
    }

    private static byte[] ToDisplayPixelsWithFinishing(HostImageSource.Frame original, byte[]? rendered, double strength,
        PreviewInputSpace space, string outputEncoding, CancellationToken token, FinishingTransform? finishing)
    {
        var started = PreviewPerformance.Start();
        var pixels = GC.AllocateUninitializedArray<byte>(original.FloatBytes.Length / sizeof(float));
        var prepared = strength > 0 ? PrepareSpatialFinishing(original, rendered, outputEncoding, finishing, token) : null;
        var transform = new SrgbTransform(space, prepared is null ? outputEncoding : "srgb", strength, prepared is null ? finishing : null, prepared is not null);
        var hasPreviewCodes = original.Codes.Length != 0;
        var rowWidth = original.Width * 4;
        PreviewRows.Process(original.Width, original.Height, token, (first, last) =>
            ConvertDisplayRows(original, rendered, prepared, transform, pixels, rowWidth, hasPreviewCodes, first, last, token));
        PreviewPerformance.End(started, "display.convert", $"{original.Width}x{original.Height}");
        return pixels;
    }

    private static void ConvertDisplayRows(HostImageSource.Frame original, byte[]? rendered, float[]? prepared,
        SrgbTransform transform, byte[] pixels, int rowWidth, bool hasPreviewCodes, int first, int last, CancellationToken token)
    {
        var source = MemoryMarshal.Cast<byte, float>(original.FloatBytes.AsSpan());
        var floats = prepared is null ? MemoryMarshal.Cast<byte, float>(rendered.AsSpan()) : prepared.AsSpan();
        for (var y = first; y < last; y++)
        {
            token.ThrowIfCancellationRequested();
            var start = y * rowWidth;
            for (var i = start; i < start + rowWidth; i += 4)
            {
                var rgb = transform.GetSrgb(source, floats, i);
                pixels[i] = (byte)Math.Round(rgb.Blue * 255);
                pixels[i + 1] = (byte)Math.Round(rgb.Green * 255);
                pixels[i + 2] = (byte)Math.Round(rgb.Red * 255);
                pixels[i + 3] = hasPreviewCodes ? original.Codes[i + 3] : (byte)MathF.Round(255 * Math.Clamp(source[i + 3], 0, 1));
            }
        }
    }

    private static BitmapSource DecodeExport(HostImageSource.Frame frame, byte[]? rendered, double strength,
        PreviewInputSpace space, string outputEncoding, PixelFormat format, System.Drawing.Rectangle crop, CancellationToken token)
    {
        return DecodeExportWithFinishing(frame, rendered, strength, space, outputEncoding, format, crop, token, null);
    }

    private static BitmapSource DecodeExportWithFinishing(HostImageSource.Frame frame, byte[]? rendered, double strength,
        PreviewInputSpace space, string outputEncoding, PixelFormat format, System.Drawing.Rectangle crop,
        CancellationToken token, FinishingTransform? finishing)
    {
        var prepared = strength > 0 ? PrepareSpatialFinishing(frame, rendered, outputEncoding, finishing, token) : null;
        var source = MemoryMarshal.Cast<byte, float>(frame.FloatBytes.AsSpan());
        var floats = prepared is null ? MemoryMarshal.Cast<byte, float>(rendered.AsSpan()) : prepared.AsSpan();
        var transform = new SrgbTransform(space, prepared is null ? outputEncoding : "srgb", strength, prepared is null ? finishing : null, prepared is not null);
        var hasAlpha = format == PixelFormats.Rgba64;
        var channels = hasAlpha ? 4 : 3;
        var stride = checked(crop.Width * channels * sizeof(ushort));
        var pixels = GC.AllocateUninitializedArray<ushort>(checked(crop.Width * crop.Height * channels));
        var destination = 0;
        for (var y = crop.Top; y < crop.Bottom; y++)
        {
            token.ThrowIfCancellationRequested();
            var i = (y * frame.Width + crop.Left) * 4;
            for (var x = 0; x < crop.Width; x++, i += 4)
            {
                var rgb = transform.GetSrgb(source, floats, i);
                pixels[destination++] = (ushort)Math.Round(rgb.Red * ushort.MaxValue);
                pixels[destination++] = (ushort)Math.Round(rgb.Green * ushort.MaxValue);
                pixels[destination++] = (ushort)Math.Round(rgb.Blue * ushort.MaxValue);
                if (hasAlpha)
                    pixels[destination++] = (ushort)Math.Round(Math.Clamp((double)source[i + 3], 0, 1) * ushort.MaxValue);
            }
        }
        var image = BitmapSource.Create(crop.Width, crop.Height, 96, 96, format, null, pixels, stride);
        image.Freeze();
        return image;
    }

    private static BitmapSource DecodeExportColorManaged(HostImageSource.Frame frame, byte[]? rendered, double strength,
        PreviewInputSpace space, string outputEncoding, PixelFormat format, System.Drawing.Rectangle crop,
        CancellationToken token, FinishingTransform? finishing, ExportRgbSpace destinationSpace)
    {
        if (destinationSpace == ExportRgbSpace.Srgb)
            return DecodeExportWithFinishing(frame, rendered, strength, space, outputEncoding, format, crop, token, finishing);
        var prepared = strength > 0 ? PrepareSpatialFinishing(frame, rendered, outputEncoding, finishing, token) : null;
        var transform = new SrgbTransform(space, prepared is null ? outputEncoding : "srgb", strength, prepared is null ? finishing : null, prepared is not null);
        var directLinear = strength >= 1 && prepared is null && outputEncoding == "linear_rec709" && finishing?.IsIdentity != false;
        var directSource = strength <= 0 && ((space, destinationSpace) is (PreviewInputSpace.AdobeRgb, ExportRgbSpace.AdobeRgb)
            or (PreviewInputSpace.ProPhotoRgb, ExportRgbSpace.ProPhotoRgb) or (PreviewInputSpace.LinearRec2020, ExportRgbSpace.LinearRec2020));
        var hasAlpha = format == PixelFormats.Rgba64;
        var channels = hasAlpha ? 4 : 3;
        var stride = checked(crop.Width * channels * sizeof(ushort));
        var pixels = GC.AllocateUninitializedArray<ushort>(checked(crop.Width * crop.Height * channels));
        PreviewRows.Process(crop.Width, crop.Height, token, (first, last) =>
        {
            var source = MemoryMarshal.Cast<byte, float>(frame.FloatBytes.AsSpan());
            var floats = prepared is null ? MemoryMarshal.Cast<byte, float>(rendered.AsSpan()) : prepared.AsSpan();
            for (var y = first; y < last; y++)
            {
                token.ThrowIfCancellationRequested();
                var destination = y * crop.Width * channels;
                var i = ((crop.Top + y) * frame.Width + crop.Left) * 4;
                for (var x = 0; x < crop.Width; x++, i += 4)
                {
                    ExportColorSpace.Rgb rgb;
                    if (directSource) rgb = new(source[i], source[i + 1], source[i + 2]);
                    else if (directLinear) rgb = ExportColorSpace.FromLinearSrgb(floats[i], floats[i + 1], floats[i + 2], destinationSpace);
                    else
                    {
                        var encoded = transform.GetUnclippedSrgb(source, floats, i);
                        rgb = ExportColorSpace.FromSrgb(encoded.Red, encoded.Green, encoded.Blue, destinationSpace);
                    }
                    pixels[destination++] = (ushort)Math.Round(Math.Clamp(rgb.Red, 0, 1) * ushort.MaxValue);
                    pixels[destination++] = (ushort)Math.Round(Math.Clamp(rgb.Green, 0, 1) * ushort.MaxValue);
                    pixels[destination++] = (ushort)Math.Round(Math.Clamp(rgb.Blue, 0, 1) * ushort.MaxValue);
                    if (hasAlpha) pixels[destination++] = (ushort)Math.Round(Math.Clamp((double)source[i + 3], 0, 1) * ushort.MaxValue);
                }
            }
        });
        var image = BitmapSource.Create(crop.Width, crop.Height, 96, 96, format, null, pixels, stride); image.Freeze(); return image;
    }

    // Linear RGB DNG keeps positive headroom until the writer's documented
    // power-of-two scaling. No sRGB/16-bit intermediate is allowed here.
    private static float[] DecodeLinearDng(HostImageSource.Frame frame, byte[]? rendered, double strength,
        PreviewInputSpace space, string outputEncoding, System.Drawing.Rectangle crop, CancellationToken token, FinishingTransform? finishing)
    {
        if (crop.Left < 0 || crop.Top < 0 || crop.Right > frame.Width || crop.Bottom > frame.Height || crop.Width <= 0 || crop.Height <= 0)
            throw new InvalidDataException("Invalid DNG crop.");
        var prepared = strength > 0 ? PrepareSpatialFinishing(frame, rendered, outputEncoding, finishing, token) : null;
        var transform = new SrgbTransform(space, prepared is null ? outputEncoding : "srgb", strength, prepared is null ? finishing : null, prepared is not null);
        var directLinear = strength >= 1 && prepared is null && outputEncoding == "linear_rec709" && finishing?.IsIdentity != false;
        var directSource = strength <= 0 && space == PreviewInputSpace.LinearRec2020;
        var pixels = GC.AllocateUninitializedArray<float>(checked(crop.Width * crop.Height * 4));
        PreviewRows.Process(crop.Width, crop.Height, token, (first, last) =>
        {
            var source = MemoryMarshal.Cast<byte, float>(frame.FloatBytes.AsSpan());
            var floats = prepared is null ? MemoryMarshal.Cast<byte, float>(rendered.AsSpan()) : prepared.AsSpan();
            for (var y = first; y < last; y++)
            {
                token.ThrowIfCancellationRequested();
                var destination = y * crop.Width * 4;
                var i = ((crop.Top + y) * frame.Width + crop.Left) * 4;
                for (var x = 0; x < crop.Width; x++, i += 4)
                {
                    ExportColorSpace.Rgb rgb;
                    if (directSource) rgb = new(source[i], source[i + 1], source[i + 2]);
                    else if (directLinear) rgb = ExportColorSpace.FromLinearSrgb(floats[i], floats[i + 1], floats[i + 2], ExportRgbSpace.LinearRec2020);
                    else {
                        var encoded = transform.GetUnclippedSrgb(source, floats, i);
                        rgb = ExportColorSpace.FromSrgb(encoded.Red, encoded.Green, encoded.Blue, ExportRgbSpace.LinearRec2020);
                    }
                    pixels[destination++] = (float)rgb.Red; pixels[destination++] = (float)rgb.Green; pixels[destination++] = (float)rgb.Blue;
                    pixels[destination++] = source[i + 3];
                }
            }
        });
        return pixels;
    }

    private static float[]? PrepareSpatialFinishing(HostImageSource.Frame frame, byte[]? rendered, string encoding,
        FinishingTransform? finishing, CancellationToken token)
    {
        if (finishing is null || finishing.MicroContrast == 0) return null;
        if (rendered is null || rendered.Length != frame.FloatBytes.Length) throw new InvalidDataException("Missing finishing pixels.");
        var pixels = MemoryMarshal.Cast<byte, float>(rendered.AsSpan()).ToArray();
        PreviewRows.Process(frame.Width, frame.Height, token, (first, last) =>
        {
            for (var y = first; y < last; y++)
            {
                token.ThrowIfCancellationRequested();
                for (var x = 0; x < frame.Width; x++)
                {
                    var p = (y * frame.Width + x) * 4;
                    var red = EncodeRendered(pixels[p], encoding); var green = EncodeRendered(pixels[p + 1], encoding); var blue = EncodeRendered(pixels[p + 2], encoding);
                    finishing.Apply(ref red, ref green, ref blue);
                    pixels[p] = (float)red; pixels[p + 1] = (float)green; pixels[p + 2] = (float)blue;
                }
            }
        });
        FinishingMicroContrast.Apply(pixels, frame.Width, frame.Height, finishing.MicroContrast, token);
        return pixels;
    }

    private static string ResultEncoding(string requested) => requested is "srgb" or "rec709_gamma24" ? "native_" + requested : requested;

    private static double EncodeRendered(float value, string encoding)
    {
        if (!float.IsFinite(value)) throw new InvalidDataException("The optical renderer returned an invalid pixel.");
        if (encoding == "native_srgb") return value < 0 ? EncodeSrgb(-DecodeSrgb(-value)) : value;
        if (encoding == "native_rec709_gamma24") return EncodeSrgb(Math.CopySign(Math.Pow(Math.Abs(value), 2.4), value));
        return encoding == "srgb" ? value : EncodeSrgb(encoding == "linear_rec709" ? value : Math.Pow(Math.Max(0, value), 2.4));
    }

    private readonly struct SrgbTransform(PreviewInputSpace space, string outputEncoding, double strength, FinishingTransform? finishing = null, bool prefinished = false)
    {
        private readonly bool useWideGamut = space != PreviewInputSpace.Srgb && strength < 1;
        private readonly double[]? inputTransfer = space switch
        {
            PreviewInputSpace.AdobeRgb => AdobeDecode,
            PreviewInputSpace.ProPhotoRgb => ProPhotoDecode,
            _ => null,
        };
        private readonly double[]? outputTransfer = outputEncoding switch
        {
            "srgb" or "native_srgb" => SrgbDecode,
            "linear_rec709" => null,
            _ => Rec709Gamma24Decode,
        };

        internal LinearBgr GetSrgb(ReadOnlySpan<float> source, ReadOnlySpan<float> rendered, int i)
        {
            if (strength <= 0 && space == PreviewInputSpace.Srgb)
                return new(Math.Clamp(source[i + 2], 0, 1), Math.Clamp(source[i + 1], 0, 1), Math.Clamp(source[i], 0, 1));

            var baseline = strength >= 1 ? default : useWideGamut
                ? ToLinearSrgb(space, DecodeInput(source[i]), DecodeInput(source[i + 1]), DecodeInput(source[i + 2]))
                : new LinearBgr(Sample(SrgbDecode, source[i + 2]), Sample(SrgbDecode, source[i + 1]), Sample(SrgbDecode, source[i]));
            if (strength > 0 && (prefinished || finishing is { IsIdentity: false }))
            {
                // Finishing receives high precision before output clipping and before
                // Look strength. Keep the neutral legacy path exactly unchanged.
                var red = Encoded(rendered[i]);
                var green = Encoded(rendered[i + 1]);
                var blue = Encoded(rendered[i + 2]);
                if (!prefinished) finishing!.Apply(ref red, ref green, ref blue);
                return new(FinishBlend(baseline.Blue, blue), FinishBlend(baseline.Green, green), FinishBlend(baseline.Red, red));
            }
            return new(
                Blend(baseline.Blue, strength > 0 ? rendered[i + 2] : 0),
                Blend(baseline.Green, strength > 0 ? rendered[i + 1] : 0),
                Blend(baseline.Red, strength > 0 ? rendered[i] : 0));
        }

        internal LinearBgr GetUnclippedSrgb(ReadOnlySpan<float> source, ReadOnlySpan<float> rendered, int i)
        {
            if (strength <= 0 && space == PreviewInputSpace.Srgb) return new(source[i + 2], source[i + 1], source[i]);
            var decoded = strength >= 1 ? default : ExportColorSpace.ToLinearSrgb(source[i], source[i + 1], source[i + 2], space switch
            {
                PreviewInputSpace.Srgb => ExportRgbSpace.Srgb,
                PreviewInputSpace.AdobeRgb => ExportRgbSpace.AdobeRgb,
                PreviewInputSpace.ProPhotoRgb => ExportRgbSpace.ProPhotoRgb,
                _ => ExportRgbSpace.LinearRec2020,
            });
            var baseline = new LinearBgr(decoded.Blue, decoded.Green, decoded.Red);
            if (strength <= 0) return new(EncodeSrgb(baseline.Blue), EncodeSrgb(baseline.Green), EncodeSrgb(baseline.Red));
            var red = Encoded(rendered[i]); var green = Encoded(rendered[i + 1]); var blue = Encoded(rendered[i + 2]);
            if (!prefinished) finishing?.Apply(ref red, ref green, ref blue);
            return strength >= 1 ? new(blue, green, red)
                : new(EncodeSrgb(baseline.Blue + strength * (DecodeSrgb(blue) - baseline.Blue)),
                    EncodeSrgb(baseline.Green + strength * (DecodeSrgb(green) - baseline.Green)),
                    EncodeSrgb(baseline.Red + strength * (DecodeSrgb(red) - baseline.Red)));
        }

        private double Encoded(float value)
        {
            return EncodeRendered(value, outputEncoding);
        }

        private double FinishBlend(double baseline, double encoded)
        {
            if (!double.IsFinite(encoded)) throw new InvalidDataException("Finishing returned an invalid pixel.");
            return Math.Clamp(strength >= 1 ? encoded : EncodeSrgb(baseline + strength * (DecodeSrgb(encoded) - baseline)), 0, 1);
        }

        private double DecodeInput(float value) => inputTransfer is null ? value : Sample(inputTransfer, value);

        private double Blend(double baseline, float processed)
        {
            if (!float.IsFinite(processed))
                throw new InvalidDataException("The optical renderer returned an invalid pixel.");
            var display = (double)processed;
            if (strength >= 1 && ReferenceEquals(outputTransfer, SrgbDecode))
                return Math.Clamp(display, 0, 1);
            var film = outputTransfer is null ? display
                : display is >= 0 and <= 1 ? Sample(outputTransfer, display)
                : outputEncoding == "native_srgb" ? Math.CopySign(DecodeSrgb(Math.Abs(display)), display)
                : outputEncoding == "native_rec709_gamma24" ? Math.CopySign(Math.Pow(Math.Abs(display), 2.4), display)
                : outputEncoding == "srgb" ? DecodeSrgb(display) : Math.Pow(Math.Max(0, display), 2.4);
            return Sample(SrgbEncode, baseline + strength * (film - baseline));
        }
    }

    // ProPhoto includes Bradford adaptation from D50 to the D65 sRGB white point.
    // Keep wide-gamut components out of range until final display encoding.
    private static LinearBgr ToLinearSrgb(PreviewInputSpace space, double red, double green, double blue) => space switch
    {
        PreviewInputSpace.AdobeRgb => new(-0.04294 * green + 1.04294 * blue, green, 1.39828 * red - 0.39828 * green),
        PreviewInputSpace.ProPhotoRgb => new(
            -0.008555533 * red - 0.153286575 * green + 1.161842108 * blue,
            -0.228798056 * red + 1.231730282 * green - 0.002932226 * blue,
            2.034218153 * red - 0.727536473 * green - 0.306681680 * blue),
        _ => new(
            -0.018150763 * red - 0.100578898 * green + 1.118729661 * blue,
            -0.124550475 * red + 1.132899897 * green - 0.008349423 * blue,
            1.660491002 * red - 0.587641139 * green - 0.072849863 * blue),
    };

    private static double[] BuildTransfer(Func<double, double> transfer)
    {
        var lookup = new double[TransferSteps + 1];
        for (var i = 0; i <= TransferSteps; i++)
            lookup[i] = transfer(i / (double)TransferSteps);
        return lookup;
    }

    private static double Sample(double[] lookup, double value)
    {
        var coordinate = Math.Clamp(value, 0, 1) * TransferSteps;
        var lower = Math.Min((int)coordinate, TransferSteps - 1);
        return lookup[lower] + (coordinate - lower) * (lookup[lower + 1] - lookup[lower]);
    }

    private static double DecodeSrgb(double value) => value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);

    private static double EncodeSrgb(double value) => value <= 0.0031308 ? 12.92 * value : 1.055 * Math.Pow(value, 1 / 2.4) - 0.055;

    private sealed record Choice(string Id, string Label);
    private sealed record DragSize(int LongEdge, string Label);
    private sealed record InputChoice(PreviewInputSpace Space, string Id, string Label);
    private readonly record struct LinearBgr(double Blue, double Green, double Red);
    internal sealed record Settings(Dictionary<string, double> Numbers, Dictionary<string, string> Selections, Dictionary<string, bool> Switches);
}
