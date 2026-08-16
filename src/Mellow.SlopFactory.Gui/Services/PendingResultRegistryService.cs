using System.Text.Json;

namespace Mellow.SlopFactory.Gui.Services;

public sealed class PendingResultRegistryService : IPendingResultRegistryService
{
    private const string PreferenceKey = "recovery_staging_registry_v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();

    public IReadOnlyList<StagedResultEntry> GetAll()
    {
        lock (_gate) return Read().OrderByDescending(entry => entry.CreatedAt).ToArray();
    }

    public void Add(StagedResultEntry entry)
    {
        lock (_gate)
        {
            var items = Read();
            items.Add(entry);
            Write(items);
        }
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            var items = Read();
            items.RemoveAll(entry => entry.Id == id);
            Write(items);
        }
    }

    private static List<StagedResultEntry> Read()
    {
        var json = Preferences.Default.Get(PreferenceKey, string.Empty);
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<StagedResultEntry>>(json, JsonOptions) ?? []; }
        catch (JsonException) { return []; }
    }

    private static void Write(List<StagedResultEntry> items) => Preferences.Default.Set(PreferenceKey, JsonSerializer.Serialize(items, JsonOptions));
}
