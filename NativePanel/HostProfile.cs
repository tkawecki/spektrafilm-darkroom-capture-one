using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using P1.C1.Core;
using P1.C1.Core.Interfaces;
using P1.C1.Core.ModelEntities;
using P1.ModelCoreWrapper;

internal static class HostProfile
{
    internal sealed record Target(Guid VariantId, string SourcePath)
    {
        internal string SourceFormat => Path.GetExtension(SourcePath).TrimStart('.').ToUpperInvariant();
    }

    internal static Target CaptureTarget(object window)
    {
        var variant = ReadSelection(window).Variant;
        var image = variant.CoreImage ?? throw new InvalidOperationException("The selected variant has no source image.");
        return new Target(variant.UUID, image.FullpathFilename);
    }

    internal static (object Variant, Type Type, Guid Id) SelectedVariant(object window)
    {
        var selection = ReadSelection(window);
        return (selection.Variant, typeof(ICoreVariant), selection.Variant.UUID);
    }

    internal static Selection ReadSelection(object window)
    {
        if (window is not Control { IsDisposed: false, IsHandleCreated: true, InvokeRequired: false }
            || SelectionAccess.DocumentManager.GetValue(null) is not { } manager
            || SelectionAccess.ActiveDocumentPath.GetValue(manager) is not string path
            || !ApprovedCatalog.IsApproved(path)
            || SelectionAccess.ActiveDocument.GetValue(manager) is not ICaptureOneDocument document
            || !ReferenceEquals(SelectionAccess.GetMainForm.Invoke(null, new object[] { document }), window)
            || !StartupHook.IsBoundDocument(document, window, path))
            throw new InvalidOperationException("Waiting for Spektrafilm to attach to the active catalog or session.");

        if (((ISelectedCollectionsProvider)document).PrimarySelectedCollection is not ISimpleCollection collection
            || collection.PrimaryVariant is not ICoreVariant variant)
            throw new InvalidOperationException("Select an image in the Viewer first.");
        return new Selection(manager, document, variant);
    }

    internal readonly record struct Selection(object Manager, ICaptureOneDocument Document, ICoreVariant Variant);

    /// <summary>The catalog or session file of the active document.</summary>
    internal static string? CatalogPath(object manager) => SelectionAccess.ActiveDocumentPath.GetValue(manager) as string;

    /// <summary>Variants of the collection shown in the browser, by ID (for resuming an export).</summary>
    internal static IReadOnlyDictionary<Guid, ICoreVariant> CollectionVariants(ICaptureOneDocument document)
    {
        var result = new Dictionary<Guid, ICoreVariant>();
        if (((ISelectedCollectionsProvider)document).PrimarySelectedCollection is ISimpleCollection collection)
            foreach (var variant in collection.GetAllFilteredVariants().OfType<ICoreVariant>()) result.TryAdd(variant.UUID, variant);
        return result;
    }

    private static class SelectionAccess
    {
        private static readonly Type Program = Assembly.Load("CaptureOne").GetType("P1.C1.Program", throwOnError: true)!;
        internal static readonly PropertyInfo DocumentManager = RequireProperty(Program, "DocumentManager");
        internal static readonly PropertyInfo ActiveDocumentPath = RequireProperty(DocumentManager.PropertyType, "ActiveDocumentPath");
        internal static readonly PropertyInfo ActiveDocument = RequireProperty(DocumentManager.PropertyType, "ActiveDocument");
        internal static readonly MethodInfo GetMainForm = Program.GetMethod("GetMainForm", new[] { typeof(ICaptureOneDocument) })
            ?? throw new MissingMethodException(Program.FullName, "GetMainForm");

        private static PropertyInfo RequireProperty(Type type, string name) =>
            type.GetProperty(name) ?? throw new MissingMemberException(type.FullName, name);
    }
}
