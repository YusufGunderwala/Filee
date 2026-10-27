// Extracts text from images using native platform OCR (Windows OCR) and reads QR codes.

using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Platform;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Text;

/// <summary>Extracts text from images using native platform OCR and reads QR codes.</summary>
public sealed class OcrTextConverter : IConverter
{
    private readonly IPlatformServices? _platform;

    public OcrTextConverter(IPlatformServices? platform = null)
    {
        _platform = platform;
    }

    public string Id => "ocr-text";
    public string DisplayName => "OCR & QR Extractor (built-in)";
    public int MaxParallelism => 0;

    public IReadOnlyList<ConversionEdge> Edges { get; } =
    [
        new("jpg", "txt", 15, DirectOnly: true),
        new("jpeg", "txt", 15, DirectOnly: true),
        new("png", "txt", 15, DirectOnly: true),
        new("webp", "txt", 15, DirectOnly: true),
        new("bmp", "txt", 15, DirectOnly: true),
        new("tiff", "txt", 15, DirectOnly: true),
        new("heic", "txt", 15, DirectOnly: true),
        new("avif", "txt", 15, DirectOnly: true),
        new("gif", "txt", 15, DirectOnly: true),
    ];

    public EngineStatus GetStatus() =>
        EngineStatus.Available("OCR & QR Reader", "Native Windows OCR & QR Decoder");

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            progress?.Report(0.1);
            string? text = null;

            // 1. Try QR code decoding first
            try
            {
                text = QrDecoder.DecodeFromFile(step.InputPath);
            }
            catch
            {
                // Continue to OCR
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(0.4);

            // 2. If no QR code found or preset is specifically copy-text, run native OCR
            if (string.IsNullOrWhiteSpace(text) && _platform is not null)
            {
                text = await _platform.ExtractTextFromImageAsync(step.InputPath, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(0.8);

            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException("No readable text or QR code found in the image.");

            var output = step.Output.Allocate("txt");
            if (output is null)
                return [];

            // Write UTF-8 with BOM for compatibility with Notepad and standard editors
            File.WriteAllText(output, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            progress?.Report(1.0);
            return (IReadOnlyList<string>)[output];
        }, cancellationToken);
}
