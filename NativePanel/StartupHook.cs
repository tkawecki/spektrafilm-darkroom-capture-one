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
    // Show the panel's tab when it first attaches in this Capture One session, and later only if it was the shown
    // tab when the palette was rebuilt; otherwise keep the user's tab (after upstream 3acf354, Luka Kauzlaric, GPL-3.0).
    private static bool selectOnAttach = true;
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

            // Capture One rebuilt the tool palette (a workspace change) for the same document: let a running export
            // finish first, then attach to the new palette (after upstream 479b709, Luka Kauzlaric, GPL-3.0).
            if (ReferenceEquals(document, boundDocument) && string.Equals(database, boundDatabase, StringComparison.OrdinalIgnoreCase)
                && ReferenceEquals(form, window) && hostedTool is { IsExporting: true })
            {
                LogState("Waiting for the Spektrafilm export to finish before attaching to the new tool palette.");
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
        var priorSelection = palette.Current;
        // A workspace saved while the panel was attached keeps an empty tab with the panel's name; reuse its place
        // instead of adding another one next to it (after upstream d34b6bc, Luka Kauzlaric, GPL-3.0).
        var emptyTabs = palette.Tabs.Select((item, index) => (Tab: item, Index: index))
            .Where(item => IsEmptyPanelTab(item.Tab)).ToArray();
        var lastUserTab = emptyTabs.Any(item => ReferenceEquals(item.Tab, priorSelection)) ? null : priorSelection;
        var names = palette.Tabs.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        var id = emptyTabs.FirstOrDefault().Tab?.Name ?? CustomTabIds.FirstOrDefault(name => !names.Contains(name))
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
            foreach (var (empty, _) in emptyTabs)
                palette.Tabs.Remove(empty);
            palette.Tabs.Insert(emptyTabs.Length == 0 ? palette.Tabs.Count : Math.Min(emptyTabs[0].Index, palette.Tabs.Count), newTab);
            var selected = selectOnAttach || lastUserTab is null ? newTab : lastUserTab;
            if (!ReferenceEquals(palette.Current, selected))
                SelectTab(palette, selected);
        }
        catch
        {
            palette.Tabs.Remove(newTab);
            foreach (var (empty, index) in emptyTabs)
                if (!palette.Tabs.Contains(empty))
                    palette.Tabs.Insert(Math.Min(index, palette.Tabs.Count), empty);
            if (!ReferenceEquals(palette.Current, priorSelection))
                SelectTab(palette, priorSelection);
            hosted?.Dispose();
            boundDocument = null;
            boundDatabase = null;
            window = null;
            newTab.Dispose();
            throw;
        }

        foreach (var (empty, _) in emptyTabs)
            if (empty is IDisposable disposable) disposable.Dispose();
        if (emptyTabs.Length > 0)
            Log($"Replaced {emptyTabs.Length} empty {ProductName} tab(s) left in the workspace.");
        selectOnAttach = false;
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

    private static bool IsEmptyPanelTab(IInspectorPaletteTabViewModel candidate) =>
        candidate is InspectorPaletteTabViewModel native && native.GetType() == typeof(InspectorPaletteTabViewModel)
        && native.Text == ProductName && CustomTabIds.Contains(native.Name, StringComparer.Ordinal) && !native.AllTools.Any();

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
            // An edit that reached neither its look file nor a recovery copy would be lost silently on close
            // (after upstream bb5aa9b, Spektrafilm for Capture One, Luka Kauzlaric, GPL-3.0).
            if (hostedTool?.ProtectPendingLooks() == false)
            {
                Log("Capture One close requested while a Spektrafilm edit is only in memory.");
                if (args.CloseReason == Forms.CloseReason.UserClosing && Forms.MessageBox.Show(sender as Forms.IWin32Window,
                        "Spektrafilm could not save your latest look, neither to its normal folder nor to the recovery folder. "
                        + "Keep Capture One open to let it try again when the drive is available.\n\nClose Capture One and lose that change?",
                        ProductName, Forms.MessageBoxButtons.YesNo, Forms.MessageBoxIcon.Warning, Forms.MessageBoxDefaultButton.Button2)
                    != Forms.DialogResult.Yes)
                {
                    args.Cancel = true;
                    return;
                }
            }
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
        selectOnAttach = ReferenceEquals(oldOwner.Current, oldTab);
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
        var path = Environment.GetEnvironmentVariable("SPEKTRAFILM_PANEL_LOG");
        if (!string.IsNullOrWhiteSpace(path))
            PanelLog.Write(path, $"{DateTime.Now:O} {text}{Environment.NewLine}");
    }
}
