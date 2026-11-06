using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
namespace AssetWiiUdio;
public static class MaterialBundleReader
{
    public sealed record Section(string Tag,int Offset,int Size,uint Version,int Count);
    public sealed record Material(int Index,string Shader,string Uuid,string Hash,int Offset,int Size,int[] TextureIndices,int ColorSlot=0,string[]? SamplerNames=null);
    public sealed class Bundle
    {
        public bool Big; public byte[] Bytes=Array.Empty<byte>();
        public List<Section> Sections=new();public List<string> Textures=new();public List<Material> Materials=new();
        public string Status="Material shader layout unresolved";
        public string? ColorTexture(int reference)
        {
            var m=Materials.FirstOrDefault(m=>m.Index==reference);
            if(m==null||m.ColorSlot<0||m.ColorSlot>=m.TextureIndices.Length)return null;
            int i=m.TextureIndices[m.ColorSlot];return i>=0&&i<Textures.Count?Textures[i]:null;
        }
    }
    public static bool Recognizes(byte[] b)=>b.Length>=16&&(Encoding.ASCII.GetString(b,0,4)=="BNDL"||Encoding.ASCII.GetString(b,0,4)=="LDNB");
    public static Bundle Read(byte[] bytes)
    {
        if(!Recognizes(bytes))throw new InvalidDataException("Not an MTB material bundle.");
        var b=new Bundle{Bytes=bytes,Big=Encoding.ASCII.GetString(bytes,0,4)=="LDNB"};
        using var r=new BinaryReader(new MemoryStream(bytes));
        void Check(long p,long n){if(p<0||n<0||p+n>bytes.Length)throw new InvalidDataException("MTB field outside bundle.");}
        uint U(int p,int n=4){Check(p,n);uint v=0;for(int i=0;i<n;i++)if(b.Big)v=(v<<8)|bytes[p+i];else v|=(uint)bytes[p+i]<<(8*i);return v;}
        int Align(int p,int n)=>(p+n-1)&~(n-1);
        if(U(8)!=bytes.Length)throw new InvalidDataException("MTB total length mismatch.");
        int at=16;uint sectionCount=U(12);if(sectionCount>4096)throw new InvalidDataException("MTB section count too large.");
        for(int i=0;i<sectionCount;i++)
        {
            Check(at,16);string tag=Encoding.ASCII.GetString(bytes,at,4);if(b.Big)tag=new string(tag.Reverse().ToArray());
            int size=checked((int)U(at+8)),count=checked((int)U(at+12));Check(at,size);if(size<16)throw new InvalidDataException("Invalid MTB section length.");
            b.Sections.Add(new(tag,at,size,U(at+4),count));
            if(tag=="TEXB")
            {
                if(b.Sections.Count(s=>s.Tag=="TEXB")>1)throw new InvalidDataException("Duplicate TEXB table.");
                b.Textures.AddRange(DIResearch.Formats.MtbTextureTable.Read(bytes));
            }
            at=checked(at+size);
        }
        if(at!=bytes.Length)throw new InvalidDataException("MTB sections do not cover bundle.");
        var mat=b.Sections.FirstOrDefault(s=>s.Tag=="MATP");if(mat==null)return b;
        int q=mat.Offset+16,end=mat.Offset+mat.Size;
        string[] Strings()
        {
            int count=checked((int)U(q));q+=4;if(count>4096)throw new InvalidDataException("Invalid MTB string count.");var a=new string[count];
            for(int i=0;i<count;i++){int stop=Array.IndexOf(bytes,(byte)0,q,end-q);if(stop<0)throw new InvalidDataException("Unterminated MTB string.");a[i]=Encoding.ASCII.GetString(bytes,q,stop-q);q=stop+1;}return a;
        }
        var shaders=Strings();q=Align(q,4);var uuids=Strings();q=Align(q,2);
        int[] shaderIds=new int[mat.Count];for(int i=0;i<mat.Count;i++){shaderIds[i]=(int)U(q,2);q+=2;}
        q=Align(q,4);var hashes=new string[mat.Count];for(int i=0;i<mat.Count;i++){Check(q,4);hashes[i]=Convert.ToHexString(bytes.AsSpan(q,4));q+=4;}q=Align(q,16);
        var parsed=new List<Material>();
        for(int i=0;i<mat.Count;i++)
        {
            if(shaderIds[i]>=shaders.Length)throw new InvalidDataException("Invalid material shader index.");string shader=shaders[shaderIds[i]];
            (int Size,int Offset,int Slots) layout;
            WiiUShaderProfiles.Profile? native=null;
            if(b.Big)
            {
                if(!WiiUShaderProfiles.Entries.TryGetValue(shader,out native)){b.Status="Unresolved Wii U shader layout: "+shader+"; no automatic assignment";return b;}
                layout=(native.Size,0,native.Offsets.Length);
            }
            else if(!PcShaderProfiles.Entries.TryGetValue(shader,out layout)){b.Status="Unresolved PC shader layout: "+shader+"; no automatic assignment";return b;}
            int size=layout.Size,width=b.Big?4:8;Check(q,size);if(q+size>end)throw new InvalidDataException("Material exceeds MATP section.");
            if(layout.Offset<0||layout.Slots<0||layout.Offset+(long)layout.Slots*width>size)throw new InvalidDataException("Invalid shader sampler layout.");
            var indices=new int[layout.Slots];for(int j=0;j<indices.Length;j++)
            {
                int p=q+(native!=null?native.Offsets[j]:layout.Offset+j*width);
                if(width==8){uint high=U(p+4);if(high!=0&&high!=uint.MaxValue)throw new InvalidDataException("Invalid PC sampler index.");}
                indices[j]=unchecked((int)U(p));if(indices[j]<-1||indices[j]>=b.Textures.Count)throw new InvalidDataException("Sampler outside MTB texture table.");
            }
            parsed.Add(new(i,shader,i<uuids.Length?uuids[i]:"",hashes[i],q,size,indices,native?.ColorSlot??0,native?.Samplers));q+=size;
        }
        if(q!=end){b.Status="Shader record sizes do not cover MATP; no automatic assignment";return b;}
        b.Materials=parsed;b.Status="Sampler references decoded; Wii U color chosen by native sampler name, PC preview uses reviewed slot 0";return b;
    }
}
