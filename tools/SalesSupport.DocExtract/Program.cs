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

// Part of the cache key: bump only when the prompt changes what gets extracted. Additive
// taxonomy rows are not worth re-paying for 48 brochures — they apply to new extractions.
const string PromptVersion = "v1";
string? input = null, outPath = null, only = null;
var vendor = "Yokogawa";
var cacheDir = Path.Combine("testdata", ".extract-cache");
var dryRun = false;
var parallel = 4;
// Paid API calls are opt-in per run. The default merges what is already in the cache —
// cache files can also be authored in a Claude Code session on the subscription (D27),
// which is the zero-cost path for development-time extraction.
var allowApi = false;

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
        case "--parallel": parallel = int.Parse(args[++i]); break;
        case "--allow-api": allowApi = true; break;
        default: Console.Error.WriteLine($"Unknown argument: {args[i]}"); return 1;
    }
}

if (input is null || (!dryRun && outPath is null))
{
    Console.Error.WriteLine("Usage: SalesSupport.DocExtract --input <pdf dir> --out <canonical.jsonl> [--vendor Yokogawa] [--only <name filter>] [--cache <dir>] [--parallel N] [--allow-api] [--dry-run]");
    Console.Error.WriteLine("       Without --allow-api no paid calls are made: uncached brochures are reported as missing.");
    return 1;
}
if (allowApi && !dryRun && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
{
    Console.Error.WriteLine("--allow-api needs ANTHROPIC_API_KEY.");
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

const int MaxChunkChars = 50_000;

// Option codes (/G7, -SPM) are only unique per instrument — the same code means different
// things on different models, and the pack would slug "/G7" and "G7" to one id. Qualify
// them by their (alphabetically first) host: WT5000/G7, the way Yokogawa orders read.
// Part-numbered modules, accessories and software are globally unique and stay as printed.
static bool IsOptionCode(string code) => code.StartsWith('/') || code.StartsWith('-');
static string FinalSku(ExtractedProduct product)
{
    if (product.Kind != "option" && !IsOptionCode(product.ModelCode)) return product.ModelCode;
    var host = product.Relations
        .Where(r => r.Kind is "option_of" or "module_of" or "accessory_of" or "software_for")
        .Select(r => r.TargetModelCode.Trim())
        .Where(t => t.Length > 0)
        .OrderBy(t => t, StringComparer.Ordinal)
        .FirstOrDefault();
    return host is null ? product.ModelCode : host + (IsOptionCode(product.ModelCode) ? "" : "/") + product.ModelCode;
}

// Control characters in PDF text (a stray \b on a DLM ordering page) are stripped from
// the request only — the cache hash stays on the raw text so nothing re-extracts.
static string ForRequest(string text) => new(text.Where(c => !char.IsControl(c) || c is '\n' or '\t').ToArray());

var localSkus = new Dictionary<string, Dictionary<string, string>>(); // brochure → printed code → final sku
string CachePathFor(string text) =>
    Path.Combine(cacheDir, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PromptVersion + "\n" + text)))[..24] + ".json");
string SplitMarkerFor(string text) => CachePathFor(text) + ".split";

// Extracts a page range through the cache. Oversized ranges — or ranges whose output
// once hit the token cap (remembered by a marker so re-runs never re-pay for the
// truncated attempt) — are split into halves; the merge below unifies by model code.
// Phase 1 calls this in parallel to warm the cache; phase 2 calls it again, served
// entirely from cache.
async Task<List<ExtractedProduct>> ExtractRangeAsync(string name, IReadOnlyList<string> pages, int first, int last, List<string> notes)
{
    var count = last - first + 1;
    var text = PdfText.Join(pages.Skip(first).Take(count).ToList());

    // A cached whole-range result always wins — even for ranges that would be split
    // today (the WT5000 probe was extracted whole before the size limit existed).
    var cachePath = CachePathFor(text);
    if (File.Exists(cachePath))
    {
        var cached = JsonDefaults.Deserialize<ExtractedCatalog>(File.ReadAllText(cachePath));
        lock (notes) notes.AddRange(cached.Notes);
        return cached.Products;
    }
    if (count > 1 && (text.Length > MaxChunkChars || File.Exists(SplitMarkerFor(text))))
        return await SplitAsync();

    var label = count == pages.Count ? name : $"{name} (pages {first + 1}–{last + 1} of {pages.Count})";
    if (!allowApi)
        throw new InvalidOperationException($"not in cache ({Path.GetFileName(cachePath)}) and --allow-api not given — author the cache file in-session or re-run with --allow-api");
    var conversation = new LlmConversation(
        Prompts.System(vendor),
        [LlmMessage.User($"DOCUMENT FILE: {label}\nVENDOR: {vendor}\n\n{ForRequest(text)}")]);
    ExtractedCatalog catalog;
    try
    {
        catalog = await llm.CompleteJsonAsync<ExtractedCatalog>(LlmRole.Drafter, conversation);
    }
    catch (LlmOutputTruncatedException) when (count > 1)
    {
        File.WriteAllText(SplitMarkerFor(text), "");
        Console.WriteLine($"   {label}: output truncated — splitting into halves");
        return await SplitAsync();
    }
    File.WriteAllText(cachePath, JsonDefaults.Serialize(catalog));
    lock (notes) notes.AddRange(catalog.Notes);
    return catalog.Products;

    async Task<List<ExtractedProduct>> SplitAsync()
    {
        var mid = first + count / 2;
        var left = await ExtractRangeAsync(name, pages, first, mid - 1, notes);
        var right = await ExtractRangeAsync(name, pages, mid, last, notes);
        return [.. left, .. right];
    }
}

// Phase 1: warm the cache in parallel — one Opus call per brochure takes minutes, and
// the merge below needs every result before it can resolve cross-brochure relations.
// A failing document is reported, not fatal: everything else still lands.
var failures = new Dictionary<string, string>();
if (!dryRun)
{
    var docs = pdfs.Select(pdf => (Name: Path.GetFileName(pdf), Pages: PdfText.Pages(pdf))).ToList();
    Console.WriteLine($"{docs.Count} brochures, up to {parallel} in parallel; paid API calls {(allowApi ? "ENABLED (--allow-api)" : "disabled — cache only")}");
    await Parallel.ForEachAsync(docs, new ParallelOptions { MaxDegreeOfParallelism = parallel }, async (doc, _) =>
    {
        var started = Environment.TickCount64;
        try
        {
            var products = await ExtractRangeAsync(doc.Name, doc.Pages, 0, doc.Pages.Count - 1, []);
            var seconds = (Environment.TickCount64 - started) / 1000;
            Console.WriteLine($"   {doc.Name}: {products.Count} products {(seconds < 2 ? "(cached)" : $"in {seconds} s")}");
        }
        catch (Exception ex)
        {
            // Keep the provider's detail (API error bodies live in inner exceptions/ToString).
            var detail = string.Join(" | ", ex.ToString().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(4));
            lock (failures) failures[doc.Name] = detail;
            Console.WriteLine($"   FAILED {doc.Name}: {detail}");
        }
    });
}

// Phase 2: verify, merge, prune, validate — sequential over cached results.
foreach (var pdf in pdfs)
{
    var name = Path.GetFileName(pdf);
    var pages = PdfText.Pages(pdf);
    var text = PdfText.Join(pages);
    Console.WriteLine($"→ {name}: {pages.Count} pages, {text.Length:N0} chars");
    if (dryRun) continue;
    if (failures.TryGetValue(name, out var failure))
    {
        report.AppendLine($"{name}: FAILED — {failure}");
        continue;
    }

    var notes = new List<string>();
    var products = await ExtractRangeAsync(name, pages, 0, pages.Count - 1, notes);

    var kept = 0;
    foreach (var product in products)
    {
        if (!Verifier.AppearsIn(product.ModelCode, text))
        {
            droppedCodes++;
            report.AppendLine($"  DROP {name}: model code '{product.ModelCode}' not found in document text");
            continue;
        }
        kept++;
        var sku = FinalSku(product);
        if (!localSkus.TryGetValue(name, out var local))
            localSkus[name] = local = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        local[product.ModelCode] = sku;
        if (merged.TryGetValue(sku, out var existing))
            existing.Absorb(product, name);
        else
            merged[sku] = new MergedProduct(sku, product, name);
    }
    Console.WriteLine($"     {kept} products kept ({products.Count - kept} dropped); kinds: " +
                      string.Join(", ", products.GroupBy(p => p.Kind).Select(g => $"{g.Key}={g.Count()}")));
    report.AppendLine($"{name}: {kept} kept / {products.Count - kept} dropped");
    foreach (var note in notes) report.AppendLine($"  note: {note}");
}

if (dryRun) return 0;

// Relations may only point at products that exist after merging (the pipeline rejects the rest).
var prunedRelations = 0;
var rows = new List<RawProduct>();
foreach (var item in merged.Values.OrderBy(m => m.Product.CategoryPath, StringComparer.Ordinal).ThenBy(m => m.Sku, StringComparer.Ordinal))
{
    var relations = new List<RawRelation>();
    foreach (var (relation, doc) in item.Relations)
    {
        // Targets are printed codes: resolve through the brochure that printed them first
        // (options qualified per host), then globally (part numbers are unique).
        var printed = relation.TargetModelCode.Trim();
        var target = localSkus.TryGetValue(doc, out var local) && local.TryGetValue(printed, out var mapped) ? mapped
            : merged.TryGetValue(printed, out var global) ? global.Sku
            : null;
        if (target is null || target.Equals(item.Sku, StringComparison.OrdinalIgnoreCase))
        {
            prunedRelations++;
            report.AppendLine($"  PRUNE {item.Sku}: {relation.Kind} → '{printed}' (not extracted)");
            continue;
        }
        if (relations.Any(r => r.Kind == relation.Kind && r.TargetSku.Equals(target, StringComparison.OrdinalIgnoreCase))) continue;
        relations.Add(new RawRelation(relation.Kind, target, relation.Note));
    }

    // Dependents the extractor filed under Övrigt take their host's family instead.
    var category = item.Product.CategoryPath.Trim();
    if (category.StartsWith("Övrigt", StringComparison.OrdinalIgnoreCase) && item.Product.Kind != "instrument")
    {
        var hostCategory = relations
            .Select(r => merged[r.TargetSku].Product.CategoryPath.Trim())
            .FirstOrDefault(c => c.Contains('>') && !c.StartsWith("Övrigt", StringComparison.OrdinalIgnoreCase));
        if (hostCategory is not null)
            category = $"{hostCategory.Split('>')[0].Trim()} > {(item.Product.Kind == "software" ? "Programvara" : "Tillbehör och optioner")}";
    }
    rows.Add(item.ToRaw(vendor, relations, category));
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
if (failures.Count > 0) summary.AppendLine($"FAILED documents: {failures.Count} — {string.Join("; ", failures.Keys)}");
report.AppendLine();
report.Append(summary);
File.WriteAllText(outPath + ".report.txt", report.ToString());

Console.WriteLine();
Console.Write(summary);
Console.WriteLine($"Wrote {outPath} and {outPath}.report.txt");
return errors.Count == 0 && failures.Count == 0 ? 0 : 1;

/// <summary>One product across every brochure that mentions it: longest description wins; relations, aliases and doc refs union.</summary>
sealed class MergedProduct
{
    public string Sku { get; }
    public ExtractedProduct Product { get; private set; }
    public List<(ExtractedRelation Relation, string Doc)> Relations { get; } = [];
    public HashSet<string> Aliases { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> DocRefs { get; } = [];

    public MergedProduct(string sku, ExtractedProduct product, string docRef)
    {
        Sku = sku;
        Product = product;
        Absorb(product, docRef);
    }

    private string _homeDoc = "";

    /// <summary>
    /// Which extraction describes the product: its own brochure wins (the file name carries
    /// the model code — "Brochure WT5000.pdf" for WT5000, so the WT5000T brochure's
    /// "Transformer Version" text cannot overwrite it); among equals, the longest description.
    /// </summary>
    public void Absorb(ExtractedProduct other, string docRef)
    {
        var otherIsHome = IsHomeDoc(docRef, other.ModelCode);
        var currentIsHome = IsHomeDoc(_homeDoc, Product.ModelCode);
        if (DocRefs.Count == 0 || (otherIsHome && !currentIsHome) ||
            (otherIsHome == currentIsHome && other.Description.Length > Product.Description.Length))
        {
            Product = other;
            _homeDoc = docRef;
        }
        Relations.AddRange(other.Relations.Select(r => (r, docRef)));
        foreach (var alias in other.Aliases) Aliases.Add(alias);
        if (!DocRefs.Contains(docRef)) DocRefs.Add(docRef);
    }

    private static bool IsHomeDoc(string docRef, string modelCode) =>
        Path.GetFileNameWithoutExtension(docRef)
            .Split([' ', ',', '_', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Any(token => token.Equals(modelCode, StringComparison.OrdinalIgnoreCase));

    public RawProduct ToRaw(string vendor, List<RawRelation> relations, string category)
    {
        var attributes = new Dictionary<string, string> { ["kind"] = Product.Kind, ["vendor"] = vendor };
        foreach (var attribute in Product.Attributes)
            if (!string.IsNullOrWhiteSpace(attribute.Key) && !attributes.ContainsKey(attribute.Key.Trim()))
                attributes[attribute.Key.Trim()] = attribute.Value.Trim();

        if (!category.Contains('>')) category = $"Övrigt > {category}";
        var aliases = Aliases
            .Where(a => !a.Equals(Sku, StringComparison.OrdinalIgnoreCase) && !a.Equals(Product.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        // A qualified option keeps its printed code as a spoken alias ("G7" for WT5000/G7).
        if (!Sku.Equals(Product.ModelCode, StringComparison.OrdinalIgnoreCase))
        {
            var bare = Product.ModelCode.TrimStart('/', '-');
            if (bare.Length > 0 && !aliases.Contains(bare, StringComparer.OrdinalIgnoreCase)) aliases.Add(bare);
        }

        return new RawProduct(
            ExternalId: $"{vendor.ToLowerInvariant()}:{Sku}",
            Sku: Sku,
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
        - Källor > Programvara
        - Källor > Tillbehör och optioner
        - Nätverkstest > Ethernet-testare
        - Nätverkstest > Tillbehör och optioner
        - Kalibrering > Programvara
        - Mätinstrument > Programvara
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
