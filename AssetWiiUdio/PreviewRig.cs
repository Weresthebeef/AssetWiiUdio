using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
namespace AssetWiiUdio;
internal static class PreviewRig
{
    internal sealed record Joint(string Name,Vector3 Position,int Parent);
    internal static List<Joint> Read(string path)
    {
        var tree=DI3MeshReader.Parse(path);
        var influences=tree.All("Influence").Where(n=>n.Get("BindPoseSkinLocalToWorldMatrixInverseData")!=null).ToArray();
        if(influences.Length>0)return SkinRig(tree,influences);
        var nodes=tree.All("SceneTreeNodePool").First().children.Where(n=>n.name=="Node").ToArray();
        var ids=nodes.Select((n,i)=>(n,i)).ToDictionary(x=>x.n.value as string??"",x=>x.i);
        var worlds=new Matrix4x4[nodes.Length];var state=new int[nodes.Length];
        Matrix4x4 World(int i)
        {
            if(state[i]==2)return worlds[i];if(state[i]==1)throw new InvalidDataException("Cyclic scene hierarchy.");state[i]=1;
            var a=nodes[i].Get("LocalToParentMatrix")?.value as float[];var m=Matrix4x4.Identity;
            if(a!=null){if(a.Length!=16)throw new InvalidDataException("Invalid rig matrix.");m=new(a[0],a[1],a[2],a[3],a[4],a[5],a[6],a[7],a[8],a[9],a[10],a[11],a[12],a[13],a[14],a[15]);}
            var parents=nodes[i].Numbers("ParentNodeReferences");
            if(parents?.Length>1)throw new InvalidDataException("Instanced rig node has multiple parents.");
            if(parents?.Length==1){if(!ids.TryGetValue(parents[0].ToString(),out int parent))throw new InvalidDataException("Missing rig parent.");m*=World(parent);}
            worlds[i]=m;state[i]=2;return m;
        }
        var result=new List<Joint>();
        for(int i=0;i<nodes.Length;i++)
        {
            var p=Vector3.Transform(Vector3.Zero,World(i));p.X=-p.X;
            var refs=nodes[i].Numbers("ParentNodeReferences");int parent=refs?.Length==1?ids[refs[0].ToString()]:-1;
            result.Add(new(nodes[i].Text("NodeName")??nodes[i].Text("Type")??i.ToString(),p,parent));
        }
        return result;
    }
    static List<Joint> SkinRig(DI3MeshReader.Node tree,DI3MeshReader.Node[] influences)
    {
        var names=new List<string>();var matrices=new List<Matrix4x4>();var lookup=new Dictionary<string,int>();
        foreach(var influence in influences)
        {
            string name=influence.Text("Name")??throw new InvalidDataException("Missing bone name.");
            var a=influence.Get("BindPoseSkinLocalToWorldMatrixInverseData")?.value as float[];
            if(a==null||a.Length!=16||a.Any(v=>!float.IsFinite(v)))throw new InvalidDataException("Invalid skin bind matrix.");
            var inverse=new Matrix4x4(a[0],a[1],a[2],a[3],a[4],a[5],a[6],a[7],a[8],a[9],a[10],a[11],a[12],a[13],a[14],a[15]);
            if(!Matrix4x4.Invert(inverse,out var world))throw new InvalidDataException("Singular skin bind matrix.");
            if(lookup.TryGetValue(name,out int existing))
            {
                var old=matrices[existing];
                for(int r=0;r<4;r++)for(int c=0;c<4;c++)if(Math.Abs(old[r,c]-world[r,c])>.002f)throw new InvalidDataException("Conflicting bind poses for "+name);
            }
            else {lookup.Add(name,names.Count);names.Add(name);matrices.Add(world);}
        }
        var pool=tree.All("BasisPool").OrderByDescending(p=>p.children.Count(n=>n.Text("Name")!=null&&lookup.ContainsKey(n.Text("Name")))).FirstOrDefault();
        if(pool==null)throw new InvalidDataException("Missing skin Basis hierarchy.");
        var basis=pool.children.Where(n=>n.name=="Basis").ToDictionary(n=>int.Parse(n.value as string??"-1"));
        var parents=Enumerable.Repeat(-1,names.Count).ToArray();
        for(int i=0;i<names.Count;i++)
        {
            var matches=basis.Values.Where(n=>n.Text("Name")==names[i]).ToArray();
            if(matches.Length>1)throw new InvalidDataException("Ambiguous Basis bone "+names[i]);
            if(matches.Length==0)continue; // No guessed helper links.
            int ancestor=matches[0].Number("ParentRef",-1);var seen=new HashSet<int>();
            while(ancestor>=0)
            {
                if(!seen.Add(ancestor)||!basis.TryGetValue(ancestor,out var node))throw new InvalidDataException("Invalid Basis chain.");
                if(node.Text("Name") is string parent&&lookup.TryGetValue(parent,out int index)){parents[i]=index;break;}
                ancestor=node.Number("ParentRef",-1);
            }
        }
        var result=new List<Joint>();
        for(int i=0;i<names.Count;i++)
        {
            var seen=new HashSet<int>();for(int j=i;j>=0;j=parents[j])if(!seen.Add(j))throw new InvalidDataException("Cyclic skin hierarchy.");
            var p=Vector3.Transform(Vector3.Zero,matrices[i]);p.X=-p.X;
            result.Add(new Joint(names[i],p,parents[i]));
        }
        return result;
    }
}
