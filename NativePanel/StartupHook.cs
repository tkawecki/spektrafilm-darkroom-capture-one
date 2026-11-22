using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using P1.C1.Controls.Inspector.WPF;
using P1.C1.Core.Messaging;
using P1.C1.Core.ModelEntities;
using P1.C1.Framework.Messaging;
using P1.C1.ViewModels.Controls.Inspector;
using Forms = System.Windows.Forms;

internal static class StartupHook
{
    internal const string ProductName = "Spektrafilm Darkroom for Capture One";
    internal const string BrandName = "Spektrafilm";
    private const BindingFlags PublicStatic = BindingFlags.Public | BindingFlags.Static;
    private static readonly string[] CustomTabIds = Enumerable.Range(1, 18)
        .Select(index => $"Custom{index}PaletteTab").ToArray();

    private static Forms.Timer? timer;
    private static InspectorPaletteViewModel? owner;
    private static InspectorPaletteTabViewModel? tab;
    private static SpektraFilmTool.HostedTool? hostedTool;
    private static ExportHook? exportHook;
    private static IInspectorPaletteTabViewModel? previousTab;
    private static Forms.Form? window;
    private static string? lastState;
    private static ICaptureOneDocument? boundDocument;
    private static string? boundDatabase;
    internal static bool IsBoundDocument(ICaptureOneDocument document, object form, string path) =>
        ReferenceEquals(boundDocument, document) && ReferenceEquals(window, form)
        && string.Equals(boundDatabase, ApprovedCatalog.ApprovedDatabase(path), StringComparison.OrdinalIgnoreCase);

    public static void Initialize()
    {
        try
        {
            if (Environment.ProcessPath is not { } exe
                || !string.Equals(Path.GetFileName(exe), "CaptureOne.exe", StringComparison.OrdinalIgnoreCase)
                || !ApprovedCatalog.HasSelection)
                return;

            Log($"{ProductName} startup hook loaded.");
            // Wait for Capture One's own WinForms message loop before accessing its WPF palette.
            Forms.Application.Idle += OnApplicationIdle;
        }
        catch (Exception error)
        {
            Log(error.ToString());
        }
    }

    private static void OnApplicationIdle(object? sender, EventArgs args)
    {
        try
        {
            var program = FindProgram();
            var manager = program?.GetProperty("DocumentManager", PublicStatic)?.GetValue(null);
            var document = manager?.GetType().GetProperty("ActiveDocument")?.GetValue(manager) as ICaptureOneDocument;
            var form = GetMainForm(program, document);
            if (form is not { IsHandleCreated: true, IsDisposed: false })
                return;

            Forms.Application.Idle -= OnApplicationIdle;
            timer = new Forms.Timer { Interval = 1000 };
            timer.Tick += OnTick;
            timer.Start();
            OnTick(sender, args);
        }
        catch (Exception error)
        {
            LogState(error.ToString());
        }
    }

    private static void OnTick(object? sender, EventArgs args)
    {
        try
        {
            var program = FindProgram();
            var manager = program?.GetProperty("DocumentManager", PublicStatic)?.GetValue(null);
            var managerType = manager?.GetType();
            var path = managerType?.GetProperty("ActiveDocumentPath")?.GetValue(manager) as string;
            var database = ApprovedCatalog.ApprovedDatabase(path);
            if (database is null)
            {
                Detach();
                LogState("Waiting for an active catalog or session permitted by this launcher.");
                return;
            }

            var document = managerType!.GetProperty("ActiveDocument")?.GetValue(manager) as ICaptureOneDocument;
            var form = GetMainForm(program, document);
            var palette = form?.GetType().GetProperty("InspectorPaletteViewModel")?.GetValue(form) as InspectorPaletteViewModel;
            if (document is null || form is not { IsHandleCreated: true, IsDisposed: false } || palette is null)
            {
                Detach();
                LogState("Waiting for the active document's tool palette.");
                return;
            }

            if (ReferenceEquals(document, boundDocument) && string.Equals(database, boundDatabase, StringComparison.OrdinalIgnoreCase)
                && ReferenceEquals(form, window) && ReferenceEquals(palette, owner) && tab is not null && palette.Tabs.Contains(tab)
                && hostedTool is { } existing && tab.AllTools.Contains(existing.Tool))
            {
                existing.CheckSelectionAndSave();
                return;
            }

            Detach();
            Attach(form, palette, document, database);
        }
        catch (Exception error)
        {
            LogState(error.ToString());
        }
    }

    private static Type? FindProgram() => AppDomain.CurrentDomain.GetAssemblies()
        .FirstOrDefault(assembly => assembly.GetName().Name == "CaptureOne")?.GetType("P1.C1.Program");

    private static Forms.Form? GetMainForm(Type? program, ICaptureOneDocument? document) => document is null
        ? null
        : program?.GetMethod("GetMainForm", PublicStatic)?.Invoke(null, new object[] { document }) as Forms.Form;

    private static void Attach(Forms.Form form, InspectorPaletteViewModel palette, ICaptureOneDocument document, string database)
    {
        var bus = MessageBusProvider.DefaultInstance;
        var lastUserTab = palette.Current;
        var names = palette.Tabs.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        var id = CustomTabIds.FirstOrDefault(name => !names.Contains(name))
            ?? throw new InvalidOperationException($"Remove an unused custom tool tab to make room for {ProductName}.");
        var newTab = new FilmToolsTab(id, bus.GlobalBus, document)
        {
            IsVisible = true,
            ParentPalette = palette,
        };

        SpektraFilmTool.HostedTool? hosted = null;
        try
        {
            boundDocument = document;
            boundDatabase = database;
            window = form;
            hosted = SpektraFilmTool.Create(form, bus, document, database);
            // The menu-level AddTool helper also emits a tour event and expects
            // a factory-registered caption. Add the hosted control to its area.
            newTab.ScrollAreaViewModel.AddTool(hosted.Tool);
            palette.Tabs.Add(newTab);
            SelectTab(palette, newTab);
        }
        catch
        {
            if (ReferenceEquals(palette.Current, newTab))
                SelectTab(palette, lastUserTab);
            palette.Tabs.Remove(newTab);
            hosted?.Dispose();
            boundDocument = null;
            boundDatabase = null;
            window = null;
            newTab.Dispose();
            throw;
        }

        owner = palette;
        tab = newTab;
        hostedTool = hosted;
        previousTab = lastUserTab;
        window = form;
        window.FormClosing += OnFormClosing;
        LogState($"Attached {ProductName} to {database} as {id}.");
        if (hosted is { } attached)
        {
            try
            {
                exportHook = new ExportHook(form, editMultiple => attached.IsActiveForExport(editMultiple),
                    editMultiple => attached.ExportAsync(exportHook?.Dialog, editMultiple),
                    () => HostRecipeWriter.Unavailable is not null);
                attached.SetExportBridgeStatus(null);
            }
            catch (Exception error)
            {
                attached.SetExportBridgeStatus("Capture One's Export command could not be connected, so its exports will NOT include Spektrafilm. Use \"Export with Spektrafilm…\" or the batch export buttons in this panel. "
                    + error.GetBaseException().Message);
                Log($"{BrandName} could not bind the native Export command: {error}");
            }
        }
    }

    private sealed class FilmToolsTab(string id, IMessageBus bus, ICaptureOneDocument document)
        : InspectorPaletteTabViewModel(id, bus, ProductName, document)
    {
        private static readonly FilmstripTabInfoFactory FilmstripFactory = new();

        protected override IInspectorPaletteTabInfoFactory TabInfoFactory => FilmstripFactory;
    }

    private sealed class FilmstripTabInfoFactory : IInspectorPaletteTabInfoFactory
    {
        private readonly InspectorPaletteTabInfoFactory nativeFactory = new();

        public InspectorPaletteTabInfo Create(string name) => nativeFactory.Create(name) with
        {
            Image = DrawFilmstrip(Color.FromRgb(196, 196, 196)),
            CheckedImage = DrawFilmstrip(Color.FromRgb(253, 137, 20)),
        };

        private static ImageSource DrawFilmstrip(Color color)
        {
            // The same transparent bounds in both states keep the tab centered when selected.
            var geometry = Geometry.Parse("F0 M 6,8 H 26 V 24 H 6 Z "
                + "M 8,10 H 11 V 12 H 8 Z M 14,10 H 18 V 12 H 14 Z M 21,10 H 24 V 12 H 21 Z "
                + "M 8,14 H 24 V 18 H 8 Z "
                + "M 8,20 H 11 V 22 H 8 Z M 14,20 H 18 V 22 H 14 Z M 21,20 H 24 V 22 H 21 Z");
            var drawing = new DrawingGroup();
            drawing.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 32, 32))));
            drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(color), null, geometry));
            var icon = new DrawingImage(drawing);
            icon.Freeze();
            return icon;
        }
    }

    private static void OnFormClosing(object? sender, Forms.FormClosingEventArgs args)
    {
        if (args.Cancel)
            return;
        try
        {
            Detach(saveWorkspace: true);
        }
        catch (Exception error)
        {
            Log(error.ToString());
        }
    }

    private static void Detach(bool saveWorkspace = false)
    {
        if (tab is not { } oldTab || owner is not { } oldOwner)
            return;

        var originalWindow = window;
        var oldTool = hostedTool;
        var oldExport = exportHook;
        var oldPrevious = previousTab;
        tab = null;
        owner = null;
        hostedTool = null;
        exportHook = null;
        previousTab = null;
        window = null;
        boundDocument = null;
        boundDatabase = null;
        Log($"Detached Spektrafilm from previous document; pending rendering canceled and looks flushed.");

        if (originalWindow is not null)
            originalWindow.FormClosing -= OnFormClosing;
        try { oldExport?.Dispose(); }
        finally { oldTool?.Dispose(); }
        if (oldOwner.Tabs.Contains(oldTab))
        {
            if (ReferenceEquals(oldOwner.Current, oldTab))
            {
                var other = oldPrevious is not null && oldOwner.Tabs.Contains(oldPrevious)
                    ? oldPrevious
                    : oldOwner.Tabs.FirstOrDefault(item => !ReferenceEquals(item, oldTab));
                SelectTab(oldOwner, other);
            }
            oldOwner.Tabs.Remove(oldTab);
            oldTab.Dispose();
        }
        // The main form saves its palette before raising FormClosing. Refresh the
        // in-memory workspace snapshot once the temporary Spektrafilm tab has been removed.
        if (saveWorkspace && originalWindow is { IsDisposed: false })
            oldOwner.SaveSettingsToWorkspace();
    }

    private static void SelectTab(InspectorPaletteViewModel palette, IInspectorPaletteTabViewModel? current) =>
        typeof(InspectorPaletteViewModel).GetProperty("Current")!.SetValue(palette, current);

    private static void LogState(string text)
    {
        if (string.Equals(lastState, text, StringComparison.Ordinal))
            return;
        lastState = text;
        Log(text);
    }

    internal static void Log(string text)
    {
        try
        {
            var path = Environment.GetEnvironmentVariable("SPEKTRAFILM_PANEL_LOG");
            if (!string.IsNullOrWhiteSpace(path))
                File.AppendAllText(path, $"{DateTime.Now:O} {text}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
