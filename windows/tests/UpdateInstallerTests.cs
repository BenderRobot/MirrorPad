using System;
using System.IO;
using bSideDesk;

class UpdateInstallerTests
{
    static void Equal(string expected, string path)
    {
        if (File.ReadAllText(path) != expected) throw new Exception("Unexpected contents: " + path);
    }

    static void Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "bsidedesk-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "source"), app = Path.Combine(root, "app");
            Directory.CreateDirectory(source); Directory.CreateDirectory(app);
            string exe = Path.Combine(app, "bSideDesk.exe"), marker = Path.Combine(root, "updated.txt");
            string support = Path.Combine(app, "version.txt"), added = Path.Combine(app, "new.txt");
            File.WriteAllText(exe, "old exe"); File.WriteAllText(support, "old version");
            File.WriteAllText(Path.Combine(source, "bSideDesk.exe"), "new exe");
            File.WriteAllText(Path.Combine(source, "version.txt"), "new version");
            File.WriteAllText(Path.Combine(source, "new.txt"), "new file");

            // Fail on the last move, after the exe and support files were replaced.
            Directory.CreateDirectory(marker);
            try { UpdateInstaller.Apply(source, app, exe, marker, "2.0"); throw new Exception("Expected failure"); }
            catch (IOException) { }
            Equal("old exe", exe); Equal("old version", support);
            if (File.Exists(added)) throw new Exception("New file survived rollback");
            Directory.Delete(marker);

            // Windows: a locked running exe must leave supporting files intact.
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            using (var locked = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { UpdateInstaller.Apply(source, app, exe, marker, "2.0"); throw new Exception("Expected locked-exe failure"); }
                catch (IOException) { }
                Equal("old exe", exe); Equal("old version", support);
                if (File.Exists(added)) throw new Exception("New file survived rollback");
            }

            File.WriteAllText(marker, "previous marker");
            UpdateInstaller.Apply(source, app, exe, marker, "2.0");
            Equal("new exe", exe); Equal("new version", support); Equal("2.0", marker); Equal("new file", added);

            File.Delete(Path.Combine(source, "bSideDesk.exe"));
            try { UpdateInstaller.Apply(source, app, exe, marker, "3.0"); throw new Exception("Expected incomplete-archive failure"); }
            catch (IOException) { }
            Equal("new exe", exe); Equal("2.0", marker);
            if (Directory.GetFiles(app, "*.update-*").Length != 0) throw new Exception("Staging files leaked");
            Console.WriteLine("PASS: success, rollback after exe replacement, locked exe (Windows), incomplete archive");
        }
        finally { Directory.Delete(root, true); }
    }
}
