using System;
using System.Collections.Generic;
using System.IO;

namespace bSideDesk
{
    // Prepare every file before touching the installation; retain originals until
    // all replacements (including the version marker) have succeeded.
    public static class UpdateInstaller
    {
        class Entry
        {
            public string Target, Staged, Backup;
            public bool Saved, Installed;
        }

        public static void Apply(string source, string appDir, string exe, string marker, string version)
        {
            var entries = new List<Entry>();
            var suffix = ".update-" + Guid.NewGuid().ToString("N");
            try
            {
                if (!File.Exists(Path.Combine(source, Path.GetFileName(exe))))
                    throw new IOException("archive incomplète");
                // Install the executable last, after all supporting files.
                var sources = new List<string>(Directory.GetFiles(source));
                sources.Sort((a, b) => a == b ? 0 : string.Equals(Path.GetFileName(a), Path.GetFileName(exe), StringComparison.OrdinalIgnoreCase) ? 1 :
                    string.Equals(Path.GetFileName(b), Path.GetFileName(exe), StringComparison.OrdinalIgnoreCase) ? -1 : string.CompareOrdinal(a, b));
                foreach (var file in sources)
                {
                    var target = Path.Combine(appDir, Path.GetFileName(file));
                    var entry = new Entry { Target = target, Staged = target + suffix + ".new",
                        Backup = string.Equals(target, exe, StringComparison.OrdinalIgnoreCase) ? exe + ".old" : target + suffix + ".old" };
                    entries.Add(entry);
                    File.Copy(file, entry.Staged, false);
                }
                var flag = new Entry { Target = marker, Staged = marker + suffix + ".new", Backup = marker + suffix + ".old" };
                entries.Add(flag);
                File.WriteAllText(flag.Staged, version);
                // A previous running executable may have left this backup behind.
                if (File.Exists(exe + ".old")) File.Delete(exe + ".old");
                foreach (var entry in entries)
                {
                    if (File.Exists(entry.Target)) { File.Move(entry.Target, entry.Backup); entry.Saved = true; }
                    File.Move(entry.Staged, entry.Target);
                    entry.Installed = true;
                }
            }
            catch (Exception failure)
            {
                var errors = new List<Exception> { failure };
                for (int i = entries.Count - 1; i >= 0; i--)
                {
                    var entry = entries[i];
                    try
                    {
                        if (entry.Installed) File.Delete(entry.Target);
                        if (entry.Saved) File.Move(entry.Backup, entry.Target);
                    }
                    catch (Exception rollback) { errors.Add(new IOException("Restauration impossible : " + entry.Target + "; sauvegarde : " + entry.Backup, rollback)); }
                }
                if (errors.Count > 1) throw new AggregateException("Mise à jour et restauration incomplètes", errors);
                throw;
            }
            finally
            {
                foreach (var entry in entries) { try { File.Delete(entry.Staged); } catch { } }
            }
            // The old running exe can remain locked until this process exits.
            foreach (var entry in entries) { try { if (entry.Saved) File.Delete(entry.Backup); } catch { } }
        }
    }
}
