using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using P1.C1.Core;
using P1.C1.Core.DestinationProcessing;
using P1.C1.Core.Interfaces;
using P1.C1.Core.ModelEntities;
using P1.ModelCoreWrapper;

/// <summary>Stable per-variant snapshots and serial, non-overwriting batch publication.</summary>
internal static class BatchExportSupport
{
    internal static LookSettings RenderingLook(LookSettings saved, LookOptions catalog)
    {
        var stock = catalog.Films.FirstOrDefault(x => x.Id == saved.Film)
            ?? throw new InvalidDataException("The batch variant requires an unavailable film: " + saved.Film);
        // Persisted hidden values must survive, but cannot become active merely
        // because a variant is exported without loading its visible controls.
        return saved with { ScanInvert = saved.ScanInvert && saved.Mode == "scan" && stock.Kind == "negative" };
    }
    internal sealed class Entry
    {
        private readonly VariantLook settings;
        internal HostProfile.Target Target { get; }
        internal ICaptureOneDocument? Document { get; }
        internal ICoreVariant? Variant { get; }
        internal HostImageSource.Revision? Revision { get; }
        // A caller can load/normalize this copy in a private rendering context
        // without modifying the captured batch or the live VariantLookStore.
        internal VariantLook Settings => Clone(settings);

        internal Entry(HostProfile.Target target, VariantLook settings, ICaptureOneDocument? document = null,
            ICoreVariant? variant = null, HostImageSource.Revision? revision = null)
        {
            if (target.VariantId == Guid.Empty) throw new ArgumentException("A batch target requires a variant ID.", nameof(target));
            if (string.IsNullOrWhiteSpace(target.SourcePath)) throw new ArgumentException("A batch target requires a source path.", nameof(target));
            Target = target with { SourcePath = Path.GetFullPath(target.SourcePath) };
            this.settings = Clone(settings);
            Document = document; Variant = variant; Revision = revision;
        }

        internal HostImageSource.Snapshot CaptureSource(object window, PreviewInputSpace inputSpace,
            bool captureRecipe = false, SourcePreparation preparation = SourcePreparation.CaptureOne,
            IDestinationProcessingSettings? recipe = null)
        {
            if (Document is null || Variant is null || Revision is null)
                throw new InvalidOperationException("This batch entry has no captured Capture One variant.");
            if (Variant.UUID != Target.VariantId || !StringComparer.OrdinalIgnoreCase.Equals(
                    Variant.CoreImage?.FullpathFilename, Target.SourcePath) || ReadRevision(Variant) != Revision)
                throw new OperationCanceledException("A batch variant changed before its export source could be captured.");
            var snapshot = HostImageSource.CaptureVariant(window, Document, Variant, inputSpace, captureRecipe, preparation, recipe);
            if (snapshot.AdjustmentKey.Counters != Revision)
                throw new OperationCanceledException("A batch variant changed while its export source was captured.");
            return snapshot;
        }
    }

    internal sealed record Item(Entry Entry, string OutputPath);
    internal sealed class Plan
    {
        internal string Directory { get; }
        internal IReadOnlyList<Item> Items { get; }
        internal Plan(string directory, IEnumerable<Item> items)
        {
            Directory = directory; Items = Array.AsReadOnly(items.ToArray());
        }
    }
    internal sealed record CompletedFile(Entry Entry, string OutputPath);
    internal sealed record Progress(int Completed, int Total, Entry? Current, string? OutputPath);
    internal sealed record Result(IReadOnlyList<CompletedFile> Completed, bool Cancelled);
    internal sealed class BatchExportException(Entry entry, IReadOnlyList<CompletedFile> completed, Exception inner)
        : Exception($"Batch export stopped at {Path.GetFileName(entry.Target.SourcePath)} after {completed.Count} completed file(s). {inner.Message}", inner)
    {
        internal Entry Entry { get; } = entry;
        internal IReadOnlyList<CompletedFile> Completed { get; } = completed;
    }

    /// <summary>Call on the C1 UI thread after queuing the current panel's look.</summary>
    internal static IReadOnlyList<Entry> Capture(object window, VariantLookStore looks,
        Func<Guid, VariantLook> defaultLook, bool requireSingleRecipe = false, bool primaryOnly = false,
        IReadOnlyCollection<Guid>? only = null)
    {
        ArgumentNullException.ThrowIfNull(looks);
        ArgumentNullException.ThrowIfNull(defaultLook);
        var active = HostProfile.ReadSelection(window);
        if (requireSingleRecipe) RequireSingleRecipe(active);
        var selection = ((ISelectedCollectionsProvider)active.Document).PrimarySelectedCollectionSelection
            ?? throw new InvalidOperationException("Select the variants to export first.");
        var selected = selection.SelectedVariants?.Cast<ICoreVariant>().ToArray() ?? Array.Empty<ICoreVariant>();
        if (selected.Length == 0 || primaryOnly) selected = new[] { active.Variant };
        // only: the variants of an interrupted export, found in the collection shown in the browser.
        if (only is not null)
        {
            var found = HostProfile.CollectionVariants(active.Document);
            selected = only.Where(found.ContainsKey).Select(id => found[id]).ToArray();
        }
        var seen = new HashSet<Guid>();
        var entries = new List<Entry>(selected.Length);
        foreach (var variant in selected)
        {
            if (!seen.Add(variant.UUID)) continue;
            var image = variant.CoreImage ?? throw new InvalidOperationException("A selected variant has no source image.");
            if (!image.IsFileAvailable || !image.IsICProcessable || image.IsMovie || image.IsCorrupt)
                throw new InvalidOperationException($"Capture One cannot export {Path.GetFileName(image.FullpathFilename)}.");
            var target = new HostProfile.Target(variant.UUID, image.FullpathFilename);
            var settings = looks.Read(variant.UUID) ?? defaultLook(variant.UUID);
            entries.Add(new Entry(target, settings, active.Document, variant, ReadSettledRevision(variant)));
        }
        return entries.AsReadOnly();
    }

    internal static VariantLook Clone(VariantLook value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Version != 1 || value.Look is null || value.Optics is null)
            throw new InvalidDataException("The variant has an unsupported Spektrafilm look.");
        return value with
        {
            Look = value.Look with { Development = new Dictionary<string, double>(value.Look.Development ?? new()) },
            Optics = new SpatialPreview.Settings(new(value.Optics.Numbers ?? new()),
                new(value.Optics.Selections ?? new()), new(value.Optics.Switches ?? new())),
        };
    }

    internal static Plan CreatePlan(IReadOnlyList<Entry> entries, string outputDirectory, string extension)
    {
        if (entries.Count == 0) throw new InvalidOperationException("Select at least one variant to export.");
        extension = extension.ToLowerInvariant();
        if (extension is not ".tif" and not ".tiff" and not ".png" and not ".jpg" and not ".jpeg" and not ".dng")
            throw new InvalidOperationException("Batch export supports TIFF, PNG, JPEG or DNG.");
        var directory = Path.GetFullPath(outputDirectory);
        var protectedSources = new HashSet<string>(entries.Select(x => x.Target.SourcePath), StringComparer.OrdinalIgnoreCase);
        var reserved = new HashSet<string>(protectedSources, StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<Guid>();
        var items = new List<Item>(entries.Count);
        foreach (var entry in entries)
        {
            if (!ids.Add(entry.Target.VariantId)) throw new InvalidOperationException("The batch contains a duplicate variant.");
            var stem = SafeStem(Path.GetFileNameWithoutExtension(entry.Target.SourcePath)) + "-Spektrafilm";
            var desired = Path.Combine(directory, stem + extension);
            var path = AvailablePath(desired, reserved);
            reserved.Add(path); items.Add(new Item(entry, path));
        }
        return new Plan(directory, items);
    }

    // The callback uses the frozen source/renderer options prepared on the UI
    // thread, renders RGB48/RGBA64 and calls SpektraExport.Save(temp,...). There is
    // exactly one in-flight callback. It must never apply controls to the live
    // panel or save settings back to a variant.
    internal static async Task<Result> RunAsync(Plan plan,
        Func<Entry, string, CancellationToken, Task> renderToTemporaryFile, CancellationToken token,
        IProgress<Progress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(renderToTemporaryFile);
        var completed = new List<CompletedFile>();
        if (token.IsCancellationRequested) return new Result(completed.AsReadOnly(), true);
        System.IO.Directory.CreateDirectory(plan.Directory);
        var reserved = new HashSet<string>(plan.Items.Select(x => x.Entry.Target.SourcePath).Concat(plan.Items.Select(x => x.OutputPath)), StringComparer.OrdinalIgnoreCase);
        foreach (var item in plan.Items)
        {
            string? temporary = null;
            try
            {
                token.ThrowIfCancellationRequested();
                progress?.Report(new Progress(completed.Count, plan.Items.Count, item.Entry, item.OutputPath));
                temporary = Path.Combine(plan.Directory, ".spektrafilm-" + Guid.NewGuid().ToString("N") + Path.GetExtension(item.OutputPath));
                // Reserve only our own temporary file. SpektraExport may replace
                // this reservation atomically; the final destination is never
                // passed to an encoder that supports overwriting.
                using (new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                await renderToTemporaryFile(item.Entry, temporary, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0)
                    throw new IOException("The batch renderer did not produce an export file.");
                reserved.Remove(item.OutputPath);
                var desired = Path.Combine(plan.Directory, SafeStem(Path.GetFileNameWithoutExtension(item.Entry.Target.SourcePath))
                    + "-Spektrafilm" + Path.GetExtension(item.OutputPath));
                var destination = Publish(temporary, desired, reserved);
                temporary = null;
                reserved.Add(destination);
                completed.Add(new CompletedFile(item.Entry, destination));
                progress?.Report(new Progress(completed.Count, plan.Items.Count, null, destination));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return new Result(completed.AsReadOnly(), true);
            }
            catch (Exception error)
            {
                throw new BatchExportException(item.Entry, completed.AsReadOnly(), error);
            }
            finally
            {
                if (temporary is not null)
                {
                    try { File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
        return new Result(completed.AsReadOnly(), false);
    }

    private static string Publish(string temporary, string desired, HashSet<string> reserved)
    {
        for (var attempt = 0; attempt < 10_000; attempt++)
        {
            var destination = AvailablePath(desired, reserved);
            try { File.Move(temporary, destination, overwrite: false); return destination; }
            catch (IOException) when (File.Exists(destination) || System.IO.Directory.Exists(destination))
            {
                reserved.Add(destination); // Another writer won; never overwrite it.
            }
        }
        throw new IOException("Could not reserve a unique batch export filename.");
    }

    private static string AvailablePath(string desired, HashSet<string> reserved)
    {
        var directory = Path.GetDirectoryName(desired)!;
        var stem = Path.GetFileNameWithoutExtension(desired);
        var extension = Path.GetExtension(desired);
        for (var index = 1; index <= 100_000; index++)
        {
            var path = index == 1 ? desired : Path.Combine(directory, stem + "-" + index + extension);
            if (!reserved.Contains(path) && !File.Exists(path) && !System.IO.Directory.Exists(path)) return path;
        }
        throw new IOException("Too many conflicting batch export filenames.");
    }

    private static string SafeStem(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var stem = new string(value.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (stem.Length == 0) stem = "Image";
        if (stem.Length > 100) stem = stem[..100].TrimEnd();
        return stem;
    }

    // A batch entry keeps the settings it was captured with; a denoise file finished in the meantime is not a change.
    private static HostImageSource.Revision ReadRevision(ICoreVariant variant) => HostImageSource.ReadRevision(variant).Counters;
    private static HostImageSource.Revision ReadSettledRevision(ICoreVariant variant) => HostImageSource.ReadSettledRevision(variant).Counters;

    private static void RequireSingleRecipe(HostProfile.Selection active)
    {
        var getManager = active.Manager.GetType().GetMethod("GetDestinationDocumentManager", new[] { typeof(ICaptureOneDocument) })
            ?? throw new MissingMethodException("Capture One destination manager is unavailable.");
        var manager = getManager.Invoke(active.Manager, new object[] { active.Document }) as IDestinationDocumentManager
            ?? throw new InvalidOperationException("Capture One export recipes are unavailable.");
        var enabled = manager.EnabledDestinations;
        ValidateSingleRecipe(enabled.Count, enabled.Count == 1 ? enabled[0].Name : null, manager.SelectedDestination?.Name);
    }

    internal static void ValidateSingleRecipe(int enabledCount, string? enabledName, string? selectedName)
    {
        if (enabledCount != 1 || string.IsNullOrEmpty(selectedName) || !StringComparer.Ordinal.Equals(enabledName, selectedName))
            throw new InvalidOperationException("Enable and select one Export Recipe for a Spektrafilm batch.");
    }
}
