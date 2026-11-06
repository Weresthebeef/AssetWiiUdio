using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Numerics;
namespace AssetWiiUdio;
internal sealed class NativeVertexData
{
    internal Vector3[] Normals=Array.Empty<Vector3>();
    internal Vector4[] Tangents=Array.Empty<Vector4>();
    internal int[][] Indices=Array.Empty<int[]>();
    internal float[][] Weights=Array.Empty<float[]>();
    internal Matrix4x4[] BindWorld=Array.Empty<Matrix4x4>();
    internal string Status="";
    internal static NativeVertexData Read(DI3MeshReader.Model model,List<PreviewRig.Joint> joints)
    {
        var result=new NativeVertexData();var tree=DI3MeshReader.Parse(model.path);
        var vb=tree.All("VertexBufferPool").First();var streams=tree.All("VertexStreamPool").FirstOrDefault();
        var normals=new List<Vector3>();var tangents=new List<Vector4>();var indices=new List<int[]>();var weights=new List<float[]>();
        var lookup=joints.Select((j,i)=>(j,i)).ToDictionary(x=>x.j.Name,x=>x.i);
        result.BindWorld=Enumerable.Repeat(Matrix4x4.Identity,joints.Count).ToArray();
        bool big;using(var f=File.OpenRead(model.path))big=f.ReadByte()==0x45;
        int normalCount=0,skinCount=0;var notes=new HashSet<string>();
        foreach(var geometry in DI3MeshReader.Geometries(tree))
        {
            var influences=geometry.Get("Influences")?.children.Where(n=>n.name=="Influence").ToArray()??Array.Empty<DI3MeshReader.Node>();
            var palette=new int[influences.Length];
            for(int i=0;i<palette.Length;i++)
            {
                string name=influences[i].Text("Name");if(!lookup.TryGetValue(name,out palette[i]))throw new InvalidDataException("Unknown skin bone: "+name);
                var a=influences[i].Get("BindPoseSkinLocalToWorldMatrixInverseData")?.value as float[];
                if(a==null||a.Length!=16)throw new InvalidDataException("Missing inverse bind matrix.");
                var inv=new Matrix4x4(a[0],a[1],a[2],a[3],a[4],a[5],a[6],a[7],a[8],a[9],a[10],a[11],a[12],a[13],a[14],a[15]);
                if(!Matrix4x4.Invert(inv,out var world))throw new InvalidDataException("Singular inverse bind.");var reflect=Matrix4x4.CreateScale(-1,1,1);result.BindWorld[palette[i]]=reflect*world*reflect;
            }
            var remap=geometry.Numbers("InfluenceRemapInfluenceRefs");var ranges=geometry.Numbers("InfluenceRemapPrimitiveData");int primitiveIndex=0;
            var placement=DI3MeshReader.ScenePlacement(tree,geometry);
            if(!Matrix4x4.Invert(placement,out var inversePlacement))throw new InvalidDataException("Singular placement.");var normalMatrix=Matrix4x4.Transpose(inversePlacement);
            foreach(var primitive in geometry.Get("Primitives").children.Where(n=>n.name=="Primitive"))
            {
                var v=primitive.Numbers("Vdata");int count,buffer=-1,offset=0,stride=0,normalOffset=0,tangentOffset=0;bool packed=false,skin=false;
                if(v!=null)
                {
                    count=v[1];if(v.Length>=8&&v[7]==24&&!big){buffer=v[5];offset=v[6];stride=24;normalOffset=8;tangentOffset=16;skin=influences.Length>0;}
                }
                else
                {
                    var refs=primitive.Numbers("VertexStreamReferences")??throw new InvalidDataException("Missing vertex references.");
                    var candidates=streams!.children.Where(n=>refs.Contains(int.Parse((string)n.value))).ToArray();
                    count=candidates.First(n=>n.Get("Elements")?.children.Any(e=>e.Text("Name")=="position")==true).Number("Length");
                    var source=candidates.FirstOrDefault(n=>n.Get("Elements")?.children.Any(e=>e.Text("Name")=="normal")==true);
                    if(source!=null)
                    {
                        var normal=source.Get("Elements").children.First(e=>e.Text("Name")=="normal");var tangent=source.Get("Elements").children.FirstOrDefault(e=>e.Text("Name")=="tangent0_signed");
                        int width=source.Number("Width");
                        var blendIndices=source.Get("Elements").children.FirstOrDefault(e=>e.Text("Name")=="blendIndices");
                        var blendWeights=source.Get("Elements").children.FirstOrDefault(e=>e.Text("Name")=="blendWeights");
                        bool skinLayout=width==16&&blendIndices?.Number("Type")==24&&blendIndices.Number("Offset")==0&&blendWeights?.Number("Type")==25&&blendWeights.Number("Offset")==4&&normal.Number("Offset")==8&&tangent?.Number("Offset")==12;
                        if(normal.Number("Type")==35&&tangent?.Number("Type")==35&&(width==8||skinLayout))
                        {buffer=source.Number("VertexBufferReference");offset=source.Number("VertexBufferOffset");stride=width;normalOffset=normal.Number("Offset");tangentOffset=tangent.Number("Offset");packed=true;skin=skinLayout&&influences.Length>0;}
                    }
                }
                byte[] data=Array.Empty<byte>();if(buffer>=0)
                {
                    string name=vb.children.Single(n=>n.value as string==buffer.ToString()).Text("FileName");if(Path.GetFileName(name)!=name)throw new InvalidDataException("Invalid stream filename.");
                    data=File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(model.path)!,name));if(offset<0||offset+(long)count*stride>data.Length)throw new InvalidDataException("Attribute stream out of bounds.");
                }
                int start=0,length=palette.Length;
                if(skin&&remap!=null){if(ranges==null||ranges.Length<primitiveIndex*2+2)throw new InvalidDataException("Missing primitive palette.");start=ranges[primitiveIndex*2];length=ranges[primitiveIndex*2+1];if(start<0||length<1||start+(long)length>remap.Length)throw new InvalidDataException("Invalid palette.");}
                for(int i=0;i<count;i++)
                {
                    Vector3 n=Vector3.Zero;Vector4 t=Vector4.Zero;int p=offset+i*stride;
                    if(buffer>=0)
                    {
                        if(packed){n=Packed(data,p+normalOffset,big).AsVector3();t=Packed(data,p+tangentOffset,big);}
                        else {n=new(Half(data,p+8),Half(data,p+10),Half(data,p+12));t=new(Half(data,p+16),Half(data,p+18),Half(data,p+20),Half(data,p+22));}
                        if(!float.IsFinite(n.LengthSquared())||n.LengthSquared()<.5f||n.LengthSquared()>1.5f)throw new InvalidDataException("Unsupported/corrupt source normal.");
                        n=Vector3.Normalize(Vector3.TransformNormal(n,normalMatrix));n.X=-n.X;
                        var tv=Vector3.TransformNormal(new Vector3(t.X,t.Y,t.Z),placement);if(tv.LengthSquared()>1e-8f)tv=Vector3.Normalize(tv);tv.X=-tv.X;t=new(tv,-t.W);normalCount++;
                    }
                    normals.Add(n);tangents.Add(t);var ids=new int[4];var ws=new float[4];
                    if(skin)
                    {
                        int sum=0;for(int k=0;k<4;k++)sum+=data[p+4+k];if(sum==0)throw new InvalidDataException("Unweighted source vertex.");
                        for(int k=0;k<4;k++)if(data[p+4+k]>0){int id=data[p+k];if(id>=length)throw new InvalidDataException("Skin palette index out of bounds.");if(remap!=null)id=remap[start+id];if(id<0||id>=palette.Length)throw new InvalidDataException("Invalid remapped influence.");ids[k]=palette[id];ws[k]=data[p+4+k]/(float)sum;}skinCount++;
                    }
                    indices.Add(ids);weights.Add(ws);
                }
                primitiveIndex++;
            }
        }
        if(normals.Count!=model.positions.Length)throw new InvalidDataException("Attribute/mesh vertex count mismatch.");
        result.Normals=normalCount==normals.Count?normals.ToArray():Array.Empty<Vector3>();result.Tangents=tangents.ToArray();result.Indices=indices.ToArray();result.Weights=weights.ToArray();
        result.Status=$"Source normals: {normalCount}/{normals.Count}; weighted vertices: {skinCount}/{normals.Count}. Tangents retained; WPF diffuse shading does not consume tangent maps.";return result;
    }
    static float Half(byte[] b,int p)=>(float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(b,p));
    static Vector4 Packed(byte[] b,int p,bool big)
    {
        uint w=big?System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p,4)):BitConverter.ToUInt32(b,p);
        float S(int shift){int v=(int)((w>>shift)&1023);return Math.Max(-1,(v>=512?v-1024:v)/511f);}int sign=(int)(w>>30);return new(S(0),S(10),S(20),sign>=2?-1:1);
    }
    internal (Vector3[] Positions,Vector3[] Normals) Deform(DI3MeshReader.Model model,Matrix4x4[] posed)
    {
        if(posed.Length!=BindWorld.Length)throw new InvalidDataException("Pose bone count mismatch.");var matrices=new Matrix4x4[posed.Length];
        for(int i=0;i<posed.Length;i++){if(!Matrix4x4.Invert(BindWorld[i],out var inverse))throw new InvalidDataException("Singular bind.");matrices[i]=inverse*posed[i];}
        var points=(Vector3[])model.positions.Clone();var normals=(Vector3[])Normals.Clone();
        for(int i=0;i<points.Length;i++)
        {
            if(Weights[i].Sum()<=0)continue;Vector3 p=Vector3.Zero,n=Vector3.Zero;
            for(int k=0;k<4;k++){float weight=Weights[i][k];if(weight<=0)continue;var m=matrices[Indices[i][k]];p+=Vector3.Transform(model.positions[i],m)*weight;if(normals.Length>0){Matrix4x4.Invert(m,out var inv);n+=Vector3.TransformNormal(Normals[i],Matrix4x4.Transpose(inv))*weight;}}
            points[i]=p;if(normals.Length>0)normals[i]=n.LengthSquared()>1e-12f?Vector3.Normalize(n):Normals[i];
        }
        return(points,normals);
    }
}
internal static class PreviewVectorExtensions {internal static Vector3 AsVector3(this Vector4 v)=>new(v.X,v.Y,v.Z);}
