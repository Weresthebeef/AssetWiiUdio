using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Linq;

namespace AssetWiiUdio;

// Read-only investigation of Wii U hashed content. No keys or decrypted payloads are logged.
public static class HashedContentDiagnostics
{
    private const int Cluster = 0x10000, Header = 0x400, Data = 0xFC00, Chunk = 0xFC0;
    public static string Run(string packageDir, byte[] titleKey, VirtualEntry entry)
    {
        var sb = new StringBuilder();
        sb.AppendLine("AssetWiiUdio v0.4.5 — Hashed Content Diagnostics (read only)");
        sb.AppendLine("No keys, decrypted data, or hash values are included.");
        sb.AppendLine($"Virtual file: {entry.Path} | length {entry.Length} | FST offset {entry.Offset} (0x{entry.Offset:X})");
        var tmd = TitleManifest.Read(Path.Combine(packageDir, "title.tmd"));
        if (entry.ContentIndex < 0 || entry.ContentIndex >= tmd.Contents.Count) throw new InvalidDataException("Invalid FST content index.");
        var content = tmd.Contents[entry.ContentIndex];
        sb.AppendLine($"Content ID: {content.Id} | index {content.Index} | type 0x{content.Type:X4} | TMD size {content.Size}");
        if ((content.Type & 2) == 0) { sb.AppendLine("Selected file uses non-hashed content."); return sb.ToString(); }
        string path = Path.Combine(packageDir, content.Id + ".app");
        using var fs = File.OpenRead(path);
        sb.AppendLine($"Physical encrypted size: {fs.Length}");
        // JNUSLib uses a physical 0x10000-byte cluster offset, and one H0
        // SHA-1 digest of the entire 0xFC00-byte decrypted data region.
        foreach (long clusterIndex in new[] { 0L, entry.Offset / Cluster, entry.Offset / Data }.Distinct())
        {
            long physical = checked(clusterIndex * Cluster);
            sb.AppendLine($"\nCluster {clusterIndex}, physical 0x{physical:X}:");
            if (physical + Cluster > fs.Length) { sb.AppendLine("Out of bounds"); continue; }
            fs.Position = physical;
            byte[] encryptedHeader = new byte[Header], encryptedData = new byte[Data];
            fs.ReadExactly(encryptedHeader); fs.ReadExactly(encryptedData);
            byte[] hashes = Decrypt(encryptedHeader, titleKey, new byte[16]);
            int slot = (int)(clusterIndex % 16);
            byte[] iv = new byte[16];
            Array.Copy(hashes, slot * 20, iv, 0, 16);
            byte[] data = Decrypt(encryptedData, titleKey, iv);
            bool pass = SHA1.HashData(data).AsSpan().SequenceEqual(hashes.AsSpan(slot * 20, 20));
            sb.AppendLine($"  Zero-IV hash header, H0 slot {slot}, full 0xFC00 data SHA-1: {(pass ? "PASS" : "FAIL")}");
        }
        sb.AppendLine("\nInterpretation: PASS confirms H0 for that cluster. FAIL indicates a remaining mapping, key, or decryption issue. No data exported.");
        return sb.ToString();
    }
    private static byte[] Decrypt(byte[] ciphertext, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.None; aes.Key = key; aes.IV = iv;
        using var transform = aes.CreateDecryptor();
        return transform.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
    }
}
