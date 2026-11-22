using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal sealed partial class SpatialPreview
{
    private PaperDecorationSettings paperDecoration = PaperDecorationSettings.Default;
    private PaperDecorationSettings? renderedPaper;
    private readonly TextBlock paperSummary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 5) };
    private static readonly ConcurrentDictionary<string, PaperTextureStore> PaperStores = new(StringComparer.OrdinalIgnoreCase);
    // User data next to panel.log (like Panel preferences.json): textures survive
    // renderer/runtime updates, and a copied test instance keeps its own store.
    private static PaperTextureStore CurrentPaperStore => PaperStores.GetOrAdd(PaperTextureDirectory, root => new(root));
    internal static string PaperTextureDirectory
    {
        get
        {
            var log = Environment.GetEnvironmentVariable("SPEKTRAFILM_PANEL_LOG");
            var userData = string.IsNullOrWhiteSpace(log)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FilmToolsForCaptureOne")
                : Path.GetDirectoryName(Path.GetFullPath(log))!;
            return Path.Combine(userData, "Paper textures");
        }
    }

    private FrameworkElement BuildPaper()
    {
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = "Paper textures and a burned-in border are applied after color encoding and scan geometry. Active texture layers clamp the finished image to the paper range, including linear DNG. Show source bypasses them. Texture files are copied into a content-addressed store.", TextWrapping = TextWrapping.Wrap });
        var edit = new Button { Content = "Edit paper layers…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 4) };
        edit.Click += (_, _) => {
            try {
                var revision = inputRevision; var sourceIdentity = HostImageSource.GetSelectedRevision(window);
                var edited = PaperDecorationEditor.Edit(Window.GetWindow(Element), paperDecoration, CurrentPaperStore);
                if (edited is null) return;
                if (disposed || inputRevision != revision || HostImageSource.GetSelectedRevision(window) != sourceIdentity)
                    throw new InvalidOperationException("The photo or settings changed while editing paper layers. Reopen the editor for the current photo.");
                paperDecoration = edited; UpdatePaperSummary(); OnSettingsChanged();
            } catch (Exception error) { status.Text = error.Message; }
        };
        body.Children.Add(edit); body.Children.Add(paperSummary); UpdatePaperSummary();
        return Section("Paper textures and border", body);
    }
    // As in the reference implementation, a paper finish without a visible texture layer changes nothing, not even with the
    // border switched on. Say so instead of naming a border that will not appear.
    private void UpdatePaperSummary() => paperSummary.Text = !paperDecoration.Active ? "Paper finish disabled."
        : paperDecoration.IsIdentity ? (paperDecoration.PaddingBurn && paperDecoration.PaddingFraction > 0
            ? "No visible texture layer: the paper finish and its border have no effect. Add a visible texture layer."
            : "No visible texture layer: the paper finish has no effect.")
        : $"{paperDecoration.Layers.Length} texture layer(s)" + (paperDecoration.PaddingBurn ? $" · border {paperDecoration.PaddingFraction:P0} of the long edge" : " · no burned-in border");

    private static PaperDecorationResult DecoratePaper(ReadOnlyMemory<float> pixels, int width, int height, PaperDecorationSettings paper, CancellationToken token) =>
        PaperDecoration.ApplyRgba(pixels, width, height, paper, id => CurrentPaperStore.Load(id, token), includePadding: true, token: token);

    private static BitmapSource DecodeDecoratedPreview(HostImageSource.Frame frame, byte[]? rendered, double strength,
        PreviewInputSpace input, string encoding, CancellationToken token, FinishingTransform finishing, ScanGeometrySettings geometry, PaperDecorationSettings paper)
    {
        if (paper.IsIdentity) return DecodeGeometryPreview(frame, rendered, strength, input, encoding, token, finishing, geometry);
        var crop = GeometryLayout(frame).Crop;
        var finished = DecodeFinishedFloat(frame, rendered, strength, input, encoding, crop, token, finishing, ExportRgbSpace.Srgb);
        var transformed = ScanGeometry.ApplyRgba(finished, crop.Width, crop.Height, geometry, token);
        var result = DecoratePaper(transformed.Pixels, transformed.Width, transformed.Height, paper, token);
        return PackFinishedFloat(result.Pixels, result.Width, result.Height, PixelFormats.Bgra32, token);
    }

    private static (int PixelWidth, int PixelHeight, string Detail) WritePaperExportImage(string path,
        HostImageSource.Frame source, byte[]? pixels, double strength, PreviewInputSpace input, string encoding,
        System.Drawing.Rectangle crop, CancellationToken token, FinishingTransform finishing,
        ExportRgbSpace color, string? profile, int jpegQuality, ScanGeometrySettings geometry, PaperDecorationSettings paper,
        bool intermediate = false)
    {
        if (paper.IsIdentity) return WriteGeometryExportImage(path, source, pixels, strength, input, encoding, crop, token, finishing, color, profile, jpegQuality, geometry, intermediate);
        var target = ExportFileOptions.IsDng(path) ? ExportRgbSpace.LinearRec2020 : color;
        var finished = DecodeFinishedFloat(source, pixels, strength, input, encoding, crop, token, finishing, target);
        var transformed = ScanGeometry.ApplyRgba(finished, crop.Width, crop.Height, geometry, token);
        var result = DecoratePaper(transformed.Pixels, transformed.Width, transformed.Height, paper, token);
        if (ExportFileOptions.IsDng(path)) {
            var info = SpektraExport.SaveLinearDng(path, result.Pixels, result.Width, result.Height, token);
            return (result.Width, result.Height, $" · paper finish · linear Rec.2020 DNG; headroom {info.HeadroomStops} EV; {info.NegativeSamples} negative and {info.OverRangeSamples} over-range samples clipped");
        }
        var output = PackFinishedFloat(result.Pixels, result.Width, result.Height, ExportFileOptions.WorkingFormat(path), token);
        if (ExportFileOptions.IsJpeg(path)) SpektraExport.SaveJpegWithProfile(path, output, token, profile, jpegQuality);
        else SpektraExport.SaveWithProfile(path, output, token, profile, intermediate);
        return (result.Width, result.Height, " · paper finish" + (ExportFileOptions.IsJpeg(path) ? $" · JPEG quality {jpegQuality}" : " · 16-bit"));
    }
}
