using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AssetWiiUdio;

// User-provided, local-only JSON. Never downloads, logs, or exports secret key material.
public sealed class TitleKeyDatabase
{
    private readonly Dictionary<string, string> entries = new(StringComparer.OrdinalIgnoreCase);
    public int Count => entries.Count;
    public string? Find(string titleId) => entries.GetValueOrDefault(titleId.Trim());
    private static bool IsId(string s) => Regex.IsMatch(s, "^[0-9a-fA-F]{16}$");
    private static bool IsKey(string s) => Regex.IsMatch(s, "^[0-9a-fA-F]{32}$");
    private static string? Str(JsonElement item, params string[] names)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in item.EnumerateObject())
            if (names.Any(n => string.Equals(n, property.Name, StringComparison.OrdinalIgnoreCase)) && property.Value.ValueKind == JsonValueKind.String)
                return property.Value.GetString()?.Trim();
        return null;
    }
    public static TitleKeyDatabase Load(string path)
    {
        if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new InvalidDataException("JSON database exceeds 32 MB limit.");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var db = new TitleKeyDatabase();
        void Add(string? id, string? key)
        {
            if (id != null && key != null && IsId(id) && IsKey(key)) db.entries[id.ToUpperInvariant()] = key.ToLowerInvariant();
        }
        void ReadItem(JsonElement item)
        {
            if (item.ValueKind != JsonValueKind.Object) return;
            Add(Str(item, "titleId", "title_id", "titleID", "id", "titleid"), Str(item, "titleKey", "title_key", "titlekey", "key"));
        }
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
            foreach (var item in doc.RootElement.EnumerateArray()) ReadItem(item);
        else if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (IsId(prop.Name))
                {
                    if (prop.Value.ValueKind == JsonValueKind.String) Add(prop.Name, prop.Value.GetString()?.Trim());
                    else Add(prop.Name, Str(prop.Value, "titleKey", "title_key", "titlekey", "key"));
                }
                else if (prop.Value.ValueKind == JsonValueKind.Array)
                    foreach (var item in prop.Value.EnumerateArray()) ReadItem(item);
            }
            ReadItem(doc.RootElement);
        }
        else throw new InvalidDataException("Expected a JSON object or array.");
        if (db.Count == 0) throw new InvalidDataException("No valid title ID/key pairs found. Expected 16-hex ID and 32-hex key.");
        return db;
    }
}
