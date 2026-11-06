using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetWiiUdio.EyeMath;
using N=AssetWiiUdio.DI3MeshReader.Node;
namespace AssetWiiUdio {
// Conservative decoder for the relationship layouts measured in the source OCTs.
// Unknown output opcodes are rejected before any scene transform is changed.
internal static class DI3SourceEyeDecoder {
public sealed class Pose { public string bone; public Matrix4x4 delta; }
sealed class Bytes {
 public byte[] b;public readonly bool big;public Bytes(byte[] data,bool big){b=data;this.big=big;}
 void Check(int p,int n){if(p<0||n<0||p>b.Length-n)throw new InvalidDataException("Truncated eyelid relationship.");}
 public int U16(int p){Check(p,2);return big?System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p,2)):BitConverter.ToUInt16(b,p);}
 public int I16(int p){Check(p,2);return unchecked((short)U16(p));}
 public int I32(int p){Check(p,4);return big?System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(p,4)):BitConverter.ToInt32(b,p);}
 public float CommandF(int p){int bits=I32(p);if(big)bits=unchecked((int)(((uint)bits<<16)|((uint)bits>>16)));float f=BitConverter.Int32BitsToSingle(bits);if(!float.IsFinite(f))throw new InvalidDataException("Nonfinite eye command scalar.");return f;}
 public float F(int p){Check(p,4);float f=BitConverter.Int32BitsToSingle(I32(p));if(!float.IsFinite(f))throw new InvalidDataException("Nonfinite eyelid curve.");return f;}
}
static Dictionary<string,int> Refs(N n){var a=n.Get("DataNodeAttributes")?.value as string[];var v=n.Numbers("DataNodeRefs");if(a==null||v==null||a.Length!=v.Length)throw new InvalidDataException("Invalid processor references.");return a.Select((s,i)=>new{s,i}).ToDictionary(x=>x.s,x=>v[x.i]);}
static float[] Curve(Bytes b,int start,int end,float[] input){
 int channel=b.I32(start),kind=b.U16(start+(b.big?6:4)),size=b.U16(start+(b.big?4:6)),dim=kind>>9,count=size/16;
 if(channel<0||channel>=input.Length||!(kind==512||kind==1024||kind==2048)||size%16!=0||count<1||count>64)throw new InvalidDataException("Unsupported eyelid curve header.");
 float lo=b.F(start+8),hi=b.F(start+12);if(hi<=lo)throw new InvalidDataException("Invalid curve domain.");
 int pos=start+16+16*dim;var knots=new float[count];for(int i=0;i<count;i++){knots[i]=b.U16(pos)/65535f;pos+=2;if((i==0&&knots[i]!=0)||(i>0&&knots[i]<=knots[i-1])||knots[i]>=1)throw new InvalidDataException("Invalid curve knots.");}
 float u=Mathf.Clamp01((input[channel]-lo)/(hi-lo));int seg=0;for(int i=1;i<count;i++)if(u>=knots[i])seg=i;float t=(u-knots[seg])/((seg+1<count?knots[seg+1]:1)-knots[seg]);var result=new float[dim];var previous=new float[dim];
 for(int i=0;i<count;i++)for(int d=0;d<dim;d++){
 float bias=b.F(start+16+d*16+8),scale=b.F(start+16+d*16+12);if(scale<0)throw new InvalidDataException("Negative quantization scale.");float a=bias+b.I16(pos)*scale,c=a+b.I16(pos+2)*scale,e=c+b.I16(pos+4)*scale,f=e+b.I16(pos+6)*scale;pos+=8;
 if(i>0&&Mathf.Abs(previous[d]-a)>Mathf.Max(8*scale,1e-6f))throw new InvalidDataException("Discontinuous eyelid curve.");previous[d]=f;
 if(i==seg)result[d]=(1-t)*(1-t)*(1-t)*a+3*(1-t)*(1-t)*t*c+3*(1-t)*t*t*e+t*t*t*f;
 }
 if((pos+3)/4*4!=end)throw new InvalidDataException("Eyelid curve boundary mismatch.");return result;
}
static float[] Stage(Bytes b,int off,float[] input,out int cmd,out int curveStart,out int end){
 int count=b.U16(off+14);if(b.U16(off+12)!=1||count<1||count>128)throw new InvalidDataException("Unsupported relationship stage.");end=off+b.I32(off+16);cmd=off+b.I32(off+20);var ptr=new int[count+1];for(int i=0;i<count;i++)ptr[i]=off+b.I32(off+24+4*i);ptr[count]=end;curveStart=ptr[0];
 if(cmd<off+24+4*count||cmd>=curveStart||end>b.b.Length)throw new InvalidDataException("Invalid stage offsets.");var values=new List<float>();for(int i=0;i<count;i++){if(ptr[i]>=ptr[i+1])throw new InvalidDataException("Invalid curve order.");values.AddRange(Curve(b,ptr[i],ptr[i+1],input));}
 if(b.U16(cmd)!=values.Count||b.U16(cmd+2)!=count)throw new InvalidDataException("Unsupported curve register map.");for(int i=0;i<count;i++)if(b.U16(cmd+4+2*i)!=i)throw new InvalidDataException("Nonidentity curve register map.");cmd+=4+2*count;return values.ToArray();
}
public static Pose[] Decode(string path,float value,out string diagnostic,N sourceTree=null){
 var tree=sourceTree??DI3MeshReader.Parse(path);var net=tree.All("SubNetwork").FirstOrDefault();if(net==null)throw new InvalidDataException("No control network.");var nodes=net.All("DataNode").ToDictionary(n=>int.Parse((string)n.value));var basis=net.All("Basis").ToDictionary(n=>int.Parse((string)n.value),n=>n.Text("Name")??"");var processors=net.All("ProcessorNode").ToArray();
 var proxies=nodes.Where(k=>basis.TryGetValue(k.Value.Number("BasisRef",-1),out string name)&&name.StartsWith("Proxy_")&&name.EndsWith("_Grp")&&name.Contains("Lid")).ToDictionary(k=>k.Key,k=>basis[k.Value.Number("BasisRef")].Substring(6).Replace("_Grp",""));
 if(proxies.Count!=4)throw new InvalidDataException("Expected four named eyelid proxies; found "+proxies.Count+".");
 var candidates=processors.Where(n=>n.Text("Type")=="RelationshipProcessor").Where(n=>Refs(n).Where(k=>k.Key.StartsWith("Output[")).Select(k=>k.Value).OrderBy(x=>x).SequenceEqual(proxies.Keys.OrderBy(x=>x))).ToArray();if(candidates.Length!=1)throw new InvalidDataException("No unique eyelid relationship processor.");var refs=Refs(candidates[0]);
 object raw=nodes[refs["RelationshipData"]].Get("RelationshipDataBlock")?.value;byte[] data=raw as byte[];if(data==null&&raw is int[] ints){if(ints.Any(i=>i<0||i>255))throw new InvalidDataException("Invalid relationship bytes.");data=ints.Select(i=>(byte)i).ToArray();}if(data==null)throw new InvalidDataException("Missing relationship bytes.");bool big;using(var file=File.OpenRead(path))big=file.ReadByte()==0x45;var b=new Bytes(data,big);int inputCount=refs.Keys.Count(k=>k.StartsWith("Input["));var input=new float[inputCount];var labels=net.Get("HeaderStrings")?.value as string[];var headerMap=net.Numbers("HeaderStringIndices");int lidInputs=0;
for(int i=0;i<inputCount;i++){var node=nodes[refs["Input["+i+"]"]];int h=node.Number("Header")-1;if(labels==null||headerMap==null||h<0||h>=headerMap.Length||headerMap[h]<0||headerMap[h]>=labels.Length)throw new InvalidDataException("Unsupported input header mapping.");string name=labels[headerMap[h]];if(proxies.Values.Contains(name)){input[i]=value;lidInputs++;}else if(name=="AngleLeft"||name=="AngleRight"){object stored=node.Get("Data")?.value;if(!(stored is float)||!float.IsFinite((float)stored))throw new InvalidDataException("Invalid auxiliary eye input.");input[i]=(float)stored;}else throw new InvalidDataException("Unknown eye input "+name+".");}
if(lidInputs!=4)throw new InvalidDataException("Expected four mapped lid inputs.");
 int cmd,first,end;var remap=Stage(b,0,input,out cmd,out first,out end);int next=b.I32(8);if(cmd!=first||next!=end)throw new InvalidDataException("Unsupported input remap commands.");var values=Stage(b,next,remap,out cmd,out first,out end);if(b.I32(next+8)!=0)throw new InvalidDataException("More than two relationship stages.");
 int commandBytes=b.U16(cmd),records=b.U16(cmd+2);cmd+=4;int commandEnd=next+b.I32(next+20)+commandBytes;if(records<1||records>32)throw new InvalidDataException("Invalid output record count.");var output=new Dictionary<int,Matrix4x4>();var alternatives=new Dictionary<int,Matrix4x4>();var pendingCombined=new System.Collections.Generic.HashSet<int>();bool combinedUsed=false;Func<int,float> reg=i=>i>=0&&i<values.Length?values[i]:throw new InvalidDataException("Invalid curve register.");
 for(int r=0;r<records;r++){
 int ops=b.U16(cmd),index=b.U16(cmd+2);cmd+=4;if(!refs.ContainsKey("Output["+index+"]"))throw new InvalidDataException("Unsupported repeated eyelid output composition at record "+r+".");var pos=Vector3.zero;var rot=Matrix4x4.identity;var alternate=Matrix4x4.identity;bool beginCombined=false,endCombined=false;
 for(int j=0;j<ops;j++){
 int code=b.U16(cmd),x=b.U16(cmd+2),y=b.U16(cmd+4),z=b.U16(cmd+6);cmd+=8;
 if(code==0x12||code==0x13){/* The trailing 32-bit scalar is zero in tested layouts; Han stores 1.776e-17. Accept only numerical residue, not an unverified nonzero offset. */ if(Mathf.Abs(b.CommandF(cmd-4))>1e-12f)throw new InvalidDataException("Unsupported nonzero translation descriptor scalar at byte "+(cmd-4)+".");if(code==0x12)pos.y=reg(x);else pos.z=reg(x);}
 else if(code==0x6090||code==0x6095){float qx=b.CommandF(cmd),qy=b.CommandF(cmd+4),qz=b.CommandF(cmd+8),qw=b.CommandF(cmd+12);cmd+=16;if(Mathf.Abs(qx)+Mathf.Abs(qy)+Mathf.Abs(qz)+Mathf.Abs(qw-1)>1e-5)throw new InvalidDataException($"Unsupported rotation offset {qx},{qy},{qz},{qw} at {cmd-16}.");float ax=reg(x),ay=code==0x6095?reg(y):0,az=code==0x6095?reg(z):0;rot=(Matrix4x4.Rotate(Quaternion.AngleAxis(ay*Mathf.Rad2Deg,Vector3.up))*Matrix4x4.Rotate(Quaternion.AngleAxis(ax*Mathf.Rad2Deg,Vector3.right))*Matrix4x4.Rotate(Quaternion.AngleAxis(az*Mathf.Rad2Deg,Vector3.forward))); /* Single-axis angles already use the evaluated matrix convention. Multi-axis order remains the previous hypothesis. */ if(code==0x6095)rot=rot.transpose;}
 else if(code==0x3094||code==0x3090||code==0x5081){
 if(ops!=1)throw new InvalidDataException("Unsupported combined rotation record.");float qx=b.CommandF(cmd),qy=b.CommandF(cmd+4),qz=b.CommandF(cmd+8),qw=b.CommandF(cmd+12);cmd+=16;if(Mathf.Abs(qx)+Mathf.Abs(qy)+Mathf.Abs(qz)+Mathf.Abs(qw-1)>1e-5)throw new InvalidDataException("Unsupported combined rotation offset.");
 if(code==0x5081){if(x!=0||y!=0||!pendingCombined.Contains(index))throw new InvalidDataException("Unexpected auxiliary rotation record.");rot=Matrix4x4.Rotate(Quaternion.AngleAxis(reg(z)*Mathf.Rad2Deg,Vector3.forward));endCombined=true;}
 else {if(z!=0||output.ContainsKey(index))throw new InvalidDataException("Unexpected combined rotation start.");var rx=Matrix4x4.Rotate(Quaternion.AngleAxis(reg(x)*Mathf.Rad2Deg,Vector3.right));var ry=Matrix4x4.Rotate(Quaternion.AngleAxis((code==0x3094?reg(y):0)*Mathf.Rad2Deg,Vector3.up));rot=ry*rx;alternate=rx*ry;beginCombined=true;}
 } else throw new InvalidDataException("Unsupported eyelid opcode 0x"+code.ToString("X4")+" at byte "+(cmd-8)+"; character left unchanged.");
 }
  if(beginCombined){output[index]=rot;alternatives[index]=alternate;pendingCombined.Add(index);combinedUsed=true;}
 else if(endCombined){var a=rot*output[index];var rotationCandidates=new[]{output[index]*rot,rot*alternatives[index],alternatives[index]*rot};float uncertainty=0;foreach(var m in rotationCandidates)for(int k=0;k<16;k++)uncertainty=Mathf.Max(uncertainty,Mathf.Abs(a[k]-m[k]));if(uncertainty>.00015f)throw new InvalidDataException("Combined eyelid rotation order is unresolved at this value. Use control 0 for the source-validated neutral pose; full range is not supported yet.");output[index]=a;pendingCombined.Remove(index);}
 else {if(output.ContainsKey(index))throw new InvalidDataException("Unsupported repeated output.");rot.SetColumn(3,new Vector4(pos.x,pos.y,pos.z,1));output[index]=rot;}
 }
 if(cmd!=commandEnd||b.U16(cmd)!=0||(cmd+2+3)/4*4!=first)throw new InvalidDataException("Output command boundary mismatch.");
 var reflect=Matrix4x4.Scale(new Vector3(-1,1,1));var poses=new List<Pose>();foreach(var pair in output){int proxy=refs["Output["+pair.Key+"]"];var convert=processors.Where(n=>(n.Text("Type")=="TransformTransformWithDilation"||n.Text("Type")=="TransformDilationWithDilation")&&Refs(n).TryGetValue("Input",out int v)&&v==proxy).ToArray();if(convert.Length!=1)throw new InvalidDataException("Unsupported proxy transform connection.");int transformed=Refs(convert[0])["Output"];bool dilation=convert[0].Text("Type")=="TransformDilationWithDilation";if(dilation){var scalar=nodes[proxy].Get("Data")?.Get("Scale")?.value;if(nodes[proxy].Text("Type")!="Dilation"||!(scalar is float)||Mathf.Abs((float)scalar-1)>1e-6f)throw new InvalidDataException("Unsupported scaled eyelid dilation.");}string finalType=dilation?"DilationTranslationOrientationConnection":"TransformTranslationOrientationScaleConnection";if(processors.Count(n=>n.Text("Type")==finalType&&Refs(n).Count==2&&Refs(n).TryGetValue("Input",out int v)&&v==transformed)!=1)throw new InvalidDataException("Unsupported final eyelid connection.");poses.Add(new Pose{bone=proxies[proxy],delta=reflect*pair.Value*reflect});}
 if(combinedUsed){
for(int i=0;i<inputCount;i++){object stored=nodes[refs["Input["+i+"]"]].Get("Data")?.value;if(!(stored is float)||Mathf.Abs(input[i]-(float)stored)>1e-6f)throw new InvalidDataException("Combined rotations currently support the stored source pose only. Captain America WWII uses control 0; full blink order remains unresolved.");}
foreach(var pair in output){var dataNode=nodes[refs["Output["+pair.Key+"]"]].Get("Data");var q=dataNode?.Get("Orientation")?.value as float[];var t=dataNode?.Get("Translation")?.value as float[];if(q==null||q.Length!=4||t==null||t.Length!=3)throw new InvalidDataException("Missing stored combined pose.");var stored=Matrix4x4.Rotate(new Quaternion(q[0],q[1],q[2],q[3])).transpose;stored.SetColumn(3,new Vector4(t[0],t[1],t[2],1));for(int k=0;k<16;k++)if(!float.IsFinite(stored[k])||Mathf.Abs(stored[k]-pair.Value[k])>.00015f)throw new InvalidDataException("Combined rotation does not match stored source pose.");}
}if(pendingCombined.Count!=0)throw new InvalidDataException("Incomplete combined rotation pair.");if(poses.Count!=4)throw new InvalidDataException("Incomplete eyelid output.");diagnostic=(combinedUsed?"Limited combined-rotation pose; supported only where candidate orders agree. Auxiliary angles retained. ":"")+"Decoded "+inputCount+" inputs and "+poses.Count+" eyelid outputs from OCT at control value "+value+". Experimental curve/transform interpretation; visual review required.";return poses.ToArray();
}
}}









