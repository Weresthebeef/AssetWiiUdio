using System;
using System.Collections.Generic;
using System.IO;
using System.Buffers.Binary;

namespace AssetWiiUdio;

// Wii U TMD metadata reader; does not decrypt content or store title keys.
public sealed record TitleContent(string Id, ushort Index, ushort Type, ulong Size);
public sealed record TitleManifest(string TitleId, ushort Version, IReadOnlyList<TitleContent> Contents)
{
    public static TitleManifest Read(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        // Layout verified against the TitleMetaData structure in bundled CDecrypt 4.8.
        const int recordOffset = 0xB04;
        const int recordSize = 0x30;
        if (data.Length < recordOffset) throw new InvalidDataException("TMD is too short.");
        ushort count = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(0x1DE, 2));
        if (count > 4096 || (long)recordOffset + (long)count * recordSize > data.Length)
            throw new InvalidDataException("TMD content record count exceeds file length.");
        ulong id = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(0x18C, 8));
        ushort version = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(0x1DC, 2));
        var contents = new List<TitleContent>(count);
        for (int i = 0; i < count; i++)
        {
            var r = data.AsSpan(recordOffset + i * recordSize, recordSize);
            contents.Add(new TitleContent(
                BinaryPrimitives.ReadUInt32BigEndian(r[..4]).ToString("x8"),
                BinaryPrimitives.ReadUInt16BigEndian(r.Slice(4, 2)),
                BinaryPrimitives.ReadUInt16BigEndian(r.Slice(6, 2)),
                BinaryPrimitives.ReadUInt64BigEndian(r.Slice(8, 8))));
        }
        return new TitleManifest(id.ToString("X16"), version, contents);
    }
}
