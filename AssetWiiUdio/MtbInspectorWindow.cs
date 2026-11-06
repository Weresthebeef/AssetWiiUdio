using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
namespace AssetWiiUdio;
public sealed class MtbInspectorWindow:Window
{
    public MtbInspectorWindow(string name,byte[] bytes)
    {
        var b=MaterialBundleReader.Read(bytes);Title="MTB Inspector · "+name;Width=950;Height=720;
        var root=new DockPanel{Margin=new Thickness(10)};Content=root;
        var status=new TextBlock{Text=$"{(b.Big?"Wii U / big endian":"PC / little endian")} · {b.Sections.Count} sections · {b.Textures.Count} textures\n{b.Status}",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(4,4,4,10)};DockPanel.SetDock(status,Dock.Top);root.Children.Add(status);
        var tree=new System.Windows.Controls.TreeView();root.Children.Add(tree);
        TreeViewItem Node(ItemsControl parent,string label){var n=new TreeViewItem{Header=label,IsExpanded=false};parent.Items.Add(n);return n;}
        var header=Node(tree,"Bundle header");Node(header,$"Magic: {System.Text.Encoding.ASCII.GetString(bytes,0,4)}");Node(header,$"Length: {bytes.Length} bytes");
        foreach(var s in b.Sections)
        {
            var section=Node(tree,$"{s.Tag} · offset 0x{s.Offset:X} · {s.Size} bytes");Node(section,$"Version: {s.Version}");Node(section,$"Entry count: {s.Count}");
            if(s.Tag=="TEXB")for(int i=0;i<b.Textures.Count;i++)Node(section,$"Texture [{i}]: {b.Textures[i]}");
            if(s.Tag=="MATP")foreach(var m in b.Materials)
            {
                var material=Node(section,$"Material [{m.Index}] · 0x{m.Offset:X} · {m.Size} bytes");Node(material,"Shader: "+m.Shader);Node(material,"UUID: "+m.Uuid);Node(material,"Material hash: "+m.Hash);Node(material,$"Color sampler slot: {m.ColorSlot}");
                for(int i=0;i<m.TextureIndices.Length;i++){int t=m.TextureIndices[i];Node(material,$"Sampler [{i}] → texture [{t}]"+(t>=0?" → "+b.Textures[t]:" (unused)"));}
            }
            var raw=Node(section,"Raw section data (offset / hex)");
            // Populate hex only when opened, keeping initial load inexpensive.
            raw.Items.Add("Expand to inspect bytes");bool loaded=false;
            raw.Expanded+=(_,e)=>{if(loaded||!ReferenceEquals(e.OriginalSource,raw))return;loaded=true;raw.Items.Clear();for(int p=s.Offset;p<s.Offset+s.Size;p+=16)Node(raw,$"{p:X8}  {Convert.ToHexString(bytes.AsSpan(p,Math.Min(16,s.Offset+s.Size-p)))}");};
        }
    }
}
