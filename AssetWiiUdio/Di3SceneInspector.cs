using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AssetWiiUdio;

/// <summary>Read-only diagnostic for Wii U DI3 Tupperware-style OCT/BENT scenes.
/// Extracts validated string-table tokens and cross-file references; does not pretend
/// to reconstruct unverified typed node records or animation keyframes.</summary>
public static class Di3SceneInspector
{
    private static uint Be32(byte[] data,int offset) =>
        ((uint)data[offset]<<24)|((uint)data[offset+1]<<16)|((uint)data[offset+2]<<8)|data[offset+3];

    public static string Inspect(byte[] data,string filename,IEnumerable<WiiUOctInspector.Companion> companions)
    {
        var report=new StringBuilder();
        report.AppendLine("Disney Infinity Wii U — OCT / BENT scene inspector");
        report.AppendLine("Source: "+filename);
        report.AppendLine("Size: "+data.Length.ToString("N0")+" bytes");
        report.AppendLine("Mode: read-only diagnostic; binary record grammar is NOT fully decoded.");
        report.AppendLine();
        if(data.Length<61){report.AppendLine("INVALID: file is shorter than 61-byte observed header.");return report.ToString();}
        report.AppendLine("Header (big-endian uint32):");
        for(int i=0;i<60;i+=4)
            report.AppendLine($"  +0x{i:X2}: 0x{Be32(data,i):X8} ({Be32(data,i)})");
        // Known samples contain 61 header bytes followed by NUL-delimited strings.
        // End of table is indicated by a NUL after the last terminated string.
        var strings=new List<(int offset,string value)>();
        int pos=61;
        while(pos<data.Length && strings.Count<200000)
        {
            if(data[pos]==0){pos++;break;}
            int end=Array.IndexOf(data,(byte)0,pos);
            if(end<0 || end-pos>8192){report.AppendLine($"WARNING: unterminated/oversized string at 0x{pos:X}");break;}
            string value=Encoding.UTF8.GetString(data,pos,end-pos);
            if(value.Any(c=>char.IsControl(c) && c!='\t'))
            {report.AppendLine($"WARNING: non-text byte in table at 0x{pos:X}; stopped.");break;}
            strings.Add((pos,value));pos=end+1;
        }
        report.AppendLine();
        report.AppendLine($"String tokens ({strings.Count}); record data starts near 0x{pos:X}:");
        for(int i=0;i<strings.Count;i++)report.AppendLine($"  [{i,4}] 0x{strings[i].offset:X6}  {strings[i].value}");
        report.AppendLine();
        report.AppendLine("Asset references and matching archive members:");
        var files=companions.ToArray();
        int refs=0;
        foreach(var token in strings.Select(x=>x.value).Distinct())
        {
            if(!token.Contains('/') && !token.Contains('\\') &&
                !token.EndsWith(".oct",StringComparison.OrdinalIgnoreCase) &&
                !token.EndsWith(".bent",StringComparison.OrdinalIgnoreCase))continue;
            refs++;
            string normalized=token.Replace('\\','/');
            var matches=files.Where(f=>f.Name.Replace('\\','/').EndsWith(normalized,StringComparison.OrdinalIgnoreCase)).ToArray();
            report.AppendLine("  "+token+(matches.Length>0?" [FOUND]":" [not in this archive]"));
            foreach(var match in matches)report.AppendLine($"    -> {match.Name} ({match.Size:N0} bytes)");
        }
        if(refs==0)report.AppendLine("  (No path-like tokens in the string table.)");
        report.AppendLine();
        report.AppendLine("Potential node/property identifiers (token names only; NOT decoded hierarchy):");
        foreach(var token in strings.Select(x=>x.value).Where(x=>x.IndexOf("Channel",StringComparison.OrdinalIgnoreCase)>=0 ||
            x.IndexOf("Model",StringComparison.OrdinalIgnoreCase)>=0 ||
            x.IndexOf("Blend",StringComparison.OrdinalIgnoreCase)>=0 ||
            x.IndexOf("Animation",StringComparison.OrdinalIgnoreCase)>=0 ||
            x.IndexOf("Motion",StringComparison.OrdinalIgnoreCase)>=0 ||
            x.IndexOf("Mesh",StringComparison.OrdinalIgnoreCase)>=0).Distinct().Take(120))
            report.AppendLine("  "+token);
        report.AppendLine();
        report.AppendLine("First 128 bytes of remaining binary records:");
        for(int i=pos;i<Math.Min(data.Length,pos+128);i+=16)
            report.AppendLine($"  0x{i:X6}: "+BitConverter.ToString(data,i,Math.Min(16,data.Length-i)).Replace('-', ' '));
        report.AppendLine();
        report.AppendLine("Limits: record tree, typed values, channel indices, and animation clips are not yet decoded.");
        report.AppendLine("A successful inspection does not prove the full file is valid or mesh-preview compatible.");
        return report.ToString();
    }
}
