using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AssetWiiUdio;

// Read-only inspection; deliberately never prints encrypted or decrypted key bytes.
public static class MetadataInspector
{
    static ushort U16(byte[] b, int p) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p, 2));
    static uint U32(byte[] b, int p) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p, 4));
    static ulong U64(byte[] b, int p) => BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(p, 8));
    static string Text(byte[] b, int p, int length) => Encoding.ASCII.GetString(b, p, length).TrimEnd('\0', ' ');
    static string Hex(byte[] b, int p, int length) => Convert.ToHexString(b.AsSpan(p, length));
    public static Dictionary<string, string> Build(string folder)
    {
        var results = new Dictionary<string, string>();
        var tmd = TitleManifest.Read(Path.Combine(folder, "title.tmd"));
        results["Overview"] = Overview(folder, tmd);
        results["Ticket"] = Ticket(folder, tmd);
        results["TMD"] = Tmd(folder, tmd);
        results["Contents"] = Contents(folder, tmd);
        results["Certificates"] = Certificates(folder);
        return results;
    }
    static string Overview(string folder, TitleManifest m)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Wii U NUS Package — read-only metadata");
        sb.AppendLine(new string('=', 64));
        sb.AppendLine($"Folder: {folder}");
        sb.AppendLine($"Title ID: {m.TitleId}");
        sb.AppendLine($"Title version: {m.Version}");
        sb.AppendLine($"Content records: {m.Contents.Count}");
        foreach (var name in new[] { "title.tik", "title.tmd", "title.cert" })
        {
            var path = Path.Combine(folder, name);
            sb.AppendLine($"{name}: {(File.Exists(path) ? new FileInfo(path).Length.ToString("N0") + " bytes" : "MISSING")}");
        }
        int present = m.Contents.Count(c => File.Exists(Path.Combine(folder, c.Id + ".app")));
        sb.AppendLine($"Content files present: {present} / {m.Contents.Count}");
        sb.AppendLine($"Common key configuration: {TicketKeyResolver.KeyFileStatus()}");
        sb.AppendLine();
        sb.AppendLine("Notes: Reading metadata does not verify Nintendo signatures or content hashes.");
        sb.AppendLine("Keys are never displayed or saved in inspector reports.");
        return sb.ToString();
    }
    static string Ticket(string folder, TitleManifest m)
    {
        var path = Path.Combine(folder, "title.tik");
        if (!File.Exists(path)) return "title.tik is missing.";
        byte[] b = File.ReadAllBytes(path);
        var sb = new StringBuilder();
        sb.AppendLine("TICKET — title.tik"); sb.AppendLine($"Size: {b.Length:N0} bytes");
        if (b.Length < 0x1F2) { sb.AppendLine("Ticket too short for expected Wii U ticket fields."); return sb.ToString(); }
        sb.AppendLine($"Signature type: 0x{U32(b, 0):X8} (not verified)");
        sb.AppendLine($"Issuer: {Text(b, 0x140, 0x40)}");
        sb.AppendLine($"Ticket ID: 0x{U64(b, 0x1D0):X16}");
        sb.AppendLine($"Title ID: {Hex(b, 0x1DC, 8)}");
        sb.AppendLine($"Matches TMD title ID: {Hex(b, 0x1DC, 8).Equals(m.TitleId, StringComparison.OrdinalIgnoreCase)}");
        sb.AppendLine($"Encrypted title key: present (16 bytes; value hidden)");
        sb.AppendLine($"Common-key index: {b[0x1F1]} (raw ticket byte; interpretation depends on format)");
        sb.AppendLine();
        try
        {
            var key = TicketKeyResolver.Unwrap(folder, m);
            sb.AppendLine("Ticket title-key derivation: SUCCESS");
            var first = m.Contents.FirstOrDefault();
            if (first != null)
            {
                string file = Path.Combine(folder, first.Id + ".app");
                if (File.Exists(file) && new FileInfo(file).Length >= 16)
                {
                    byte[] encrypted = new byte[16];
                    using (var fs = File.OpenRead(file)) fs.ReadExactly(encrypted);
                    sb.AppendLine($"FST magic verification: {(TicketKeyResolver.Matches(key, first.Index, encrypted) ? "PASS" : "FAIL")}");
                }
            }
        }
        catch (Exception ex) { sb.AppendLine("Ticket title-key derivation: UNAVAILABLE — " + ex.Message); }
        return sb.ToString();
    }
    static string Tmd(string folder, TitleManifest m)
    {
        var b = File.ReadAllBytes(Path.Combine(folder, "title.tmd"));
        var sb = new StringBuilder();
        sb.AppendLine("TITLE METADATA — title.tmd");
        sb.AppendLine($"Size: {b.Length:N0} bytes");
        sb.AppendLine($"Signature type: 0x{U32(b, 0):X8} (not verified)");
        sb.AppendLine($"Issuer: {Text(b, 0x140, 0x40)}");
        sb.AppendLine($"Title ID: {m.TitleId}");
        sb.AppendLine($"Title version: {m.Version}");
        sb.AppendLine($"Content records: {m.Contents.Count}");
        sb.AppendLine($"System version field (raw): 0x{U64(b, 0x184):X16}");
        sb.AppendLine();
        sb.AppendLine("Signature authenticity and content hashes have NOT been verified.");
        return sb.ToString();
    }
    static string Contents(string folder, TitleManifest m)
    {
        var sb = new StringBuilder();
        sb.AppendLine("CONTENT MANIFEST — TMD records");
        sb.AppendLine("ID         Index  Type    TMD Size          Actual Size       State");
        sb.AppendLine(new string('-', 85));
        foreach (var c in m.Contents)
        {
            var path = Path.Combine(folder, c.Id + ".app");
            bool present = File.Exists(path);
            long actual = present ? new FileInfo(path).Length : -1;
            string state = !present ? "MISSING" : (ulong)actual == c.Size ? "size matches" : "SIZE MISMATCH";
            sb.AppendLine($"{c.Id}   {c.Index,4}   0x{c.Type:X4}   {c.Size,15:N0}   {(present ? actual.ToString("N0") : "—"),15}   {state}{((c.Type & 2) != 0 ? " [hashed]" : "")}");
        }
        sb.AppendLine(); sb.AppendLine("Size checks are not cryptographic hash checks.");
        return sb.ToString();
    }
    static string Certificates(string folder)
    {
        var path = Path.Combine(folder, "title.cert");
        if (!File.Exists(path)) return "title.cert is missing.";
        byte[] b = File.ReadAllBytes(path);
        var sb = new StringBuilder();
        sb.AppendLine("CERTIFICATE CHAIN — title.cert");
        sb.AppendLine($"Size: {b.Length:N0} bytes");
        sb.AppendLine("Certificate records are scanned for plausible signature headers and issuer/name fields.");
        sb.AppendLine("This is structural inspection, NOT cryptographic signature verification.");
        int pos = 0, count = 0;
        while (pos + 0xC0 <= b.Length && count < 32)
        {
            uint type = U32(b, pos);
            int signatureLength = type switch { 0x00010000 => 0x200, 0x00010001 => 0x100, 0x00010002 => 0x3C, _ => 0 };
            if (signatureLength == 0) break;
            int header = signatureLength + 0x40;
            if (pos + header + 0x88 > b.Length) break;
            string issuer = Text(b, pos + header, 0x40);
            string name = Text(b, pos + header + 0x44, 0x40);
            uint keyType = U32(b, pos + header + 0x40);
            int keyBytes = keyType switch { 0 => 0x200, 1 => 0x100, 2 => 0x3C, _ => 0 };
            int recordLength = keyBytes == 0 ? 0 : (header + 0x88 + keyBytes + (keyType == 2 ? 0 : 4) + 0x3F) & ~0x3F;
            sb.AppendLine($"\nRecord {++count} at 0x{pos:X}: signature 0x{type:X8}; key type {keyType}");
            sb.AppendLine($"  Issuer: {issuer}"); sb.AppendLine($"  Name: {name}");
            if (recordLength <= 0 || pos + recordLength > b.Length) break;
            pos += recordLength;
        }
        if (count == 0) sb.AppendLine("No supported certificate record header detected.");
        sb.AppendLine($"\nParsed candidate records: {count}");
        return sb.ToString();
    }
}
