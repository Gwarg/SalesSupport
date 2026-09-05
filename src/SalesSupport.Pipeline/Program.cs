using System.Security.Cryptography;
using System.Text;
using SalesSupport.Core.Serialization;
using SalesSupport.Knowledge;
using SalesSupport.Pipeline;

Console.OutputEncoding = Encoding.UTF8;

string? input = null;
string? company = null;
var outDir = "packs";
string? version = null;
var embedderChoice = "hashing";
var modelDir = Path.Combine("models", "multilingual-e5-small");
var fp32 = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--input": input = args[++i]; break;
        case "--company": company = args[++i]; break;
        case "--out": outDir = args[++i]; break;
        case "--version": version = args[++i]; break;
        case "--embedder": embedderChoice = args[++i]; break;
        case "--model-dir": modelDir = args[++i]; break;
        case "--fp32": fp32 = true; break;
    }
}

if (args.Length > 0 && args[0] == "fetch-model")
{
    Console.WriteLine($"Fetching multilingual-e5-small ({(fp32 ? "fp32" : "quantized")}) into {modelDir}");
    await ModelFetcher.FetchAsync(modelDir, quantized: !fp32, Console.WriteLine);
    Console.WriteLine("Done.");
    return 0;
}

if (input is null || company is null)
{
    Console.Error.WriteLine("Usage: SalesSupport.Pipeline --input <canonical.jsonl> --company <id> [--out <dir>] [--version <v>] [--embedder hashing|e5] [--model-dir <dir>] [--fp32]");
    Console.Error.WriteLine("       SalesSupport.Pipeline fetch-model [--model-dir <dir>] [--fp32]");
    return 1;
}

if (!File.Exists(input))
{
    Console.Error.WriteLine($"Input not found: {input}");
    return 1;
}

version ??= DateTime.UtcNow.ToString("yyyy-MM-dd.HHmmss");

Console.WriteLine($"Pipeline build: company={company} version={version}");

var rows = File.ReadLines(input)
    .Where(l => !string.IsNullOrWhiteSpace(l))
    .Select(JsonDefaults.Deserialize<RawProduct>)
    .ToList();
Console.WriteLine($"  canonical import: {rows.Count} products");

var errors = PackAssembler.Validate(rows);
if (errors.Count > 0)
{
    Console.Error.WriteLine($"Validation at the waist FAILED ({errors.Count} errors):");
    foreach (var error in errors) Console.Error.WriteLine($"  - {error}");
    return 1;
}
Console.WriteLine("  validation: OK");

IEmbedder embedder = embedderChoice switch
{
    "hashing" => new HashingEmbedder(),
    "e5" => OnnxEmbedder.Load(modelDir, quantized: !fp32),
    _ => throw new ArgumentException($"Unknown embedder '{embedderChoice}' (use hashing or e5)"),
};
Console.WriteLine($"  embedder: {embedder.ModelId} ({embedder.Dims} dims)");
var assembly = PackAssembler.Assemble(rows, embedder);
Console.WriteLine($"  assembled: {assembly.Families.Count} families, {assembly.Products.Count} cards, " +
                  $"{assembly.Aliases.Count} aliases, {assembly.Relations.Count} relations, " +
                  $"catalog map ~{assembly.CatalogMap.Length / 4} tokens (compact ~{assembly.CatalogMapCompact.Length / 4})");

var feedSnapshot = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input)))[..12].ToLowerInvariant();
Directory.CreateDirectory(outDir);
var packPath = Path.Combine(outDir, $"{company}_{version}.pack.sqlite");

PackBuilder.Build(
    packPath,
    new PackMeta(company, version, "sv", feedSnapshot),
    embedder,
    assembly.Products,
    assembly.Families,
    assembly.Aliases,
    assembly.Relations,
    assembly.CatalogMap,
    assembly.SttVocab,
    assembly.CatalogMapCompact);

var size = new FileInfo(packPath).Length;
Console.WriteLine($"  pack written: {packPath} ({size / 1024} KB, embedder {embedder.ModelId}/{embedder.Dims})");

var probe = SqlitePackKnowledge.Load(packPath, embedder);
var hits = await probe.SearchAsync("frysklassad skanner", 3);
Console.WriteLine($"  probe search 'frysklassad skanner': {string.Join(", ", hits.Select(h => h.DocId))}");
Console.WriteLine("Done.");
return 0;
