using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace AssetWiiUdio;

// Mirrors ticket title-key unwrapping in the bundled CDecrypt 4.8 source.
// Never saves or logs title keys.
public static class TicketKeyResolver
{
    // Common keys are supplied by the user. No cryptographic key material is embedded.
    private static readonly string ConfigDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetWiiUdio");
    private static readonly string ConfigFile = Path.Combine(ConfigDir, "common-key-settings.json");
    private sealed class KeySettings { public string? RetailKeyFile { get; set; } public string? DevKeyFile { get; set; } }
    public static void SetCommonKeyFile(string path, bool development = false)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Common key file missing", path);
        _ = ReadKeyFile(path); // validate before saving the location
        KeySettings settings = ReadSettings();
        if (development) settings.DevKeyFile = Path.GetFullPath(path);
        else settings.RetailKeyFile = Path.GetFullPath(path);
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(ConfigFile, System.Text.Json.JsonSerializer.Serialize(settings));
    }
    private static KeySettings ReadSettings()
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<KeySettings>(File.ReadAllText(ConfigFile)) ?? new KeySettings(); }
        catch (IOException) { return new KeySettings(); }
        catch (System.Text.Json.JsonException) { return new KeySettings(); }
    }
    private static byte[] ReadKeyFile(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        if (data.Length == 16) return data;
        string value = System.Text.Encoding.UTF8.GetString(data).Trim().Replace(" ", "").Replace("-", "");
        if (value.Length == 32)
        {
            try { return Convert.FromHexString(value); } catch (FormatException) { }
        }
        throw new InvalidDataException("Common key file must contain 16 binary bytes or 32 hexadecimal characters.");
    }
    public static string KeyFileStatus()
    {
        var settings = ReadSettings();
        return $"Retail: {(settings.RetailKeyFile is { } r && File.Exists(r) ? "configured" : "not configured")} | Development: {(settings.DevKeyFile is { } d && File.Exists(d) ? "configured" : "not configured")}";
    }
    public static byte[] Unwrap(string packageDir, TitleManifest manifest)
    {
        var tmd = File.ReadAllBytes(Path.Combine(packageDir, "title.tmd"));
        var ticket = File.ReadAllBytes(Path.Combine(packageDir, "title.tik"));
        if (ticket.Length < 0x1CF || tmd.Length < 0x194)
            throw new InvalidDataException("Ticket or TMD is too short for title-key processing.");
        string issuer = System.Text.Encoding.ASCII.GetString(tmd, 0x140, Math.Min(64, tmd.Length - 0x140)).TrimEnd('\0');
        var settings = ReadSettings();
        string? keyPath = issuer.StartsWith("Root-CA00000003-CP0000000b", StringComparison.Ordinal) ? settings.RetailKeyFile :
                          issuer.StartsWith("Root-CA00000004-CP00000010", StringComparison.Ordinal) ? settings.DevKeyFile :
                          throw new InvalidDataException("Unrecognized TMD issuer; cannot select common key.");
        if (string.IsNullOrWhiteSpace(keyPath) || !File.Exists(keyPath))
            throw new InvalidOperationException("Common key file is not configured or cannot be found. Use Configure Common Key first.");
        byte[] common = ReadKeyFile(keyPath);
        byte[] iv = new byte[16];
        Convert.FromHexString(manifest.TitleId).CopyTo(iv, 0);
        byte[] wrapped = ticket.AsSpan(0x1BF, 16).ToArray();
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.None;
        aes.Key = common; aes.IV = iv;
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(wrapped, 0, 16);
    }
    public static byte[] Resolve(string packageDir, TitleManifest manifest, string suppliedKeyHex, out string source)
    {
        var fst = manifest.Contents.FirstOrDefault() ?? throw new InvalidDataException("No TMD contents.");
        var file = Path.Combine(packageDir, fst.Id + ".app");
        if (!File.Exists(file)) throw new FileNotFoundException("FST content missing", file);
        byte[] encrypted = new byte[16];
        using (var stream = File.OpenRead(file))
            if (stream.Read(encrypted, 0, 16) != 16) throw new InvalidDataException("FST content too short.");
        // Ticket first; only fall back to an explicitly supplied decrypted title key.
        Exception? ticketError = null;
        try
        {
            byte[] ticketKey = Unwrap(packageDir, manifest);
            if (Matches(ticketKey, fst.Index, encrypted)) { source = "Ticket-derived decrypted title key"; return ticketKey; }
            ticketError = new InvalidDataException("Ticket-derived key did not match FST magic.");
        }
        catch (Exception ex) { ticketError = ex; }
        if (string.IsNullOrWhiteSpace(suppliedKeyHex))
            throw new InvalidDataException("Automatic ticket decryption failed: " + ticketError?.Message + " Configure a common key or supply a decrypted title key.");
        byte[] supplied;
        try { supplied = Convert.FromHexString(suppliedKeyHex.Trim()); }
        catch { throw new InvalidDataException("Supplied title key must be hexadecimal."); }
        if (supplied.Length != 16) throw new InvalidDataException("Supplied title key must be 16 bytes.");
        if (Matches(supplied, fst.Index, encrypted)) { source = "Supplied decrypted title key"; return supplied; }
        // A common user mistake is providing the encrypted ticket key itself.
        var ticket = File.ReadAllBytes(Path.Combine(packageDir, "title.tik"));
        if (supplied.SequenceEqual(ticket.AsSpan(0x1BF, 16).ToArray()))
            throw new InvalidDataException("Supplied key equals the encrypted ticket field. Ticket-derived key also failed FST verification. Check ticket/TMD pairing and archive integrity.");
        throw new InvalidDataException("Neither ticket-derived nor supplied key produces FST magic. Verify the ticket/TMD match the .app files and compare with original CDecrypt.");
    }
    public static bool Matches(byte[] key, ushort index, byte[] encrypted)
    {
        byte[] iv = new byte[16]; iv[0] = (byte)(index >> 8); iv[1] = (byte)index;
        using var aes = Aes.Create(); aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.None;
        aes.Key = key; aes.IV = iv;
        using var decryptor = aes.CreateDecryptor();
        byte[] first = decryptor.TransformFinalBlock(encrypted, 0, 16);
        return first[0] == 0x46 && first[1] == 0x53 && first[2] == 0x54 && first[3] == 0;
    }
}
