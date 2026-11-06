// Canonical texture-reference reader shared by DI research tools. No graphics dependencies.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DIResearch.Formats
{
    public static class MtbTextureTable
    {
        public const string Revision = "2026-10-30-texb-alignment-1";

        static uint U32(byte[] data, int at, bool big)
        {
            if (at < 0 || at > data.Length - 4) throw new InvalidDataException("MTB field outside file.");
            uint value = 0;
            for (int j = 0; j < 4; j++)
                if (big) value = (value << 8) | data[at + j];
                else value |= (uint)data[at + j] << (j * 8);
            return value;
        }

        public static List<string> Read(byte[] data)
        {
            if (data == null || data.Length < 16 || data.Length > 64 * 1024 * 1024)
                throw new InvalidDataException("Invalid MTB size.");
            string magic = Encoding.ASCII.GetString(data, 0, 4);
            bool big = magic == "LDNB";
            if (magic != "BNDL" && !big) throw new InvalidDataException("Not an MTB bundle.");
            if (U32(data, 4, big) != 1 || U32(data, 8, big) != data.Length)
                throw new InvalidDataException("Unsupported MTB header or total length.");
            uint sections = U32(data, 12, big);
            if (sections > 4096) throw new InvalidDataException("Too many MTB sections.");
            int at = 16;
            var result = new List<string>();
            for (uint i = 0; i < sections; i++)
            {
                if (at > data.Length - 16) throw new InvalidDataException("Truncated MTB section.");
                string tag = Encoding.ASCII.GetString(data, at, 4);
                uint version = U32(data, at + 4, big);
                uint size = U32(data, at + 8, big), count = U32(data, at + 12, big);
                if (size < 16 || size > data.Length - at) throw new InvalidDataException("Invalid MTB section bounds.");
                long end = at + (long)size;
                if (tag == (big ? "BXET" : "TEXB"))
                {
                    int header = big ? 24 : 20, stride = big ? 16 : 12;
                    if (version != 3 || size < header || count > 4096)
                        throw new InvalidDataException("Unsupported TEXB version or record count.");
                    uint nameBytes = U32(data, at + 16, big);
                    long namesEnd = at + (long)header + nameBytes;
                    long first = (namesEnd + 3) & ~3L;
                    if (namesEnd > end || first > end || first + (long)count * stride > end)
                        throw new InvalidDataException("TEXB names/records exceed their section.");
                    // IDs are opaque bytes, not endian-swapped integers. Keep source order.
                    for (uint record = 0; record < count; record++)
                    {
                        int offset = checked((int)(first + record * (long)stride));
                        result.Add(BitConverter.ToString(data, offset, 8).Replace("-", "").ToLowerInvariant());
                    }
                }
                at = checked((int)end);
            }
            if (at != data.Length) throw new InvalidDataException("MTB sections do not cover the file.");
            return result;
        }
    }
}
