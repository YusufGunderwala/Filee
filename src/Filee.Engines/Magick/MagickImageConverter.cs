// Raster image ↔ raster image conversion with ImageMagick (Magick.NET, Apache-2.0).

using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;
using ImageMagick;

namespace Filee.Engines.Magick;

/// <summary>
/// Converts between PNG, JPG, WEBP, TIFF, BMP, GIF, ICO, AVIF, JPEG XL, JPEG 2000, PSD/PSB, TGA, PPM, APNG, MNG, QOI,
/// DDS, PCX, CUR, XPM, XBM, WBMP, PAM, PFM, SGI, Sun Raster and MIFF, and reads HEIC, XCF, camera RAW, DICOM,
/// Photo CD, OpenEXR, Radiance HDR and (on Windows) EMF/WMF.
/// </summary>
public sealed class MagickImageConverter : IConverter, ITiffMerger, IImageTransformer
{
    public string Id => "magick";
    public string DisplayName => "ImageMagick";
    public int MaxParallelism => 0;

    public bool Transforms(string from, string to) => true;

    public IReadOnlyList<ConversionEdge> Edges { get; } =
        (from source in ImageEncoder.Sources
         from target in ImageEncoder.Writable
         select ImageEncoder.Edge(source, target)).ToList();

    public EngineStatus GetStatus() =>
        EngineStatus.Available(MagickNET.ImageMagickVersion,
            $"{EngineVersions.Library("Magick.NET", typeof(MagickImage))} · ImageMagick {ImageMagickNumber()}");

    /// <summary>"7.1.2-5" out of "ImageMagick 7.1.2-5 Q8 x64 …".</summary>
    private static string ImageMagickNumber() =>
        MagickNET.ImageMagickVersion.Split(' ').FirstOrDefault(part => part.Length > 0 && char.IsAsciiDigit(part[0])) ?? "?";

    /// <summary>
    /// Writes the pages into one multi-page TIFF. Each page is flattened onto the preset's background (no alpha) at 8 bits
    /// and marked as a page of a multi-page document (SubfileType 2), so older viewers (Microsoft Office Document
    /// Imaging, fax software) show it page by page; compression is the preset's.
    /// </summary>
    public Task MergeAsync(IReadOnlyList<string> inputPaths, string outputPath, Filee.Core.Presets.ImageOptions options,
        CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var compression = options.TiffCompression switch
            {
                Filee.Core.Presets.TiffCompression.Lzw => CompressionMethod.LZW,
                Filee.Core.Presets.TiffCompression.Zip => CompressionMethod.Zip,
                _ => CompressionMethod.NoCompression,
            };
            using var pages = new MagickImageCollection();
            foreach (var path in inputPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var source = new MagickImageCollection(path);
                foreach (var frame in source)
                {
                    var page = frame.Clone();
                    page.BackgroundColor = new MagickColor(string.IsNullOrWhiteSpace(options.Background) ? "#FFFFFF" : options.Background);
                    page.Alpha(AlphaOption.Remove);
                    page.Depth = 8;
                    page.Settings.Compression = compression;
                    page.SetAttribute("tiff:subfiletype", "2");
                    pages.Add(page);
                }
            }
            pages.Write(outputPath, MagickFormat.Tiff);
        }, cancellationToken);

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            // Animated PNGs are often saved as .png: read them as APNG, so they stay animated as GIF or WebP.
            var from = step.From == "png" && Apng.IsAnimated(step.InputPath) ? "apng" : step.From;
            using var images = ImageReader.Read(step.InputPath, from, step.Preset);
            return Write(images, from, step, progress, cancellationToken);
        }, cancellationToken);

    /// <summary>
    /// The rest of a conversion once the picture is read (also for pictures another engine decoded, see
    /// Wic/WicImageConverter): the frames kept for the target, the preset's options, the files.
    /// </summary>
    /// <param name="from">Format the frames were read from (animations and icons keep different frames).</param>
    internal static IReadOnlyList<string> Write(MagickImageCollection images, string from, ConversionStep step,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(0.3);

        var animation = ImageEncoder.KeepsAnimation(from, step.To);
        var frames = ImageEncoder.SelectFrames(images, from, step.To);
        // Before the options: frames dropped for speed aren't resized for nothing.
        if (animation && step.Preset.Image.ChangesSpeed)
            frames = AnimationTiming.ChangeSpeed(frames, step.Preset.Image.PlaybackSpeed, step.To);
        foreach (var frame in frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImageEncoder.ApplyOptions(frame, step.Preset.Image, step.To);
        }
        progress?.Report(0.7);

        var written = ImageEncoder.Write(frames, step.To, step.Output, step.Preset.Image, step.InputPath, animation);
        if (step.Preset.TargetSizeBytes is > 0)
            EnforceTargetSize(written, frames, step.To, step.Preset.TargetSizeBytes.Value);
        progress?.Report(1);
        return written;
    }

    private static void EnforceTargetSize(IReadOnlyList<string> written, IReadOnlyList<IMagickImage<byte>> frames, string targetFormat, long targetSizeBytes)
    {
        if (written.Count == 0 || frames.Count == 0 || targetSizeBytes <= 0)
            return;

        var (format, _) = ImageEncoder.OutputFormat(targetFormat, new Filee.Core.Presets.ImageOptions());

        for (var i = 0; i < written.Count; i++)
        {
            var path = written[i];
            if (!File.Exists(path))
                continue;

            var fi = new FileInfo(path);
            if (fi.Length <= targetSizeBytes)
                continue;

            var frameIndex = Math.Min(i, frames.Count - 1);
            using var candidate = frames[frameIndex].Clone();

            var qualityLevels = new[] { 75, 60, 45, 30, 20, 10 };
            var squeezed = false;

            foreach (var q in qualityLevels)
            {
                candidate.Quality = (uint)q;
                candidate.Write(path, format);
                if (new FileInfo(path).Length <= targetSizeBytes)
                {
                    squeezed = true;
                    break;
                }
            }

            if (!squeezed)
            {
                var scaleFactors = new[] { 0.8, 0.6, 0.45, 0.3 };
                foreach (var factor in scaleFactors)
                {
                    var newWidth = Math.Max(16, (int)(candidate.Width * factor));
                    var newHeight = Math.Max(16, (int)(candidate.Height * factor));
                    candidate.Resize((uint)newWidth, (uint)newHeight);
                    candidate.Quality = 35;
                    candidate.Write(path, format);
                    if (new FileInfo(path).Length <= targetSizeBytes)
                        break;
                }
            }
        }
    }
}
