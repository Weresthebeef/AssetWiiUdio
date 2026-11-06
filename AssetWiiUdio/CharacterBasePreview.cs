using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Media3D;
namespace AssetWiiUdio;
// Preview-only pedestal from the user's DIBASE.fbx; never changes source meshes or bind poses.
internal static class CharacterBasePreview
{
 internal static Model3DGroup Create(DI3MeshReader.Model character)
 {
  using var stream=typeof(CharacterBasePreview).Assembly.GetManifestResourceStream("AssetWiiUdio.DIBASE.json")??throw new FileNotFoundException("Bundled character base missing.");
  using var doc=JsonDocument.Parse(stream);
  var lo=character.positions.Aggregate(System.Numerics.Vector3.Min);var hi=character.positions.Aggregate(System.Numerics.Vector3.Max);
  float height=Math.Max(hi.Y-lo.Y,.001f);
  var feet=character.positions.Where(v=>v.Y<=lo.Y+height*.08f).ToArray();
  var footLo=feet.Aggregate(System.Numerics.Vector3.Min);var footHi=feet.Aggregate(System.Numerics.Vector3.Max);
  double cx=(footLo.X+footHi.X)*.5,cz=(footLo.Z+footHi.Z)*.5;
  double diameter=Math.Max(height*.55,Math.Sqrt(Math.Pow(footHi.X-footLo.X,2)+Math.Pow(footHi.Z-footLo.Z,2))*1.25);
  var all=doc.RootElement.EnumerateArray().SelectMany(o=>o.GetProperty("vertices").EnumerateArray().Select(v=>new Point3D(v[0].GetDouble(),v[1].GetDouble(),v[2].GetDouble()))).ToArray();
  double baseMinX=all.Min(v=>v.X),baseMaxX=all.Max(v=>v.X),baseMinZ=all.Min(v=>v.Z),baseMaxZ=all.Max(v=>v.Z),top=all.Max(v=>v.Y);
  double scale=diameter/Math.Max(baseMaxX-baseMinX,baseMaxZ-baseMinZ),bx=(baseMinX+baseMaxX)*.5,bz=(baseMinZ+baseMaxZ)*.5;
  var result=new Model3DGroup();
  foreach(var part in doc.RootElement.EnumerateArray())
  {
   var points=new Point3DCollection(part.GetProperty("vertices").EnumerateArray().Select(v=>new Point3D((v[0].GetDouble()-bx)*scale+cx,(v[1].GetDouble()-top)*scale+lo.Y,(v[2].GetDouble()-bz)*scale+cz)));
   var triangles=part.GetProperty("triangles").EnumerateArray().ToArray();var ids=part.GetProperty("materialIndices").EnumerateArray().Select(x=>x.GetInt32()).ToArray();var colors=part.GetProperty("colors").EnumerateArray().ToArray();
   foreach(int id in ids.Distinct())
   {
    var indices=new Int32Collection();for(int i=0;i<triangles.Length;i++)if(ids[i]==id)foreach(var index in triangles[i].EnumerateArray())indices.Add(index.GetInt32());
    byte Channel(double value)=>(byte)Math.Clamp((int)Math.Round(Math.Pow(Math.Clamp(value,0,1),1/2.2)*255),0,255);
    var color=id>=0&&id<colors.Length?Color.FromRgb(Channel(colors[id][0].GetDouble()),Channel(colors[id][1].GetDouble()),Channel(colors[id][2].GetDouble())):Colors.DimGray;
    var material=new DiffuseMaterial(new SolidColorBrush(color));var geometry=new MeshGeometry3D{Positions=points,TriangleIndices=indices};result.Children.Add(new GeometryModel3D(geometry,material){BackMaterial=material});
   }
  }
  result.Freeze();return result;
 }
}
