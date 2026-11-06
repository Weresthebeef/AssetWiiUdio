// Minimal matrix adapter preserving the original Unity decoder's column-vector operations.
// Storage is System.Numerics row-vector form; multiplication order is deliberately reversed.
namespace AssetWiiUdio.EyeMath;
internal struct Vector3
{
 internal float x,y,z;internal Vector3(float x,float y,float z){this.x=x;this.y=y;this.z=z;}
 internal static Vector3 zero=>new(0,0,0);internal static Vector3 up=>new(0,1,0);internal static Vector3 right=>new(1,0,0);internal static Vector3 forward=>new(0,0,1);
}
internal struct Vector4{internal float x,y,z,w;internal Vector4(float x,float y,float z,float w){this.x=x;this.y=y;this.z=z;this.w=w;}}
internal struct Quaternion
{
 internal System.Numerics.Quaternion Value;internal Quaternion(float x,float y,float z,float w){Value=new(x,y,z,w);}
 internal static Quaternion AngleAxis(float degrees,Vector3 axis)=>new(){Value=System.Numerics.Quaternion.CreateFromAxisAngle(new(axis.x,axis.y,axis.z),degrees*System.MathF.PI/180)};
}
internal struct Matrix4x4
{
 internal System.Numerics.Matrix4x4 Row;
 internal static Matrix4x4 identity=>new(){Row=System.Numerics.Matrix4x4.Identity};
 internal static Matrix4x4 Scale(Vector3 v)=>new(){Row=System.Numerics.Matrix4x4.CreateScale(v.x,v.y,v.z)};
 internal static Matrix4x4 Rotate(Quaternion q)=>new(){Row=System.Numerics.Matrix4x4.CreateFromQuaternion(q.Value)};
 internal Matrix4x4 transpose=>new(){Row=System.Numerics.Matrix4x4.Transpose(Row)};
 internal float this[int index]=>Row[index/4,index%4];
 internal void SetColumn(int column,Vector4 v){Row[column,0]=v.x;Row[column,1]=v.y;Row[column,2]=v.z;Row[column,3]=v.w;}
 public static Matrix4x4 operator *(Matrix4x4 a,Matrix4x4 b)=>new(){Row=b.Row*a.Row};
}
internal static class Mathf
{
 internal const float Rad2Deg=180/System.MathF.PI;
 internal static float Abs(float a)=>System.MathF.Abs(a);internal static float Max(float a,float b)=>System.MathF.Max(a,b);internal static float Clamp01(float a)=>System.Math.Clamp(a,0,1);
}
