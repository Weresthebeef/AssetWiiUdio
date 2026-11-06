using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

namespace AssetWiiUdio;

// Random-access AES-CBC reader for non-hashed Wii U NUS content.
// Only the selected encrypted blocks are read; the archive is never modified.
public sealed class VirtualContentReader
{
    private readonly string packageDir;
    private readonly TitleManifest manifest;
    private readonly byte[] key;
    public VirtualContentReader(string packageDir, byte[] key)
    {
        this.packageDir = packageDir;
        this.key = (byte[])key.Clone();
        manifest = TitleManifest.Read(Path.Combine(packageDir, "title.tmd"));
    }
    public byte[] Read(VirtualEntry entry, int maxBytes)
    {
        if (entry.IsDirectory) throw new InvalidOperationException("Cannot read a directory.");
        if (entry.Length < 0 || entry.Offset < 0 || maxBytes < 0) throw new InvalidDataException("Invalid virtual file range.");
        if (entry.ContentIndex < 0 || entry.ContentIndex >= manifest.Contents.Count) throw new InvalidDataException("Invalid content index.");
        var content = manifest.Contents[entry.ContentIndex];
        if ((content.Type & 2) != 0) return ReadHashed(entry, content, maxBytes);
        string path = Path.Combine(packageDir, content.Id + ".app");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        long count = Math.Min(entry.Length, (long)maxBytes);
        if (count > int.MaxValue - 32) throw new InvalidOperationException("Requested read is too large.");
        long firstBlock = entry.Offset / 16;
        long last = checked(entry.Offset + count);
        if (last > file.Length || (ulong)last > content.Size) throw new InvalidDataException("Virtual file extends beyond encrypted content.");
        if (count == 0) return Array.Empty<byte>();
        long cipherStart = firstBlock * 16;
        long cipherEnd = checked(((last + 15) / 16) * 16);
        if (cipherEnd > file.Length) throw new InvalidDataException("Incomplete final encrypted block.");
        byte[] iv = new byte[16];
        if (firstBlock == 0) BinaryPrimitives.WriteUInt16BigEndian(iv.AsSpan(0, 2), content.Index);
        else
        {
            file.Position = cipherStart - 16;
            file.ReadExactly(iv);
        }
        file.Position = cipherStart;
        int cipherLength = checked((int)(cipherEnd - cipherStart));
        byte[] encrypted = new byte[cipherLength];
        file.ReadExactly(encrypted);
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.None; aes.Key = key; aes.IV = iv;
        using var decryptor = aes.CreateDecryptor();
        byte[] plain = decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length);
        byte[] output = new byte[(int)count];
        Buffer.BlockCopy(plain, (int)(entry.Offset - cipherStart), output, 0, output.Length);
        return output;
    }

    // Hashed FST offsets address the logical 0xFC00-byte data stream.
    // Map each logical cluster to a physical 0x10000-byte encrypted cluster.
    // The 16 H0 entries cover 16 successive CLUSTERS, not 16 subchunks.
    private byte[] ReadHashed(VirtualEntry entry, TitleContent content, int maxBytes)
    {
        const int ClusterSize = 0x10000, HashSize = 0x400, DataSize = 0xFC00;
        long count = Math.Min(entry.Length, (long)maxBytes);
        if (count == 0) return Array.Empty<byte>();
        if (count > int.MaxValue) throw new InvalidOperationException("Read too large.");
        string path = Path.Combine(packageDir, content.Id + ".app");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] result = new byte[checked((int)count)];
        long logicalCursor = entry.Offset;
        int written = 0;
        while (written < result.Length)
        {
            long clusterIndex = logicalCursor / DataSize;
            int within = checked((int)(logicalCursor % DataSize));
            long physical = checked(clusterIndex * ClusterSize);
            if (physical + ClusterSize > file.Length || (ulong)(physical + ClusterSize) > content.Size)
                throw new InvalidDataException("Hashed content cluster exceeds encrypted file.");
            file.Position = physical;
            byte[] encryptedHashes = new byte[HashSize];
            byte[] encryptedData = new byte[DataSize];
            file.ReadExactly(encryptedHashes);
            file.ReadExactly(encryptedData);
            byte[] hashes = DecryptCbc(encryptedHashes, new byte[16]);
            int h0Slot = checked((int)(clusterIndex % 16));
            byte[] dataIv = new byte[16];
            Buffer.BlockCopy(hashes, h0Slot * 20, dataIv, 0, 16);
            byte[] data = DecryptCbc(encryptedData, dataIv);
            // SHA1 covers the complete 0xFC00 data region of ONE cluster.
            byte[] digest = SHA1.HashData(data);
            if (!digest.AsSpan().SequenceEqual(hashes.AsSpan(h0Slot * 20, 20)))
                throw new InvalidDataException($"Hashed content H0 mismatch at cluster {clusterIndex}, slot {h0Slot}. The data was not exported.");
            int take = Math.Min(result.Length - written, DataSize - within);
            Buffer.BlockCopy(data, within, result, written, take);
            written += take;
            logicalCursor = checked(logicalCursor + take);
        }
        return result;
    }

    private byte[] DecryptCbc(byte[] ciphertext, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        aes.IV = iv;
        using var transform = aes.CreateDecryptor();
        return transform.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
    }

    public void Extract(VirtualEntry entry, string destination)
    {
        if (entry.Length > int.MaxValue) throw new NotSupportedException("Files larger than 2 GiB require streaming extraction, not implemented yet.");
        // Avoid leaving partial output on decryption errors.
        byte[] bytes = Read(entry, checked((int)entry.Length));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temp = destination + ".assetwiiudio-partial";
        try { File.WriteAllBytes(temp, bytes); File.Move(temp, destination, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
