using System.Text.Json;
using SalesSupport.Core.Serialization;

namespace SalesSupport.Backend.Telephony;

/// <summary>One row of the installation's customer index (D28/D30): phone → customer.</summary>
public sealed record CustomerIndexEntry(string Phone, string Company, string? CrmId = null, string? Notes = null);

/// <summary>
/// Phone → customer lookup over a JSONL file the CRM adapter (D28/D30) will eventually
/// write — until then hand-authored per installation. Reloaded when the file changes,
/// so a fresh snapshot needs no restart. Missing file = empty index, never an error:
/// number resolution is a bonus on top of the ring signal, not a dependency.
/// </summary>
public sealed class CustomerIndex(string path, string defaultCountryCode = "+46")
{
    private readonly Lock _gate = new();
    private DateTime _loadedStamp = DateTime.MinValue;
    private Dictionary<string, CustomerIndexEntry> _byNumber = new();

    public string Path => path;

    public int Count
    {
        get { Refresh(); return _byNumber.Count; }
    }

    public CustomerIndexEntry? Resolve(string? rawNumber)
    {
        var key = PhoneNumbers.Normalize(rawNumber, defaultCountryCode);
        if (key is null) return null;
        Refresh();
        return _byNumber.GetValueOrDefault(key);
    }

    private void Refresh()
    {
        lock (_gate)
        {
            if (!File.Exists(path))
            {
                _byNumber = new Dictionary<string, CustomerIndexEntry>();
                _loadedStamp = DateTime.MinValue;
                return;
            }
            var stamp = File.GetLastWriteTimeUtc(path);
            if (stamp == _loadedStamp) return;

            var map = new Dictionary<string, CustomerIndexEntry>();
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                CustomerIndexEntry entry;
                try { entry = JsonDefaults.Deserialize<CustomerIndexEntry>(line); }
                catch (JsonException) { continue; }
                var key = PhoneNumbers.Normalize(entry.Phone, defaultCountryCode);
                if (key is not null) map[key] = entry;
            }
            _byNumber = map;
            _loadedStamp = stamp;
        }
    }
}
