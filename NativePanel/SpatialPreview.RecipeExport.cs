using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using P1.C1.Core.DestinationProcessing;
using P1.ModelCoreWrapper;

// Renders: how many Spektrafilm renders the export needed (one per variant and group of compatible recipes).
internal sealed record RecipeExportResult(int Written, int Skipped, int Failed, bool Cancelled,
    IReadOnlyList<string> Errors, IReadOnlyList<string> Notes, ExportJournal Journal, IReadOnlyList<string> Files, int Renders = 0);

// Recipe export: each variant is rendered once per group of recipes with the same size, crop and colour space,
// kept as an uncompressed 16-bit intermediate and written by Capture One through every recipe of the group.
internal sealed partial class SpatialPreview
{
    // Capture: reads the variant's Capture One settings again on the UI thread just before the group develops; the
    // settings read while planning can be minutes old by then (after upstream 3c90b88, Luka Kauzlaric, GPL-3.0).
    private sealed record RecipeRenderGroup(BatchRender Render, Func<HostImageSource.Snapshot> Capture, bool LinearDng, IReadOnlyList<RecipeJob> Jobs);

    internal async Task<RecipeExportResult> RunRecipeExportAsync(RecipeExportPlan plan, Action<ExportJournal> saveJournal,
        CancellationToken token = default)
    {
        if (exportWork is not null) throw new InvalidOperationException("Wait for the current export to finish.");
        var journal = plan.Journal;
        var sync = new object();
        var errors = new List<string>();
        var notes = new List<string>(plan.Notes);
        var files = new List<string>();
        void Mark(RecipeJob job, JournalState state, string? temporary = null, string? error = null, string? output = null)
        {
            ExportJournal snapshot;
            lock (sync)
            {
                var item = journal.Items[job.Index];
                journal = journal.With(job.Index, item with { State = state, Temporary = temporary, Error = error, OutputPath = output ?? item.OutputPath });
                snapshot = journal;
            }
            try { saveJournal(snapshot); }
            catch (Exception failure) { StartupHook.Log("The export journal could not be saved: " + failure.GetBaseException().Message); }
        }
        void Fail(RecipeJob job, Exception error)
        {
            var message = $"{Path.GetFileName(job.File.OutputPath)}: {error.GetBaseException().Message}";
            lock (sync) errors.Add(message);
            StartupHook.Log("Recipe export failed for " + job.File.OutputPath + ": " + error);
            Mark(job, JournalState.Failed, error: error.GetBaseException().Message);
        }

        var groups = PrepareRecipeGroups(plan, Fail);
        var total = groups.Sum(group => group.Jobs.Count);
        var progress = (IProgress<string>)new Progress<string>(text => status.Text = text);
        var cancelled = false;
        using var work = CancellationTokenSource.CreateLinkedTokenSource(token);
        exportWork = work;
        var run = new RecipeRun(Path.Combine(Path.GetTempPath(), "Spektrafilm export " + plan.Journal.Id.ToString("N")[..8]),
            plan.Jobs.Select(job => job.Entry.Target.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            sync, Mark, Fail, notes, files, total, progress, status.Dispatcher, work.Token);
        try
        {
            Directory.CreateDirectory(run.WorkFolder);
            foreach (var group in groups)
            {
                if (work.IsCancellationRequested) { cancelled = true; break; }
                try { await Task.Run(() => RenderRecipeGroup(group, run), work.Token).ConfigureAwait(true); }
                catch (OperationCanceledException) when (work.IsCancellationRequested) { cancelled = true; break; }
                catch (Exception error)
                {
                    foreach (var job in group.Jobs.Where(job => journal.Items[job.Index].State == JournalState.Pending)) Fail(job, error);
                }
            }
        }
        finally
        {
            exportWork = null;
            try { if (Directory.Exists(run.WorkFolder)) Directory.Delete(run.WorkFolder, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { StartupHook.Log("Recipe export work folder left: " + run.WorkFolder); }
        }
        var written = journal.Items.Count(item => item.State == JournalState.Done);
        var skipped = journal.Items.Count(item => item.State == JournalState.Skipped);
        var failed = journal.Items.Count(item => item.State == JournalState.Failed);
        static string Files(int count) => count == 1 ? "1 file" : $"{count} files";
        status.Text = cancelled
            ? $"Export cancelled · {Files(written)} written, {journal.RemainingCount} can be resumed."
            : failed > 0 ? $"Export finished with {Files(failed)} failed · {written} written."
            : $"Exported {Files(written)} with Capture One recipes" + (skipped > 0 ? $" · {skipped} skipped" : "") + ".";
        return new RecipeExportResult(written, skipped, failed, cancelled, errors.AsReadOnly(), notes.AsReadOnly(), journal, files.AsReadOnly(), groups.Count);
    }

    // UI thread: one frozen render per variant and group of recipes that share size, crop handling and colour space.
    private List<RecipeRenderGroup> PrepareRecipeGroups(RecipeExportPlan plan, Action<RecipeJob, Exception> fail)
    {
        var groups = new List<RecipeRenderGroup>();
        LookOptions? catalog = null;
        LookSettings? look = null;
        using var context = BuildForPanel(window, () => look, _ => false);
        foreach (var variantJobs in plan.Writing.GroupBy(job => job.Entry.Target.VariantId))
        {
            var keyed = new Dictionary<string, (BatchRender Render, Func<HostImageSource.Snapshot> Capture, List<RecipeJob> Jobs)>();
            foreach (var job in variantJobs)
            {
                try
                {
                    var linearDng = job.Format == RecipeFormat.LinearDng;
                    var render = PrepareRecipeRender(context, value => look = value, job, linearDng, ref catalog, out var capture);
                    var recipe = render.Source.Recipe!;
                    var key = FormattableString.Invariant($"{recipe.Width}x{recipe.Height}|{recipe.Scale:R}|{recipe.IgnoreCrop}|{recipe.CropMode}|{render.Color}|{linearDng}");
                    if (!keyed.TryGetValue(key, out var group)) keyed[key] = group = (render, capture, new List<RecipeJob>());
                    group.Jobs.Add(job);
                }
                catch (Exception error) { fail(job, error); }
            }
            groups.AddRange(keyed.Values.Select(group => new RecipeRenderGroup(group.Render, group.Capture, group.Jobs[0].Format == RecipeFormat.LinearDng, group.Jobs)));
        }
        return groups;
    }

    // The same per-variant preparation as the batch export, at the size of one recipe and in its render space.
    private BatchRender PrepareRecipeRender(SpatialPreview context, Action<LookSettings?> setLook, RecipeJob job, bool linearDng,
        ref LookOptions? catalog, out Func<HostImageSource.Snapshot> capture)
    {
        var saved = job.Entry.Settings;
        var look = saved.Look;
        setLook(look);
        if (saved.Enabled) RejectRemovedEngine(saved.Optics);
        context.LoadSettings(saved.Optics);
        catalog ??= System.Text.Json.JsonSerializer.Deserialize<LookOptions>(File.ReadAllText(Path.Combine(
            Environment.GetEnvironmentVariable("SPEKTRAFILM_ROOT")!, "stocks.json")))!;
        look = saved.Enabled && look.Strength > 0 ? BatchExportSupport.RenderingLook(look, catalog) : look;
        setLook(look);
        var exportColor = linearDng ? ExportRgbSpace.LinearRec2020 : job.Space;
        var profile = ExportColorSpace.ResolveProfileOrNull(exportColor);
        var input = saved.Enabled ? (InputChoice)context.inputSpace.SelectedItem
            : InputSpaces.First(x => (int)x.Space == ((int)exportColor < 4 ? (int)exportColor : (int)PreviewInputSpace.LinearRec2020));
        var preparation = saved.Enabled ? context.Preparation : SourcePreparation.CaptureOne;
        var source = job.Entry.CaptureSource(window, input.Space, true, preparation, job.Recipe);
        var space = input.Space;
        capture = () => job.Entry.CaptureSource(window, space, true, preparation, job.Recipe);
        var strength = saved.Enabled ? look.Strength : 0;
        var encoding = exportColor == ExportRgbSpace.Srgb ? ((Choice)context.outputSpace.SelectedItem).Id : "linear_rec709";
        var arguments = strength > 0 ? context.RendererOptions(look, input.Id, encoding, export: true) : Array.Empty<string>();
        return new BatchRender(source, input.Space, encoding, strength, arguments, new FinishingTransform(context.CaptureFinishingSettings()),
            exportColor, profile, (int)Math.Round(context.values["export-jpeg-quality"].Value),
            saved.Enabled ? context.CaptureGeometrySettings() : new(Active: false),
            saved.Enabled ? context.paperDecoration : PaperDecorationSettings.Default);
    }

    // State shared by the groups of one run. Ui: Capture One's UI thread, where its model objects are used.
    private sealed class RecipeRun(string workFolder, IReadOnlyCollection<string> originals, object sync,
        Action<RecipeJob, JournalState, string?, string?, string?> mark, Action<RecipeJob, Exception> fail,
        List<string> notes, List<string> files, int total, IProgress<string> progress,
        System.Windows.Threading.Dispatcher ui, CancellationToken stop)
    {
        private int started;
        internal string WorkFolder { get; } = workFolder;
        internal IReadOnlyCollection<string> Originals { get; } = originals;
        internal HashSet<string> Claimed { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal object Sync { get; } = sync;
        internal Action<RecipeJob, JournalState, string?, string?, string?> Mark { get; } = mark;
        internal Action<RecipeJob, Exception> Fail { get; } = fail;
        internal CancellationToken Stop { get; } = stop;
        internal T OnUi<T>(Func<T> action) => ui.Invoke(action);
        // For a wait the export's Cancel must be able to end (the UI can be busy, or Capture One closing).
        internal async Task<T> OnUiAsync<T>(Func<T> action) =>
            ui.HasShutdownStarted ? throw new OperationCanceledException("Capture One is closing.")
                : await ui.InvokeAsync(action, System.Windows.Threading.DispatcherPriority.Normal, Stop).Task.ConfigureAwait(false)
                    ?? throw new OperationCanceledException("Capture One is closing.");
        internal void OnUi(Action action) => ui.Invoke(action);
        internal void Note(string note) { lock (Sync) notes.Add(note); }
        internal void Published(string file) { lock (Sync) files.Add(file); }
        internal void Starting(RecipeJob job) =>
            progress.Report($"Exporting with recipes · {Interlocked.Increment(ref started)}/{total} · {Path.GetFileName(job.File.OutputPath)}");
    }

    // Worker thread: render once, then write every recipe of the group.
    private static async Task RenderRecipeGroup(RecipeRenderGroup group, RecipeRun run)
    {
        var stop = run.Stop;
        run.Starting(group.Jobs[0]);
        await HostImageSource.WaitForNativeDevelopmentAsync(stop).ConfigureAwait(false);
        var render = group.Render with { Source = await run.OnUiAsync(group.Capture).ConfigureAwait(false) };
        var developed = render.Source.RenderExport(null, stop, followRecipeSize: true);
        var source = developed.Image;
        if (source.BitDepth < 16) throw new InvalidDataException("Capture One did not produce high-bit-depth export data.");
        byte[]? pixels = null;
        if (render.Strength > 0)
        {
            using var renderer = new OpticalRenderer(true, false);
            pixels = await renderer.RenderAsync(source.Width, source.Height, source.FloatBytes, render.Arguments, stop).ConfigureAwait(false);
        }
        stop.ThrowIfCancellationRequested();

        // The recipe exports the crop as a path: keep the whole canvas when the panel does not reshape the frame.
        var crop = developed.Crop;
        Rectangle? cropPath = null;
        if (developed.PathCrop is { } pathCrop)
        {
            if (render.Geometry.IsIdentity && render.Paper.IsIdentity)
                cropPath = new Rectangle(pathCrop.X - crop.X, pathCrop.Y - crop.Y, pathCrop.Width, pathCrop.Height);
            else
            {
                crop = pathCrop;
                run.Note($"{Path.GetFileName(group.Jobs[0].Entry.Target.SourcePath)}: scan geometry or a paper border reshape the frame, so the crop is applied instead of written as a path.");
            }
        }

        if (group.LinearDng)
        {
            foreach (var job in group.Jobs)
                WriteJob(job, run, temporary => WritePaperExportImage(temporary, source, pixels, render.Strength, render.Input,
                    ResultEncoding(render.Encoding), crop, stop, render.Finishing, render.Color, render.Profile, render.JpegQuality,
                    render.Geometry, render.Paper));
            return;
        }

        var intermediatePath = Path.Combine(run.WorkFolder, Guid.NewGuid().ToString("N") + ".tif");
        HostRecipeWriter.Intermediate? image = null;
        try
        {
            var size = WritePaperExportImage(intermediatePath, source, pixels, render.Strength, render.Input, ResultEncoding(render.Encoding),
                crop, stop, render.Finishing, render.Color, render.Profile, render.JpegQuality, render.Geometry, render.Paper, intermediate: true);
            pixels = null;
            var document = group.Jobs[0].Entry.Document ?? throw new InvalidOperationException("The export has no Capture One document.");
            image = run.OnUi(() => HostRecipeWriter.Intermediate.Open(document, intermediatePath, size.PixelWidth, size.PixelHeight));
            var region = cropPath ?? new Rectangle(0, 0, size.PixelWidth, size.PixelHeight);
            foreach (var job in group.Jobs)
                WriteJob(job, run, temporary =>
                {
                    using var prepared = run.OnUi(() => HostRecipeWriter.Prepare(image,
                        new HostRecipeWriter.WriteRequest(job.Recipe!, temporary, job.Metadata, job.Watermark, cropPath)));
                    HostRecipeWriter.Process(image, prepared, temporary, stop);
                    if (job.Format == RecipeFormat.Png && RepairPng(temporary, intermediatePath, region) is { } note)
                        run.Note($"{Path.GetFileName(job.File.OutputPath)}: {note}");
                });
        }
        finally
        {
            if (image is not null) run.OnUi(image.Dispose);
            try { if (File.Exists(intermediatePath)) File.Delete(intermediatePath); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { StartupHook.Log("Recipe export could not remove " + intermediatePath + ": " + error.Message); }
        }
    }

    private static void WriteJob(RecipeJob job, RecipeRun run, Action<string> write)
    {
        var stop = run.Stop;
        string? temporary = null;
        try
        {
            stop.ThrowIfCancellationRequested();
            run.Starting(job);
            var folder = Path.GetDirectoryName(job.File.OutputPath)!;
            Directory.CreateDirectory(folder);
            temporary = Path.Combine(folder, ".spektrafilm-" + Guid.NewGuid().ToString("N") + Path.GetExtension(job.File.OutputPath));
            run.Mark(job, JournalState.Pending, temporary, null, null);
            write(temporary);
            stop.ThrowIfCancellationRequested();
            string published;
            lock (run.Sync) published = PublishRecipeFile(temporary, job.File, run.Originals, run.Claimed);
            temporary = null;
            run.Published(published);
            run.Mark(job, JournalState.Done, null, null, published);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { throw; }
        catch (Exception error) { run.Fail(job, error); }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { StartupHook.Log("Recipe export could not remove " + temporary + ": " + error.Message); }
        }
    }

    /// <summary>
    /// Moves a finished file to its planned name. "Replace" replaces only what was there before the export; the
    /// originals are never replaced, and a name that became taken meanwhile gets the next free name.
    /// </summary>
    internal static string PublishRecipeFile(string temporary, PlannedFile file, IReadOnlyCollection<string> originals, HashSet<string> claimed)
    {
        foreach (var original in originals) OutputPathGuard.Validate(file.OutputPath, original);
        if (file.Outcome == PlannedOutcome.Replaces && !claimed.Contains(file.OutputPath) && !Directory.Exists(file.OutputPath))
        {
            File.Move(temporary, file.OutputPath, overwrite: true);
            claimed.Add(file.OutputPath);
            return file.OutputPath;
        }
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            var target = RecipeExportPlanning.FreeName(file.OutputPath, path => claimed.Contains(path) || File.Exists(path) || Directory.Exists(path));
            foreach (var original in originals) OutputPathGuard.Validate(target, original);
            try
            {
                File.Move(temporary, target, overwrite: false);
                claimed.Add(target);
                return target;
            }
            catch (IOException) when (File.Exists(target) || Directory.Exists(target)) { claimed.Add(target); }
        }
        throw new IOException("Could not find a free name for " + Path.GetFileName(file.OutputPath) + ".");
    }

    // Capture One writes 16-bit PNG samples in the wrong byte order; compare with the render and repair.
    // region: the part of the rendered image the file holds (the crop when the crop is written as a path).
    // Returns a note for the user when the order could not be checked.
    private static string? RepairPng(string path, string intermediate, Rectangle region)
    {
        RecipeExportPng.Image header;
        var expected = new Dictionary<int, ushort[]>();
        using (var png = File.OpenRead(path))
        {
            header = RecipeExportPng.ReadHeader(png);
            if (header.BitDepth != 16 || header.Interlaced) return null;
            if (header.Channels != 3 || header.Width != region.Width || header.Height != region.Height)
                return "the 16-bit byte order could not be checked; if other programs show noise, export this file as TIFF.";
            using var tiff = File.OpenRead(intermediate);
            var frame = new TiffBitmapDecoder(tiff, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.None).Frames[0];
            for (var i = 1; i <= 8; i++)
            {
                var row = (int)((long)header.Height * i / 9);
                var values = new ushort[region.Width * 3];
                frame.CopyPixels(new System.Windows.Int32Rect(region.X, region.Y + row, region.Width, 1), values, region.Width * 6, 0);
                expected[row] = values;
            }
            if (!RecipeExportPng.IsSwapped(png, expected)) return null;
        }
        var repaired = Path.Combine(Path.GetDirectoryName(path)!, ".spektrafilm-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            using (var source = File.OpenRead(path))
            using (var target = new FileStream(repaired, FileMode.CreateNew, FileAccess.Write))
                RecipeExportPng.SwapSamples(source, target);
            File.Move(repaired, path, overwrite: true);
        }
        finally { if (File.Exists(repaired)) File.Delete(repaired); }
        StartupHook.Log("Repaired the byte order of 16-bit PNG " + Path.GetFileName(path) + ".");
        return null;
    }
}
