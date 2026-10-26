using System.IO;
using System.Text.Json;

namespace Orbit.Services;

/// <summary>
/// Remembers names and profile pictures of people Orbit has shown (friends, requests), in
/// %AppData%\Orbit\people.json. That way the friend list shows names right away – even when Roblox
/// is slow or busy right after start – and the fresh data is filled in as soon as it arrives.
/// </summary>
public static class PeopleCache
{
    public sealed class Person
    {
        public string Name { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string? AvatarUrl { get; set; }
    }

    private static Dictionary<long, Person>? _people;
    private static readonly object Lock = new();
    private static string FilePath => Path.Combine(AppPaths.DataDir, "people.json");

    private static Dictionary<long, Person> People
    {
        get
        {
            if (_people != null) return _people;
            try
            {
                _people = File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<Dictionary<long, Person>>(File.ReadAllText(FilePath)) ?? new()
                    : new();
            }
            catch
            {
                _people = new();
            }
            return _people;
        }
    }

    public static Person? Get(long id)
    {
        lock (Lock) return People.TryGetValue(id, out var p) ? p : null;
    }

    public static void Update(IReadOnlyDictionary<long, FriendInfo> users, IReadOnlyDictionary<long, string> pictures)
    {
        if (users.Count == 0 && pictures.Count == 0) return;
        lock (Lock)
        {
            foreach (var (id, u) in users)
            {
                if (!People.TryGetValue(id, out var p)) People[id] = p = new Person();
                p.Name = u.Name;
                p.DisplayName = u.DisplayName;
            }
            foreach (var (id, url) in pictures)
            {
                if (!People.TryGetValue(id, out var p)) People[id] = p = new Person();
                p.AvatarUrl = url;
            }
            try { File.WriteAllText(FilePath, JsonSerializer.Serialize(People)); }
            catch { /* only a cache */ }
        }
    }
}
