using System;
using System.IO;
using System.Linq;
using System.Text;

namespace AssetWiiUdio;

public static class NusPackageCheck
{
    public static bool IsValid(string folder, out string report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("NUS PACKAGE FILE CHECK (read only)");
        sb.AppendLine("Folder: " + folder);
        sb.AppendLine();
        if (!Directory.Exists(folder))
        {
            report = sb.AppendLine("ERROR: Folder does not exist.").ToString();
            return false;
        }
        string[] required = { "title.tmd", "title.tik" };
        bool ok = true;
        foreach (string filename in required)
        {
            string file = Path.Combine(folder, filename);
            bool exists = File.Exists(file);
            ok &= exists;
            sb.AppendLine($"{(exists ? "OK" : "MISSING")}: {filename}" +
                (exists ? $" ({new FileInfo(file).Length:N0} bytes)" : ""));
        }
        string[] apps = Directory.GetFiles(folder, "*.app", SearchOption.TopDirectoryOnly);
        sb.AppendLine($"{(apps.Length > 0 ? "OK" : "MISSING")}: .app files: {apps.Length}");
        ok &= apps.Length > 0;
        if (File.Exists(Path.Combine(folder, "title.tmd")))
        {
            try
            {
                var manifest = TitleManifest.Read(Path.Combine(folder, "title.tmd"));
                sb.AppendLine($"TMD parsed: {manifest.Contents.Count} content records");
                int missing = 0;
                foreach (var content in manifest.Contents)
                {
                    string app = Path.Combine(folder, content.Id + ".app");
                    if (!File.Exists(app))
                    {
                        missing++;
                        if (missing <= 15) sb.AppendLine("MISSING content: " + content.Id + ".app");
                    }
                }
                if (missing > 15) sb.AppendLine($"...and {missing - 15} additional missing content files.");
                if (missing > 0) ok = false;
                else sb.AppendLine("OK: All TMD-listed .app files are present.");
            }
            catch (Exception ex)
            {
                ok = false;
                sb.AppendLine("ERROR: Could not parse title.tmd: " + ex.Message);
            }
        }
        sb.AppendLine();
        if (!ok)
        {
            sb.AppendLine("Select the original NUS package directory, not its Exports subfolder.");
            sb.AppendLine("No files were modified. Ticket decryption was not attempted.");
        }
        else sb.AppendLine("Required files found. This does not independently verify keys or decryption.");
        report = sb.ToString();
        return ok;
    }
}
