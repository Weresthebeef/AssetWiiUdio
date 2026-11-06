using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AssetWiiUdio;

// Optional second-stage transform based on the user-supplied Disney Infinity 3.0 QuickBMS script.
// Detection is evidence-based: an AES-CTR candidate is never called confirmed unless a known signature appears.
public static class DisneyInfinityAsset
{
    private static readonly byte[] StandardKey = Convert.FromHexString("681BBEEA63160188F9B7945104A51499");
    private static readonly byte[] PsxKey = Convert.FromHexString("3E9FB2A6BE59CCCBD904BEEFFFE118FB");
    public sealed record Result(string Layer, string Encryption, string Format, byte[] Bytes, bool Confirmed, string Details);

    public static Result Analyze(string filename, byte[] raw)
    {
        string direct = Identify(raw);
        if (direct != "Unknown") return new Result("Nintendo decrypted file", "No additional encryption detected", direct, raw, true, "Recognized signature in extracted bytes.");
        if (raw.Length == 0) return new Result("Nintendo decrypted file", "Unknown", "Empty", raw, false, "Empty file.");
        // Physical Wii U paths are hashed, so filename prefixes alone cannot select a key.
        // Try the known standard and recovered PSX keys, requiring a recognized signature.
        bool psxHint = Path.GetFileName(filename).StartsWith("psx_", StringComparison.OrdinalIgnoreCase);
        foreach (var entry in psxHint
            ? new[] { (Name: "recovered PSX", Key: PsxKey), (Name: "standard", Key: StandardKey) }
            : new[] { (Name: "standard", Key: StandardKey), (Name: "recovered PSX", Key: PsxKey) })
        {
            byte[] candidate = TransformCtr(raw, entry.Key);
            string signature = Identify(candidate);
            if (signature != "Unknown")
                return new Result("Disney Infinity second stage", $"AES-128-CTR ({entry.Name} key; signature confirmed)",
                    signature, candidate, true, "Archive key selected by verified decrypted signature, not hashed filename.");
        }
        return new Result("Nintendo decrypted file", "Unknown; both archive keys failed signature checks", "Unknown", raw, false,
            "Neither standard nor recovered PSX AES-CTR key produced a recognized header. Original bytes retained.");
    }

    public static byte[] TransformCtr(byte[] input, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.None; aes.Key = key;
        using var transform = aes.CreateEncryptor();
        byte[] counter = new byte[16], stream = new byte[16], output = new byte[input.Length];
        for (int pos = 0; pos < input.Length; pos += 16)
        {
            transform.TransformBlock(counter, 0, 16, stream, 0);
            for (int j = 0; j < 16 && pos + j < input.Length; j++) output[pos + j] = (byte)(input[pos + j] ^ stream[j]);
            for (int k = 15; k >= 0; k--) { if (++counter[k] != 0) break; }
        }
        return output;
    }

    public static string Identify(byte[] b)
    {
        if (b.Length >= 4 && b[0] == 0x50 && b[1] == 0x4B && b[2] == 0xFF && b[3] == 0xFF) return "Disney Infinity PK-FFFF container";
        if (b.Length >= 4 && Encoding.ASCII.GetString(b, 0, 4) == "DBNK") return "Disney Infinity DBNK sound bank";
        if (b.Length >= 4 && b[0] == 0x50 && b[1] == 0x4B && b[2] == 3 && b[3] == 4) return "ZIP local file";
        if (b.Length >= 4 && b[0] == 0x46 && b[1] == 0x53 && b[2] == 0x54 && b[3] == 0) return "Wii U FST";
        if (b.Length >= 5 && Encoding.ASCII.GetString(b, 0, 5) == "<?xml") return "XML";
        if (b.Length >= 4 && b[0] == 0x7F && b[1] == 0x45 && b[2] == 0x4C && b[3] == 0x46) return "ELF executable";
        if (b.Length >= 4 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "PNG image";
        if (b.Length >= 4 && Encoding.ASCII.GetString(b, 0, 4) == "DDS ") return "DDS texture";
        return "Unknown";
    }

    // PK-FFFF index: signature, u32 count, then (u32 name CRC, u32 local-header offset).
    // This is a listing/inspection operation, not a complete QuickBMS-compatible decompressor.
    public static string DescribeContainer(Result result)
    {
        byte[] b = result.Bytes;
        if (result.Format == "Disney Infinity PK-FFFF container" && b.Length >= 8)
        {
            uint count = BitConverter.ToUInt32(b, 4);
            if (count > 100000 || 8L + count * 8L > b.Length) return "Container index is truncated or invalid.";
            var sb = new StringBuilder($"Embedded archive entries: {count:N0}\n");
            for (int i = 0; i < Math.Min((int)count, 40); i++)
            {
                int p = 8 + i * 8;
                uint crc = BitConverter.ToUInt32(b, p), offset = BitConverter.ToUInt32(b, p + 4);
                sb.AppendLine($"  {i,4}  CRC {crc:X8}  local header 0x{offset:X8}");
            }
            if (count > 40) sb.AppendLine("  … first 40 entries shown");
            return sb.ToString();
        }
        if (result.Format == "Disney Infinity DBNK sound bank" && b.Length >= 0x20)
        {
            ushort entries = BitConverter.ToUInt16(b, 0x14), files = BitConverter.ToUInt16(b, 0x16);
            uint baseOffset = BitConverter.ToUInt32(b, 0x18);
            return $"DBNK entries: {entries}; files: {files}; base offset: 0x{baseOffset:X8}";
        }
        return "No embedded archive index identified.";
    }
}
