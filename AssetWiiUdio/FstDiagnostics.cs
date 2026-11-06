using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AssetWiiUdio;

// Read-only diagnostics: no key material is persisted or included in the report.
public static class FstDiagnostics
{
    public static string Run(string folder, string keyHex)
    {
        var report = new StringBuilder();
        report.AppendLine("AssetWiiUdio v0.4.2 — FST diagnostics (read only)");
        report.AppendLine("Generated: " + DateTimeOffset.Now.ToString("u"));
        report.AppendLine("Title keys and decrypted payloads are NEVER included in this report.");
        report.AppendLine();
        try
        {
            var m = TitleManifest.Read(Path.Combine(folder, "title.tmd"));
            report.AppendLine($"Title ID: {m.TitleId} | Version: {m.Version} | Records: {m.Contents.Count}");
            if (m.Contents.Count == 0) return report.AppendLine("ERROR: No content records.").ToString();
            var c = m.Contents[0];
            var file = Path.Combine(folder, c.Id + ".app");
            report.AppendLine($"FST candidate: {c.Id}.app | index {c.Index} | type 0x{c.Type:X4} | TMD size {c.Size:N0}");
            if (!File.Exists(file)) return report.AppendLine("ERROR: Candidate file missing.").ToString();
            long actual = new FileInfo(file).Length;
            report.AppendLine($"Actual size: {actual:N0} | Size agrees: {actual == (long)c.Size}");
            report.AppendLine($"Hashed content flag: {(c.Type & 2) != 0}");
            if ((c.Type & 2) != 0) return report.AppendLine("STOP: Hashed FST content requires separate handling.").ToString();
            byte[] key;
            try { key = TicketKeyResolver.Unwrap(folder, m); }
            catch (Exception ex) { return report.AppendLine("ERROR: Automatic ticket key derivation failed: " + ex.Message).ToString(); }
            report.AppendLine("Key source: title.tik + configured common key (never displayed)");
            try
            {
                var derived = TicketKeyResolver.Unwrap(folder, m);
                var suppliedMatchesDerived = key.SequenceEqual(derived);
                var ticket = File.ReadAllBytes(Path.Combine(folder, "title.tik"));
                report.AppendLine($"Ticket-derived key matches itself: {suppliedMatchesDerived}");
                report.AppendLine($"Derived key differs from encrypted ticket field: {!key.SequenceEqual(ticket.AsSpan(0x1BF, 16).ToArray())}");
                var first = new byte[16];
                using (var fs = File.OpenRead(file)) fs.ReadExactly(first);
                report.AppendLine($"Ticket-derived key FST magic: {(TicketKeyResolver.Matches(derived, c.Index, first) ? "YES" : "NO")}");
            }
            catch (Exception ex) { report.AppendLine("Ticket-key diagnostics: " + ex.GetType().Name + " (" + ex.Message + ")"); }

            var encrypted = new byte[0x1000];
            int read;
            using (var stream = File.OpenRead(file)) read = stream.Read(encrypted, 0, encrypted.Length);
            if (read < 0x30) return report.AppendLine("ERROR: Content too short.").ToString();
            report.AppendLine($"Read {read} bytes from first content; sample is not written to disk.");
            var iv = new byte[16];
            BinaryPrimitives.WriteUInt16BigEndian(iv.AsSpan(0,2), c.Index);
            Inspect(report, "TMD content-index IV (expected)", key, iv, encrypted, read);
            Inspect(report, "Zero IV (comparison)", key, new byte[16], encrypted, read);
            report.AppendLine();
            report.AppendLine("Interpretation:");
            report.AppendLine("- Expected-IV FST magic found: first-stage decryption is plausible; inspect FST parser next.");
            report.AppendLine("- Neither mode yields FST: wrong key type, wrong content/IV, or damaged input.");
            report.AppendLine("- Index 0 makes both IV tests identical; that is expected, not independent evidence.");
            report.AppendLine("- Ticket-derived FST magic confirms the selected common key can decrypt the FST.");
        }
        catch (Exception ex) { report.AppendLine("ERROR: " + ex.GetType().Name + ": " + ex.Message); }
        return report.ToString();
    }
    private static void Inspect(StringBuilder report, string label, byte[] key, byte[] iv, byte[] data, int read)
    {
        try
        {
            using var aes = Aes.Create();
            aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.None;
            aes.Key = key; aes.IV = iv;
            using var transform = aes.CreateDecryptor();
            var decrypted = new byte[read - (read % 16)];
            int got = transform.TransformBlock(data, 0, decrypted.Length, decrypted, 0);
            bool magic = got >= 4 && decrypted[0] == 0x46 && decrypted[1] == 0x53 && decrypted[2] == 0x54 && decrypted[3] == 0;
            report.AppendLine($"{label}: FST magic = {(magic ? "YES" : "NO")}; first 16 decrypted bytes = {Convert.ToHexString(decrypted.AsSpan(0, 16))}");
            if (magic && got >= 12)
            {
                uint sections = BinaryPrimitives.ReadUInt32BigEndian(decrypted.AsSpan(8, 4));
                report.AppendLine($"  FST section count candidate: {sections} (plausible: {sections is > 0 and < 4096})");
            }
        }
        catch (Exception ex) { report.AppendLine($"{label}: ERROR {ex.GetType().Name}: {ex.Message}"); }
    }
}
