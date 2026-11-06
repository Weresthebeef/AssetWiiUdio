using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AssetWiiUdio;

// Read-only PS4 reference routes. These are candidate names for matching Wii U paths,
// not proof of Wii U identity; preserve the hash and report ambiguous matches.
public sealed class AssetNameResolver
{
    private readonly Dictionary<string, HashSet<string>> names = new(StringComparer.OrdinalIgnoreCase);
    public int Count => names.Count;
    public static AssetNameResolver Load(string path)
    {
        var resolver = new AssetNameResolver();
        if (!File.Exists(path)) return resolver;
        foreach (string line in File.ReadLines(path))
        {
            int tab = line.IndexOf('\t');
            if (tab <= 0) continue;
            string route = line[..tab].Trim().Replace('\\', '/').ToLowerInvariant();
            string name = line[(tab + 1)..].Trim();
            if (name.Length == 0 || name.Contains('/') || name.Contains('\\')) continue;
            if (!resolver.names.TryGetValue(route, out var options)) resolver.names[route] = options = new(StringComparer.Ordinal);
            options.Add(name);
        }
        return resolver;
    }
    public string Label(string virtualPath, bool friendlyOnly = false)
    {
        string original = virtualPath.Split('/').Last();
        string normalized = virtualPath.Replace('\\', '/').ToLowerInvariant();
        int start = normalized.IndexOf("assets/", StringComparison.Ordinal);
        if (start < 0) return original;
        string route = normalized[(start + "assets/".Length)..];
        if (!names.TryGetValue(route, out var options) || options.Count == 0) return original;
        if (options.Count > 1) return original + " (ambiguous: " + string.Join(" | ", options.OrderBy(x => x)) + ")";
        string name = options.First();
        return friendlyOnly ? name : original + " (" + name + ")";
    }
    public string Describe(string virtualPath)
    {
        string label = Label(virtualPath);
        return label == virtualPath.Split('/').Last() ? "No mapping found" : label;
    }
}
