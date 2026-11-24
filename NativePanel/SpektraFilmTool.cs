using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using P1.C1.Components.Docking.WPF;
using P1.C1.Components.Docking.WPF.Floating;
using P1.C1.Components.InspectorTools;
using P1.C1.Controls.Inspector.Dummy;
using P1.C1.Core.Interfaces;
using P1.C1.Core.Messaging;
using P1.C1.Core.Messaging.ModelMessages;
using P1.C1.Core.ModelEntities;
using P1.C1.Framework.Messaging;
using P1.C1.ViewModels.Menu;
using P1.C1.ViewModels.Notification;
using P1.C1.ViewModels.Tools.InspectorTool;
using P1.C1.WPF.Styles;

internal static partial class SpektraFilmTool
{
    private const BindingFlags InstanceNonPublic = BindingFlags.Instance | BindingFlags.NonPublic;

    public static HostedTool Create(System.Windows.Forms.Form window, IMessageBusProvider bus, ICaptureOneDocument document, string database)
    {
        // The base class loads BAML from CaptureOne.dll, so use the host's empty tool.
        var tool = new DummyInspectorTool(bus);
        var toolType = typeof(InspectorToolWpf);
        var registration = (ILifeCycleRegistrationService?)toolType.GetProperty("LifeCycleEventRegistration", InstanceNonPublic)?.GetValue(tool)
            ?? throw new MissingMemberException(toolType.FullName, "LifeCycleEventRegistration");
        var initialize = toolType.GetMethod("Initialize", InstanceNonPublic, null,
            new[] { typeof(UserControl), typeof(IInspectorToolContentViewModel), typeof(InspectorToolHeaderViewModel) }, null)
            ?? throw new MissingMethodException(toolType.FullName, "Initialize");
        var hosted = new HostedTool(window, tool, document, database);
        try
        {
            var header = hosted.CreateDockedHeader(registration);
            tool.SetProperties(new Dictionary<string, object>());
            initialize.Invoke(tool, new object[] { hosted.DockedContent, EmptyInspectorToolContentViewModel.Instance, header });
            tool.DockingLogic.DockableArea = DockableArea.ScrollableArea;
            // Move the content between windows without registering the dummy tool as a native workspace tool.
            tool.DockingLogic.CanDragDrop = false;
            return hosted;
        }
        catch
        {
            hosted.Dispose();
            tool.Dispose();
            throw;
        }
    }

    internal sealed class HostedTool : IDisposable
    {
        private static readonly Uri CaptureOneStyle = new("pack://application:,,,/CaptureOne;component/WPF/Styles/DefaultStyle.xaml");
        private readonly System.Windows.Forms.Form mainWindow;
        private readonly LookPanel panel;
        private DockableWindow? floating;
        private ScrollViewer? floatingScroll;
        private Rect? floatingBounds;
        private bool disposed;

        internal HostedTool(System.Windows.Forms.Form mainWindow, InspectorToolWpf tool, ICaptureOneDocument document, string database)
        {
            this.mainWindow = mainWindow;
            Tool = tool;
            panel = new LookPanel(mainWindow, document, database);
            Holder = new ContentControl { Content = panel, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            DockedContent = new UserControl { Content = Holder };
        }

        internal InspectorToolWpf Tool { get; }
        internal UserControl DockedContent { get; }
        private ContentControl Holder { get; }
        internal bool IsSpektraExportActive => !disposed && panel.IsSpektraExportActive;
        internal bool IsExporting => !disposed && panel.IsExporting;
        internal bool IsActiveForExport(bool editMultiple) => !disposed && panel.IsActiveForExport(editMultiple);
        internal void CheckSelectionAndSave()
        {
            panel.SyncSelection();
            panel.FlushLooks();
        }
        internal bool ProtectPendingLooks() => disposed || panel.ProtectPendingLooks();
        internal Task<bool> ExportAsync(Window? owner, bool editMultiple) =>
            panel.ExportAsync(useRecipeSize: true, owner, editMultiple);
        internal void SetExportBridgeStatus(string? message) => panel.SetExportBridgeStatus(message);
        internal InspectorToolHeaderViewModel CreateDockedHeader(ILifeCycleRegistrationService registration) =>
            new InspectorToolHeaderViewModelBuilder(StartupHook.ProductName, () => Tool.Document, registration)
                .DoShowActionMenu(new PanelActionMenu(() => floating is null ? "Float Tool" : "Show Floating Tool", ShowFloating))
                .Build();

        private void ShowFloating()
        {
            if (disposed)
                return;
            if (floating is not null)
            {
                if (floating.WindowState == WindowState.Minimized)
                    floating.WindowState = WindowState.Normal;
                floating.Activate();
                return;
            }

            DockableWindow? window = null;
            try
            {
                var scroll = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                };
                window = CreateFloatingWindow(scroll);
                if (floatingBounds is { } bounds)
                {
                    window.WindowStartupLocation = WindowStartupLocation.Manual;
                    window.Left = bounds.Left;
                    window.Top = bounds.Top;
                    window.Width = bounds.Width;
                    window.Height = bounds.Height;
                }
                else if (DockedContent.ActualWidth > 0)
                {
                    window.Width = Math.Max(window.MinWidth, DockedContent.ActualWidth);
                }

                new WindowInteropHelper(window).Owner = mainWindow.Handle;
                ElementHost.EnableModelessKeyboardInterop(window);
                window.Closed += OnFloatingClosed;
                floating = window;
                floatingScroll = scroll;
                Holder.Content = null;
                scroll.Content = panel;
                window.Show();
                StartupHook.Log($"{StartupHook.ProductName} panel is floating.");
            }
            catch (Exception error)
            {
                if (window is not null)
                {
                    if (ReferenceEquals(window, floating))
                        RestoreDock(window);
                    if (!window.IsClosed)
                        window.Close();
                }
                StartupHook.Log(error.ToString());
            }
        }

        private static DockableWindow CreateFloatingWindow(ScrollViewer scroll)
        {
            var layout = new DockPanel
            {
                Background = DefaultConstants.CaptureOneBrushPanelBackground,
                Margin = new Thickness(FloatingWindowConstants.BorderThicknessUnscaled),
            };
            // Like Capture One's FloatingToolsHostPanel, provide implicit styles to the tool's whole tree.
            layout.Resources.MergedDictionaries.Add(new SharedResourceDictionary { Source = CaptureOneStyle });
            var window = new DockableWindow
            {
                Title = StartupHook.ProductName,
                Content = layout,
                Background = DefaultConstants.CaptureOneBrushDarkSeparator,
                Width = 360,
                Height = 620,
                MinWidth = 290,
                MinHeight = 260,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            var header = new InspectorToolHeader
            {
                Background = DefaultConstants.CaptureOneBrushHeaderBackground,
                DataContext = new InspectorToolHeaderViewModel(StartupHook.ProductName,
                    argActionMenuGenerator: new PanelActionMenu(() => "Dock Tool", window.Close)),
            };
            // The Inspector header handles its action-menu input before it reaches this handler.
            header.MouseLeftButtonDown += (_, args) =>
            {
                if (args.LeftButton == MouseButtonState.Pressed && args.ClickCount == 1)
                {
                    args.Handled = true;
                    window.DragMove();
                }
            };
            DockPanel.SetDock(header, Dock.Top);
            layout.Children.Add(header);
            layout.Children.Add(scroll);
            return window;
        }

        private void OnFloatingClosed(object? sender, EventArgs args)
        {
            if (sender is not DockableWindow window || !ReferenceEquals(window, floating))
                return;
            var bounds = window.RestoreBounds;
            if (!bounds.IsEmpty)
                floatingBounds = bounds;
            RestoreDock(window);
            if (!disposed)
                StartupHook.Log($"{StartupHook.ProductName} panel is docked.");
        }

        private void RestoreDock(DockableWindow window)
        {
            window.Closed -= OnFloatingClosed;
            if (floatingScroll is not null)
                floatingScroll.Content = null;
            floatingScroll = null;
            floating = null;
            if (!disposed)
                Holder.Content = panel;
        }

        private sealed class PanelActionMenu(Func<string> label, Action action) : IActionMenuGenerator
        {
            public bool HasItems => true;

            public IEnumerable<IMenuItemViewModel> Generate()
            {
                yield return new CommandMenuItemViewModel(label(), action);
            }
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            panel.Dispose();
            if (floating is { } window)
            {
                RestoreDock(window);
                window.Close();
            }
            Holder.Content = null;
        }
    }

    private sealed partial class LookPanel : UserControl, IDisposable
    {
        private const string DefaultFilm = "kodak_portra_400";
        private const string DefaultPaper = "kodak_portra_endura";
        // New looks, Defaults and photos without a look (2026-11-23). "hanatos2026" is the reference's current method
        // (Python main: hanatos2025 with its fitted adaptation window, tests/ParityTests); "hanatos2025" is the archived
        // bandpass earlier looks were made with. Each look keeps the method it was saved with.
        private const string DefaultSpectralMethod = "hanatos2026";
        private readonly object window;
        private readonly ICaptureOneDocument document;
        private readonly ISelectedCollectionsProvider selections;
        private readonly VariantLookStore looks;
        private readonly ComboBox mode = new() { DisplayMemberPath = nameof(Stock.Label) };
        private readonly ComboBox film = new() { DisplayMemberPath = nameof(Stock.Label) };
        private readonly ComboBox paper = new() { DisplayMemberPath = nameof(Stock.Label) };
        private readonly ComboBox spectralMethod = new() { DisplayMemberPath = nameof(Stock.Label) };
        private readonly ComboBox filmPushPullMode = new() { DisplayMemberPath = nameof(Stock.Label) };
        private readonly CheckBox scanInvert = new() { Content = "Invert negative scan", Margin = new Thickness(0, 5, 0, 2) };
        private readonly TextBlock scanHint = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3), Visibility = Visibility.Collapsed };
        private readonly CheckBox enableSpektraFilm = new()
        {
            Content = "Enable " + StartupHook.BrandName,
            IsChecked = true,
            Margin = new Thickness(0, 0, 0, 6),
        };
        private readonly TextBlock status = new() { Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock saveError = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        private readonly TextBlock exportBridgeStatus = new()
        {
            TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 6),
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x60)),
        };
        // Look controls above and below "Look versions and copying". The versions section sits between them
        // and stays usable while Spektrafilm is off for the variant, as before.
        private readonly StackPanel controls = new() { IsEnabled = false };
        private readonly StackPanel sections = new() { IsEnabled = false };
        private readonly NativeSlider exposure;
        private readonly NativeSlider printExposure;
        private readonly NativeSlider strength;
        private readonly ComboBox grainType = new()
        {
            ItemsSource = new[] { "Fine Grain", "Silver Rich", "Soft", "Cubic Grain", "Tabular Grain", "Harsh" },
            SelectedIndex = 2,
        };
        private readonly NativeSlider grainImpact = new("Grain impact", 0, 100, 25, 1);
        private readonly NativeSlider grainSize = new("Grain granularity", 0, 100, 50, 1);
        private static readonly GrainControls DefaultGrain = new(2, 25, 50);
        private readonly StackPanel printing = new();
        private readonly Dictionary<string, (NativeSlider Slider, double Default)> development = new();
        private bool saveBlockedForInvalidLook;
        private readonly SpatialPreview opticalPreview;
        private Guid? displayedVariant;
        private readonly Button resumeExport;
        // Shown when the displayed photo has no look here but one in the folder of a moved or renamed catalog.
        private readonly Button movedLooks;
        private string? movedLooksFolder;
        private bool selectionQueued;
        private LookOptions? options;
        private VariantLook? copiedLook;
        private LookBaker? baker;
        private bool settingValues;
        private bool disposed;

        internal LookPanel(object window, ICaptureOneDocument document, string database)
        {
            this.window = window;
            this.document = document;
            selections = (ISelectedCollectionsProvider)document;
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FilmToolsForCaptureOne", "Variant looks");
            looks = new VariantLookStore(root, database);
            Foreground = Brushes.Gainsboro;
            enableSpektraFilm.IsEnabled = false;
            var layout = new StackPanel { Margin = new Thickness(8, 5, 8, 9) };
            layout.Children.Add(enableSpektraFilm);
            layout.Children.Add(MakeHistoryControls());
            layout.Children.Add(exportBridgeStatus);
            layout.Children.Add(saveError);
            movedLooks = Button("Copy looks from the moved catalog", CopyMovedLooksAsync);
            movedLooks.Visibility = Visibility.Collapsed;
            movedLooks.Margin = new Thickness(0, 4, 0, 4);
            movedLooks.ToolTip = "This catalog or session was moved or renamed, or its drive letter changed. Its earlier Spektrafilm looks are kept in a folder for the old location; this copies them here and leaves the old folder unchanged. Looks you already made here are not replaced.";
            layout.Children.Add(movedLooks);
            layout.Children.Add(controls);
            layout.Children.Add(MakeVersionControls());
            layout.Children.Add(sections);
            var batchExports = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            batchExports.Children.Add(Button("Export selected TIFFs…", () => ExportSelectedAsync(false, ".tif")));
            batchExports.Children.Add(Button("Export selected PNGs…", () => ExportSelectedAsync(false, ".png")));
            batchExports.Children.Add(Button("Export selected JPEGs…", () => ExportSelectedAsync(false, ".jpg")));
            batchExports.Children.Add(Button("Export selected DNGs…", () => ExportSelectedAsync(false, ".dng")));
            batchExports.Children.Add(Button("Cancel export", () => { opticalPreview?.CancelExport(); return Task.CompletedTask; }));
            resumeExport = Button("Resume interrupted export…", ResumeRecipeExportAsync);
            resumeExport.Visibility = Visibility.Collapsed;
            batchExports.Children.Add(resumeExport);
            layout.Children.Add(batchExports);
            UpdateResumeButton();
            Content = layout;
            AddStock(controls, "Processing mode", mode, () =>
            {
                if (settingValues)
                    return;
                SetModeControls();
                RefreshLook();
            });
            AddStock(controls, "Film stock", film, () =>
            {
                SetScanOptions();
                RefreshLook();
            });
            scanInvert.Checked += (_, _) => RefreshLook();
            scanInvert.Unchecked += (_, _) => RefreshLook();
            controls.Children.Add(scanInvert);
            controls.Children.Add(scanHint);
            AddStock(controls, "Print stock", paper);
            exposure = AddSlider(controls, "Film exposure", -8, 8, 0, 0.1, "EV");
            printExposure = AddSlider(controls, "Print exposure", -5, 5, 0, 0.1, "EV");
            printExposure.ToolTip = "More light on the print stock makes the result darker.";
            strength = AddSlider(controls, "Look strength", 0, 100, 100, 1, "%");
            opticalPreview = SpatialPreview.BuildForPanel(window, CurrentSettings, UseSelectedLook);
            opticalPreview.SettingsChanged += OnOpticalSettingsChanged;
            opticalPreview.RecommendedInputRequested += OnRecommendedInputRequested;
            opticalPreview.FilmModelChanged += UpdateDirWarning;
            exposure.ToolTip = "Light on the film. With Automatic film exposure on, the source is metered first and this is an offset from that. The print is timed for 0 EV, so more film exposure gives a denser negative and a brighter print; use Print exposure to bring brightness back while keeping the film's tone and grain.";
            opticalPreview.TrackSliderDrag(exposure);
            opticalPreview.TrackSliderDrag(printExposure);
            opticalPreview.TrackSliderDrag(strength);
            controls.Children.Add(opticalPreview.LiveControls);

            var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            buttons.Children.Add(Button("Defaults", () =>
            {
                if (options is not null && KeepVersionBefore("Before defaults"))
                {
                    ApplyControls(null, null);
                    SaveLookStep(discrete: true);
                    status.Text = string.Empty;
                }
                return Task.CompletedTask;
            }));
            controls.Children.Add(buttons);
            controls.Children.Add(opticalPreview.MeteringControls);
            // The message line takes no space while it is empty (below the collapsed versions section).
            status.Visibility = Visibility.Collapsed;
            StatusText.AddValueChanged(status, OnStatusTextChanged);
            sections.Children.Add(status);
            sections.Children.Add(MakeFilmControls());
            sections.Children.Add(MakePrintControls());
            sections.Children.Add(MakeAdvancedControls());
            sections.Children.Add(MakeGrainControls());
            sections.Children.Add(opticalPreview.Element);
            sections.Children.Add(MakeFileControls());

            enableSpektraFilm.Checked += (_, _) => OnEnabledChanged();
            enableSpektraFilm.Unchecked += (_, _) => OnEnabledChanged();
            Loaded += (_, _) => LoadStocks();

            selections.CollectionsSelectionChanged += OnCollectionsSelectionChanged;
        }

        public Guid Id { get; } = Guid.NewGuid();

        // The descriptor holds a strong reference to the handler; it is removed again in Dispose.
        private static readonly System.ComponentModel.DependencyPropertyDescriptor StatusText =
            System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
        private void OnStatusTextChanged(object? sender, EventArgs args) =>
            status.Visibility = string.IsNullOrEmpty(status.Text) ? Visibility.Collapsed : Visibility.Visible;

        // Shown when Capture One's own Export could not be routed through Spektrafilm (upstream FIX-01):
        // without it, C1 would silently export files without the film look.
        internal void SetExportBridgeStatus(string? message)
        {
            exportBridgeStatus.Text = message ?? string.Empty;
            exportBridgeStatus.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }

        // Capture One 16.9: selection synchronization uses the existing timer and collection event.

        private void OnCollectionsSelectionChanged(object? sender, CollectionsSelectionChangedEventArgs args) => ScheduleSelectionSync();

        private void ScheduleSelectionSync()
        {
            // Read the primary variant after the host finishes processing the selection.
            if (disposed || selectionQueued)
                return;
            selectionQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
            {
                selectionQueued = false;
                SyncSelection();
            }));
        }

        private bool IsSpektraFilmEnabled => !disposed && enableSpektraFilm.IsChecked == true;

        private void OnEnabledChanged()
        {
            if (settingValues)
                return;
            UpdateEnabledState();
            SaveLook();
        }

        private void UpdateEnabledState()
        {
            if (disposed)
                return;
            var enabled = displayedVariant is not null && IsSpektraFilmEnabled;
            enableSpektraFilm.IsEnabled = displayedVariant is not null && StocksReady;
            controls.IsEnabled = sections.IsEnabled = enabled && StocksReady;
            opticalPreview.SetEnabled(enabled);
            UpdateHistoryButtons();
        }

        public void Dispose()
        {
            if (disposed)
                return;
            FlushLooks();
            disposed = true;
            selections.CollectionsSelectionChanged -= OnCollectionsSelectionChanged;
            StatusText.RemoveValueChanged(status, OnStatusTextChanged);

            opticalPreview.SettingsChanged -= OnOpticalSettingsChanged;
            opticalPreview.RecommendedInputRequested -= OnRecommendedInputRequested;
            opticalPreview.Dispose();
            baker?.Dispose();
            baker = null;
        }

        private FrameworkElement MakeFilmControls()
        {
            var section = new StackPanel();
            AddDevelopment(section, "film_gamma", "Negative gamma", 0.1, 2, 1, 0.05);
            filmPushPullMode.ItemsSource = new[] { new Stock("standard", "Standard"), new Stock("experimental", "Experimental") };
            filmPushPullMode.SelectedIndex = 0;
            AddStock(section, "Push / pull model", filmPushPullMode);
            AddDevelopment(section, "film_push_pull", "Film push / pull", -2, 2, 0, 0.1, "EV");
            AddDevelopment(section, "negative_bleach_bypass", "Negative bleach bypass", 0, 1, 0, 0.01, "%", 100);
            AddDevelopment(section, "negative_leuco_cyan", "Negative leuco cyan", 0, 2, 1, 0.05);
            AddDevelopment(section, "uv_cutoff", "UV cutoff", 380, 450, 380, 1, "nm");
            AddDevelopment(section, "ir_cutoff", "IR cutoff", 600, 780, 780, 1, "nm");
            return Section("Film development", section);
        }

        private FrameworkElement MakePrintControls()
        {
            AddDevelopment(printing, "print_gamma", "Print gamma", 0.1, 2, 1, 0.05);
            AddDevelopment(printing, "print_push_pull", "Print push / pull", -2, 2, 0, 0.1, "EV");
            AddDevelopment(printing, "print_shadows", "Print shadow shape", -1, 1, 0, 0.05);
            AddDevelopment(printing, "print_highlights", "Print highlight shape", -1, 1, 0, 0.05);
            AddDevelopment(printing, "print_exhaustion", "Developer exhaustion", 0, 1, 0, 0.01, "%", 100);
            development["print_exhaustion"].Slider.ToolTip = "Changes the paper development curve in Print Simulation. Zero preserves the original paper profile.";
            AddDevelopment(printing, "print_bleach_bypass", "Print bleach bypass", 0, 1, 0, 0.01, "%", 100);
            AddDevelopment(printing, "cyan_filter", "Cyan filtration", 0, 120, 0, 1, "CC");
            AddDevelopment(printing, "magenta_filter", "Magenta filter shift", -60, 60, 0, 1, "CC");
            AddDevelopment(printing, "yellow_filter", "Yellow filter shift", -60, 60, 0, 1, "CC");
            // Useful values are 0.005-0.03 and 0.05 is already very strong, so the field needs thousandths.
            AddDevelopment(printing, "preflash_exposure", "Preflash exposure", 0, 1, 0, 0.001);
            AddDevelopment(printing, "preflash_magenta", "Preflash magenta", -60, 60, 0, 1, "CC");
            AddDevelopment(printing, "preflash_yellow", "Preflash yellow", -60, 60, 0, 1, "CC");
            return Section("Enlarger and print", printing);
        }

        private FrameworkElement MakeAdvancedControls()
        {
            var section = new StackPanel();
            AddStock(section, "Spectral reconstruction", spectralMethod);
            spectralMethod.ToolTip = "How the RGB image is turned into the light spectrum that exposes the film. Hanatos 2026 "
                + "(default for new looks) matches the current Spektrafilm reference; Hanatos 2025 is the archived calibration "
                + "looks made before 23 November 2026 use. They differ mainly in very saturated colours. Each look keeps its method.";
            AddDevelopment(section, "dir_amount", "DIR coupler amount", 0, 2, 1, 0.05);
            AddDevelopment(section, "dir_same_layer", "Same-layer inhibition", 0, 2, 1, 0.05);
            AddDevelopment(section, "dir_interlayer", "Interlayer inhibition", 0, 2, 1, 0.05);
            section.Children.Add(dirWarning);
            foreach (var key in new[] { "dir_amount", "dir_same_layer", "dir_interlayer" })
                development[key].Slider.ValueChanged += (_, _) => UpdateDirWarning(); // Also when a look is loaded.
            return Section("Spectral reconstruction and DIR", section);
        }

        private readonly TextBlock dirWarning = new()
        {
            TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 2),
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x60)),
        };

        private void UpdateDirWarning()
        {
            var text = DirToneGuard.Warning(development["dir_amount"].Slider.Value,
                development["dir_same_layer"].Slider.Value, development["dir_interlayer"].Slider.Value,
                opticalPreview?.FilmModelGuardsDir == true);
            dirWarning.Text = text ?? "";
            dirWarning.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        }

        private void AddDevelopment(StackPanel section, string key, string label, double min, double max,
            double initial, double step, string unit = "", double displayScale = 1)
        {
            var slider = AddSlider(section, label, min, max, initial, step, unit, displayScale);
            development.Add(key, (slider, initial));
        }

        private static Expander Section(string header, FrameworkElement content) =>
            new() { Header = header, Content = content, Margin = new Thickness(0, 7, 0, 0) };

        private FrameworkElement MakeFileControls()
        {
            var section = new StackPanel();
            var presets = new WrapPanel { Margin = new Thickness(0, 5, 0, 3) };
            presets.Children.Add(Button("Save preset…", () =>
            {
                if (CurrentSettings() is { } settings)
                {
                    var preset = new PanelPreset(settings, opticalPreview.CaptureSettings(), 1);
                    var picker = new SaveFileDialog { Filter = "Spektrafilm preset|*.spektra.json", FileName = "Film look.spektra.json" };
                    if (picker.ShowDialog() == true)
                    {
                        Run(() => PanelFiles.WritePreset(picker.FileName, preset), "Preset saved.");
                    }
                }
                return Task.CompletedTask;
            }));
            presets.Children.Add(Button("Load preset…", LoadPresetAsync));
            section.Children.Add(presets);

            var export = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
            export.Children.Add(Button("Exposure strips…", () => { opticalPreview.ShowExposureComparison(ev => exposure.Value = ev); return Task.CompletedTask; }));
            export.Children.Add(Button("Export with Spektrafilm…", () => ExportAsync(useRecipeSize: false)));
            foreach (var size in new[] { 33, 65 })
            {
                var lut = Button($"Export color-only {size}-point LUT…", () => ExportLutAsync(size));
                lut.ToolTip = "sRGB to sRGB, using the film simulation, finishing color/tone and Look strength. Excludes micro contrast, adaptive exposure, grain, spatial optics, geometry and C1 input preparation.";
                export.Children.Add(lut);
            }
            export.Children.Add(Button("Export Hald CLUT…", () => ExportLutFileAsync(64, "hald")));
            export.Children.Add(Button("Export OCIO bundle…", () => ExportLutFileAsync(65, "ocio")));
            section.Children.Add(export);
            section.Children.Add(new TextBlock
            {
                Text = "Capture One's Export uses its active export recipes: location, file names, format (including PSD and PSB), ICC profile, size, metadata, watermark and output sharpening. The files are listed before the export starts, and an interrupted export can be resumed.\nThe panel's export buttons use the panel's settings instead: TIFF/PNG 16-bit or JPEG 8-bit in the selected export color space. Linear DNG uses Rec.2020 with highlight headroom. DaVinci Intermediate and V-Log are written without an embedded profile; tag the colour space in the receiving application. Selected variants keep their own saved settings; disabled variants use C1 development. Batch creates new filenames and retains completed files if canceled. Up to 100 MP / 16384 px per side; Native Grain Synthesis up to 32 MP.\nColor-only LUTs map sRGB input to sRGB and include finishing color and tone. They exclude micro contrast, adaptive exposure, spatial effects and geometry, so they do not reproduce the complete Viewer look.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 2),
            });
            return Section("Presets and LUT export", section);
        }

        internal bool IsSpektraExportActive => IsActiveForExport(true);
        internal bool IsExporting => opticalPreview.IsExporting;
        internal bool IsActiveForExport(bool editMultiple)
        {
            if (!SyncSelection()) return false;
            if (IsSpektraFilmEnabled) return true;
            if (!editMultiple || selections.PrimarySelectedCollectionSelection?.SelectedVariants is not { Count: > 1 } selected) return false;
            return selected.Any(variant => (looks.Read(variant.UUID)?.Enabled ?? true));
        }
        // The native Export goes through Capture One's recipes when this Capture One offers what that needs;
        // otherwise (another version) it keeps the previous behaviour with a save dialog.
        internal Task<bool> ExportAsync(bool useRecipeSize, Window? owner = null, bool editMultiple = true) =>
            !IsActiveForExport(editMultiple) ? Task.FromResult(false)
            : useRecipeSize && RecipeExportAvailable() ? RecipeExportAsync(owner, editMultiple, null)
            : editMultiple && selections.PrimarySelectedCollectionSelection?.SelectedVariants?.Count > 1
                ? ExportSelectedAsync(useRecipeSize, opticalPreview.ExportExtensionFor(HostImageSource.ActiveRecipe(window)?.FormatId), owner)
                : opticalPreview.ExportAsync(useRecipeSize, owner);

        private IReadOnlyList<BatchExportSupport.Entry> CaptureBatch(bool useRecipeSize, bool primaryOnly = false,
            IReadOnlyCollection<Guid>? only = null)
        {
            SaveLook(); FlushLooks();
            var look = new LookSettings(DefaultFilm, DefaultPaper, 0, 0, 1, "print", DefaultSpectralMethod, false,
                development.ToDictionary(x => x.Key, x => x.Value.Default));
            using var context = SpatialPreview.BuildForPanel(window, () => look, _ => false);
            var optics = context.CaptureSettings();
            return BatchExportSupport.Capture(window, looks, _ => new VariantLook(look, optics, true, Grain: DefaultGrain),
                useRecipeSize, primaryOnly, only);
        }

        private bool recipeExportWarned;
        private bool RecipeExportAvailable()
        {
            if (HostRecipeWriter.Unavailable is not { } missing) return true;
            if (!recipeExportWarned)
            {
                recipeExportWarned = true;
                StartupHook.Log("Recipe export is unavailable in this Capture One version; using the save dialog. Missing: " + missing);
            }
            return false;
        }

        private RecipeExportPlanning.LookDescription? DescribeLook(VariantLook saved)
        {
            if (options is null) return null;
            var look = saved.Look;
            string Label(Stock[] stocks, string id) => stocks.FirstOrDefault(x => x.Id == id)?.Label ?? id;
            return new RecipeExportPlanning.LookDescription(Label(options.Films, look.Film), Label(options.Papers, look.Paper),
                Label(options.Modes, look.Mode), look.Exposure, look.PrintExposure, look.Strength, null,
                StartupHook.ProductName + " " + typeof(LookPanel).Assembly.GetName().Version?.ToString(3));
        }

        private string? CatalogPath()
        {
            try { return HostProfile.CatalogPath(HostProfile.ReadSelection(window).Manager); }
            catch (InvalidOperationException) { return null; }
        }

        private void UpdateResumeButton()
        {
            var journal = CatalogPath() is { } catalog ? RecipeExportJournal.Load(catalog) : null;
            resumeExport.Visibility = journal is null ? Visibility.Collapsed : Visibility.Visible;
            resumeExport.Content = journal is null ? "Resume interrupted export…" : $"Resume interrupted export ({journal.RemainingCount} files)…";
        }

        private Task ResumeRecipeExportAsync() =>
            CatalogPath() is { } catalog && RecipeExportJournal.Load(catalog) is { } journal
                ? RecipeExportAsync(null, true, journal)
                : Task.Run(() => Dispatcher.Invoke(UpdateResumeButton));

        /// <summary>
        /// Export through Capture One's recipes: the plan (paths, collisions, metadata) is built here on the UI
        /// thread, shown for confirmation, recorded in the journal and then rendered and written.
        /// </summary>
        private async Task<bool> RecipeExportAsync(Window? owner, bool editMultiple, ExportJournal? resume)
        {
            if (!SyncSelection()) return false;
            RecipeExportPlan? plan = null;
            var started = false;
            try
            {
                var multiple = editMultiple && selections.PrimarySelectedCollectionSelection?.SelectedVariants?.Count > 1;
                var ids = resume?.Remaining.Select(item => item.VariantId).Distinct().ToArray();
                var entries = CaptureBatch(true, primaryOnly: resume is null && !multiple, only: ids);
                plan = RecipeExportPlanner.Build(window, entries, DescribeLook, resume);
                if (ids is not null && entries.Count < ids.Length)
                    plan = plan with { Notes = plan.Notes.Append($"{ids.Length - entries.Count} images of the interrupted export are not in the collection shown in the browser; open that collection to export them.").ToArray() };
                var interrupted = resume is null ? RecipeExportJournal.Load(plan.Catalog) : null;
                var owning = owner ?? Window.GetWindow(this);
                if (resume is not null || interrupted is not null || PanelPreferences.Current.ConfirmRecipeExport || !plan.Writing.Any())
                {
                    var choice = RecipeExportWindow.Confirm(owning, plan, interrupted);
                    if (choice == RecipeExportChoice.Discard)
                    {
                        RecipeExportJournal.RemoveLeftovers(resume!);
                        RecipeExportJournal.Delete(plan.Catalog);
                        UpdateResumeButton();
                        return false;
                    }
                    if (choice != RecipeExportChoice.Export) return false;
                }
                if (!plan.Writing.Any()) return false;
                // The journal about to be replaced may list temporary files a crashed export left behind.
                if ((resume ?? interrupted) is { } previous) RecipeExportJournal.RemoveLeftovers(previous);
                RecipeExportJournal.Save(plan.Journal);
                started = true;
                RecipeExportResult result;
                using (ExportProgressWindow.Show(owning, opticalPreview.ExportStatus, opticalPreview.CancelExport))
                    result = await opticalPreview.RunRecipeExportAsync(plan, journal => RecipeExportJournal.Save(journal));
                if (result.Journal.Finished) RecipeExportJournal.Delete(plan.Catalog);
                UpdateResumeButton();
                foreach (var note in result.Notes) StartupHook.Log("Recipe export: " + note);
                if (result.Failed > 0)
                    MessageBox.Show(owning, string.Join(Environment.NewLine, result.Errors.Take(8))
                        + (result.Errors.Count > 8 ? Environment.NewLine + "…" : "")
                        + Environment.NewLine + Environment.NewLine + "The files that failed can be exported again with “Resume interrupted export”.",
                        StartupHook.ProductName, MessageBoxButton.OK, MessageBoxImage.Warning);
                return !result.Cancelled && result.Failed == 0;
            }
            catch (Exception error) { Report(error); return false; }
            finally
            {
                // An export that did not start (the list was cancelled) takes no numbers from Capture One's counter.
                if (!started && plan?.ReleaseNumbers is { } release)
                    try { release(); }
                    catch (Exception error) { StartupHook.Log("The export counter could not be put back: " + error.GetBaseException().Message); }
            }
        }

        private async Task<bool> ExportSelectedAsync(bool useRecipeSize, string extension, Window? owner = null)
        {
            if (!SyncSelection()) return false;
            try
            {
                var entries = CaptureBatch(useRecipeSize);
                var picker = new OpenFolderDialog { Title = $"Export {entries.Count} selected variants · {extension.TrimStart('.').ToUpperInvariant()} with saved settings" };
                if ((owner is { IsVisible: true } ? picker.ShowDialog(owner) : picker.ShowDialog(Window.GetWindow(this))) != true) return false;
                using var progress = ExportProgressWindow.Show(owner ?? Window.GetWindow(this), opticalPreview.ExportStatus, opticalPreview.CancelExport);
                var result = await opticalPreview.ExportBatchAsync(entries, picker.FolderName, extension, useRecipeSize);
                return !result.Cancelled;
            }
            catch (Exception error) { Report(error); return false; }
        }
        internal bool StocksReady => !disposed && options is not null;
        private async Task LoadPresetAsync()
        {
            if (!SyncSelection())
                return;
            var variant = displayedVariant;
            try
            {
                var picker = new OpenFileDialog { Filter = "Spektrafilm preset|*.spektra.json", CheckFileExists = true };
                if (picker.ShowDialog() != true)
                    return;
                var preset = await Task.Run(() => PanelFiles.ReadPreset<PanelPreset>(picker.FileName));
                if (preset.SchemaVersion is < 0 or > 1)
                    throw new InvalidDataException("This preset version requires a different Spektrafilm build.");
                if (preset.Look is not { } settings)
                    throw new InvalidDataException("The preset contains no look settings.");
                if (!SyncSelection() || displayedVariant != variant)
                    return;
                if (!KeepVersionBefore("Before loading preset " + VersionSourceName(picker.FileName)))
                    return;
                ApplyControls(settings, preset.Optics);
                SaveLookStep(discrete: true);
                status.Text = "Preset loaded. The previous look is kept as a version; Undo also restores it.";
            }
            catch (Exception error)
            {
                Report(error);
            }
        }

        private sealed record PanelPreset(LookSettings Look, SpatialPreview.Settings? Optics, int SchemaVersion = 0);

        private FrameworkElement MakeVersionControls()
        {
            var actions = new WrapPanel();
            actions.Children.Add(Button("Save look version…", SaveVersionAsync));
            actions.Children.Add(Button("Restore look version…", RestoreVersionAsync));
            actions.Children.Add(Button("Copy look", () => {
                if (!SyncSelection()) return Task.CompletedTask;
                try
                {
                    SaveLook();
                    var current = looks.Read(displayedVariant!.Value)!;
                    SpatialPreview.RejectRemovedEngine(current.Optics);
                    SpatialPreview.RejectUnknownFilmModel(current.Optics);
                    copiedLook = BatchExportSupport.Clone(current); status.Text = "Spektrafilm look copied. Select target variants and apply it.";
                }
                catch (Exception error) { Report(error); }
                return Task.CompletedTask;
            }));
            actions.Children.Add(Button("Apply copied look to selected", ApplyCopiedLookAsync));
            return Section("Look versions and copying", actions);
        }

        // Keeps the previous look restorable before switching it to scene-linear input.
        private void OnRecommendedInputRequested()
        {
            if (!SyncSelection() || displayedVariant is not { } id || !IsSpektraFilmEnabled) return;
            try
            {
                SaveLook();
                if (looks.Read(id) is { } previous)
                    LookVersions.Save(looks.VersionsFolder(id), id, "Before recommended input", previous);
                opticalPreview.ApplyRecommendedInput();
                SaveLookStep(discrete: true);
                status.Text = "Input switched to Neutral for film · ProPhoto RGB. The previous look is saved as version \"Before recommended input\". Brightness and contrast will change; readjust Film exposure.";
            }
            catch (Exception error) { Report(error); }
        }

        private Task SaveVersionAsync()
        {
            if (!SyncSelection() || displayedVariant is not { } id) return Task.CompletedTask;
            var name = LookVersionDialogs.Name(Window.GetWindow(this));
            if (name is null || !SyncSelection() || displayedVariant != id) return Task.CompletedTask;
            try { SaveLook(); LookVersions.Save(looks.VersionsFolder(id), id, name, looks.Read(id)!); status.Text = "Saved look version: " + name; }
            catch (Exception error) { Report(error); }
            return Task.CompletedTask;
        }

        private async Task RestoreVersionAsync()
        {
            if (!SyncSelection() || displayedVariant is not { } id) return;
            try
            {
                var folder = looks.VersionsFolder(id);
                var versions = await Task.Run(() => LookVersions.Read(folder, id, StartupHook.Log));
                if (disposed || !SyncSelection() || displayedVariant != id) return;
                if (versions.Count == 0) { status.Text = "No saved look versions for this variant."; return; }
                var version = LookVersionDialogs.Choose(Window.GetWindow(this), versions);
                if (version is null || !SyncSelection() || displayedVariant != id) return;
                SaveLook(); LookVersions.Save(folder, id, "Before restoring " + version.Name[..Math.Min(90, version.Name.Length)], looks.Read(id)!);
                var saved = version.Settings;
                ApplyControls(saved.Look, saved.Optics, saved.Enabled, saved.Grain); SaveLookStep(discrete: true);
                status.Text = "Restored look version: " + version.Name;
            }
            catch (Exception error) { Report(error); }
        }

        private Task ApplyCopiedLookAsync()
        {
            if (!SyncSelection()) return Task.CompletedTask;
            if (copiedLook is null) { status.Text = "Copy a Spektrafilm look first."; return Task.CompletedTask; }
            var applied = 0;
            try
            {
                SpatialPreview.RejectRemovedEngine(copiedLook.Optics);
                SpatialPreview.RejectUnknownFilmModel(copiedLook.Optics);
                var entries = CaptureBatch(false);
                foreach (var entry in entries)
                    LookVersions.Save(looks.VersionsFolder(entry.Target.VariantId), entry.Target.VariantId, "Before applying copied look", entry.Settings);
                foreach (var entry in entries)
                {
                    looks.Queue(entry.Target.VariantId, BatchExportSupport.Clone(copiedLook)); looks.Flush(); applied++;
                }
                var active = looks.Read(displayedVariant!.Value)!;
                ApplyControls(active.Look, active.Optics, active.Enabled, active.Grain);
                RecordDisplayedLook();
                status.Text = $"Applied Spektrafilm look to {applied} variants. Previous looks are saved in each variant’s versions.";
            }
            catch (Exception error) { Report(new InvalidOperationException($"Applied to {applied} variants before stopping. Previous looks remain in saved versions. {error.Message}", error)); }
            return Task.CompletedTask;
        }

        private void ApplyControls(LookSettings? look, SpatialPreview.Settings? optics, bool? enabled = null,
            GrainControls? grain = null)
        {
            SpatialPreview.RejectRemovedEngine(optics);
            if (look is not null)
                ValidatePreset(look, options ?? throw new InvalidOperationException("The stock catalog is still loading."));
            settingValues = true;
            try
            {
                opticalPreview.LoadSettings(optics ?? (look is null ? null : new SpatialPreview.Settings(new(), new(), new())));
                if (look is null)
                    SetDefaults();
                else
                    SetPreset(look);
                // Old presets can predate the entire optics section. Missing optics
                // means historical defaults when a saved look was supplied.
                if (enabled is { } useFilmTools)
                {
                    enableSpektraFilm.IsChecked = useFilmTools;
                    SetGrainControls(grain ?? DefaultGrain);
                }
            }
            finally
            {
                settingValues = false;
            }
            saveBlockedForInvalidLook = false;
            UpdateEnabledState();
        }

        internal bool SyncSelection()
        {
            if (!StocksReady)
                return false;
            Guid variant;
            try
            {
                variant = HostProfile.ReadSelection(window).Variant.UUID;
            }
            catch (InvalidOperationException)
            {
                variant = Guid.Empty;
            }
            if (variant != Guid.Empty)
            {
                UseSelectedLook(variant);
                return true;
            }
            if (displayedVariant is not null)
            {
                displayedVariant = null;
                UpdateEnabledState();
            }
            return false;
        }

        private bool UseSelectedLook(Guid variant)
        {
            if (!StocksReady || variant == Guid.Empty)
                return false;
            if (displayedVariant != variant)
            {
                status.Text = string.Empty;
                FlushLooks();
                displayedVariant = variant;
                try
                {
                    var saved = looks.Read(variant);
                    var recovered = looks.RecoveryNotice;
                    ApplyControls(saved?.Look, saved?.Optics, saved?.Enabled ?? true, saved?.Grain);
                    RecordDisplayedLook();
                    if (recovered is not null) status.Text = recovered;
                    ShowMovedLooks(saved is null ? looks.MovedLooksFolder(variant) : null);
                }
                catch (Exception error)
                {
                    looks.ProtectUnloadable(variant);
                    ShowMovedLooks(null);
                    ApplyControls(null, null, false);
                    saveBlockedForInvalidLook = true;
                    status.Text = $"Could not load this image’s Spektrafilm look. Its saved parameters are protected; restore the missing dependency, load a valid preset or use Defaults (a copy of the unreadable file is kept before it is replaced). {error.Message}";
                    StartupHook.Log(status.Text);
                }
            }
            return IsSpektraFilmEnabled;
        }

        private void ShowMovedLooks(string? folder)
        {
            movedLooksFolder = folder;
            movedLooks.Visibility = folder is null ? Visibility.Collapsed : Visibility.Visible;
            if (folder is not null)
                status.Text = "This photo has a Spektrafilm look saved before its catalog was moved or renamed. Use \"Copy looks from the moved catalog\" to bring the looks here.";
        }

        private Task CopyMovedLooksAsync()
        {
            if (movedLooksFolder is not { } folder || displayedVariant is not { } variant)
                return Task.CompletedTask;
            FlushLooks();
            int copied;
            try { copied = looks.CopyLooksFrom(folder); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                status.Text = "Not all looks could be copied from the moved catalog; the ones copied are kept, nothing was replaced. " + error.Message;
                StartupHook.Log(status.Text);
                return Task.CompletedTask;
            }
            // Read the displayed photo again from this catalog's folder.
            displayedVariant = null;
            UseSelectedLook(variant);
            if (movedLooks.Visibility != Visibility.Visible)
                status.Text = copied == 1 ? "Copied 1 look from the moved catalog." : $"Copied {copied} looks from the moved catalog.";
            return Task.CompletedTask;
        }

        /// <summary>Saves pending edits; false when one could be kept neither in its look file nor in a recovery copy.</summary>
        internal bool ProtectPendingLooks()
        {
            FlushLooks();
            return !looks.HasUnprotectedPending;
        }

        private void SaveLook() => SaveLookStep(discrete: false);

        // discrete: the change is one whole action (Defaults, preset, version), recorded as its own undo step.
        // A separate name keeps SaveLook parameterless for callers that find it by name.
        private void SaveLookStep(bool discrete)
        {
            if (saveBlockedForInvalidLook) return;
            if (settingValues || displayedVariant is not { } variant || CaptureLook() is not { } look)
                return;
            looks.Queue(variant, look);
            FlushLooks();
            RecordHistory(variant, look, discrete);
        }

        // The look shown by the controls. The key belongs to the displayed controls, even if the host selection
        // has just changed.
        private VariantLook? CaptureLook()
        {
            if (!StocksReady || displayedVariant is null || CurrentSettings() is not { } look)
                return null;
            look = look with { ScanInvert = scanInvert.IsChecked == true };
            var grain = new GrainControls(grainType.SelectedIndex, grainImpact.Value, grainSize.Value);
            return new VariantLook(look, opticalPreview.CaptureSettings(), IsSpektraFilmEnabled, Grain: grain);
        }

        internal void FlushLooks()
        {
            try
            {
                looks.Flush();
                saveError.Visibility = Visibility.Collapsed;
            }
            catch (Exception error)
            {
                var message = $"Spektrafilm could not save your look. {error.Message}";
                if (saveError.Visibility != Visibility.Visible || saveError.Text != message)
                    StartupHook.Log(message);
                saveError.Text = message;
                saveError.Visibility = Visibility.Visible;
            }
        }

        private void SetPreset(LookSettings settings)
        {
            var available = options ?? throw new InvalidOperationException("The available stocks are still loading.");
            static Stock Find(Stock[] stocks, string id) => stocks.FirstOrDefault(option => option.Id == id)
                ?? throw new InvalidDataException("This preset uses a stock or mode that is unavailable.");
            var selectedMode = Find(available.Modes, settings.Mode);
            var selectedFilm = Find(available.Films, settings.Film);
            var selectedPaper = Find(available.Papers, settings.Paper);
            var selectedMethod = Find(available.SpectralMethods, settings.SpectralMethod);
            if (selectedMode.Id == "print" && selectedFilm.Kind == "positive")
                throw new InvalidDataException("This positive film needs scan mode.");

            var previousSetting = settingValues;
            settingValues = true;
            try
            {
                mode.SelectedItem = selectedMode;
                SetModeControls();
                film.SelectedItem = selectedFilm;
                paper.SelectedItem = selectedPaper;
                spectralMethod.SelectedItem = selectedMethod;
                filmPushPullMode.SelectedIndex = settings.FilmPushPullMode == "experimental" ? 1 : 0;
                scanInvert.IsChecked = settings.ScanInvert;
                exposure.Value = settings.Exposure;
                printExposure.Value = settings.PrintExposure;
                strength.Value = settings.Strength * 100;
                foreach (var (key, entry) in development)
                    entry.Slider.Value = settings.Development?.GetValueOrDefault(key, entry.Default) ?? entry.Default;
            }
            finally
            {
                settingValues = previousSetting;
            }
        }

        private static void ValidatePreset(LookSettings settings, LookOptions available)
        {
            static Stock Find(Stock[] stocks, string id) => stocks.FirstOrDefault(x => x.Id == id)
                ?? throw new InvalidDataException("This preset uses a stock or mode that is unavailable.");
            Find(available.Modes, settings.Mode);
            var film = Find(available.Films, settings.Film);
            Find(available.Papers, settings.Paper);
            Find(available.SpectralMethods, settings.SpectralMethod);
            if (settings.Mode == "print" && film.Kind == "positive")
                throw new InvalidDataException("This positive film needs scan mode.");
        }

        private Task ExportLutAsync(int resolution) => ExportLutFileAsync(resolution, "cube");
        private async Task ExportLutFileAsync(int resolution, string kind)
        {
            if (!IsSpektraFilmEnabled || CurrentSettings() is not { } settings)
                return;
            try
            {
                var revision = opticalPreview.SettingsRevision; var variant = displayedVariant;
                string destination;
                if (kind == "ocio") {
                    var picker = new OpenFolderDialog { Title = "Choose a parent folder for a new Spektrafilm OCIO bundle" };
                    if (picker.ShowDialog() != true) return;
                    destination = picker.FolderName;
                } else {
                    var picker = new SaveFileDialog { Filter = kind == "hald" ? "Hald CLUT level 8 PNG|*.png" : "3D cube LUT|*.cube|Autodesk 10-bit LUT|*.3dl",
                        FileName = $"Spektrafilm color LUT {resolution}" + (kind == "hald" ? ".png" : ".cube"), AddExtension = true, OverwritePrompt = true };
                    if (picker.ShowDialog() != true) return;
                    destination = picker.FileName;
                }
                if (displayedVariant != variant || opticalPreview.SettingsRevision != revision)
                    throw new InvalidOperationException("The selected look changed while choosing a destination. Start LUT export again.");
                status.Text = "Baking the color LUT…";
                var cube = await opticalPreview.ExportColorLutAsync(settings, resolution, CancellationToken.None);
                if (!disposed)
                {
                    if (kind == "ocio") destination = ColorLutFiles.SaveBundle(destination, cube, "srgb");
                    else if (kind == "hald") ColorLutFiles.SaveHald(destination, cube);
                    else PanelFiles.WriteAtomically(destination, stream => {
                        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), leaveOpen: true);
                        writer.Write(Path.GetExtension(destination).Equals(".3dl", StringComparison.OrdinalIgnoreCase) ? ColorLutFiles.ThreeDl(cube) : cube);
                    });
                    status.Text = "LUT exported: " + destination;
                }
            }
            catch (Exception error)
            {
                if (!disposed)
                    Report(error);
            }
        }

        private FrameworkElement MakeGrainControls()
        {
            var section = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            section.Children.Add(new TextBlock
            {
                Text = "Capture One's own Film Grain, not Spektrafilm grain. These sliders change nothing until you click Apply grain; it then writes C1 grain onto the variant, before the film simulation. For simulated film grain use Spektrafilm grain › Grain model.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
            });
            AutomationProperties.SetName(grainType, "Grain type");
            section.Children.Add(grainType);
            section.Children.Add(grainImpact);
            section.Children.Add(grainSize);
            grainType.SelectionChanged += (_, _) =>
            {
                grainSize.IsEnabled = grainType.SelectedIndex != 0;
                SaveLook();
            };
            grainImpact.ValueChanged += (_, _) => SaveLook();
            grainSize.ValueChanged += (_, _) => SaveLook();

            var buttons = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
            buttons.Children.Add(Button("Apply grain", () =>
            {
                if (SyncSelection() && displayedVariant is { } variant && IsSpektraFilmEnabled)
                    Run(() => NativeGrain.Apply(window, looks.GrainRecords, variant,
                        grainType.SelectedIndex, grainImpact.Value, grainSize.Value), "Capture One Grain applied.");
                return Task.CompletedTask;
            }));
            buttons.Children.Add(Button("Restore grain", () =>
            {
                Run(() =>
                {
                    var current = NativeGrain.Read(window);
                    if (!NativeGrain.HasApplied(window, looks.GrainRecords, current.VariantId))
                        throw new InvalidOperationException("No grain adjustment from this panel is active.");
                    NativeGrain.Restore(window, looks.GrainRecords, current.VariantId);
                    var original = NativeGrain.Read(window);
                    if (SyncSelection() && displayedVariant == original.VariantId)
                    {
                        SetGrainControls(new GrainControls(original.Type, original.Impact, original.Granularity));
                        SaveLook();
                    }
                }, "Prior grain restored.");
                return Task.CompletedTask;
            }));
            section.Children.Add(buttons);
            return new Expander { Header = "Capture One Grain (applied to the C1 variant)", Content = section, Margin = new Thickness(0, 9, 0, 0) };
        }

        private void SetGrainControls(GrainControls grain)
        {
            var wasSetting = settingValues;
            settingValues = true;
            try
            {
                grainType.SelectedIndex = Math.Clamp(grain.Type, 0, grainType.Items.Count - 1);
                grainImpact.Value = grain.Impact;
                grainSize.Value = grain.Granularity;
            }
            finally
            {
                settingValues = wasSetting;
            }
        }

        private void AddStock(StackPanel section, string label, ComboBox chooser, Action? changed = null)
        {
            section.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 4, 0, 2) });
            AutomationProperties.SetName(chooser, label);
            section.Children.Add(chooser);
            chooser.SelectionChanged += (_, _) => (changed ?? RefreshLook)();
        }

        private NativeSlider AddSlider(StackPanel section, string title, double min, double max, double initial, double step,
            string unit = "", double displayScale = 1)
        {
            var slider = new NativeSlider(title, min, max, initial, step, unit, displayScale);
            slider.ValueChanged += (_, _) => RefreshLook();
            if (opticalPreview is not null)
                opticalPreview.TrackSliderDrag(slider);
            section.Children.Add(slider);
            return slider;
        }

        private void LoadStocks()
        {
            if (disposed || options is not null)
                return;
            try
            {
                var root = Environment.GetEnvironmentVariable("SPEKTRAFILM_ROOT")
                    ?? throw new InvalidOperationException("Start Capture One from the Spektrafilm launcher.");
                var available = System.Text.Json.JsonSerializer.Deserialize<LookOptions>(File.ReadAllText(Path.Combine(root, "stocks.json")))
                    ?? throw new InvalidDataException("Spektrafilm stock data is missing.");
                options = available with { Films = Sort(available.Films), Papers = Sort(available.Papers) };
                SetDefaults();
                SyncSelection();
            }
            catch (Exception error)
            {
                options = null;
                controls.IsEnabled = sections.IsEnabled = false;
                Report(error);
            }
        }

        private static Stock[] Sort(Stock[] stocks) => stocks.OrderBy(stock => stock.Label, StringComparer.CurrentCulture).ToArray();

        private void OnOpticalSettingsChanged()
        {
            if (settingValues) return;
            try { SetPrintControls(); SaveLook(); }
            catch (Exception error) { Report(error); }
        }

        private void SetModeControls()
        {
            if (options is null)
                return;

            var inPrintMode = mode.SelectedItem is Stock { Id: "print" };
            var currentFilm = (film.SelectedItem as Stock)?.Id;
            var previousSetting = settingValues;
            settingValues = true;
            try
            {
                var films = options.Films.Where(stock => !inPrintMode || stock.Kind != "positive").ToArray();
                film.ItemsSource = films;
                film.SelectedItem = films.FirstOrDefault(stock => stock.Id == currentFilm) ?? films.FirstOrDefault(stock => stock.Id == DefaultFilm);
                SetPrintControls();
                SetScanOptions();
            }
            finally
            {
                settingValues = previousSetting;
            }
        }

        private void SetScanOptions()
        {
            var canInvert = mode.SelectedItem is Stock { Id: "scan" } && film.SelectedItem is Stock { Kind: "negative" };
            scanInvert.Visibility = canInvert ? Visibility.Visible : Visibility.Collapsed;
            scanHint.Visibility = mode.SelectedItem is Stock { Id: "scan" } ? Visibility.Visible : Visibility.Collapsed;
            scanHint.Text = canInvert
                ? "Scan shows the simulated negative. Invert creates a linear positive that may need exposure adjustment. Use Print Simulation for a paper response."
                : "Scan shows the reversal film directly, without a print stock. Print diffusion and scanner glare do not apply.";
        }

        private void SetPrintControls()
        {
            paper.IsEnabled = printExposure.IsEnabled = printing.IsEnabled = mode.SelectedItem is Stock { Id: "print" };
        }

        private void SetDefaults()
        {
            if (options is null)
                return;
            var previousSetting = settingValues;
            settingValues = true;
            try
            {
                mode.ItemsSource = options.Modes;
                mode.SelectedItem = options.Modes.First(stock => stock.Id == "print");
                paper.ItemsSource = options.Papers;
                paper.SelectedItem = options.Papers.First(stock => stock.Id == DefaultPaper);
                spectralMethod.ItemsSource = options.SpectralMethods;
                spectralMethod.SelectedItem = options.SpectralMethods.First(stock => stock.Id == DefaultSpectralMethod);
                SetModeControls();
                film.SelectedItem = options.Films.First(stock => stock.Id == DefaultFilm);
                scanInvert.IsChecked = false;
                filmPushPullMode.SelectedIndex = 0;
                exposure.Value = printExposure.Value = 0;
                strength.Value = 100;
                foreach (var (slider, initial) in development.Values)
                    slider.Value = initial;
            }
            finally
            {
                settingValues = previousSetting;
            }
        }

        private LookSettings? CurrentSettings()
        {
            if (mode.SelectedItem is not Stock selectedMode || film.SelectedItem is not Stock selectedFilm
                || paper.SelectedItem is not Stock selectedPaper || spectralMethod.SelectedItem is not Stock selectedMethod)
                return null;
            return new LookSettings(selectedFilm.Id, selectedPaper.Id, exposure.Value,
                printExposure.Value, strength.Value / 100, selectedMode.Id, selectedMethod.Id,
                scanInvert.Visibility == Visibility.Visible && scanInvert.IsChecked == true,
                development.ToDictionary(item => item.Key, item => item.Value.Slider.Value),
                (filmPushPullMode.SelectedItem as Stock)?.Id ?? "standard");
        }

        private void RefreshLook()
        {
            if (settingValues)
                return;
            SaveLook();
            opticalPreview.RequestRefresh();
        }

        private void Run(Action action, string success)
        {
            try
            {
                action();
                status.Text = success;
                StartupHook.Log(success);
            }
            catch (Exception error)
            {
                Report(error);
            }
        }

        private void Report(Exception error)
        {
            while (error is TargetInvocationException { InnerException: not null } nested)
                error = nested.InnerException!;
            status.Text = error.Message;
            StartupHook.Log(error.ToString());
        }

        private Button Button(string title, Func<Task> action)
        {
            var button = new Button { Content = title, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 5, 0) };
            button.Click += async (_, _) => { try { await action(); } catch (Exception error) { Report(error); } };
            return button;
        }

    }
}
