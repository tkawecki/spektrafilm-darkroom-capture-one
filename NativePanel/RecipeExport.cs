using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using P1.C1.Core;
using P1.C1.Core.DestinationProcessing;

/// <summary>One output of a recipe export: a variant written through one recipe.</summary>
internal sealed record RecipeJob(int Index, BatchExportSupport.Entry Entry, IDestinationProcessingSettings? Recipe,
    PlannedFile File, RecipeFormat Format, ExportRgbSpace Space, string Metadata, string? Watermark);

/// <summary>Everything decided before the export starts; the journal records it for a resume.</summary>
internal sealed record RecipeExportPlan(string Catalog, IReadOnlyList<RecipeJob> Jobs, IReadOnlyList<string> Notes,
    ExportJournal Journal, bool IsResume)
{
    /// <summary>Puts Capture One's export counter back when the planned export does not start (the list is cancelled).</summary>
    internal Action? ReleaseNumbers { get; init; }
    internal IEnumerable<RecipeJob> Writing => Jobs.Where(job => job.File.Writes);
    internal int FolderCount => Writing.Select(job => Path.GetDirectoryName(job.File.OutputPath)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
}

/// <summary>
/// Builds a recipe export on Capture One's UI thread: the recipes Capture One's export would use, its output
/// paths and tokens, collisions, metadata and watermark text. Rendering and writing happen later, in
/// <see cref="SpatialPreview.RunRecipeExportAsync"/>.
/// </summary>
internal static class RecipeExportPlanner
{
    internal static RecipeExportPlan Build(object window, IReadOnlyList<BatchExportSupport.Entry> entries,
        Func<VariantLook, RecipeExportPlanning.LookDescription?> describe, ExportJournal? resume = null,
        Func<ICoreVariant, IReadOnlyList<IDestinationProcessingSettings>>? recipesFor = null)
    {
        var selection = HostProfile.ReadSelection(window);
        // A resume takes its names from the journal and leaves the counter alone.
        int? counterBefore = resume is null ? HostRecipeWriter.ExportCounter(selection.Manager, selection.Document) : null;
        try { return Plan(selection, entries, describe, resume, recipesFor, counterBefore); }
        catch when (counterBefore is not null)
        {
            // A plan that failed takes no numbers from Capture One's counter.
            try { HostRecipeWriter.SetExportCounter(selection.Manager, selection.Document, counterBefore.Value); }
            catch (Exception error) { StartupHook.Log("The export counter could not be put back: " + error.GetBaseException().Message); }
            throw;
        }
    }

    private static RecipeExportPlan Plan(HostProfile.Selection selection, IReadOnlyList<BatchExportSupport.Entry> entries,
        Func<VariantLook, RecipeExportPlanning.LookDescription?> describe, ExportJournal? resume,
        Func<ICoreVariant, IReadOnlyList<IDestinationProcessingSettings>>? recipesFor, int? counterBefore)
    {
        var catalog = HostProfile.CatalogPath(selection.Manager)
            ?? throw new InvalidOperationException("Capture One has no active catalog or session.");
        var exportDate = DateTime.Now;
        var notes = new List<string>();
        var requests = new List<(PlanRequest Request, BatchExportSupport.Entry Entry, IDestinationProcessingSettings? Recipe)>();
        foreach (var entry in entries)
        {
            var variant = entry.Variant ?? throw new InvalidOperationException("The export has no Capture One variant.");
            var name = Path.GetFileName(entry.Target.SourcePath);
            // recipesFor: tests on a copied catalog supply their own detached recipes.
            var recipes = recipesFor?.Invoke(variant) ?? HostRecipeWriter.RecipesFor(selection.Document, variant);
            if (recipes.Count == 0) { notes.Add($"{name}: Capture One has no export recipe for this image."); continue; }
            var planned = resume?.Remaining.Where(item => item.VariantId == entry.Target.VariantId).ToList();
            // As Capture One's export: one counter value per image, the same for all its recipes.
            var noted = planned is null ? HostRecipeWriter.ExportCounter(selection.Manager, selection.Document) : 0;
            foreach (var recipe in recipes)
            {
                CollisionAction collision;
                string desired;
                if (planned is not null)
                {
                    // Each planned file is used once, also when two recipes share a name.
                    if (planned.FirstOrDefault(item => item.RecipeName == recipe.Name) is not { } item) continue;
                    planned.Remove(item);
                    if (WrittenBeforeInterruption(item, resume!))
                    {
                        notes.Add($"{Path.GetFileName(item.OutputPath)} was written before the interruption.");
                        continue;
                    }
                    desired = item.OutputPath;
                    collision = item.Outcome == PlannedOutcome.Replaces ? CollisionAction.Overwrite : CollisionAction.Rename;
                }
                else
                {
                    collision = RecipeExportPlanning.CollisionFrom(recipe.FileOverwriteAction.ToString());
                    var reached = HostRecipeWriter.ExportCounter(selection.Manager, selection.Document);
                    try { desired = HostRecipeWriter.PlannedPath(selection.Manager, selection.Document, recipe, variant, exportDate, noted); }
                    catch (Exception error)
                    {
                        notes.Add($"{name} · {recipe.Name}: {error.GetBaseException().Message}");
                        continue;
                    }
                    finally
                    {
                        if (HostRecipeWriter.OutsideCaptureOneExport(recipe))
                            HostRecipeWriter.SetExportCounter(selection.Manager, selection.Document, reached);
                    }
                }
                var unsupported = HostRecipeWriter.FormatOf(recipe) == RecipeFormat.Unsupported
                    ? $"the recipe format {recipe.FileType} is not a processed image" : null;
                requests.Add((new PlanRequest(entry.Target.VariantId, entry.Target.SourcePath, recipe.Name, desired, collision, unsupported), entry, recipe));
            }
            if (planned is not null)
                foreach (var missing in planned)
                    notes.Add($"{name}: the recipe \"{missing.RecipeName}\" is no longer active, so its file is not exported.");
        }
        var originals = entries.Select(entry => entry.Target.SourcePath).ToArray();
        var files = RecipeExportPlanning.Resolve(requests.Select(r => r.Request), originals,
            path => File.Exists(path) || Directory.Exists(path), SameFile);
        var jobs = new List<RecipeJob>(files.Count);
        // The export works with copies: a recipe edited in Capture One meanwhile does not change what was confirmed.
        var snapshots = new Dictionary<IDestinationProcessingSettings, IDestinationProcessingSettings>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < files.Count; i++)
        {
            var (_, entry, live) = requests[i];
            var recipe = live is null ? null
                : snapshots.TryGetValue(live, out var copy) ? copy : snapshots[live] = HostRecipeWriter.Detached(live);
            var file = files[i];
            var format = recipe is null ? RecipeFormat.Unsupported : HostRecipeWriter.FormatOf(recipe);
            string metadata = string.Empty;
            string? watermark = null;
            var space = ExportRgbSpace.Srgb;
            if (file.Writes && recipe is not null && format != RecipeFormat.LinearDng)
            {
                space = HostRecipeWriter.RenderSpaceFor(recipe);
                metadata = HostRecipeWriter.Metadata(entry.Variant!, recipe);
                var settings = entry.Settings;
                if (settings.Enabled && recipe is IMetadataProcessingSettings { MetadataIncludeAllOther: true } && describe(settings) is { } look)
                    metadata = RecipeExportPlanning.AddLook(metadata, look);
                watermark = HostRecipeWriter.WatermarkText(selection.Document, entry.Variant!, recipe);
            }
            if (file.Writes && format == RecipeFormat.LinearDng)
                notes.Add($"{Path.GetFileName(file.OutputPath)}: linear DNG is written by Spektrafilm, without the recipe's metadata and watermark.");
            jobs.Add(new RecipeJob(i, entry, recipe, file, format, space, metadata, watermark));
        }
        var journalItems = jobs.Select(job => new JournalItem(job.File.VariantId, job.File.SourcePath, job.File.RecipeName,
            job.File.OutputPath, job.File.Outcome, job.File.Writes ? JournalState.Pending : JournalState.Skipped)).ToArray();
        var journal = new ExportJournal(ExportJournal.CurrentVersion, resume?.Id ?? Guid.NewGuid(), catalog, DateTime.UtcNow, journalItems);
        return new RecipeExportPlan(catalog, jobs.AsReadOnly(), notes.AsReadOnly(), journal, resume is not null)
        {
            ReleaseNumbers = counterBefore is { } counter
                ? () => HostRecipeWriter.SetExportCounter(selection.Manager, selection.Document, counter) : null,
        };
    }

    // An output must never replace an original, also through another spelling of the same path.
    private static bool SameFile(string path, string original)
    {
        try
        {
            // A folder the export has yet to create cannot hold the original.
            if (!Directory.Exists(Path.GetDirectoryName(path))) return false;
            OutputPathGuard.Validate(path, original);
            return false;
        }
        catch (IOException) when (!File.Exists(original)) { return false; }
        catch (Exception error) when (error is IOException or ArgumentException or NotSupportedException or UnauthorizedAccessException) { return true; }
    }

    // A file moved to its name just before the export stopped, without the journal hearing of it: its temporary
    // file is gone and the output is newer than the export.
    private static bool WrittenBeforeInterruption(JournalItem item, ExportJournal journal) =>
        item.State == JournalState.Pending && item.Temporary is { } temporary && !File.Exists(temporary)
        && File.Exists(item.OutputPath) && File.GetLastWriteTimeUtc(item.OutputPath) >= journal.CreatedUtc;
}
