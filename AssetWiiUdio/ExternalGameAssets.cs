using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Text.RegularExpressions;
namespace AssetWiiUdio;
internal static class ExternalGameAssets
{
    internal static Func<string,(byte[] Bytes,string Source)?>? VirtualRead;
    internal static Func<string,(byte[] Bytes,string Source)?>? VirtualTextureRead;
    static readonly string Settings=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AssetWiiUdio","preview-assets-root.txt");
    internal static string Root {get {try{return File.ReadAllText(Settings).Trim();}catch{return "";}}set{Directory.CreateDirectory(Path.GetDirectoryName(Settings)!);File.WriteAllText(Settings,value);}}
    internal static string Assets {get {string root=Root;return Directory.Exists(Path.Combine(root,"assets"))?Path.Combine(root,"assets"):root;}}
    static readonly Dictionary<string,DisneyInfinityArchive.Archive> Cache=new(StringComparer.OrdinalIgnoreCase);
    static uint Hash(string text)
    {
        byte[] b=Encoding.UTF8.GetBytes(text.ToLowerInvariant());uint h=1000;int p=0;
        uint R(uint v,int n)=>(v<<n)|(v>>(32-n));
        while(p+4<=b.Length){uint k=System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p,4));p+=4;k*=0xcc9e2d51;k=R(k,15);k*=0x1b873593;h^=k;h=R(h,13);h=h*5+0xe6546b64;}
        uint tail=0;for(int i=0;p+i<b.Length;i++)tail|=(uint)b[p+i]<<(8*i);if(p<b.Length){tail*=0xcc9e2d51;tail=R(tail,15);tail*=0x1b873593;h^=tail;}h^=(uint)b.Length;h^=h>>16;h*=0x85ebca6b;h^=h>>13;h*=0xc2b2ae35;h^=h>>16;return h;
    }
    internal static string HashRoute(string logical)
    {
        string prefix="";return string.Join(Path.DirectorySeparatorChar,logical.Replace('\\','/').Split('/').Select(part=>{prefix+=(prefix.Length>0?"/":"")+part;return Hash(prefix).ToString("x8");}));
    }
    static IEnumerable<string> Paths(string logical)
    {
        string safe=DisneyInfinityArchive.SafeRelativePath(logical);if(string.IsNullOrEmpty(Assets))yield break;
        yield return Path.Combine(Assets,safe);
        if(Path.GetFileName(Assets.TrimEnd(Path.DirectorySeparatorChar)).Equals("textures",StringComparison.OrdinalIgnoreCase)&&logical.StartsWith("textures/",StringComparison.OrdinalIgnoreCase))yield return Path.Combine(Assets,Path.GetFileName(logical));yield return Path.Combine(Assets,"gamedb",safe);yield return Path.Combine(Assets,HashRoute(logical));yield return Path.Combine(Assets,HashRoute("gamedb/"+logical));
    }
    internal static bool TryRead(string logical,out byte[] bytes,out string source)
    {
        bytes=Array.Empty<byte>();source="";
        if(logical.StartsWith("textures/",StringComparison.OrdinalIgnoreCase))
        {
            string id=Path.GetFileNameWithoutExtension(logical);
            foreach(string folder in new[]{Path.Combine(Assets,"textures"),Assets,Path.Combine(Assets,"Textures")}.Distinct(StringComparer.OrdinalIgnoreCase))
            foreach(string extension in logical.EndsWith(".tstream",StringComparison.OrdinalIgnoreCase)?new[]{".tstream"}:new[]{".tbody",".dds",".png"})
            {
                string candidate=Path.Combine(folder,id+extension);
                if(File.Exists(candidate)){if(new FileInfo(candidate).Length>128*1024*1024)throw new InvalidDataException("Texture exceeds preview limit.");bytes=File.ReadAllBytes(candidate);source=candidate;return true;}
            }
        }
        var directVirtual=VirtualRead?.Invoke(logical);if(directVirtual.HasValue){bytes=directVirtual.Value.Bytes;source=directVirtual.Value.Source;return true;}
        foreach(string path in Paths(logical))if(File.Exists(path)){if(new FileInfo(path).Length>128*1024*1024)throw new InvalidDataException("Asset exceeds preview limit.");bytes=File.ReadAllBytes(path);source=path;return true;}
        var streamed=VirtualTextureRead?.Invoke(logical);if(streamed.HasValue){bytes=streamed.Value.Bytes;source=streamed.Value.Source;return true;}
        var parts=logical.Replace('\\','/').Split('/');var archives=new List<string>();
        if(parts.Length>=3){string directory=string.Join('/',parts[..^1]);archives.Add(directory+"/"+Path.GetFileNameWithoutExtension(parts[^1])+".zip");archives.Add(directory+"/"+parts[^2]+".zip");archives.Add("gamedb/"+parts[^2]+".zip");}
        if(logical.StartsWith("textures/",StringComparison.OrdinalIgnoreCase)){string id=Path.GetFileNameWithoutExtension(logical);if(id.Length>=2)archives.Insert(0,"textures/"+id[..2]+".zip");}
        foreach(string archiveName in archives.Distinct())
        {
            var virtualArchive=VirtualRead?.Invoke(archiveName);
            if(!virtualArchive.HasValue)continue;
            string key=virtualArchive.Value.Source;
            if(!Cache.TryGetValue(key,out var archive)){if(Cache.Count>=4)Cache.Clear();archive=DisneyInfinityArchive.Open(archiveName,virtualArchive.Value.Bytes);Cache[key]=archive;}
            var members=archive.Members.Where(m=>m.Name.Replace('\\','/').Equals(logical,StringComparison.OrdinalIgnoreCase)).ToArray();
            if(members.Length==0)members=archive.Members.Where(m=>Path.GetFileName(m.Name).Equals(Path.GetFileName(logical),StringComparison.OrdinalIgnoreCase)).ToArray();
            if(members.Length!=1)continue;bytes=DisneyInfinityArchive.Extract(archive,members[0]);source=key+" :: "+members[0].Name;return true;
        }
        foreach(string archiveName in archives.Distinct())foreach(string path in Paths(archiveName))if(File.Exists(path))
        {
            var key=path+"|"+new FileInfo(path).LastWriteTimeUtc.Ticks;
            if(!Cache.TryGetValue(key,out var archive)){if(new FileInfo(path).Length>128*1024*1024)continue;if(Cache.Count>=4)Cache.Clear();archive=DisneyInfinityArchive.Open(archiveName,File.ReadAllBytes(path));Cache[key]=archive;}
            var matches=archive.Members.Where(m=>m.Name.Replace('\\','/').Equals(logical,StringComparison.OrdinalIgnoreCase)).ToArray();
            if(matches.Length==0)matches=archive.Members.Where(m=>Path.GetFileName(m.Name).Equals(Path.GetFileName(logical),StringComparison.OrdinalIgnoreCase)).ToArray();
            if(matches.Length!=1)continue;bytes=DisneyInfinityArchive.Extract(archive,matches[0]);source=path+" :: "+matches[0].Name;return true;
        }
        return false;
    }
    internal static string RoutingReport(string dnax,byte[] data)
    {
        var report=new StringBuilder("Animation routing references (static inspection; not animation decoding)\nSource: "+dnax+"\n\n");
        var pending=new Queue<(string Name,byte[] Data,int Depth)>();pending.Enqueue((dnax,data,0));var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);int visited=0;
        // Paths recovered from readable strings in source/compiled data are candidates;
        // this does not evaluate Lua branches or override precedence.
        while(pending.Count>0&&visited++<64)
        {
            var item=pending.Dequeue();report.AppendLine("FROM "+item.Name);
            var refs=Regex.Matches(Encoding.Latin1.GetString(item.Data),@"(?:characters|animations)/[A-Za-z0-9_./\\-]+\.(?:animtreeoverrides|sx|oct|banm|bent)",RegexOptions.IgnoreCase).Select(m=>m.Value.Replace('\\','/')).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if(refs.Length==0)report.AppendLine("  No readable routing references found; not proof of no dependencies.");
            foreach(var reference in refs)
            {
                try{if(TryRead(reference,out var content,out var origin)){report.AppendLine("  FOUND "+reference+"\n    "+origin);if(item.Depth<3&&seen.Add(reference)&&!reference.EndsWith(".oct",StringComparison.OrdinalIgnoreCase)&&!reference.EndsWith(".banm",StringComparison.OrdinalIgnoreCase))pending.Enqueue((reference,content,item.Depth+1));}else report.AppendLine("  UNRESOLVED "+reference);}
                catch(Exception ex){report.AppendLine("  ERROR "+reference+": "+ex.Message);}
            }
        }
        report.AppendLine("\nCandidate string links only. No inferred ownership, skeleton retargeting, Lua execution or native clip decoding.");return report.ToString();
    }
}
