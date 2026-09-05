using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SalesSupport.Core.Contracts;
using SalesSupport.Core.Model;
using SalesSupport.Core.Serialization;

namespace SalesSupport.Core.Recording;

public enum RecordingMode { Off, Record, Replay }

/// <summary>
/// Record/replay around any ILlmProvider. Record: every response is stored keyed by the
/// exact prompt (role, output type, system, messages) as a JSONL line, together with how
/// long the model took. Replay: the same prompts get the same responses at zero cost, with
/// the recorded latency reproduced so a demo keeps its real pacing. Prompts are
/// deterministic given the same script, pack and prior responses, so a recorded call
/// replays cleanly; a prompt that was never recorded degrades to a neutral response
/// (empty gate diff, unchanged panel, placeholder summary) and is counted, never thrown.
/// </summary>
public sealed class RecordingLlmProvider : ILlmProvider
{
    private sealed record Entry(string Key, string Role, string Type, long ElapsedMs, string Fingerprint, JsonElement Response);

    private const int MaxReplayDelayMs = 15_000;

    private readonly ILlmProvider? _inner;
    private readonly bool _replayLatency;
    private readonly Action<string>? _log;
    private readonly Dictionary<string, Entry> _entries = new();
    private readonly Lock _gate = new();

    public string Path { get; }
    public RecordingMode Mode { get; }
    public int Count { get { lock (_gate) return _entries.Count; } }
    public int Misses { get; private set; }

    public RecordingLlmProvider(ILlmProvider? inner, string path, RecordingMode mode, bool replayLatency = true, Action<string>? log = null)
    {
        if (mode == RecordingMode.Record && inner is null)
            throw new ArgumentException("Record mode needs an inner provider to record from.", nameof(inner));
        _inner = inner;
        Path = path;
        Mode = mode;
        _replayLatency = replayLatency;
        _log = log;

        if (File.Exists(path))
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var entry = JsonDefaults.Deserialize<Entry>(line);
                _entries[entry.Key] = entry;
            }
    }

    public async Task<T> CompleteJsonAsync<T>(LlmRole role, LlmConversation conversation, CancellationToken ct = default)
        where T : class
    {
        var key = KeyFor(role, typeof(T).Name, conversation);
        Entry? hit;
        lock (_gate) _entries.TryGetValue(key, out hit);

        if (hit is not null)
        {
            if (Mode == RecordingMode.Replay && _replayLatency && hit.ElapsedMs > 0)
                await Task.Delay((int)Math.Min(hit.ElapsedMs, MaxReplayDelayMs), ct).ConfigureAwait(false);
            return JsonDefaults.Deserialize<T>(hit.Response.GetRawText());
        }

        if (Mode == RecordingMode.Replay || _inner is null)
        {
            lock (_gate) Misses++;
            _log?.Invoke($"replay miss #{Misses}: {Fingerprint(role, typeof(T).Name, conversation)} — neutral response used");
            return Neutral<T>();
        }

        var started = Environment.TickCount64;
        var result = await _inner.CompleteJsonAsync<T>(role, conversation, ct).ConfigureAwait(false);
        var elapsed = Environment.TickCount64 - started;

        var entry = new Entry(key, role.ToString(), typeof(T).Name, elapsed, Fingerprint(role, typeof(T).Name, conversation),
            JsonSerializer.SerializeToElement(result, JsonDefaults.Options));
        lock (_gate)
        {
            if (_entries.TryAdd(key, entry))
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
                File.AppendAllText(Path, JsonDefaults.Serialize(entry) + "\n");
            }
        }
        return result;
    }

    private static string KeyFor(LlmRole role, string type, LlmConversation conversation)
    {
        var sb = new StringBuilder().Append(role).Append('|').Append(type).Append('|').Append(conversation.System);
        foreach (var message in conversation.Messages) sb.Append('\n').Append(message.Role).Append(':').Append(message.Content);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..32];
    }

    /// <summary>Human-readable hint for logs: the last non-empty line of the last user message (the NEW utterance / QUERY).</summary>
    private static string Fingerprint(LlmRole role, string type, LlmConversation conversation)
    {
        var last = conversation.Messages.LastOrDefault()?.Content.Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
        return $"{role}/{type}: {(last.Length > 120 ? last[..120] + "…" : last)}";
    }

    private static T Neutral<T>() where T : class
    {
        if (typeof(T) == typeof(SummaryResult))
            return (T)(object)new SummaryResult { Summary = "(ingen inspelad sammanfattning — samtalet avvek från inspelningen)" };
        return JsonDefaults.Deserialize<T>("{}");
    }
}
