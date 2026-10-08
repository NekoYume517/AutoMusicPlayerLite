using System.Text.Json.Serialization;
namespace AutoMusicPlayer;
public sealed class SongItem
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("bpm_default")] public int Bpm { get; set; }
    [JsonPropertyName("source_type")] public string Source { get; set; } = "";
    [JsonPropertyName("search_key")] public string SearchKey { get; set; } = "";
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = "";
    [JsonPropertyName("updated_at")] public string UpdatedAt { get; set; } = "";
    [JsonPropertyName("favorite")] public int FavoriteValue { get; set; }
    [JsonPropertyName("group_ids")] public int[] GroupIds { get; set; } = [];
    [JsonPropertyName("name_sort_key")] public string NameSortKey { get; set; } = "";
    [JsonPropertyName("name_initial")] public string NameInitial { get; set; } = "#";
    public bool Favorite => FavoriteValue != 0;
    public string FavoriteMark => Favorite ? "♥" : "";
    public bool Matches(string query) => Name.Contains(query, StringComparison.OrdinalIgnoreCase) || SearchKey.Contains(query, StringComparison.OrdinalIgnoreCase);
    public string Subtitle => $"{Bpm} BPM  ·  {(Source == "manual" ? "手动创建" : "本地导入")}";
}
public sealed class ProfileItem
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}
public sealed class LogItem
{
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("time")] public string Time { get; set; } = "";
    [JsonPropertyName("bpm")] public double Bpm { get; set; }
    public string Display => $"{Name}\n{Time.Replace('T', ' ')} · {Bpm:0.##} BPM";
}

public sealed class LibraryGroup
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("builtin")] public bool Builtin { get; set; }
}
