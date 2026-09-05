using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace SalesSupport.DocExtract;

/// <summary>Per-page text in reading order. Brochures with a text layer only — no OCR (none of the pilot's 48 needed it).</summary>
public static class PdfText
{
    public static IReadOnlyList<string> Pages(string path)
    {
        using var document = PdfDocument.Open(path);
        return document.GetPages().Select(page => ContentOrderTextExtractor.GetText(page)).ToList();
    }

    /// <summary>One string per document with page markers, whitespace collapsed — what the extractor reads.</summary>
    public static string Join(IReadOnlyList<string> pages)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < pages.Count; i++)
        {
            sb.Append("\n=== PAGE ").Append(i + 1).Append(" ===\n");
            sb.Append(Collapse(pages[i]));
        }
        return sb.ToString().Trim();
    }

    private static string Collapse(string text) =>
        string.Join('\n', text.Split('\n').Select(line => string.Join(' ', line.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim())
            .Where(line => line.Length > 0));
}
