using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using ImgCore;
using P1.C1.Components;
using P1.C1.Components.DestinationProcessing;
using P1.C1.Core;
using P1.C1.Core.DestinationProcessing;
using P1.C1.Core.Interfaces;
using P1.C1.Core.ModelEntities;
using P1.C1.ImageCoreWrapper.ImageCore;
using P1.ModelCoreWrapper;

/// <summary>
/// Capture One's own export machinery, used to write a finished Spektrafilm render through an export recipe:
/// the recipes Capture One would use, its output paths and tokens, its metadata, watermark, output sharpening and
/// file writers. The render reaches Capture One as a 16-bit TIFF opened as a temporary image outside the catalog
/// (the mechanism Capture One uses for Match Look references); nothing is added to the catalog or the recipes.
/// Checked against Capture One 16.9.0.3894; <see cref="Unavailable"/> names what is missing in another version.
/// </summary>
internal static class HostRecipeWriter
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private const int ProxyEdge = 2560;

    private sealed record Members(MethodInfo Recipes, MethodInfo FullPath, MethodInfo CreateTemporary,
        MethodInfo ResolveWatermark, MethodInfo? KeywordWhitelist, FieldInfo Capabilities,
        MethodInfo ReadCounter, MethodInfo WriteCounter);

    private static readonly Lazy<(Members? Members, string? Missing)> Resolved = new(Resolve);

    /// <summary>Null when every Capture One member this writer needs exists; otherwise what is missing.</summary>
    internal static string? Unavailable => Resolved.Value.Missing;

    private static Members Required => Resolved.Value.Members
        ?? throw new InvalidOperationException("This Capture One version does not offer what recipe export needs: " + Resolved.Value.Missing);

    private static (Members?, string?) Resolve()
    {
        var missing = new List<string>();
        T? Find<T>(T? member, string name) where T : class { if (member is null) missing.Add(name); return member; }
        var developer = typeof(DeveloperManager);
        var recipes = Find(developer.GetMethod("GetDestinationProcessingSettings", Any), "DeveloperManager.GetDestinationProcessingSettings");
        var fullPath = Find(developer.GetMethod("GetFullPath", Any), "DeveloperManager.GetFullPath");
        var provider = typeof(ICoreVariant).Assembly.GetType("P1.C1.Core.ModelEntities.Internals.Collections.TemporaryImageProvider");
        var temporary = Find(provider?.GetMethod("CreateTemporaryImage", Any), "TemporaryImageProvider.CreateTemporaryImage");
        var watermark = Find(typeof(VariantProcessSettingsFactory).GetMethod("ResolveWatermarkText", Any), "VariantProcessSettingsFactory.ResolveWatermarkText");
        var capabilities = Find(typeof(DestinationProcessingSettings).GetField("m_capabilities", Any), "DestinationProcessingSettings.m_capabilities");
        var whitelist = developer.Assembly.GetType("P1.C1.Components.DestinationProcessing.DeveloperTaskBuilder")
            ?.GetMethod("GetKeywordLibraryWhiteList", Any);
        var naming = developer.Assembly.GetType("P1.C1.Components.DestinationNaming.NamingManager")?.GetMethods(Any);
        // GetCounterValue(type?) : int and SetCounterValue(int, type?); a null type means the manager's own counter.
        var readCounter = Find(naming?.FirstOrDefault(m => m.Name == "GetCounterValue" && m.ReturnType == typeof(int)
            && m.GetParameters() is [{ } type] && Nullable.GetUnderlyingType(type.ParameterType) is not null), "NamingManager.GetCounterValue");
        var writeCounter = Find(naming?.FirstOrDefault(m => m.Name == "SetCounterValue" && m.GetParameters() is [{ } value, { } type]
            && value.ParameterType == typeof(int) && Nullable.GetUnderlyingType(type.ParameterType) is not null), "NamingManager.SetCounterValue");
        if (missing.Count > 0) return (null, string.Join(", ", missing));
        return (new Members(recipes!, fullPath!, temporary!, watermark!, whitelist, capabilities!, readCounter!, writeCounter!), null);
    }

    /// <summary>The counter Capture One's export uses for counter tokens in file names.</summary>
    internal static int ExportCounter(object documentManager, ICaptureOneDocument document) =>
        (int)Required.ReadCounter.Invoke(Invoke(documentManager, "GetExportNamingManager", document), new object?[] { null })!;

    internal static void SetExportCounter(object documentManager, ICaptureOneDocument document, int value) =>
        Required.WriteCounter.Invoke(Invoke(documentManager, "GetExportNamingManager", document), new object?[] { value, null });

    /// <summary>Capture One's export leaves these recipes out, so they take no number from the counter.</summary>
    internal static bool OutsideCaptureOneExport(IDestinationProcessingSettings recipe) => recipe.FileType == FileFormat.Affinity;

    /// <summary>The recipes Capture One's own export would use for this variant, with its own filtering.</summary>
    internal static IReadOnlyList<IDestinationProcessingSettings> RecipesFor(ICaptureOneDocument document, ICoreVariant variant)
    {
        var image = VariantImage(variant);
        var geometry = VariantGeometryTransformer.FromVariant(variant);
        var settings = variant.GetProcessSettingsNoLocalAdjLayers(true);
        var result = Required.Recipes.Invoke(null, new object?[] { document, image, geometry, settings }) as IEnumerable
            ?? throw new InvalidOperationException("Capture One returned no export recipes.");
        return result.Cast<IDestinationProcessingSettings>().ToList().AsReadOnly();
    }

    /// <summary>
    /// The output path Capture One's export would use (location, subfolders, tokens), computed with the same
    /// function. The shared export naming manager is set as Capture One sets it and then put back.
    /// counter: the export counter noted before the image's first recipe. Capture One puts it back before every
    /// recipe, so all files of one image get the same number and the counter moves once per image.
    /// </summary>
    internal static string PlannedPath(object documentManager, ICaptureOneDocument document,
        IDestinationProcessingSettings recipe, ICoreVariant variant, DateTime exportDate, int counter)
    {
        var naming = Invoke(documentManager, "GetExportNamingManager", document);
        var subfolders = Invoke(documentManager, "GetOutputRecipeSubFolderNamingManager", document);
        var representation = naming.GetType().GetProperty("TokenNameRepresentation", Any)
            ?? throw new MissingMemberException(naming.GetType().FullName, "TokenNameRepresentation");
        var output = (document as IStandardLocationsProvider)?.OutputLocationProvider?.AbsoluteFileSystemPath;
        var collection = (document as ISelectedCollectionsProvider)?.PrimarySelectedCollection?.Name;
        var saved = representation.GetValue(naming);
        try
        {
            representation.SetValue(naming, recipe.DestinationFilename);
            Required.WriteCounter.Invoke(naming, new object?[] { counter, null });
            var path = Required.FullPath.Invoke(null, new object?[] { document, recipe, collection, naming, subfolders,
                VariantImage(variant), variant, output, false, exportDate }) as string;
            // A catalog without an output location gives a bare file name for a recipe that writes to "Output
            // Location"; resolving it would send the file to the program's working folder.
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                throw new InvalidOperationException("this document has no output location. "
                    + "Choose a folder or the image folder as the recipe's destination.");
            return Path.GetFullPath(path);
        }
        finally { representation.SetValue(naming, saved); }
    }

    /// <summary>The variant's metadata as Capture One's export writes it, following the recipe's metadata choices.</summary>
    internal static string Metadata(ICoreVariant variant, IDestinationProcessingSettings recipe)
    {
        if (recipe is not IMetadataProcessingSettings metadata) return string.Empty;
        var whitelist = Required.KeywordWhitelist?.Invoke(null, new object[] { recipe }) as IEnumerable<string>;
        // Without Capture One's whitelist a null list means "all keywords": refuse rather than export keywords the
        // recipe leaves out (after upstream a00f9d7, Spektrafilm for Capture One, Luka Kauzlaric, GPL-3.0).
        if (Required.KeywordWhitelist is null && metadata.MetadataIncludeKeywords == MetadataIncludeKeywordsMode.Whitelist)
            throw new InvalidOperationException("this Capture One version does not tell which keyword libraries the recipe "
                + "exports. Choose all keywords or none in the recipe's Metadata tab.");
        return variant.SerializeVariantInfoAsXmp(metadata.MetadataIncludeKeywords, whitelist, metadata.MetadataIncludeRating,
            metadata.MetadataIncludeCopyright, metadata.MetadataIncludeGPS, metadata.MetadataIncludeCameraData,
            metadata.MetadataIncludeAllOther) ?? string.Empty;
    }

    /// <summary>The recipe's watermark text with its tokens resolved for this variant, or null without a text watermark.</summary>
    internal static string? WatermarkText(ICaptureOneDocument document, ICoreVariant variant, IDestinationProcessingSettings recipe) =>
        recipe.WatermarkType == WatermarkType.Text
            ? Required.ResolveWatermark.Invoke(null, new object?[] { variant, recipe, document }) as string ?? string.Empty
            : null;

    /// <summary>An in-memory copy of a recipe (its model recipe is deep-copied); the stored recipe is never changed.</summary>
    internal static DestinationProcessingSettings Detached(IDestinationProcessingSettings recipe)
    {
        var copy = (DestinationProcessingSettings)Activator.CreateInstance(typeof(DestinationProcessingSettings),
            recipe, Required.Capabilities.GetValue(recipe))!;
        copy.Name = recipe.Name; // the copy constructor names it "Untitled Recipe"
        return copy;
    }

    internal static RecipeFormat FormatOf(IDestinationProcessingSettings recipe) => recipe.FileType switch
    {
        FileFormat.Dng => RecipeFormat.LinearDng,
        FileFormat.Original or FileFormat.Affinity => RecipeFormat.Unsupported,
        FileFormat.Png => RecipeFormat.Png,
        _ => RecipeFormat.CaptureOne,
    };

    /// <summary>
    /// The space the panel renders in for this recipe: sRGB and Adobe RGB pass through Capture One unchanged;
    /// any other output profile gets a ProPhoto render that Capture One converts.
    /// </summary>
    internal static ExportRgbSpace RenderSpaceFor(IDestinationProcessingSettings recipe) =>
        RecipeExportPlanning.RenderSpaceForProfile(recipe.ColorProfileName, recipe.ColorProfilePath);

    private static object VariantImage(ICoreVariant variant) =>
        variant.GetType().GetProperty("Image", Any)?.GetValue(variant)
        ?? throw new InvalidOperationException("The variant has no image.");

    private static object Invoke(object target, string method, params object[] arguments)
    {
        var info = target.GetType().GetMethods(Any).Single(m => m.Name == method && m.GetParameters().Length == arguments.Length);
        return info.Invoke(target, arguments) ?? throw new InvalidOperationException($"Capture One returned nothing from {method}.");
    }

    /// <summary>
    /// A finished render opened by Capture One as a temporary image, with its own proxy next to it. Open, prepare
    /// and dispose on Capture One's UI thread; only <see cref="Process"/> runs on a worker thread.
    /// </summary>
    internal sealed class Intermediate : IDisposable
    {
        private readonly object image;
        private bool disposed;
        internal ICoreVariant Variant { get; }
        internal IImageCoreImage Handle { get; }
        internal string Path { get; }
        internal string Proxy { get; }
        internal int Width { get; }
        internal int Height { get; }

        // Capture One's settings for the rendered file (its own film curve, embedded profile, no sharpening). Read
        // anew for every file, as Capture One's export does: the settings point into Capture One's memory (the
        // film curve), which it may release and reload between files.
        internal IC_ProcessSettings ReadSettings() => Variant.GetICProcessSettings();

        private Intermediate(object image, ICoreVariant variant, IImageCoreImage handle, string path, string proxy, int width, int height)
        {
            this.image = image; Variant = variant; Handle = handle; Path = path; Proxy = proxy; Width = width; Height = height;
        }

        internal static Intermediate Open(ICaptureOneDocument document, string tiffPath, int width, int height)
        {
            var image = Required.CreateTemporary.Invoke(null,
                    new object?[] { document, tiffPath, DateTime.Now, ProcessingEngineHelper.CurrentVersionEngine, null, false, false })
                ?? throw new InvalidOperationException("Capture One could not open the rendered image.");
            try
            {
                var variant = ((IEnumerable)(image.GetType().GetProperty("CoreVariants", Any)?.GetValue(image)
                        ?? throw new InvalidOperationException("The rendered image has no variant."))).Cast<ICoreVariant>().First();
                var handle = image.GetType().GetProperty("Handle", Any)?.GetValue(image) as IImageCoreImage
                    ?? throw new InvalidOperationException("Capture One did not open the rendered image.");
                var proxy = System.IO.Path.ChangeExtension(tiffPath, ".cop");
                using (var maker = new ImageCoreProcessor("Spektrafilm recipe proxy"))
                {
                    var made = maker.CreateFileProxy(new SourceSettings(handle, string.Empty), proxy, IC_CacheFlags.Cache_Nothing, false, false, ProxyEdge);
                    if (made != IC_Result.resOK) throw new InvalidOperationException("Capture One could not prepare the rendered image (" + made + ").");
                }
                return new Intermediate(image, variant, handle, tiffPath, proxy, width, height);
            }
            catch
            {
                Release(image);
                throw;
            }
        }

        internal SourceSettings Source => new(Handle, Proxy);

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Release(image);
            foreach (var file in new[] { Proxy, System.IO.Path.ChangeExtension(Path, ".cof") })
                try { File.Delete(file); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { StartupHook.Log("Spektrafilm could not remove " + file + ": " + error.Message); }
        }

        private static void Release(object image)
        {
            try { image.GetType().GetMethod("ReleaseManagedResources", Any, Type.EmptyTypes)?.Invoke(image, null); }
            catch (Exception error) { StartupHook.Log("Spektrafilm could not release a temporary image: " + error.GetBaseException().Message); }
        }
    }

    internal sealed record WriteRequest(IDestinationProcessingSettings Recipe, string OutputPath, string Metadata,
        string? WatermarkText, Rectangle? CropPath);

    /// <summary>
    /// Settings and destination for one file. Keeps alive, until the file is written, the Capture One objects they
    /// were made from (the file destination with its overlays, the recipe copy) and the handles they refer to.
    /// </summary>
    internal sealed class PreparedWrite(IC_ProcessSettings settings, IC_FileDestination destination, IDisposable keepAlive,
        params object[] owners) : IDisposable
    {
        internal IC_ProcessSettings Settings { get; } = settings;
        internal IC_FileDestination Destination { get; } = destination;
        // Holding them here keeps them reachable for as long as this object is; Process keeps this object alive.
        private readonly object[] owners = owners;
        public void Dispose() => keepAlive.Dispose();
    }

    /// <summary>
    /// UI thread: the recipe's file destination, metadata, output sharpening and watermark for the rendered image,
    /// as Capture One prepares them for its own export.
    /// </summary>
    internal static PreparedWrite Prepare(Intermediate source, WriteRequest request)
    {
        var recipe = request.Recipe;
        var settings = source.ReadSettings();
        var size = new Size(source.Width, source.Height);
        if (request.CropPath is { } crop)
        {
            settings.cropIsRelative = false;
            settings.cropCenterX = crop.X + crop.Width / 2f;
            settings.cropCenterY = crop.Y + crop.Height / 2f;
            settings.cropWidth = crop.Width;
            settings.cropHeight = crop.Height;
            size = crop.Size;
        }
        else if (recipe.CropExportMethod == CropExportMethod.AsPath)
        {
            // The render is already cropped (scan geometry or paper reshape the frame): no path to write.
            var copy = Detached(recipe);
            copy.CropExportMethod = CropExportMethod.Default;
            recipe = copy;
        }
        var file = new FileDestination { FileName = request.OutputPath };
        DestinationProcessingSettings.PrepareFileDestination(recipe, file, size);
        file.Scale = 1; // the render already has the recipe's size
        file.XMPSerializedVariantInfo = request.Metadata;
        file.LoadOverlayInfo(ref settings);
        settings = file.OverrideSettings(settings, false);
        if (request.WatermarkText is not null || recipe.WatermarkType == WatermarkType.Image)
        {
            var geometry = VariantGeometryTransformer.FromProcessSettings(ref settings);
            float overlayScale = 1;
            if (source.Handle.Handle is { } handle && (ImgCoreDll.IC_GetThumbnailScale(handle, out overlayScale) != IC_Result.resOK
                || !float.IsFinite(overlayScale) || overlayScale <= 0)) overlayScale = 1;
            VariantProcessSettingsUpdater.UpdateIcProcessSettingsForWatermark(true, ProcessingMode.File, 1f, overlayScale,
                request.WatermarkText ?? string.Empty, geometry, ref settings, recipe, false);
        }
        var destination = file.GetICFileDestination(System.IO.Path.GetFileNameWithoutExtension(recipe.ColorProfileName ?? string.Empty));
        var empty = new UTF8BytyesSafeHandle(string.Empty);
        destination.xmpMetadata ??= empty;
        destination.c2paAuthor ??= empty;
        return new PreparedWrite(settings, destination, empty, file, recipe, source);
    }

    /// <summary>Worker thread: Capture One's image core writes the prepared file.</summary>
    internal static void Process(Intermediate source, PreparedWrite prepared, string outputPath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var processor = new ImageCoreProcessor("Spektrafilm recipe export");
        var sourceSettings = source.Source;
        Check(processor.SetSource(sourceSettings), "open the rendered image");
        Check(processor.AddFileDestination(prepared.Destination), "prepare the output file");
        Check(processor.ProcessToFiles(prepared.Settings, IC_ProcessQuality.pqBest, Cancel), "write the output file");
        // The source and the prepared write must outlive the image core's work on them.
        GC.KeepAlive(sourceSettings);
        GC.KeepAlive(prepared);
        token.ThrowIfCancellationRequested();
        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            throw new IOException("Capture One did not write " + System.IO.Path.GetFileName(outputPath) + ".");

        void Cancel(IC_CallbackType type, int value, ref IC_CallbackReturnValue response, nint context)
        {
            if (type == IC_CallbackType.cbtProgress && token.IsCancellationRequested) response = IC_CallbackReturnValue.cbrvAbort;
        }
        void Check(IC_Result result, string step)
        {
            token.ThrowIfCancellationRequested();
            if (result != IC_Result.resOK) throw new IOException($"Capture One could not {step} ({result}).");
        }
    }
}

internal enum RecipeFormat { CaptureOne, Png, LinearDng, Unsupported }
