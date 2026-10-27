using Filee.Core.Conversion;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Office;

/// <summary>
/// Converts Word documents (DOCX, DOC, DOCM, DOTX, DOTM), HWPX, Markdown, Text, HTML, and ebooks directly
/// to vector PDF without needing Word, LibreOffice, or Pandoc.
/// </summary>
public sealed class DocumentPdfConverter : IConverter
{
    /// <summary>Below LibreOffice's direct edges (20 and more), so the built-in fast PDF writer wins.</summary>
    private const int Cost = 8;

    public string Id => "document-pdf";
    public string DisplayName => "PDF writer (built-in)";
    public int MaxParallelism => 0;

    public IReadOnlyList<ConversionEdge> Edges { get; } =
        DocumentReaders.BuiltInFormats.Where(f => !string.Equals(f, "pdf", StringComparison.OrdinalIgnoreCase))
            .Select(f => new ConversionEdge(f, "pdf", Cost)).ToList();

    public EngineStatus GetStatus() =>
        EngineStatus.Available(string.Join(", ", Edges.Select(e => e.From.ToUpperInvariant())) + " → PDF", EngineVersions.BuiltIn);

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(0.1);
        var document = await DocumentReaders.ReadAsync(step.InputPath, step.From, step.WorkDirectory, cancellationToken);
        progress?.Report(0.5);

        var output = step.Output.Allocate("pdf");
        if (output is null)
            return [];

        await Task.Run(() => DocumentPdfWriter.Write(document, output, cancellationToken), cancellationToken);
        progress?.Report(1.0);
        return [output];
    }
}
