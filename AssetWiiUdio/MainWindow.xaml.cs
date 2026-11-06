using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace AssetWiiUdio;
public partial class MainWindow : Window
{
    private string? root;
    private string? current;
    private bool showingExtracted;
    private string? packageRoot;
    private string? filesystemRoot;
    private const int PreviewLimit = 1024;
    private VirtualFst? virtualFst;
    private VirtualContentReader? virtualReader;
    private bool showingVirtual;
    private string virtualDirectory = "";
    private AssetNameResolver nameResolver = AssetNameResolver.Load(Path.Combine(AppContext.BaseDirectory, "asset_names.tsv"));
    private readonly Dictionary<string, List<VirtualEntry>> virtualChildren = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TreeViewItem> virtualTreeNodes = new(StringComparer.Ordinal);
    private sealed record SearchHit(VirtualEntry Entry, string Display);
    private const int MaxSearchResults = 250;
    private void TreeSearchBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateTreeSearch();
    private void UpdateTreeSearch()
    {
        if (TreeSearchResults == null || TreeSearchCount == null || TreeSearchBox == null) return;
        string query = TreeSearchBox.Text.Trim();
        if (query.Length == 0)
        {
            TreeSearchResults.ItemsSource = null;
            TreeSearchResults.Visibility = Visibility.Collapsed;
            TreeSearchCount.Text = "Search the full virtual filesystem";
            return;
        }
        if (!showingVirtual || virtualFst == null)
        {
            TreeSearchResults.ItemsSource = null;
            TreeSearchResults.Visibility = Visibility.Collapsed;
            TreeSearchCount.Text = "Open the virtual filesystem to search its files";
            return;
        }
        // Search FST entries, not just expanded UI nodes. Limit displayed results to keep WPF responsive.
        var matches = virtualFst.Entries.Where(v => !v.IsDirectory &&
            (v.Path.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             nameResolver.Label(v.Path).Contains(query, StringComparison.OrdinalIgnoreCase) ||
             nameResolver.Describe(v.Path).Contains(query, StringComparison.OrdinalIgnoreCase)))
            .Take(MaxSearchResults + 1).ToList();
        bool more = matches.Count > MaxSearchResults;
        TreeSearchResults.ItemsSource = matches.Take(MaxSearchResults)
            .Select(v => new SearchHit(v, nameResolver.Label(v.Path) + "  |  " + v.Path)).ToList();
        TreeSearchResults.Visibility = Visibility.Visible;
        TreeSearchCount.Text = more ? $"First {MaxSearchResults} matches (refine search)" : $"{matches.Count} match(es)";
    }
    private void TreeSearchResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TreeSearchResults.SelectedItem is not SearchHit hit || !showingVirtual) return;
        string path = hit.Entry.Path;
        int slash = path.LastIndexOf('/');
        string parent = slash < 0 ? "" : path[..slash];
        if (!virtualTreeNodes.TryGetValue(parent, out var node))
        {
            Status.Text = "Found file, but its folder is missing from the displayed tree: " + parent;
            return;
        }
        // Expand each ancestor so the target folder becomes visible.
        var ancestry = new Stack<TreeViewItem>();
        for (ItemsControl? cursor = node; cursor is TreeViewItem branch; cursor = ItemsControl.ItemsControlFromItemContainer(branch))
            ancestry.Push(branch);
        while (ancestry.Count > 0) ancestry.Pop().IsExpanded = true;
        FilterBox.Text = ""; // An old folder filter must not hide the selected match.
        node.IsSelected = true;
        node.BringIntoView();
        RefreshFiles();
        foreach (var item in FilesList.Items)
            if (item is VirtualFileRow row && row.Entry.Path == path)
            {
                FilesList.SelectedItem = item;
                FilesList.ScrollIntoView(item);
                break;
            }
        Status.Text = "Located: " + path;
    }

    public sealed record VirtualFileRow(VirtualEntry Entry, string Name)
    {
        public string Extension => Path.GetExtension(Name).TrimStart('.').ToUpperInvariant();
        public string DisplaySize => Entry.Length.ToString("N0");
    }
    public sealed record FileRow(string FullPath)
    {
        public string Name => Path.GetFileName(FullPath);
        public string Extension => Path.GetExtension(FullPath).TrimStart('.').ToUpperInvariant();
        public string DisplaySize { get { try { return new FileInfo(FullPath).Length.ToString("N0"); } catch { return "?"; } } }
    }
    private static readonly string LayoutFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AssetWiiUdio", "window-layout.json");

    private sealed class WindowLayout
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool Maximized { get; set; }
    }

    public MainWindow()
    {
        InitializeComponent();
        FilesList.MouseDoubleClick+=(_,e)=>
        {
            if(ItemsControl.ContainerFromElement(FilesList,e.OriginalSource as DependencyObject) is not System.Windows.Controls.ListViewItem item)return;
            string name=item.DataContext is VirtualFileRow v?v.Entry.Path:item.DataContext is FileRow f?f.FullPath:"";
            if(new[]{".oct",".bent",".mtb"}.Contains(Path.GetExtension(name).ToLowerInvariant()))
            {e.Handled=true;item.IsSelected=true;AssetInspector_Click(this,new RoutedEventArgs());}
        };
        SourceInitialized += (_, _) => RestoreWindowLayout();
        Closing += SaveWindowLayout;
    }

    private void RestoreWindowLayout()
    {
        try
        {
            if (!File.Exists(LayoutFile)) return;
            var layout = JsonSerializer.Deserialize<WindowLayout>(File.ReadAllText(LayoutFile));
            if (layout == null || !double.IsFinite(layout.Left) || !double.IsFinite(layout.Top) ||
                !double.IsFinite(layout.Width) || !double.IsFinite(layout.Height)) return;
            if (layout.Width < MinWidth || layout.Height < MinHeight ||
                layout.Width > 10000 || layout.Height > 10000) return;

            // Ensure at least a usable portion remains visible if monitors change.
            double screenLeft = SystemParameters.VirtualScreenLeft;
            double screenTop = SystemParameters.VirtualScreenTop;
            double screenRight = screenLeft + SystemParameters.VirtualScreenWidth;
            double screenBottom = screenTop + SystemParameters.VirtualScreenHeight;
            if (layout.Left + layout.Width < screenLeft + 80 || layout.Left > screenRight - 80 ||
                layout.Top + 40 < screenTop || layout.Top > screenBottom - 80)
                return; // Keep the default centered window placement.

            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = layout.Left;
            Top = layout.Top;
            Width = layout.Width;
            Height = layout.Height;
            // Always start restored, even when the previous session closed maximized.
            WindowState = WindowState.Normal;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("AssetWiiUdio: could not restore window layout: " + ex.Message);
        }
    }

    private void SaveWindowLayout(object? sender, CancelEventArgs e)
    {
        try
        {
            // RestoreBounds records the normal position even while maximized.
            Rect bounds = WindowState == WindowState.Normal
                ? new Rect(Left, Top, Width, Height)
                : RestoreBounds;
            if (!double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) ||
                !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height) ||
                bounds.Width < MinWidth || bounds.Height < MinHeight) return;
            Directory.CreateDirectory(Path.GetDirectoryName(LayoutFile)!);
            var layout = new WindowLayout
            {
                Left = bounds.Left, Top = bounds.Top,
                Width = bounds.Width, Height = bounds.Height,
                Maximized = false // Startup should never restore maximized state
            };
            File.WriteAllText(LayoutFile, JsonSerializer.Serialize(layout, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Debug.WriteLine("AssetWiiUdio: could not save window layout: " + ex.Message);
        }
    }
    private static string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
    private void ImportNames_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "Import AssetWiiUdio name mapping TSV", Filter = "TSV mapping (*.tsv)|*.tsv" };
        if (picker.ShowDialog() != true) return;
        try { nameResolver = AssetNameResolver.Load(picker.FileName); if (showingVirtual) BuildVirtualTree(); UpdateTreeSearch(); Status.Text = $"Loaded {nameResolver.Count:N0} name routes"; }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Name mapping error"); }
    }
    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var path = PickFolder("Select Wii U NUS folder containing title.tmd and .app files");
        if (path != null)
        {
            if (!NusPackageCheck.IsValid(path, out string report))
            {
                MessageBox.Show(report, "NUS package file check", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            OpenFolder(path, false);
        }
    }
    private void OpenExtracted_Click(object sender, RoutedEventArgs e)
    {
        var path = PickFolder("Select previously extracted code/content/meta directory");
        if (path != null) OpenFolder(path, true);
    }
    private void OpenFolder(string path, bool extracted)
    {
        root = path; showingExtracted = extracted; showingVirtual = false; current = path;
        if (extracted) filesystemRoot = path; else packageRoot = path;
        FolderTree.Items.Clear();
        UpdateTreeSearch();
        var node = MakeNode(path);
        FolderTree.Items.Add(node); node.IsExpanded = false; node.IsSelected = true;
        RefreshFiles();
        var tmd = Path.Combine(path, "title.tmd");
        var apps = Directory.GetFiles(path, "*.app", SearchOption.TopDirectoryOnly).Length;
        Status.Text = extracted ? $"Extracted folder · {path}" : $"NUS package · {apps} .app files · title.tmd {(File.Exists(tmd) ? "found" : "missing")} · {path}";
    }
    private TreeViewItem MakeNode(string path)
    {
        var node = new TreeViewItem { Header = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) ?? path, Tag = path, Foreground = System.Windows.Media.Brushes.White };
        try { foreach (var dir in Directory.EnumerateDirectories(path).OrderBy(x => x)) node.Items.Add(MakeNode(dir)); }
        catch (UnauthorizedAccessException) { }
        return node;
    }
    private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (FolderTree.SelectedItem is TreeViewItem item && item.Tag is string dir)
        {
            if (showingVirtual) virtualDirectory = dir; else current = dir;
            RefreshFiles();
        }
    }
    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) { if (FilesList != null) RefreshFiles(); }
    private void RefreshFiles()
    {
        if (showingVirtual)
        {
            string filter = FilterBox?.Text ?? "";
            FilesList.ItemsSource = virtualChildren.GetValueOrDefault(virtualDirectory, new List<VirtualEntry>())
                .Where(x => !x.IsDirectory && (x.Path.Contains(filter, StringComparison.OrdinalIgnoreCase) || nameResolver.Label(x.Path).Contains(filter, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(x => x.Path).Select(x => new VirtualFileRow(x, nameResolver.Label(x.Path))).ToList();
            return;
        }
        if (current == null) return;
        try
        {
            var filter = FilterBox?.Text ?? "";
            FilesList.ItemsSource = Directory.EnumerateFiles(current).Where(p => Path.GetFileName(p).Contains(filter, StringComparison.OrdinalIgnoreCase)).OrderBy(p => p).Select(p => new FileRow(p)).ToList();
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
    private void FilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FilesList.SelectedItem is VirtualFileRow virtualRow)
        {
            PreviewTitle.Text = virtualRow.Entry.Path + " · " + virtualRow.DisplaySize + " bytes";
            try { PreviewAsset(virtualRow.Entry.Path, virtualReader!.Read(virtualRow.Entry, PreviewLimit), "Nintendo Wii U content (AES-CBC; H0 verified for hashed clusters)");
                PreviewText.Text = "NAME MAPPING (PS4 candidate): " + nameResolver.Describe(virtualRow.Entry.Path) + "\n\n" + PreviewText.Text; }
            catch (Exception ex) { PreviewText.Text = "Preview unavailable: " + ex.Message; }
            return;
        }
        if (FilesList.SelectedItem is not FileRow row) return;
        PreviewTitle.Text = row.Name + " · " + row.DisplaySize + " bytes";
        try
        {
            using var stream = File.Open(row.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var bytes = new byte[Math.Min(PreviewLimit, (int)Math.Min(stream.Length, PreviewLimit))];
            var read = stream.Read(bytes, 0, bytes.Length);
            var sb = new StringBuilder();
            for (int i = 0; i < read; i += 16)
            {
                sb.Append($"{i:X8}  ");
                for (int j = 0; j < 16; j++) sb.Append(i + j < read ? $"{bytes[i + j]:X2} " : "   ");
                sb.Append(" | ");
                for (int j = i; j < Math.Min(i + 16, read); j++) sb.Append(bytes[j] is >= 32 and <= 126 ? (char)bytes[j] : '.');
                sb.AppendLine();
            }
            PreviewAsset(row.FullPath, File.ReadAllBytes(row.FullPath).Take(PreviewLimit).ToArray(), "Local file; encryption not established");
        }
        catch (Exception ex) { PreviewText.Text = "Cannot preview: " + ex.Message; }
    }
    private void PreviewAsset(string filename, byte[] bytes, string outerLayer)
    {
        var result = DisneyInfinityAsset.Analyze(filename, bytes);
        PreviewText.Text = $"OUTER LAYER: {outerLayer}\n" +
            $"INNER ENCRYPTION: {result.Encryption}\n" +
            $"DETECTED FORMAT: {result.Format}\n" +
            $"CONFIDENCE: {(result.Confirmed ? "Recognized header" : "Unconfirmed")}\n" +
            $"DETAILS: {result.Details}\n\n" +
            DisneyInfinityAsset.DescribeContainer(result) + "\n\n" + FormatHex(result.Bytes);
    }
    private void AssetInspector_Click(object sender, RoutedEventArgs e)
    {
        string? filename = null;
        byte[]? bytes = null;
        try
        {
            if (FilesList.SelectedItem is VirtualFileRow v)
            {
                filename = v.Entry.Path;
                if (v.Entry.Length > 64 * 1024 * 1024) { MessageBox.Show("Asset Inspector limit is 64 MiB per file."); return; }
                bytes = virtualReader!.Read(v.Entry, (int)v.Entry.Length);
            }
            else if (FilesList.SelectedItem is FileRow f)
            {
                filename = f.FullPath;
                if (new FileInfo(filename).Length > 64 * 1024 * 1024) { MessageBox.Show("Asset Inspector limit is 64 MiB per file."); return; }
                bytes = File.ReadAllBytes(filename);
            }
            if (filename == null || bytes == null) { MessageBox.Show("Select a file first."); return; }
            if(MaterialBundleReader.Recognizes(bytes)){new MtbInspectorWindow(filename,bytes){Owner=this}.ShowDialog();return;}
            if(new[]{".oct",".bent"}.Contains(Path.GetExtension(filename).ToLowerInvariant())){new SceneInspectorWindow(filename,bytes){Owner=this}.ShowDialog();return;}
            var result = DisneyInfinityAsset.Analyze(filename, bytes);
            var report = $"File: {filename}\nSize: {bytes.Length:N0}\n" +
                $"Additional encryption: {result.Encryption}\nFormat: {result.Format}\n" +
                $"Details: {result.Details}\n\n{DisneyInfinityAsset.DescribeContainer(result)}";
            var win = new Window { Title = "Disney Infinity Asset Inspector", Owner = this, Width = 920, Height = 650,
                Background = System.Windows.Media.Brushes.Black };
            var dock = new DockPanel();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var export = new Button { Content = "Export analyzed bytes", Margin = new Thickness(8) };
            export.Click += (_, _) =>
            {
                var picker = new SaveFileDialog { FileName = Path.GetFileName(filename) + (result.Confirmed && result.Layer.Contains("second stage") ? ".decoded" : ".bin") };
                if (picker.ShowDialog(win) == true) File.WriteAllBytes(picker.FileName, result.Bytes);
            };
            buttons.Children.Add(export);
            DockPanel.SetDock(buttons, Dock.Top); dock.Children.Add(buttons);
            dock.Children.Add(new TextBox { Text = report + "\n\n" + FormatHex(result.Bytes.Take(2048).ToArray()),
                IsReadOnly = true, FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                Foreground = System.Windows.Media.Brushes.LightGreen, Background = System.Windows.Media.Brushes.Black,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            win.Content = dock; win.ShowDialog();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Asset Inspector"); }
    }
    private void ArchiveBrowser_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string filename;
            byte[] raw;
            if (FilesList.SelectedItem is VirtualFileRow v)
            {
                filename = v.Entry.Path;
                if (v.Entry.Length > 128 * 1024 * 1024) throw new NotSupportedException("Archive browser limit is 128 MiB.");
                raw = virtualReader!.Read(v.Entry, checked((int)v.Entry.Length));
            }
            else if (FilesList.SelectedItem is FileRow f)
            {
                filename = f.FullPath;
                if (new FileInfo(filename).Length > 128 * 1024 * 1024) throw new NotSupportedException("Archive browser limit is 128 MiB.");
                raw = File.ReadAllBytes(filename);
            }
            else { MessageBox.Show("Select an archive file first."); return; }
            if(FilesList.SelectedItem is VirtualFileRow && virtualFst!=null && virtualReader!=null)
            {
                var reader=virtualReader;var entries=virtualFst.Entries.Where(e=>!e.IsDirectory).ToArray();string package=packageRoot??"Wii U package";
                var textureBuckets=new VirtualTextureBuckets(reader,entries);
                ExternalGameAssets.VirtualRead=logical=>
                {
                    string route=ExternalGameAssets.HashRoute(logical).Replace('\\','/');
                    var matches=entries.Where(e=>e.Path.Replace('\\','/').EndsWith("/assets/"+route,StringComparison.OrdinalIgnoreCase)||e.Path.Replace('\\','/').EndsWith("/assets/"+logical,StringComparison.OrdinalIgnoreCase)).ToArray();
                    if(matches.Length!=1)return null;var entry=matches[0];if(entry.Length>128*1024*1024)throw new NotSupportedException("Referenced asset exceeds preview limit.");
                    return (reader.Read(entry,(int)entry.Length),package+" :: "+entry.Path);
                };
                ExternalGameAssets.VirtualTextureRead=logical=>
                {
                    string id=Path.GetFileNameWithoutExtension(logical);
                    if(id.Length!=16||!logical.EndsWith(".tstream",StringComparison.OrdinalIgnoreCase))return null;
                    textureBuckets.Resolve("textures/"+id[..2]+".zip");
                    foreach(var bucket in textureBuckets.Candidates(id[..2]))
                    {
                        var archive=DisneyInfinityArchive.Open(bucket.Path,reader.Read(bucket,(int)bucket.Length));
                        var members=archive.Members.Where(m=>m.Name.Replace('\\','/').Equals(logical,StringComparison.OrdinalIgnoreCase)).ToArray();
                        if(members.Length==1)return (DisneyInfinityArchive.Extract(archive,members[0]),package+" :: "+bucket.Path+" :: "+members[0].Name);
                    }
                    return null;
                };
            }
            else {ExternalGameAssets.VirtualRead=null;ExternalGameAssets.VirtualTextureRead=null;}
            try {ArchiveBrowserWindow.Show(this, filename, raw);}finally{ExternalGameAssets.VirtualRead=null;ExternalGameAssets.VirtualTextureRead=null;}

        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Archive Browser"); }
    }

    private void ShowPackage_Click(object sender, RoutedEventArgs e)
    {
        if (packageRoot != null) OpenFolder(packageRoot, false);
        else MessageBox.Show("Open a NUS package first.");
    }
    private void ShowFilesystem_Click(object sender, RoutedEventArgs e)
    {
        if (virtualFst != null) { BuildVirtualTree(); return; }
        if (filesystemRoot != null && Directory.Exists(filesystemRoot)) OpenFolder(filesystemRoot, true);
        else MessageBox.Show("Open Virtual FST first, or open a previously extracted game directory.", "Filesystem View");
    }
    private void OpenVirtual_Click(object sender, RoutedEventArgs e)
    {
        if (packageRoot == null) { MessageBox.Show("Open a NUS package first."); return; }
        if (!NusPackageCheck.IsValid(packageRoot, out string packageReport))
        {
            MessageBox.Show(packageReport, "NUS package file check", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            virtualFst = VirtualFst.Open(packageRoot, "");
            virtualReader = new VirtualContentReader(packageRoot, virtualFst.ContentKey);
            BuildVirtualTree();
            Status.Text = $"Ticket-derived key verified · {virtualFst.Entries.Count:N0} entries · ready for preview/export";
        }
        catch (Exception ex)
        {
            MessageBox.Show("Automatic ticket decryption failed.\n\n" + ex.Message +
                "\n\nCheck that title.tik, title.tmd and .app files belong to the same package, and that the correct common key file is configured.",
                "Cannot open virtual filesystem", MessageBoxButton.OK, MessageBoxImage.Warning);
            Status.Text = "Ticket decryption failed — check common key and package files";
        }
    }
    private void CheckPackage_Click(object sender, RoutedEventArgs e)
    {
        string? dir = packageRoot;
        if (dir == null)
        {
            dir = PickFolder("Select Wii U NUS package folder to check");
            if (dir == null) return;
        }
        bool valid = NusPackageCheck.IsValid(dir, out string report);
        MessageBox.Show(report, valid ? "NUS package check — OK" : "NUS package check — problems found",
            MessageBoxButton.OK, valid ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void FileDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (FilesList.SelectedItem is not VirtualFileRow selected)
        {
            MessageBox.Show("Open the virtual filesystem and select a file first.", "File diagnostics");
            return;
        }
        var v = selected.Entry;
        long raw = (v.Flags & 4) != 0 ? v.Offset : v.Offset / 0x20;
        string report = $"FST FILE DIAGNOSTICS (read only)\n\nPath: {v.Path}\n" +
            $"Name mapping: {nameResolver.Describe(v.Path)}\n" +
            $"Content index: {v.ContentIndex}\nFlags: 0x{v.Flags:X4}\n" +
            $"Raw FST offset field (derived): 0x{raw:X} ({raw})\n" +
            $"Offset units: {((v.Flags & 4) != 0 ? "bytes" : "0x20-byte units")}\n" +
            $"Calculated logical offset: 0x{v.Offset:X} ({v.Offset})\n" +
            $"File length: {v.Length:N0} bytes\n\n" +
            "For hashed contents, logical offsets are mapped across 0xFC00-byte data regions.\n" +
            "No decrypted bytes or keys are included.";
        var win = new Window { Title = "File Diagnostics", Owner = this, Width = 720, Height = 460, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new DockPanel();
        var copy = new Button { Content = "Copy Report", Padding = new Thickness(12, 6, 12, 6), HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        copy.Click += (_, _) => System.Windows.Clipboard.SetText(report);
        DockPanel.SetDock(copy, Dock.Bottom);
        panel.Children.Add(copy);
        panel.Children.Add(new TextBox { Text = report, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new System.Windows.Media.FontFamily("Consolas") });
        win.Content = panel; win.ShowDialog();
    }

    private void ConfigureCommonKey_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "Select your Wii U common key file (16 binary bytes or 32 hex characters)", Filter = "Key files (*.bin;*.key;*.txt)|*.bin;*.key;*.txt|All files (*.*)|*.*" };
        if (picker.ShowDialog() != true) return;
        var result = MessageBox.Show("Is this a DEVELOPMENT common key?\n\nYes = development, No = retail", "Common key type", MessageBoxButton.YesNoCancel);
        if (result == MessageBoxResult.Cancel) return;
        try
        {
            TicketKeyResolver.SetCommonKeyFile(picker.FileName, result == MessageBoxResult.Yes);
            Status.Text = "Common key file location saved · " + TicketKeyResolver.KeyFileStatus();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Common key configuration"); }
    }
    private static string FormatHex(byte[] bytes)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < bytes.Length; i += 16)
        {
            sb.Append($"{i:X8}  ");
            for (int j = 0; j < 16; j++) sb.Append(i + j < bytes.Length ? $"{bytes[i + j]:X2} " : "   ");
            sb.Append(" | ");
            for (int j = i; j < Math.Min(i + 16, bytes.Length); j++) sb.Append(bytes[j] is >= 32 and <= 126 ? (char)bytes[j] : '.');
            sb.AppendLine();
        }
        return sb.ToString();
    }
    private void BuildVirtualTree()
    {
        if (virtualFst == null) return;
        showingVirtual = true;
        virtualChildren.Clear();
        virtualTreeNodes.Clear();
        var directories = new Dictionary<string, TreeViewItem>(StringComparer.Ordinal);
        FolderTree.Items.Clear();
        var rootNode = new TreeViewItem { Header = "Virtual Game Filesystem", Tag = "", Foreground = System.Windows.Media.Brushes.White };
        directories[""] = rootNode;
        virtualTreeNodes[""] = rootNode;
        FolderTree.Items.Add(rootNode);
        // Only directories become tree nodes; files are shown in the right-hand list.
        foreach (var entry in virtualFst.Entries)
        {
            int slash = entry.Path.LastIndexOf('/');
            string parent = slash < 0 ? "" : entry.Path[..slash];
            if (!virtualChildren.TryGetValue(parent, out var children)) virtualChildren[parent] = children = new List<VirtualEntry>();
            children.Add(entry);
            if (!entry.IsDirectory) continue;
            if (!directories.TryGetValue(parent, out var parentNode)) continue;
            var node = new TreeViewItem { Header = nameResolver.Label(entry.Path), Tag = entry.Path, Foreground = System.Windows.Media.Brushes.White };
            directories[entry.Path] = node;
            virtualTreeNodes[entry.Path] = node;
            parentNode.Items.Add(node);
        }
        rootNode.IsExpanded = false;
        virtualDirectory = "";
        rootNode.IsSelected = true;
        RefreshFiles();
        UpdateTreeSearch();
    }
    private void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (packageRoot == null) { MessageBox.Show("Open a NUS package first."); return; }
        var report = FstDiagnostics.Run(packageRoot, "");
        ShowReport("FST Diagnostics (ticket-based)", report, "AssetWiiUdio_FST_Diagnostics.txt");
    }
    private void HashedDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (packageRoot == null || virtualFst == null) { MessageBox.Show("Open a NUS package and its virtual filesystem first."); return; }
        var row = FilesList.SelectedItem as VirtualFileRow;
        var entry = row?.Entry ?? virtualFst.Entries.FirstOrDefault(v => !v.IsDirectory && v.Path == "content/assets/02122eb3/e7e35fa1");
        if (entry == null) { MessageBox.Show("Select a virtual file to diagnose."); return; }
        try
        {
            string report = HashedContentDiagnostics.Run(packageRoot, virtualFst.ContentKey, entry);
            ShowReport("Hashed Content Diagnostics", report, "AssetWiiUdio_Hashed_Diagnostics.txt");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Hashed Diagnostics"); }
    }
    private void Inspector_Click(object sender, RoutedEventArgs e)
    {
        if (packageRoot == null) { MessageBox.Show("Open a NUS package first."); return; }
        try
        {
            var tabs = MetadataInspector.Build(packageRoot);
            var window = new Window { Title = "Wii U Metadata Inspector", Owner = this, Width = 980, Height = 720,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = System.Windows.Media.Brushes.Black };
            var dock = new DockPanel();
            var save = new Button { Content = "Save selected tab", Padding = new Thickness(12,7,12,7), Margin = new Thickness(8) };
            var tabControl = new TabControl();
            foreach (var item in tabs)
                tabControl.Items.Add(new TabItem { Header = item.Key, Content = new TextBox { Text = item.Value, IsReadOnly = true,
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"), Foreground = System.Windows.Media.Brushes.LightGreen,
                    Background = System.Windows.Media.Brushes.Black, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.NoWrap } });
            save.Click += (_, _) =>
            {
                if (tabControl.SelectedItem is not TabItem selected || selected.Content is not TextBox content) return;
                var picker = new SaveFileDialog { Filter = "Text report (*.txt)|*.txt", FileName = "AssetWiiUdio_" + selected.Header + ".txt" };
                if (picker.ShowDialog(window) == true) File.WriteAllText(picker.FileName, content.Text);
            };
            DockPanel.SetDock(save, Dock.Top); dock.Children.Add(save); dock.Children.Add(tabControl);
            window.Content = dock; window.ShowDialog();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Metadata Inspector"); }
    }
    private void ShowReport(string title, string report, string filename)
    {
        var window = new Window { Title = title, Owner = this, Width = 950, Height = 690, Background = System.Windows.Media.Brushes.Black };
        var container = new DockPanel();
        var save = new Button { Content = "Save report", Padding = new Thickness(12,7,12,7), Margin = new Thickness(8) };
        save.Click += (_, _) => { var picker = new SaveFileDialog { Filter = "Text report (*.txt)|*.txt", FileName = filename };
            if (picker.ShowDialog(window) == true) File.WriteAllText(picker.FileName, report); };
        DockPanel.SetDock(save, Dock.Top); container.Children.Add(save);
        container.Children.Add(new TextBox { Text = report, IsReadOnly = true, FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            Foreground = System.Windows.Media.Brushes.LightGreen, Background = System.Windows.Media.Brushes.Black,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });
        window.Content = container; window.ShowDialog();
    }
    private void ExtractRaw_Click(object sender, RoutedEventArgs e)
    {
        if (FilesList.SelectedItems.Count == 0) { MessageBox.Show("Select one or more files first."); return; }
        var dest = PickFolder("Choose destination for raw file copies"); if (dest == null) return;
        if (showingVirtual)
        {
            var selected = FilesList.SelectedItems.Cast<VirtualFileRow>().ToList();
            if (selected.Count == 0) return;
            int completed = 0;
            foreach (var row in selected)
            {
                try
                {
                    string target = Path.Combine(dest, row.Entry.Path.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(target) && MessageBox.Show($"Overwrite {target}?", "Confirm", MessageBoxButton.YesNo) != MessageBoxResult.Yes) continue;
                    virtualReader!.Extract(row.Entry, target);
                    completed++;
                }
                catch (Exception ex) { MessageBox.Show($"{row.Entry.Path}\n\n{ex.Message}", "Selective extraction unavailable"); }
            }
            Status.Text = $"Extracted {completed} of {selected.Count} selected virtual files";
            return;
        }
        var rows = FilesList.SelectedItems.Cast<FileRow>().ToList();
        try
        {
            foreach (var row in rows)
            {
                var target = Path.Combine(dest, row.Name);
                if (File.Exists(target) && MessageBox.Show($"Overwrite {target}?", "Confirm", MessageBoxButton.YesNo) != MessageBoxResult.Yes) continue;
                File.Copy(row.FullPath, target, true);
            }
            Status.Text = showingExtracted ? $"Exported {rows.Count} extracted files" : $"Copied {rows.Count} encrypted/raw package files";
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Copy failed"); }
    }
    private async void ExtractAll_Click(object sender, RoutedEventArgs e)
    {
        if (root == null || showingExtracted) { MessageBox.Show("Open a NUS package folder first."); return; }
        if (!File.Exists(Path.Combine(root, "title.tmd"))) { MessageBox.Show("title.tmd not found in selected folder."); return; }
        var exe = Path.Combine(AppContext.BaseDirectory, "cdecrypt.exe");
        if (!File.Exists(exe)) { MessageBox.Show("Place a trusted, locally compiled cdecrypt.exe beside AssetWiiUdio.exe. See README for build instructions."); return; }
        var dest = PickFolder("Choose an EMPTY destination for decrypted game files"); if (dest == null) return;
        if (Directory.EnumerateFileSystemEntries(dest).Any()) { MessageBox.Show("Please choose an empty output folder to avoid overwriting existing files."); return; }
        try
        {
            Status.Text = "CDecrypt running...";
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
            start.ArgumentList.Add(root); start.ArgumentList.Add(dest);
            using var process = new Process { StartInfo = start };
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var log = (await stdoutTask) + "\n" + (await stderrTask);
            if (process.ExitCode == 0)
            {
                filesystemRoot = dest;
                Status.Text = "CDecrypt finished · Filesystem View is now available";
                OpenFolder(dest, true);
            }
            else Status.Text = $"CDecrypt exited with code {process.ExitCode}";
            MessageBox.Show(log.Length > 9000 ? log[..9000] : log, "CDecrypt output");
        }
        catch (Exception ex) { Status.Text = "Extraction failed"; MessageBox.Show(ex.Message); }
    }
    private void About_Click(object sender, RoutedEventArgs e) => MessageBox.Show("AssetWiiUdio v0.5.6\n\nCurrent: NUS package file browsing, raw hex preview, raw file copy, CDecrypt full-extraction launch, extracted filesystem browsing.\n\nVirtual FST browsing, non-hashed file preview, and selective extraction are available. Hashed content requires additional support.\n\nTitle manifest inspection and exporting selected extracted files are supported.\n\nOriginal game files are not modified.", "About AssetWiiUdio");
}
