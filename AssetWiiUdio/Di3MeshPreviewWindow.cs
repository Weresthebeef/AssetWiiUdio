using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Windows.Media.Imaging;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace AssetWiiUdio;

public sealed class Di3MeshPreviewWindow : Window
{
    readonly PerspectiveCamera camera = new();
    readonly Model3DGroup scene = new();
    readonly Viewport3D viewport = new();
    readonly TextBlock info = new();
    double radius = 5, azimuth = 0.6, elevation = 0.25;
    System.Windows.Point? last;
    Point3D center;
    bool wireframe;
    bool showRig,rigOnly;
    bool showBase=true;
    readonly Model3DGroup characterBase;
    bool initializing=true;
    readonly Dictionary<int,Material> selectedMaterials=new();
    readonly List<PreviewRig.Joint> joints;
    readonly List<(string Name,BitmapSource Image)> textures=new();
    readonly DI3MeshReader.Model model;
    readonly NativeVertexData native;
    System.Numerics.Vector3[] displayPositions,displayNormals;
    System.Numerics.Matrix4x4[] posedWorld;
    readonly Dictionary<string,string> textureSources=new();

    public Di3MeshPreviewWindow(DI3MeshReader.Model model, string label)
    {
        this.model=model;
        characterBase=CharacterBasePreview.Create(model);
        joints=PreviewRig.Read(model.path);
        native=NativeVertexData.Read(model,joints);
        displayPositions=model.positions;displayNormals=native.Normals;posedWorld=native.BindWorld;
        var blobs=new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach(var file in Directory.GetFiles(Path.GetDirectoryName(model.path)!,"*",SearchOption.AllDirectories).Where(p=>new[]{".tbody",".dds",".png"}.Contains(Path.GetExtension(p).ToLowerInvariant())))
        {string id=Path.GetFileNameWithoutExtension(file);blobs[id]=File.ReadAllBytes(file);textureSources[id]=file;}
        foreach(string id in DI3MeshReader.TextureIds(model.path))if(!blobs.ContainsKey(id))
        {
            try{if(ExternalGameAssets.TryRead("textures/"+id+".tbody",out var bytes,out var source)){blobs[id]=bytes;textureSources[id]=source;}else textureSources[id]="MISSING (select the game/assets or extracted assets folder)";}
            catch(Exception ex){textureSources[id]="Lookup failed: "+ex.Message;}
        }
        foreach(var entry in blobs)
        {
            try
            {
                BitmapSource Decode(byte[] data)
                {
                    if(data.Length>4&&data[0]==137&&data[1]==80){using var ms=new MemoryStream(data);var png=new BitmapImage();png.BeginInit();png.CacheOption=BitmapCacheOption.OnLoad;png.StreamSource=ms;png.EndInit();return png;}
                    var decoded=data.Length>=4&&System.Text.Encoding.ASCII.GetString(data,0,4)=="DDS "?Gx2ExternalPreview.ConvertDds(data):Gx2ExternalPreview.Convert(data);
                    return BitmapSource.Create(decoded.Width,decoded.Height,96,96,PixelFormats.Bgra32,null,decoded.Bgra,decoded.Width*4);
                }
                BitmapSource image=Decode(entry.Value);
                textureSources[entry.Key]+=$" � embedded/base {image.PixelWidth}�{image.PixelHeight}";
                try
                {
                    string streamPath=Path.Combine(Path.GetDirectoryName(model.path)!,entry.Key+".tstream");
                    byte[] stream;string source;
                    bool found;
                    if(File.Exists(streamPath)){stream=File.ReadAllBytes(streamPath);source=streamPath;found=true;}
                    else found=ExternalGameAssets.TryRead("textures/"+entry.Key+".tstream",out stream,out source);
                    if(found){var high=Decode(stream);textureSources[entry.Key]+=$"; stream {high.PixelWidth}�{high.PixelHeight} from {source}";
                        if((long)high.PixelWidth*high.PixelHeight>(long)image.PixelWidth*image.PixelHeight){image=high;textureSources[entry.Key]+=" [selected higher resolution]";}else textureSources[entry.Key]+=" [base retained]";}
                    else textureSources[entry.Key]+="; no stream companion found";
                }
                catch(Exception ex){textureSources[entry.Key]+="; base retained: stream unavailable: "+ex.Message;}

                image.Freeze();textures.Add((entry.Key,image));
            }
            catch(Exception ex){textures.Add((entry.Key+" ("+ex.Message+")",null!));textureSources[entry.Key]+="; Decode failed: "+ex.Message;}
        }
        Title="DI3 Mesh Preview — "+label+" · default-texture diagnostics"; Width=1100; Height=780; MinWidth=600; MinHeight=450;
        Background=Brushes.Black;
        var root=new DockPanel(); Content=root;
        var bar=new StackPanel { Orientation=Orientation.Horizontal, Margin=new Thickness(8) };
        var toggle=new Button { Content="Toggle wireframe", Padding=new Thickness(12,5,12,5) };
        toggle.Click+=(_,_)=>{wireframe=!wireframe; BuildGeometry();};bar.Children.Add(toggle);
        info.Foreground=Brushes.White;info.Margin=new Thickness(12,6,0,0);bar.Children.Add(info);
        DockPanel.SetDock(bar,Dock.Top);root.Children.Add(bar);
        var side=new StackPanel{Width=265,Margin=new Thickness(8)};
        var baseToggle=new System.Windows.Controls.CheckBox{Content="Show character base",IsChecked=true,Foreground=Brushes.White};
        baseToggle.Checked+=(_,_)=>{showBase=true;SetBounds();BuildGeometry();UpdateCamera();};baseToggle.Unchecked+=(_,_)=>{showBase=false;SetBounds();BuildGeometry();UpdateCamera();};side.Children.Add(baseToggle);
        var overlay=new System.Windows.Controls.CheckBox{Content="Show rig / scene nodes",Foreground=Brushes.White};overlay.Checked+=(_,_)=>{showRig=true;BuildGeometry();};overlay.Unchecked+=(_,_)=>{showRig=false;BuildGeometry();};side.Children.Add(overlay);
        var only=new System.Windows.Controls.CheckBox{Content="Rig only",Foreground=Brushes.White};only.Checked+=(_,_)=>{rigOnly=true;showRig=true;overlay.IsChecked=true;BuildGeometry();};only.Unchecked+=(_,_)=>{rigOnly=false;BuildGeometry();};side.Children.Add(only);
        side.Children.Add(new TextBlock{Text=$"{joints.Count} rig entries. Skin bind bones when present; otherwise scene nodes. Unverified helper links are omitted.",TextWrapping=TextWrapping.Wrap,Foreground=Brushes.LightGray,Margin=new Thickness(0,8,0,8)});
        var list=new System.Windows.Controls.ListBox{Height=155};foreach(var j in joints)list.Items.Add(j.Name);side.Children.Add(list);
        side.Children.Add(new TextBlock{Text=native.Status,TextWrapping=TextWrapping.Wrap,Foreground=Brushes.LightGray,Margin=new Thickness(0,5,0,5)});
        var poseAngle=new System.Windows.Controls.Slider{Minimum=-60,Maximum=60,Value=0,IsEnabled=native.Weights.Any(w=>w.Sum()>0)};
        var axis=new System.Windows.Controls.ComboBox{ItemsSource=new[]{"X rotation","Y rotation","Z rotation"},SelectedIndex=0};side.Children.Add(axis);
        side.Children.Add(new TextBlock{Text="Selected bone rotation test (degrees)",Foreground=Brushes.White});side.Children.Add(poseAngle);
        var eyeToggle=new System.Windows.Controls.CheckBox{Content="Evaluate source eyelids (experimental)",Foreground=Brushes.White};side.Children.Add(eyeToggle);
        side.Children.Add(new TextBlock{Text="Eye control: −100 to +100 (neutral: 0)",Foreground=Brushes.White});
        var eyeValue=new System.Windows.Controls.Slider{Minimum=-100,Maximum=100,Value=0,TickFrequency=100,IsSnapToTickEnabled=false};side.Children.Add(eyeValue);
        var eyeStatus=new TextBlock{Text="Eye control 0: tested Yoda neutral. Off retains source bind pose.",Foreground=Brushes.LightGray,TextWrapping=TextWrapping.Wrap};side.Children.Add(eyeStatus);
        void Pose()
        {
            int selected=list.SelectedIndex;
            posedWorld=(System.Numerics.Matrix4x4[])native.BindWorld.Clone();
            if(selected>=0&&poseAngle.IsEnabled)
            {
                var pivot=native.BindWorld[selected].Translation;float radians=(float)(poseAngle.Value*Math.PI/180);
                var rotation=axis.SelectedIndex==0?System.Numerics.Matrix4x4.CreateRotationX(radians):axis.SelectedIndex==1?System.Numerics.Matrix4x4.CreateRotationY(radians):System.Numerics.Matrix4x4.CreateRotationZ(radians);
                var delta=System.Numerics.Matrix4x4.CreateTranslation(-pivot)*rotation*System.Numerics.Matrix4x4.CreateTranslation(pivot);
                for(int i=0;i<joints.Count;i++){int ancestor=i;while(ancestor>=0&&ancestor!=selected)ancestor=joints[ancestor].Parent;if(ancestor==selected)posedWorld[i]=native.BindWorld[i]*delta;}
                var deformed=native.Deform(model,posedWorld);displayPositions=deformed.Positions;displayNormals=deformed.Normals;
            }
            else {displayPositions=model.positions;displayNormals=native.Normals;}
            if(eyeToggle.IsChecked==true)
            {
                try
                {
                    var poses=DI3SourceEyeDecoder.Decode(model.path,(float)eyeValue.Value,out var diagnostic);
                    var before=(System.Numerics.Matrix4x4[])posedWorld.Clone();var evaluated=(System.Numerics.Matrix4x4[])posedWorld.Clone();
                    foreach(var eye in poses)
                    {
                        var matches=joints.Select((j,i)=>(j,i)).Where(x=>x.j.Name==eye.bone).ToArray();
                        if(matches.Length!=1)throw new InvalidDataException("Expected one skin bone for "+eye.bone);
                        int index=matches[0].i,parent=joints[index].Parent;
                        if(parent<0||joints[parent].Name!="Head")throw new InvalidDataException("Unsupported eyelid parent for "+eye.bone);
                        if(!System.Numerics.Matrix4x4.Invert(before[parent],out var inverseParent)||!System.Numerics.Matrix4x4.Invert(before[index],out var inverseEye))throw new InvalidDataException("Singular eye hierarchy.");
                        var local=before[index]*inverseParent;var newWorld=eye.delta.Row*local*before[parent];
                        if(!float.IsFinite(newWorld.GetDeterminant())||Math.Abs(newWorld.GetDeterminant())<1e-6f)throw new InvalidDataException("Invalid evaluated eye transform.");
                        for(int i=0;i<joints.Count;i++){int ancestor=i;while(ancestor>=0&&ancestor!=index)ancestor=joints[ancestor].Parent;if(ancestor==index)evaluated[i]=before[i]*inverseEye*newWorld;}
                    }
                    posedWorld=evaluated;var eyeDeformed=native.Deform(model,posedWorld);displayPositions=eyeDeformed.Positions;displayNormals=eyeDeformed.Normals;
                    eyeStatus.Text=diagnostic;eyeStatus.Foreground=Brushes.LightGreen;
                }
                catch(Exception ex) when(ex is InvalidDataException||ex is ArgumentException||ex is InvalidOperationException||ex is IndexOutOfRangeException||ex is KeyNotFoundException)
                {eyeStatus.Text="Eye pose not applied: "+ex.Message;eyeStatus.Foreground=Brushes.Orange;}
            }
            else {eyeStatus.Text="Source eyelids unchanged. Control "+eyeValue.Value.ToString("0")+" (Yoda neutral: 0).";eyeStatus.Foreground=Brushes.LightGray;}
            if(!initializing)BuildGeometry();
        }
        eyeToggle.Checked+=(_,_)=>Pose();eyeToggle.Unchecked+=(_,_)=>Pose();eyeValue.ValueChanged+=(_,_)=>Pose();
        poseAngle.ValueChanged+=(_,_)=>Pose();axis.SelectionChanged+=(_,_)=>Pose();list.SelectionChanged+=(_,_)=>Pose();
        var resetPose=new Button{Content="Reset bind pose",Margin=new Thickness(0,4,0,8)};resetPose.Click+=(_,_)=>{eyeToggle.IsChecked=false;eyeValue.Value=0;poseAngle.Value=0;Pose();};side.Children.Add(resetPose);
        var assetsButton=new Button{Content="Choose external game/assets folder",Margin=new Thickness(0,4,0,4)};
        assetsButton.Click+=(_,_)=>{using var folder=new System.Windows.Forms.FolderBrowserDialog{Description="Choose game root, assets, or extracted assets folder",SelectedPath=ExternalGameAssets.Root};if(folder.ShowDialog()!=System.Windows.Forms.DialogResult.OK)return;ExternalGameAssets.Root=folder.SelectedPath;new Di3MeshPreviewWindow(model,label){Owner=this}.ShowDialog();};side.Children.Add(assetsButton);
        var textureReport=new Button{Content="Texture lookup diagnostics",Margin=new Thickness(0,4,0,4)};textureReport.Click+=(_,_)=>ShowReport("Texture lookup",string.Join("\n\n",textureSources.Select(p=>p.Key+"\n"+p.Value)));side.Children.Add(textureReport);
        var routesButton=new Button{Content="Inspect DNAX animation routing…",Margin=new Thickness(0,4,0,8)};
        routesButton.Click+=(_,_)=>{var dialog=new Microsoft.Win32.OpenFileDialog{Filter="Character DNA (*.dnax)|*.dnax|All files|*.*"};if(dialog.ShowDialog(this)==true)try{ShowReport("Animation routing",ExternalGameAssets.RoutingReport(dialog.FileName,File.ReadAllBytes(dialog.FileName)));}catch(Exception ex){ShowReport("Animation routing",ex.Message);}};side.Children.Add(routesButton);
        side.Children.Add(new TextBlock{Text="Color texture per material (MTB sampler references)",TextWrapping=TextWrapping.Wrap,Foreground=Brushes.White,Margin=new Thickness(0,10,0,5)});
        var mtbPath=Path.ChangeExtension(model.path,".mtb");
        var bundle=File.Exists(mtbPath)?MaterialBundleReader.Read(File.ReadAllBytes(mtbPath)):null;
        foreach(int reference in model.slots.Select(s=>s.reference).Distinct())
        {
            int slot=reference;string name=model.slots.First(s=>s.reference==slot).name;
            side.Children.Add(new TextBlock{Text=name,TextWrapping=TextWrapping.Wrap,Foreground=Brushes.LightGray});
            var combo=new System.Windows.Controls.ComboBox{Margin=new Thickness(0,4,0,10)};combo.Items.Add("Plain preview color");foreach(var t in textures)combo.Items.Add(t.Name);combo.SelectedIndex=0;
            combo.SelectionChanged+=(_,_)=>{int i=combo.SelectedIndex-1;if(i>=0&&textures[i].Image!=null){var brush=new ImageBrush(textures[i].Image){TileMode=TileMode.Tile,ViewportUnits=BrushMappingMode.Absolute,Viewport=new Rect(0,0,1,1)};selectedMaterials[slot]=new DiffuseMaterial(brush);}else selectedMaterials.Remove(slot);if(!initializing)BuildGeometry();};
            string? colorId=bundle?.ColorTexture(slot);
            if(colorId!=null)
            {
                int match=textures.FindIndex(t=>string.Equals(t.Name,colorId,StringComparison.OrdinalIgnoreCase)&&t.Image!=null);
                if(match>=0)combo.SelectedIndex=match+1;
            }
            side.Children.Add(combo);
            string assignment;
            if(colorId==null)
            {
                assignment=bundle==null?"No MTB found: "+mtbPath:bundle.Status+"; material reference "+slot;
                combo.Items[0]="Default unresolved (see reason below)";
            }
            else if(combo.SelectedIndex==0)
            {
                assignment="Expected default: "+colorId+"\n"+(textureSources.TryGetValue(colorId,out var failure)?failure:"Texture ID not loaded");
                combo.Items[0]="Default unavailable: "+colorId;
            }
            else assignment="Default from material sampler: "+colorId;
            side.Children.Add(new TextBlock{Text=assignment,TextWrapping=TextWrapping.Wrap,Foreground=combo.SelectedIndex>0?Brushes.LightGreen:Brushes.Orange,Margin=new Thickness(0,0,0,8)});
            textureSources["Material "+slot+" · "+name]=assignment;
        }
        var scroll=new ScrollViewer{Content=side,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};DockPanel.SetDock(scroll,Dock.Left);root.Children.Add(scroll);
        viewport.Camera=camera;root.Children.Add(viewport);
        var visual=new ModelVisual3D{Content=scene};viewport.Children.Add(visual);
        scene.Children.Add(new AmbientLight(Color.FromRgb(115,115,115)));
        scene.Children.Add(new DirectionalLight(Colors.White,new Vector3D(-0.6,-0.8,-1)));
        scene.Children.Add(new DirectionalLight(Color.FromRgb(100,110,135),new Vector3D(0.7,0.3,0.6)));
        camera.FieldOfView=50;camera.UpDirection=new Vector3D(0,1,0);
        SetBounds();initializing=false;BuildGeometry();UpdateCamera();
        viewport.MouseDown+=(_,e)=>{last=e.GetPosition(viewport);viewport.CaptureMouse();};
        viewport.MouseUp+=(_,_)=>{last=null;viewport.ReleaseMouseCapture();};
        viewport.MouseMove+=(_,e)=>{if(last is not System.Windows.Point old || e.LeftButton!=MouseButtonState.Pressed)return;var next=e.GetPosition(viewport);azimuth+=(next.X-old.X)*0.01;elevation=Math.Clamp(elevation+(next.Y-old.Y)*0.01,-1.5,1.5);last=next;UpdateCamera();};
        viewport.MouseWheel+=(_,e)=>{radius*=Math.Pow(0.85,e.Delta/120.0);radius=Math.Clamp(radius,0.01,1e8);UpdateCamera();};
    }
    void SetBounds()
    {
        if(model.positions.Length==0)throw new InvalidDataException("No vertices.");
        var lo = new System.Numerics.Vector3(float.MaxValue);
        var hi = new System.Numerics.Vector3(float.MinValue);
        foreach(var p in model.positions){lo=System.Numerics.Vector3.Min(lo,p);hi=System.Numerics.Vector3.Max(hi,p);}
        if(showBase){var bounds=characterBase.Bounds;lo=System.Numerics.Vector3.Min(lo,new System.Numerics.Vector3((float)bounds.X,(float)bounds.Y,(float)bounds.Z));hi=System.Numerics.Vector3.Max(hi,new System.Numerics.Vector3((float)(bounds.X+bounds.SizeX),(float)(bounds.Y+bounds.SizeY),(float)(bounds.Z+bounds.SizeZ)));}
        center=new Point3D((lo.X+hi.X)/2,(lo.Y+hi.Y)/2,(lo.Z+hi.Z)/2);
        radius=Math.Max(0.01,(hi-lo).Length()*1.7);
    }
    void UpdateCamera()
    {
        var ce=Math.Cos(elevation);
        camera.Position=new Point3D(center.X+radius*ce*Math.Sin(azimuth),center.Y+radius*Math.Sin(elevation),center.Z+radius*ce*Math.Cos(azimuth));
        camera.LookDirection=center-camera.Position;
        camera.NearPlaneDistance=Math.Max(0.001,radius/10000);
        camera.FarPlaneDistance=Math.Max(100,radius*100);
    }
    void BuildGeometry()
    {
        while(scene.Children.Count>3)scene.Children.RemoveAt(3);
        var pts=new Point3DCollection(displayPositions.Select(p=>new Point3D(p.X,p.Y,p.Z)));
        int triCount=0;
        for(int group=0;!rigOnly&&group<model.triangles.Count;group++)
        {
            var indices=model.triangles[group];triCount+=indices.Length/3;
            if(!wireframe)
            {
                var mesh=new MeshGeometry3D { Positions=pts, TriangleIndices=new Int32Collection(indices), TextureCoordinates=new PointCollection(model.uvs.Select(v=>new System.Windows.Point(v.X,1-v.Y))) };
                var mat=new DiffuseMaterial(new SolidColorBrush(Color.FromRgb((byte)(140+group*31%90),(byte)(150+group*17%85),(byte)(165+group*13%70))));
                if(displayNormals.Length==model.positions.Length)mesh.Normals=new Vector3DCollection(displayNormals.Select(n=>new Vector3D(n.X,n.Y,n.Z)));
                var actual=selectedMaterials.TryGetValue(model.slots[group].reference,out var chosen)?chosen:mat;
                scene.Children.Add(new GeometryModel3D(mesh,actual){BackMaterial=actual});
            }
            else
            {
                // Thin triangle-edge strips provide a wireframe-like view in native WPF 3D.
                // Duplicate edges are retained; line width is relative to model size.
                double thickness=Math.Max(radius/2500,0.00001);
                var linePts=new Point3DCollection();var lineIndices=new Int32Collection();
                void Edge(int a,int b)
                {
                    if(a<0||b<0||a>=pts.Count||b>=pts.Count)return;
                    var x=pts[a];var y=pts[b];var d=y-x;
                    if(d.LengthSquared<1e-15)return;
                    var cross=Vector3D.CrossProduct(d,new Vector3D(0,1,0));
                    if(cross.LengthSquared<1e-15)cross=Vector3D.CrossProduct(d,new Vector3D(1,0,0));
                    cross.Normalize();cross*=thickness;
                    int i=linePts.Count;
                    linePts.Add(x+cross);linePts.Add(x-cross);linePts.Add(y+cross);linePts.Add(y-cross);
                    lineIndices.Add(i);lineIndices.Add(i+1);lineIndices.Add(i+2);
                    lineIndices.Add(i+1);lineIndices.Add(i+3);lineIndices.Add(i+2);
                }
                for(int i=0;i+2<indices.Length;i+=3){Edge(indices[i],indices[i+1]);Edge(indices[i+1],indices[i+2]);Edge(indices[i+2],indices[i]);}
                var mesh=new MeshGeometry3D{Positions=linePts,TriangleIndices=lineIndices};
                var mat=new DiffuseMaterial(Brushes.Cyan);
                scene.Children.Add(new GeometryModel3D(mesh,mat){BackMaterial=mat});
            }
        }
        if(showBase&&!rigOnly)scene.Children.Add(characterBase);
        if(showRig)
        {
            double size=Math.Max(radius/180,0.002);
            foreach(var j in joints)
            {
                int jointIndex=joints.IndexOf(j);var jp=poseActive()?posedWorld[jointIndex].Translation:j.Position;
                var p=new Point3D(jp.X,jp.Y,jp.Z);
                Rod(p-new Vector3D(size,0,0),p+new Vector3D(size,0,0),size/3);
                Rod(p-new Vector3D(0,size,0),p+new Vector3D(0,size,0),size/3);
                if(j.Parent>=0){var q=poseActive()?posedWorld[j.Parent].Translation:joints[j.Parent].Position;Rod(p,new Point3D(q.X,q.Y,q.Z),size/4);}
            }
        }
        info.Text=$"{model.positions.Length:N0} vertices · {triCount:N0} triangles · {model.triangles.Count} primitives | Drag: orbit · Wheel: zoom";
    }
    void Rod(Point3D a,Point3D b,double width)
    {
        var d=b-a;if(d.LengthSquared<1e-15)return;
        var x=Vector3D.CrossProduct(d,new Vector3D(0,1,0));if(x.LengthSquared<1e-15)x=Vector3D.CrossProduct(d,new Vector3D(1,0,0));x.Normalize();x*=width;
        var y=Vector3D.CrossProduct(d,x);y.Normalize();y*=width;
        var pts=new Point3DCollection{a-x-y,a+x-y,a+x+y,a-x+y,b-x-y,b+x-y,b+x+y,b-x+y};
        var mesh=new MeshGeometry3D{Positions=pts,TriangleIndices=new Int32Collection{0,1,2,0,2,3,4,6,5,4,7,6,0,4,5,0,5,1,1,5,6,1,6,2,2,6,7,2,7,3,3,7,4,3,4,0}};
        var mat=new EmissiveMaterial(Brushes.Lime);scene.Children.Add(new GeometryModel3D(mesh,mat){BackMaterial=mat});
    }

    bool poseActive()=>native.Weights.Any(w=>w.Sum()>0);
    void ShowReport(string title,string text)
    {
        new Window{Owner=this,Title=title,Width=850,Height=600,Content=new System.Windows.Controls.TextBox{Text=text,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}}.ShowDialog();
    }

}





