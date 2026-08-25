namespace SalesSupport.Knowledge;

/// <summary>
/// Downloads the embedder model files from Hugging Face into a local directory
/// (gitignored — ~120 MB quantized, ~450 MB fp32). Candidate URLs are tried in order;
/// the Xenova mirror carries the ONNX exports.
/// </summary>
public static class ModelFetcher
{
    private const string Intfloat = "https://huggingface.co/intfloat/multilingual-e5-small/resolve/main";
    private const string Xenova = "https://huggingface.co/Xenova/multilingual-e5-small/resolve/main";

    public static async Task FetchAsync(string modelDir, bool quantized, Action<string> log, CancellationToken ct = default)
    {
        Directory.CreateDirectory(modelDir);

        var files = new List<(string FileName, string[] Urls)>
        {
            ("sentencepiece.bpe.model", [$"{Intfloat}/sentencepiece.bpe.model", $"{Xenova}/sentencepiece.bpe.model"]),
        };
        files.Add(quantized
            ? ("model_quantized.onnx", [$"{Xenova}/onnx/model_quantized.onnx", $"{Intfloat}/onnx/model_qint8_avx512_vnni.onnx"])
            : ("model.onnx", [$"{Intfloat}/onnx/model.onnx", $"{Xenova}/onnx/model.onnx"]));

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

        foreach (var (fileName, urls) in files)
        {
            var target = Path.Combine(modelDir, fileName);
            if (File.Exists(target) && new FileInfo(target).Length > 0)
            {
                log($"  {fileName}: already present ({new FileInfo(target).Length / 1024 / 1024} MB)");
                continue;
            }

            var downloaded = false;
            foreach (var url in urls)
            {
                try
                {
                    log($"  {fileName}: downloading from {new Uri(url).Host}{new Uri(url).AbsolutePath[..Math.Min(60, new Uri(url).AbsolutePath.Length)]}…");
                    using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                    response.EnsureSuccessStatusCode();

                    var temp = target + ".download";
                    await using (var source = await response.Content.ReadAsStreamAsync(ct))
                    await using (var destination = File.Create(temp))
                    {
                        await source.CopyToAsync(destination, ct);
                    }
                    File.Move(temp, target, overwrite: true);
                    log($"  {fileName}: done ({new FileInfo(target).Length / 1024 / 1024} MB)");
                    downloaded = true;
                    break;
                }
                catch (HttpRequestException ex)
                {
                    log($"  {fileName}: {ex.StatusCode} from this source, trying next…");
                }
            }

            if (!downloaded)
                throw new InvalidOperationException($"Could not download {fileName} from any source.");
        }
    }
}
