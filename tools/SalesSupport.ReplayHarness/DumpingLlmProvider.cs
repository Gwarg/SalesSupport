using SalesSupport.Core.Contracts;
using SalesSupport.Core.Serialization;

namespace SalesSupport.ReplayHarness;

/// <summary>
/// Decorator that writes every prompt and response to numbered files — for inspecting
/// what the models actually see, authoring fixtures, and diffing live runs against goldens.
/// </summary>
public sealed class DumpingLlmProvider(ILlmProvider inner, string directory) : ILlmProvider
{
    private int _sequence;

    public async Task<T> CompleteJsonAsync<T>(LlmRole role, LlmConversation conversation, CancellationToken ct = default)
        where T : class
    {
        Directory.CreateDirectory(directory);
        var n = Interlocked.Increment(ref _sequence);
        var stem = Path.Combine(directory, $"{n:000}-{role.ToString().ToLowerInvariant()}");

        var prompt = $"SYSTEM:\n{conversation.System}\n\n" +
                     string.Join("\n\n", conversation.Messages.Select(m => $"{m.Role.ToUpperInvariant()}:\n{m.Content}"));
        await File.WriteAllTextAsync($"{stem}.prompt.txt", prompt, ct);

        var result = await inner.CompleteJsonAsync<T>(role, conversation, ct);
        await File.WriteAllTextAsync($"{stem}.response.json", JsonDefaults.Serialize(result, pretty: true), ct);
        return result;
    }
}
