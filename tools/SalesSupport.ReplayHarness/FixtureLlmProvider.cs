using System.Text.Json;
using SalesSupport.Core.Contracts;
using SalesSupport.Core.Serialization;

namespace SalesSupport.ReplayHarness;

/// <summary>
/// Replays pre-authored model responses from a fixture file (a JSON array of
/// {role, response} entries, consumed in invocation order). Lets the full loop run
/// with real-quality outputs at zero API cost, and the fixture file doubles as the
/// golden corpus that live runs get diffed against later (D27: subscription-funded
/// development authors these; runtime inference stays API/local).
/// </summary>
public sealed class FixtureLlmProvider : ILlmProvider
{
    private readonly string _path;
    private readonly Queue<(string Role, JsonElement Response)> _entries;

    public FixtureLlmProvider(string path)
    {
        _path = path;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        _entries = new Queue<(string, JsonElement)>(
            doc.RootElement.EnumerateArray().Select(e => (
                e.GetProperty("role").GetString()!,
                e.GetProperty("response").Clone())));
    }

    /// <summary>Non-zero after a run signals fixture/orchestrator drift — surfaced by the harness.</summary>
    public int Remaining => _entries.Count;

    public Task<T> CompleteJsonAsync<T>(LlmRole role, LlmConversation conversation, CancellationToken ct = default)
        where T : class
    {
        var expected = role.ToString().ToLowerInvariant();
        if (_entries.Count == 0)
            throw new InvalidOperationException($"Fixture file {_path} exhausted — a {expected} call had no scripted response.");

        var (fixtureRole, response) = _entries.Dequeue();
        if (fixtureRole != expected)
            throw new InvalidOperationException(
                $"Fixture order mismatch in {_path}: orchestrator requested '{expected}' but next fixture is '{fixtureRole}'.");

        return Task.FromResult(JsonDefaults.Deserialize<T>(response.GetRawText()));
    }
}
