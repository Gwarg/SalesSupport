using System.Security.Cryptography;
using System.Text;
using SalesSupport.Core.Contracts;
using SalesSupport.Core.Serialization;
using SalesSupport.DocExtract;
using SalesSupport.Pipeline;
using SalesSupport.Providers.Claude;

// DocExtract (D33): manufacturer brochures (PDF) → canonical product JSONL (D29).
// The LLM proposes; code verifies every model code against the source text, merges
// duplicates across brochures, prunes dangling relations, and validates at the waist.

Console.OutputEncoding = Encoding.UTF8;

const string PromptVersion = "v1";
string? input = null, outPath = null, only = null;
var vendor = "Yokogawa";
var cacheDir = Path.Combine("testdata", ".extract-cache");
var dryRun = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--input": input = args[++i]; break;
        case "--out": outPath = args[++i]; break;
        case "--only": only = args[++i]; break;
        case "--vendor": vendor = args[++i]; break;
        case "--cache": cacheDir = args[++i]; break;
        case "--dry-run": dryRun = true; break;
        default: Console.Error.WriteLine($"Unknown argument: {args[i]}"); return 1;
    }
}

if (input is null || (!dryRun && outPath is null))
{
    Console.Error.WriteLine("Usage: SalesSupport.DocExtract --input <pdf dir> --out <canonical.jsonl> [--vendor Yokogawa] [--only <name filter>] [--cache <dir>] [--dry-run]");
    return 1;
}
if (!dryRun && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
{
    Console.Error.WriteLine("Extraction needs ANTHROPIC_API_KEY (use --dry-run to only inspect the PDFs).");
    return 1;
}

var pdfs = Directory.GetFiles(input, "*.pdf")
    .Where(p => only is null || Path.GetFileName(p).Contains(only, StringComparison.OrdinalIgnoreCase))
    .OrderBy(p => p, StringComparer.Ordinal)
    .ToList();
if (pdfs.Count == 0) { Console.Error.WriteLine("No PDFs matched."); return 1; }
Directory.CreateDirectory(cacheDir);

long totalIn = 0, totalCached = 0, totalOut = 0;
var llm = new ClaudeLlmProvider(new ClaudeProviderOptions
{
    Drafter = new ClaudeRoleConfig { Model = "claude-opus-5", MaxTokens = 32768, Effort = "high" },
    UsageReported = usage =>
    {
        totalIn += usage.InputTokens; totalCached += usage.CacheReadTokens; totalOut += usage.OutputTokens;
        Console.WriteLine($"     usage: in={usage.InputTokens} cached={usage.CacheReadTokens} out={usage.OutputTokens}");
    },
});

var report = new StringBuilder();
report.AppendLine($"DocExtract report — {vendor} — {DateTime.Now:yyyy-MM-dd HH:mm}");
var merged = new Dictionary<string, MergedProduct>(StringComparer.OrdinalIgnoreCase);
var droppedCodes = 0;

foreach (var pdf in pdfs)
{
    var name = Path.GetFileName(pdf);
    var pages = PdfText.Pages(pdf);
    var text = PdfText.Join(pages);
    Console.WriteLine($"→ {name}: {pages.Count} pages, {text.Length:N0} chars");
    if (dryRun) continue;

    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PromptVersion + "\n" + text)))[..24];
    var cachePath = Path.Combine(cacheDir, hash + ".json");
    ExtractedCatalog catalog;
    if (File.Exists(cachePath))
    {
        catalog = JsonDefaults.Deserialize<ExtractedCatalog>(File.ReadAllText(cachePath));
        Console.WriteLine("     cached");
    }
    else
    {
        var conversation = new LlmConversation(
            Prompts.System(vendor),
            [LlmMessage.User($"DOCUMENT FILE: {name}\nVENDOR: {vendor}\n\n{text}")]);
        catalog = await llm.CompleteJsonAsync<ExtractedCatalog>(LlmRole.Drafter, conversation);
        File.WriteAllText(cachePath, JsonDefaults.Serialize(catalog));
    }

    var kept = 0;
    foreach (var product in catalog.Products)
    {
        if (!Verifier.AppearsIn(product.ModelCode, text))
        {
            droppedCodes++;
            report.AppendLine($"  DROP {name}: model code '{product.ModelCode}' not found in document text");
            continue;
        }
        kept++;
        if (merged.TryGetValue(product.ModelCode, out var existing))
            existing.Absorb(product, name);
        else
            merged[product.ModelCode] = new MergedProduct(product, name);
    }
    Console.WriteLine($"     {kept} products kept ({catalog.Products.Count - kept} dropped); kinds: " +
                      string.Join(", ", catalog.Products.GroupBy(p => p.Kind).Select(g => $"{g.Key}={g.Count()}")));
    report.AppendLine($"{name}: {kept} kept / {catalog.Products.Count - kept} dropped");
    foreach (var note in catalog.Notes) report.AppendLine($"  note: {note}");
}

if (dryRun) return 0;

// Relations may only point at products that exist after merging (the pipeline rejects the rest).
var prunedRelations = 0;
var rows = new List<RawProduct>();
foreach (var item in merged.Values.OrderBy(m => m.Product.CategoryPath, StringComparer.Ordinal).ThenBy(m => m.Product.ModelCode, StringComparer.Ordinal))
{
    var relations = new List<RawRelation>();
    foreach (var relation in item.Relations)
    {
        if (!merged.ContainsKey(relation.TargetModelCode) || relation.TargetModelCode.Equals(item.Product.ModelCode, StringComparison.OrdinalIgnoreCase))
        {
            prunedRelations++;
            report.AppendLine($"  PRUNE {item.Product.ModelCode}: {relation.Kind} → '{relation.TargetModelCode}' (not extracted)");
            continue;
        }
        if (relations.Any(r => r.Kind == relation.Kind && r.TargetSku.Equals(relation.TargetModelCode, StringComparison.OrdinalIgnoreCase))) continue;
        relations.Add(new RawRelation(relation.Kind, merged[relation.TargetModelCode].Product.ModelCode, relation.Note));
    }
    rows.Add(item.ToRaw(vendor, relations));
}

var errors = PackAssembler.Validate(rows);
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath!))!);
File.WriteAllLines(outPath!, rows.Select(r => JsonDefaults.Serialize(r)));

var cost = totalIn * 5.0 / 1e6 + totalCached * 0.5 / 1e6 + totalOut * 25.0 / 1e6;
var summary = new StringBuilder();
summary.AppendLine($"products: {rows.Count} (dropped codes: {droppedCodes}, pruned relations: {prunedRelations})");
summary.AppendLine("kinds: " + string.Join(", ", rows.GroupBy(r => r.AttributesRaw!["kind"]).Select(g => $"{g.Key}={g.Count()}")));
summary.AppendLine("families: " + string.Join("; ", rows.GroupBy(r => r.CategoryPathRaw).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key}={g.Count()}")));
summary.AppendLine($"tokens: in={totalIn} cached={totalCached} out={totalOut} ≈ ${cost:F2} (Opus list prices)");
summary.AppendLine(errors.Count == 0 ? "validation: OK" : $"validation: {errors.Count} errors");
foreach (var error in errors) summary.AppendLine($"  - {error}");
report.AppendLine();
report.Append(summary);
File.WriteAllText(outPath + ".report.txt", report.ToString());

Console.WriteLine();
Console.Write(summary);
Console.WriteLine($"Wrote {outPath} and {outPath}.report.txt");
return errors.Count == 0 ? 0 : 1;

/// <summary>One product across every brochure that mentions it: longest description wins; relations, aliases and doc refs union.</summary>
sealed class MergedProduct
{
    public ExtractedProduct Product { get; private set; }
    public List<ExtractedRelation> Relations { get; } = [];
    public HashSet<string> Aliases { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> DocRefs { get; } = [];

    public MergedProduct(ExtractedProduct product, string docRef)
    {
        Product = product;
        Absorb(product, docRef);
    }

    public void Absorb(ExtractedProduct other, string docRef)
    {
        if (other.Description.Length > Product.Description.Length) Product = other;
        Relations.AddRange(other.Relations);
        foreach (var alias in other.Aliases) Aliases.Add(alias);
        if (!DocRefs.Contains(docRef)) DocRefs.Add(docRef);
    }

    public RawProduct ToRaw(string vendor, List<RawRelation> relations)
    {
        var attributes = new Dictionary<string, string> { ["kind"] = Product.Kind, ["vendor"] = vendor };
        foreach (var attribute in Product.Attributes)
            if (!string.IsNullOrWhiteSpace(attribute.Key) && !attributes.ContainsKey(attribute.Key.Trim()))
                attributes[attribute.Key.Trim()] = attribute.Value.Trim();

        var category = Product.CategoryPath.Contains('>') ? Product.CategoryPath.Trim() : $"Övrigt > {Product.CategoryPath.Trim()}";
        var aliases = Aliases
            .Where(a => !a.Equals(Product.ModelCode, StringComparison.OrdinalIgnoreCase) && !a.Equals(Product.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return new RawProduct(
            ExternalId: $"{vendor.ToLowerInvariant()}:{Product.ModelCode}",
            Sku: Product.ModelCode,
            Name: Product.Name,
            CategoryPathRaw: category,
            DescriptionRaw: string.IsNullOrWhiteSpace(Product.Description) ? Product.Name : Product.Description.Trim(),
            AttributesRaw: attributes,
            Price: null,
            Currency: null,
            PriceNote: null,
            AvailabilityRaw: null,
            Status: Product.Status?.Equals("discontinued", StringComparison.OrdinalIgnoreCase) == true ? "discontinued" : "active",
            AliasesRaw: aliases.Count > 0 ? aliases : null,
            RelationsRaw: relations.Count > 0 ? relations : null,
            DocRefs: DocRefs);
    }
}

static class Prompts
{
    private const string Taxonomy = """
        - Effektmätning > Effektanalysatorer
        - Effektmätning > Programvara
        - Effektmätning > Tillbehör och optioner
        - Vågformsmätning > Oscilloskop
        - Vågformsmätning > ScopeCorder och datalogger
        - Vågformsmätning > Programvara
        - Vågformsmätning > Tillbehör och optioner
        - Optisk test > OTDR
        - Optisk test > Optiska spektrumanalysatorer
        - Optisk test > Ljuskällor, effektmätare och dämpare
        - Optisk test > Våglängdsmätare och övriga optiska instrument
        - Optisk test > Programvara
        - Optisk test > Tillbehör och optioner
        - Kalibrering > Kalibratorer och referenser
        - Kalibrering > Tillbehör och optioner
        - Mätinstrument > Multimetrar och testare
        - Mätinstrument > Elkvalitetsanalysatorer
        - Mätinstrument > Tillbehör och optioner
        - Källor > Source measure units
        - Källor > Tillbehör och optioner
        """;

    public static string System(string vendor) => $$"""
        You are extracting a distributor's product catalog from a manufacturer brochure
        ({{vendor}}). The output feeds a sales-copilot knowledge pack used live on sales
        calls, so precision beats completeness: an invented code or spec would mislead a
        rep mid-call.

        WHAT COUNTS AS A PRODUCT — one entry per orderable thing named in the document:
        - instrument: a base model (WT5000, AQ6370E, DLM3054). A family brochure lists
          several models — one entry per model code, never one per family.
        - option: an orderable option code for a base model (/G7, /MTR1, /DS). model_code
          exactly as printed including the leading slash; relation option_of → the base
          model. Suffix codes that only select language or power cord (-HE, -D, …) are
          NOT products: summarize them in one instrument attribute "suffix_codes".
        - module: separately numbered plug-in elements/modules (760901 input element,
          720xxx ScopeCorder modules); relation module_of → each host instrument named.
        - accessory: probes, cables, adapters, sensors, cases with their own model number;
          relation accessory_of → each compatible instrument named in the document.
        - software: software products, licenses and add-ons (WTViewerE, IS8000 and its
          add-ons); relation software_for → each supported instrument named.
        Also record successor_of (this model replaces an older one) and complement_of
        only when the document says so explicitly.

        MODEL CODES ARE SACRED: copy every model_code character-for-character from the
        document. Never invent, guess, complete, or "correct" a code. If a table entry is
        unreadable, leave it out and say so in notes. Every model_code is checked against
        the document text afterwards; anything unverifiable is discarded.

        name: the printed product name ("Precision Power Analyzer", "Passive probe 500 MHz");
        for options, the option's description. description: 2–5 English sentences written
        from the document — who it is for, what it does, what distinguishes it, the key
        numbers. No marketing fluff, nothing the document does not say.

        attributes: key specifications as key/value pairs — snake_case keys, values with
        units exactly as printed ("bandwidth": "500 MHz", "basic_accuracy": "±0.01% of
        reading", "channels": "4", "wavelength_range": "1200 to 2400 nm"). 5–15 for an
        instrument, fewer for accessories; include "compatible_with" whenever the document
        states compatibility.

        category_path: exactly two levels "Family > Subfamily" from this taxonomy (Swedish
        labels — choose the closest; only if nothing fits, use "Övrigt > <short label>"):
        {{Taxonomy}}
        Options, modules and accessories take their instrument's Family with the Subfamily
        "Tillbehör och optioner"; software takes the Family with Subfamily "Programvara".

        aliases: short or spoken forms a sales rep would say ("WT5000", "the 6370") — only
        forms present in the document; may be empty. status: "active" unless the document
        says the model is discontinued or replaced → "discontinued".

        Return every orderable product in THIS document only, plus notes (ambiguities,
        unreadable tables, anything a human should check).
        """;
}
