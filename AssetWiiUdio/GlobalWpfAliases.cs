// Prefer WPF UI types when Windows Forms is enabled for FolderBrowserDialog.
// The Windows Forms folder picker remains explicitly qualified at its call sites.
global using Application = System.Windows.Application;
global using ListView = System.Windows.Controls.ListView;
global using TextBox = System.Windows.Controls.TextBox;
global using Brushes = System.Windows.Media.Brushes;
global using Color = System.Windows.Media.Color;
global using Orientation = System.Windows.Controls.Orientation;
global using Button = System.Windows.Controls.Button;
global using SelectionMode = System.Windows.Controls.SelectionMode;
global using MessageBox = System.Windows.MessageBox;
global using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
global using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
global using TabControl = System.Windows.Controls.TabControl;
