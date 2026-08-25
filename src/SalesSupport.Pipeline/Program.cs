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

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--input": input = args[++i]; break;
        case "--company": company = args[++i]; break;
        case "--out": outDir = args[++i]; break;
        case "--version": version = args[++i]; break;
    }
}

if (input is null || company is null)
{
    Console.Error.WriteLine("Usage: SalesSupport.Pipeline --input <canonical.jsonl> --company <id> [--out <dir>] [--version <v>]");
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

var embedder = new HashingEmbedder();
var assembly = PackAssembler.Assemble(rows, embedder);
Console.WriteLine($"  assembled: {assembly.Families.Count} families, {assembly.Products.Count} cards, " +
                  $"{assembly.Aliases.Count} aliases, {assembly.Relations.Count} relations, " +
                  $"catalog map ~{assembly.CatalogMap.Length / 4} tokens");

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
    assembly.SttVocab);

var size = new FileInfo(packPath).Length;
Console.WriteLine($"  pack written: {packPath} ({size / 1024} KB, embedder {embedder.ModelId}/{embedder.Dims})");

var probe = SqlitePackKnowledge.Load(packPath, embedder);
var hits = await probe.SearchAsync("frysklassad skanner", 3);
Console.WriteLine($"  probe search 'frysklassad skanner': {string.Join(", ", hits.Select(h => h.DocId))}");
Console.WriteLine("Done.");
return 0;
