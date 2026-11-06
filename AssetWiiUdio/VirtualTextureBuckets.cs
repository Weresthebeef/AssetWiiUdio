using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text;
namespace AssetWiiUdio;
// Bounded, read-only fallback for hashed Wii U texture buckets. Index only a
// validated first member; actual requested members still require full archive/CRC validation.
internal sealed class VirtualTextureBuckets
{
    readonly VirtualContentReader reader;readonly VirtualEntry[] entries;
    readonly Dictionary<string,List<VirtualEntry>> buckets=new(StringComparer.OrdinalIgnoreCase);
    bool indexed;
    public VirtualTextureBuckets(VirtualContentReader reader,IEnumerable<VirtualEntry> entries){this.reader=reader;this.entries=entries.ToArray();}
    public VirtualEntry? Resolve(string logical)
    {
        if(!logical.StartsWith("textures/",StringComparison.OrdinalIgnoreCase)||!logical.EndsWith(".zip",StringComparison.OrdinalIgnoreCase))return null;
        string prefix=Path.GetFileNameWithoutExtension(logical);if(prefix.Length!=2||!prefix.All(Uri.IsHexDigit))return null;
        if(!indexed)
        {
            string folder=ExternalGameAssets.HashRoute("textures").Replace('\\','/');
            var candidates=entries.Where(e=>!e.IsDirectory&&e.Length>=64&&e.Length<=128*1024*1024&&e.Path.Replace('\\','/').Contains("/assets/"+folder+"/",StringComparison.OrdinalIgnoreCase)).Take(4097).ToArray();
            if(candidates.Length>4096)throw new InvalidDataException("Texture bucket index exceeds safety limit.");
            foreach(var entry in candidates)
            {
                try
                {
                    var result=DisneyInfinityAsset.Analyze(entry.Path,reader.Read(entry,16));
                    if(!result.Confirmed||result.Format!="Disney Infinity PK-FFFF container"||BitConverter.ToUInt32(result.Bytes,4)==0)continue;
                    uint offset=BitConverter.ToUInt32(result.Bytes,12);if((long)offset+30>entry.Length)continue;
                    int n=(int)Math.Min(512,entry.Length-offset);
                    var raw=reader.Read(entry with{Offset=entry.Offset+offset,Length=n},n);
                    var key=Convert.FromHexString(result.Encryption.Contains("recovered PSX",StringComparison.OrdinalIgnoreCase)?"3E9FB2A6BE59CCCBD904BEEFFFE118FB":"681BBEEA63160188F9B7945104A51499");
                    var data=DisneyInfinityAsset.TransformCtr(raw,key);
                    if(BitConverter.ToUInt32(data)!=0x04034b50)continue;
                    int length=BitConverter.ToUInt16(data,26);if(length<1||length>data.Length-30)continue;
                    string name=Encoding.UTF8.GetString(data,30,length).Replace('\\','/');
                    string id=Path.GetFileNameWithoutExtension(name);
                    if(!name.StartsWith("textures/",StringComparison.OrdinalIgnoreCase)||id.Length!=16||!id.All(Uri.IsHexDigit)||!new[]{".tbody",".tstream"}.Contains(Path.GetExtension(name).ToLowerInvariant()))continue;
                    if(!buckets.TryGetValue(id[..2],out var list))buckets[id[..2]]=list=new();list.Add(entry);
                }
                catch(InvalidDataException){} // Unrecognized bucket is never guessed.
            }
            indexed=true;
        }
        // Prefer a stream bucket; callers may need to search multiple candidates.
        return Candidates(prefix).FirstOrDefault();
    }
    public IEnumerable<VirtualEntry> Candidates(string prefix)=>buckets.TryGetValue(prefix,out var list)?list:Array.Empty<VirtualEntry>();
}
