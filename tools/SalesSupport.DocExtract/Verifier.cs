namespace SalesSupport.DocExtract;

/// <summary>
/// Verification at the waist (D33): an LLM may propose model codes, but a code that does
/// not appear verbatim in the source document is dropped, never trusted. Matching is
/// case-insensitive and whitespace-insensitive ("WT 5000" and "WT5000" both count).
/// </summary>
public static class Verifier
{
    public static bool AppearsIn(string modelCode, string documentText)
    {
        var needle = Squash(modelCode);
        return needle.Length > 0 && Squash(documentText).Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    private static string Squash(string text) =>
        new(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
}
