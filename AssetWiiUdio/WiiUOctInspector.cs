using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AssetWiiUdio
{
    // Non-destructive Wii U OCT diagnostic. Does not claim to decode unsupported node records.
    public static class WiiUOctInspector
    {
        public sealed record Companion(string Name, long Size);
        static uint Be32(byte[] b, int i) => ((uint)b[i]<<24)|((uint)b[i+1]<<16)|((uint)b[i+2]<<8)|b[i+3];
        public static string Inspect(byte[] data, string filename, IEnumerable<Companion> companions)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Wii U Disney Infinity 3.0 OCT inspection");
            sb.AppendLine("File: " + filename);
            sb.AppendLine("Size: " + data.Length.ToString("N0") + " bytes");
            if(data.Length < 64) { sb.AppendLine("FAIL: truncated OCT header (<64 bytes)."); return sb.ToString(); }
            sb.AppendLine("Header words (big endian): " + string.Join(", ", Enumerable.Range(0,6).Select(i=>"0x"+Be32(data,i*4).ToString("X8"))));
            sb.AppendLine("Header string-table offset: 61 (format observation; not yet fully decoded)");
            var names = new List<string>(); int pos=61;
            try
            {
                while(pos<data.Length && names.Count<65536)
                {
                    int end=Array.IndexOf(data,(byte)0,pos);
                    if(end<0 || end-pos>4096) throw new InvalidDataException("Unterminated or oversized string-table entry at "+pos);
                    string name=Encoding.UTF8.GetString(data,pos,end-pos);pos=end+1;
                    if(name=="\x01") break;
                    names.Add(name);
                }
                sb.AppendLine("String table: " + names.Count + " entries; ends at byte " + pos);
                sb.AppendLine("Known scene/mesh names:");
                foreach(string name in names.Where(s=>s.IndexOf("mesh",StringComparison.OrdinalIgnoreCase)>=0 ||
                    s=="Geometry3d" || s=="VertexBufferPool" || s=="IndexBufferPool" || s=="SubNetworkPool").Distinct().Take(100))
                    sb.AppendLine("  " + name);
                sb.AppendLine("First bytes after string table: " + BitConverter.ToString(data,pos,Math.Min(32,data.Length-pos)));
            }
            catch(Exception ex) { sb.AppendLine("String-table warning: " + ex.Message); }
            string stem=Path.GetFileNameWithoutExtension(filename);
            var files=companions.ToArray();
            var vbuf=files.Where(f=>Path.GetFileName(f.Name).StartsWith(stem+"_",StringComparison.OrdinalIgnoreCase) && f.Name.EndsWith(".vbuf",StringComparison.OrdinalIgnoreCase)).ToArray();
            var ibuf=files.Where(f=>Path.GetFileName(f.Name).StartsWith(stem+"_",StringComparison.OrdinalIgnoreCase) && f.Name.EndsWith(".ibuf",StringComparison.OrdinalIgnoreCase)).ToArray();
            sb.AppendLine();sb.AppendLine("Companion geometry buffers:");
            foreach(var f in vbuf.Concat(ibuf)) sb.AppendLine("  "+f.Name+" ("+f.Size.ToString("N0")+" bytes)");
            if(vbuf.Length==0 || ibuf.Length==0) sb.AppendLine("MISSING: one or more vertex/index buffer files.");
            else sb.AppendLine("PASS: matching vertex and index buffers are present.");
            sb.AppendLine();
            sb.AppendLine("Mesh preview status: this inspection validates basic structure and companion presence only.");
            sb.AppendLine("The current DI3MeshReader is based on PC Gold OCT records; Wii U records/vertex layouts may differ.");
            sb.AppendLine("A valid string table does NOT prove the OCT hierarchy or mesh data is fully valid.");
            return sb.ToString();
        }
    }
}
