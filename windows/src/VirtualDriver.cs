// Définitions de l'écran virtuel : ajoute à C:\VirtualDisplayDriver\vdd_settings.xml une taille demandée par l'iPad
// (zone au-dessus de la Touch Bar) puis redémarre le pilote pour qu'il la propose. Nécessite le mode administrateur.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml;
using Microsoft.Win32;

namespace MirrorPad
{
    public static class VirtualDriver
    {
        const string Settings = @"C:\VirtualDisplayDriver\vdd_settings.xml";

        // true si la définition a été ajoutée et le pilote redémarré
        public static bool AddMode(int w, int h)
        {
            try
            {
                if (!File.Exists(Settings)) { Log.Write("écran virtuel : " + Settings + " introuvable"); return false; }
                var doc = new XmlDocument(); doc.Load(Settings);
                var res = doc.SelectSingleNode("/vdd_settings/resolutions");
                if (res == null) return false;
                bool has = res.SelectNodes("resolution").Cast<XmlNode>().Any(n =>
                    (n.SelectSingleNode("width") ?? n).InnerText.Trim() == w.ToString() && (n.SelectSingleNode("height") ?? n).InnerText.Trim() == h.ToString());
                if (!has)
                {
                    var r = doc.CreateElement("resolution");
                    foreach (var kv in new[] { new[] { "width", w.ToString() }, new[] { "height", h.ToString() }, new[] { "refresh_rate", "30" }, new[] { "refresh_rate", "60" } })
                    { var e = doc.CreateElement(kv[0]); e.InnerText = kv[1]; r.AppendChild(e); }
                    res.AppendChild(r);
                    doc.Save(Settings);
                    Log.Write("écran virtuel : définition " + w + "x" + h + " ajoutée à " + Settings);
                }
                var id = Instance();
                if (id == null) { Log.Write("écran virtuel : pilote introuvable dans le registre"); return false; }
                // le pilote ne relit ses définitions qu'au démarrage : pnputil le redémarre (Windows 10 2004 et plus)
                using (var p = Process.Start(new ProcessStartInfo("pnputil.exe", "/restart-device \"" + id + "\"") { UseShellExecute = false, CreateNoWindow = true }))
                {
                    p.WaitForExit(30000);
                    Log.Write("écran virtuel : pilote redémarré (" + id + ", code " + p.ExitCode + ")");
                    return p.ExitCode == 0;
                }
            }
            catch (Exception e) { Log.Write("écran virtuel : ajout de " + w + "x" + h + " impossible : " + e.Message); return false; }
        }

        // identifiant du périphérique « Root\MttVDD » (ex. ROOT\DISPLAY\0000), lu dans le registre
        static string Instance()
        {
            using (var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\ROOT\DISPLAY"))
            {
                if (root == null) return null;
                foreach (var name in root.GetSubKeyNames())
                    using (var k = root.OpenSubKey(name))
                    {
                        var ids = k == null ? null : k.GetValue("HardwareID") as string[];
                        if (ids != null && ids.Any(x => x.Equals(@"Root\MttVDD", StringComparison.OrdinalIgnoreCase))) return @"ROOT\DISPLAY\" + name;
                    }
            }
            return null;
        }
    }
}
