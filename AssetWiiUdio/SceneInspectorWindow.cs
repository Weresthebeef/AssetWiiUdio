using System;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
namespace AssetWiiUdio;

// Uses the same validated BinaryTup parser as mesh loading. No graph evaluation is implied.
public sealed class SceneInspectorWindow : Window
{
    public SceneInspectorWindow(string name,byte[] bytes)
    {
        var scene=DI3MeshReader.Parse(bytes);
        Title="OCT / BENT data · "+name;Width=1000;Height=750;
        var root=new DockPanel{Margin=new Thickness(10)};Content=root;
        var status=new TextBlock{Text=$"{name}\n{bytes.Length:N0} bytes · typed BinaryTup hierarchy. Expand nodes for values, references, offsets and flags. Animation blobs are raw data, not decoded curves.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,8)};
        DockPanel.SetDock(status,Dock.Top);root.Children.Add(status);
        var save=new Button{Content="Export decoded tree JSON",HorizontalAlignment=System.Windows.HorizontalAlignment.Left,Padding=new Thickness(10,6,10,6),Margin=new Thickness(0,0,0,8)};
        save.Click+=(_,_)=>{var dialog=new SaveFileDialog{Filter="JSON|*.json",FileName=Path.GetFileName(name)+".json"};if(dialog.ShowDialog(this)==true){try{File.WriteAllText(dialog.FileName,JsonSerializer.Serialize(scene,new JsonSerializerOptions{IncludeFields=true,WriteIndented=true,MaxDepth=256}));}catch(Exception ex){MessageBox.Show(this,ex.Message,"Export failed");}}};
        DockPanel.SetDock(save,Dock.Top);root.Children.Add(save);
        var tree=new System.Windows.Controls.TreeView();root.Children.Add(tree);
        string Value(object? v)=>v is Array a?$"{v.GetType().GetElementType()?.Name}[{a.Length:N0}]":Convert.ToString(v,CultureInfo.InvariantCulture)??"";
        TreeViewItem Leaf(string text)=>new(){Header=text,IsExpanded=false};
        TreeViewItem Node(DI3MeshReader.Node node)
        {
            var item=Leaf(node.name+(node.Reference!=null?" ["+node.Reference+"]":"")+"  "+Value(node.value));
            item.Items.Add("Expand to inspect");bool loaded=false;
            item.Expanded+=(_,e)=>
            {
                if(loaded||!ReferenceEquals(e.OriginalSource,item))return;loaded=true;item.Items.Clear();
                item.Items.Add(Leaf($"Record: 0x{node.Offset:X}–0x{node.EndOffset:X} · flags 0x{node.Flags:X4} · depth {node.Flags>>10} · kind {node.Flags&3} · type {(node.Flags>>3)&7}"));
                if(node.Reference!=null)item.Items.Add(Leaf("Reference string: "+node.Reference));
                if(node.value is Array array)
                {
                    // Page arrays so a large ClipDataBlock does not create thousands of controls at once.
                    for(int start=0;start<array.Length;start+=256)
                    {
                        int first=start,last=Math.Min(array.Length,start+256);var page=Leaf($"Values [{first}..{last-1}]");page.Items.Add("Expand values");bool done=false;
                        page.Expanded+=(_,ev)=>{if(done||!ReferenceEquals(ev.OriginalSource,page))return;done=true;page.Items.Clear();for(int i=first;i<last;i++)page.Items.Add(Leaf($"[{i}] {Value(array.GetValue(i))}"));};item.Items.Add(page);
                    }
                }
                else if(node.value!=null)item.Items.Add(Leaf("Value: "+Value(node.value)));
                foreach(var child in node.children)item.Items.Add(Node(child));
            };
            return item;
        }
        var header=Leaf("BinaryTup header / string table");header.Items.Add("Expand header");bool headerLoaded=false;
        header.Expanded+=(_,e)=>{if(headerLoaded||!ReferenceEquals(e.OriginalSource,header))return;headerLoaded=true;header.Items.Clear();
            bool big=bytes[0]==0x45;
            uint Word(int p)=>big?System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(p,4)):System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p,4));
            header.Items.Add(Leaf(big?"Big endian":"Little endian"));
            for(int i=0;i<60;i+=4)header.Items.Add(Leaf($"+0x{i:X2}: 0x{Word(i):X8} ({Word(i)})"));
            var strings=Leaf("String table (indices / byte offsets)");header.Items.Add(strings);
            int at=60,index=0,end=checked(60+(int)Word(12));
            while(at<end){int stop=Array.IndexOf(bytes,(byte)0,at,end-at);if(stop<0)break;strings.Items.Add(Leaf($"[{index++}] 0x{at:X}: {System.Text.Encoding.UTF8.GetString(bytes,at,stop-at)}"));at=stop+1;}
        };
        tree.Items.Add(header);
        foreach(var node in scene.children)tree.Items.Add(Node(node));
    }
}
