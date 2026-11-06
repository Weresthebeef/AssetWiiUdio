using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace AssetWiiUdio;

// Parser modeled on the supplied Disney Infinity 3.0 QuickBMS script.
// The PK index is parsed from outer-decrypted bytes, but embedded ZIP entries
// MUST be read from the ORIGINAL raw ciphertext. Each entry restarts AES-CTR
// at its local header. Double-decrypting outer-transformed entry bytes breaks PK0304.
public static class DisneyInfinityArchive
{
    public sealed record Member(string Name, long Offset, long PackedSize, long Size, int Method, uint Crc, bool Encrypted, string Note);
    public sealed record Archive(string Format, string Encryption, byte[] Source, byte[] Key, IReadOnlyList<Member> Members, string Note);
    private static readonly byte[] NormalKey = Convert.FromHexString("681BBEEA63160188F9B7945104A51499");
    private static readonly byte[] PsxKey = Convert.FromHexString("3E9FB2A6BE59CCCBD904BEEFFFE118FB");
    private static ushort U16(byte[] a, long i) { Check(a,i,2); return BitConverter.ToUInt16(a,(int)i); }
    private static uint U32(byte[] a, long i) { Check(a,i,4); return BitConverter.ToUInt32(a,(int)i); }
    private static void Check(byte[] a,long pos,long len) { if(pos<0 || len<0 || pos>a.Length || len>a.Length-pos) throw new InvalidDataException("Archive entry extends beyond the input."); }
    private static byte[] Slice(byte[] a,long pos,int count) { Check(a,pos,count); return a.AsSpan((int)pos,count).ToArray(); }
    private static byte[] Decode(byte[] data,byte[] key) => DisneyInfinityAsset.TransformCtr(data,key);

    public static Archive Open(string filename,byte[] raw)
    {
        var result=DisneyInfinityAsset.Analyze(filename,raw);
        if (!result.Confirmed || (result.Format!="Disney Infinity PK-FFFF container" && result.Format!="Disney Infinity DBNK sound bank"))
            throw new InvalidDataException("No supported Disney Infinity archive signature found after outer analysis.");
        // Analyze tries both keys, including for hashed Wii U filenames.
        // Use the exact key that produced the confirmed container signature.
        var key=result.Encryption.Contains("recovered PSX",StringComparison.OrdinalIgnoreCase)?PsxKey:NormalKey;
        var b=result.Bytes; // Decrypted index (and fully decrypted DBNK source).
        var members=new List<Member>();
        if(result.Format=="Disney Infinity DBNK sound bank")
        {
            Check(b,0x14,12);
            int entries=U16(b,0x14), count=U16(b,0x16);
            long table=0x20L+entries*4L;
            if(count>50000) throw new InvalidDataException("Unreasonable DBNK file count.");
            Check(b,table,count*12L);
            for(int i=0;i<count;i++)
            {
                long pos=table+i*12L;
                uint nameOffset=U32(b,pos), off=U32(b,pos+4), length=U32(b,pos+8);
                Check(b,off,length); Check(b,nameOffset,1);
                int end=Array.IndexOf(b,(byte)0,(int)nameOffset);
                if(end<0 || end-nameOffset>2048) throw new InvalidDataException("Invalid DBNK member name.");
                string name=Encoding.UTF8.GetString(b,(int)nameOffset,end-(int)nameOffset);
                members.Add(new Member(name,off,length,length,0,0,false,"DBNK raw member"));
            }
            return new Archive("DBNK",result.Encryption,b,key,members,"DBNK member offsets are relative to the decrypted archive.");
        }
        uint countPk=U32(b,4);
        if(countPk>100000) throw new InvalidDataException("Unreasonable PK entry count.");
        Check(b,8,countPk*8L); // The index is read from decrypted outer bytes.
        for(int i=0;i<countPk;i++)
        {
            long index=8+i*8L;
            uint nameCrc=U32(b,index), offset=U32(b,index+4);
            // QuickBMS starts a fresh CTR stream at each local header, then
            // consumes header, filename, extra fields and payload prefix in order.
            // These fields must NOT each restart the counter.
            var hdr=Decode(Slice(raw,offset,30),key);
            if(U32(hdr,0)!=0x04034b50) throw new InvalidDataException($"Invalid encrypted ZIP header at 0x{offset:X} (entry {i}); check the source archive and CTR key.");
            int method=U16(hdr,8);
            uint crc=U32(hdr,14),packed=U32(hdr,18),size=U32(hdr,22);
            int nameLen=U16(hdr,26),extraLen=U16(hdr,28);
            if(nameLen>4096 || extraLen>65535) throw new InvalidDataException("Unreasonable ZIP header lengths.");
            long payload=(long)offset+30+nameLen+extraLen;
            Check(raw,payload,packed);
            // Decrypt from the local header so the filename uses the correct
            // CTR position (including the 30-byte header already consumed).
            byte[] decryptedHeaderAndName = Decode(Slice(raw,offset,checked(30+nameLen)),key);
            string name=Encoding.UTF8.GetString(decryptedHeaderAndName,30,nameLen);
            if(string.IsNullOrWhiteSpace(name)) name=$"entry_{i:D5}_{nameCrc:x8}";
            members.Add(new Member(name,payload,packed,size,method,crc,true,$"index CRC {nameCrc:x8}; local header 0x{offset:X}"));
        }
        return new Archive("PK-FFFF",result.Encryption,raw,key,members,"PK member headers and up to the first 0x200 payload bytes use separately restarted AES-CTR; .dct encrypts the whole payload.");
    }

    public static byte[] Extract(Archive archive,Member member)
    {
        if(member.PackedSize>128*1024*1024 || member.Size>256*1024*1024) throw new NotSupportedException("Member exceeds extraction safety limit.");
        var payload=Slice(archive.Source,member.Offset,checked((int)member.PackedSize));
        if(member.Encrypted)
        {
            int decryptCount=Path.GetExtension(member.Name).Equals(".dct",StringComparison.OrdinalIgnoreCase)?payload.Length:Math.Min(0x200,payload.Length);
            // CTR stream begins at the local ZIP header, not at the payload.
            // The encrypted filename and extra fields consume counter bytes too.
            long localHeader = member.Offset - 30;
            // Member.Offset is payload start. Find the local header from the
            // index note, which is recorded when parsing the archive.
            const string marker = "local header 0x";
            int markerAt = member.Note.IndexOf(marker, StringComparison.Ordinal);
            if (markerAt < 0) throw new InvalidDataException("Missing local-header location.");
            string hex = member.Note[(markerAt + marker.Length)..].Split(' ', ';')[0];
            localHeader = Convert.ToInt64(hex,16);
            int headerLength = checked((int)(member.Offset - localHeader));
            byte[] encryptedPrefix = Slice(archive.Source,localHeader,checked(headerLength+decryptCount));
            byte[] decryptedPrefix = Decode(encryptedPrefix,archive.Key);
            Buffer.BlockCopy(decryptedPrefix,headerLength,payload,0,decryptCount);
        }
        byte[] unpacked;
        if(member.Method==0) unpacked=payload;
        else if(member.Method==8)
        {
            using var input=new MemoryStream(payload);
            using var z=new DeflateStream(input,CompressionMode.Decompress);
            using var output=new MemoryStream();
            z.CopyTo(output);
            unpacked=output.ToArray();
        }
        else throw new NotSupportedException($"Compression method {member.Method} is not implemented. Supported: stored (0) and Deflate (8).");
        if(unpacked.LongLength!=member.Size) throw new InvalidDataException("Extracted member length does not match its header.");
        // Validate the ZIP member CRC rather than accepting merely plausible output.
        if (member.Encrypted && Crc32(unpacked) != member.Crc)
            throw new InvalidDataException($"ZIP CRC32 mismatch for {member.Name}.");
        return unpacked;
    }
    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte value in data)
        {
            crc ^= value;
            for (int i = 0; i < 8; i++)
                crc = (crc >> 1) ^ ((crc & 1u) != 0 ? 0xEDB88320u : 0u);
        }
        return ~crc;
    }

    public static string SafeRelativePath(string name)
    {
        string n=name.Replace('\\','/');
        if(n.StartsWith('/') || n.Contains(':')) throw new InvalidDataException("Absolute member path rejected.");
        var parts=n.Split('/',StringSplitOptions.RemoveEmptyEntries);
        if(parts.Length==0 || parts.Any(x=>x=="." || x=="..")) throw new InvalidDataException("Unsafe archive member path.");
        return Path.Combine(parts);
    }
}
