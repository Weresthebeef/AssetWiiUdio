using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AssetWiiUdio;

// Read-only FST index. The title key is derived from a user-supplied common key and ticket, or supplied manually.
// No keys are embedded in the application or written to disk.
public sealed record VirtualEntry(string Path, bool IsDirectory, long Length, long Offset, int ContentIndex, ushort Flags);
public sealed class VirtualFst
{
    public IReadOnlyList<VirtualEntry> Entries { get; }
    public byte[] ContentKey { get; private set; } = Array.Empty<byte>();
    private VirtualFst(List<VirtualEntry> entries) => Entries = entries;
    private static uint U32(byte[] b, int p) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p, 4));
    private static ushort U16(byte[] b, int p) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p, 2));
    public static VirtualFst Open(string packageDir, string titleKeyHex)
    {
        var manifest = TitleManifest.Read(System.IO.Path.Combine(packageDir, "title.tmd"));
        byte[] key = TicketKeyResolver.Resolve(packageDir, manifest, titleKeyHex, out _);
        var fstContent = manifest.Contents[0];
        if ((fstContent.Type & 2) != 0) throw new NotSupportedException("Hashed FST content is not supported in this build.");
        var fstFile = System.IO.Path.Combine(packageDir, fstContent.Id + ".app");
        if (!File.Exists(fstFile)) throw new FileNotFoundException("FST content not found", fstFile);
        if (fstContent.Size > 64 * 1024 * 1024) throw new InvalidDataException("FST exceeds 64 MiB safety limit.");
        var encrypted = File.ReadAllBytes(fstFile);
        if (encrypted.Length < 0x30 || encrypted.Length % 16 != 0) throw new InvalidDataException("Invalid FST encrypted length.");
        var iv = new byte[16]; // FST is first content, content index zero
        BinaryPrimitives.WriteUInt16BigEndian(iv.AsSpan(0, 2), fstContent.Index);
        byte[] plain = new byte[encrypted.Length];
        using (var aes = Aes.Create())
        {
            aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.None; aes.Key = key; aes.IV = iv;
            using var dec = aes.CreateDecryptor();
            int count = dec.TransformBlock(encrypted, 0, encrypted.Length, plain, 0);
            if (count != plain.Length) throw new InvalidDataException("Incomplete FST decryption.");
        }
        if (U32(plain, 0) != 0x46535400) throw new InvalidDataException("FST magic mismatch. Check the title key and package region.");
        uint sectionCount = U32(plain, 8);
        if (sectionCount > 4096) throw new InvalidDataException("Unreasonable FST section count.");
        long start = 0x20L + sectionCount * 0x20L;
        if (start + 16 > plain.Length) throw new InvalidDataException("FST entries out of bounds.");
        uint countEntries = U32(plain, (int)start + 8);
        if (countEntries == 0 || countEntries > 500000 || start + countEntries * 16L > plain.Length) throw new InvalidDataException("Invalid FST entry count.");
        long names = start + countEntries * 16L;
        var entries = new List<VirtualEntry>((int)countEntries);
        var dirs = new Stack<(string path, uint end)>();
        dirs.Push(("", countEntries));
        for (uint i = 1; i < countEntries; i++)
        {
            while (dirs.Count > 1 && i >= dirs.Peek().end) dirs.Pop();
            int at = checked((int)(start + i * 16L));
            uint typeName = U32(plain, at);
            bool directory = (typeName >> 24 & 1) != 0;
            uint nameOffset = typeName & 0xFFFFFF;
            long nameAt = names + nameOffset;
            if (nameAt < names || nameAt >= plain.Length) throw new InvalidDataException("FST name outside table.");
            int endName = Array.IndexOf(plain, (byte)0, (int)nameAt);
            if (endName < 0 || endName - nameAt > 1024) throw new InvalidDataException("Invalid FST name.");
            string name = Encoding.UTF8.GetString(plain, (int)nameAt, endName - (int)nameAt);
            if (name is "" or "." or ".." || name.Contains('/') || name.Contains('\\')) throw new InvalidDataException("Unsafe FST filename.");
            string path = dirs.Peek().path.Length == 0 ? name : dirs.Peek().path + "/" + name;
            uint a = U32(plain, at + 4), b = U32(plain, at + 8);
            ushort flags = U16(plain, at + 12), contentIndex = U16(plain, at + 14);
            if (contentIndex >= manifest.Contents.Count) throw new InvalidDataException("FST content index outside manifest.");
            if (directory)
            {
                if (b <= i || b > countEntries || dirs.Count > 64) throw new InvalidDataException("Invalid FST directory range.");
                entries.Add(new VirtualEntry(path, true, 0, 0, contentIndex, flags));
                dirs.Push((path, b));
            }
            else
            {
                // Flag 0x4 selects byte offsets; otherwise units are 0x20 bytes (CDecrypt).
                long offset = (flags & 4) != 0 ? a : (long)a * 0x20;
                entries.Add(new VirtualEntry(path, false, b, offset, contentIndex, flags));
            }
        }
        return new VirtualFst(entries) { ContentKey = key };
    }
}
