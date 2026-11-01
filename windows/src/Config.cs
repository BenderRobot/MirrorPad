// Réglages de MirrorPad (%APPDATA%\MirrorPad\config.json) — compatibles avec l'ancienne version PowerShell.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace MirrorPad
{
    [DataContract]
    public class PairedDevice
    {
        [DataMember] public string Id;
        [DataMember] public string Name;
        [DataMember] public string Token;
        [DataMember] public string Added;
    }

    [DataContract]
    public class Config
    {
        public const int Port = 7272;   // canal chiffré unique (image + tactile + contrôle)

        [DataMember] public string IPad;
        [DataMember] public int Output;
        [DataMember] public int Fps;
        [DataMember] public int Mbps;
        [DataMember] public string Encoder;
        [DataMember] public string DetectedEncoder;
        [DataMember] public bool AutoDiscover;
        [DataMember] public bool AutoConnect;
        [DataMember] public bool Touch;
        [DataMember] public bool AutoUpdate;
        [DataMember] public bool AdminMode;
        [DataMember] public string UpdateRepo;
        [DataMember] public string GitHubToken;
        [DataMember] public string TouchScreen;
        [DataMember] public string PcId;
        [DataMember] public List<PairedDevice> Paired;

        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MirrorPad");
        static string FilePath { get { return Path.Combine(Dir, "config.json"); } }

        public Config() { SetDefaults(); }

        [OnDeserializing]
        void OnDeserializing(StreamingContext c) { SetDefaults(); }

        void SetDefaults()
        {
            IPad = ""; Output = 1; Fps = 30; Mbps = 12; Encoder = "auto"; DetectedEncoder = "";
            AutoDiscover = true; AutoConnect = true; Touch = true; AutoUpdate = true; AdminMode = false;
            UpdateRepo = "BenderRobot/MirrorPad"; GitHubToken = ""; TouchScreen = ""; PcId = "";
            Paired = new List<PairedDevice>();
        }

        public static Config Load()
        {
            Directory.CreateDirectory(Dir);
            Config c = null;
            try
            {
                if (File.Exists(FilePath))
                {
                    var bytes = Encoding.UTF8.GetBytes(File.ReadAllText(FilePath));   // retire l'éventuel BOM
                    using (var ms = new MemoryStream(bytes))
                        c = (Config)new DataContractJsonSerializer(typeof(Config)).ReadObject(ms);
                }
            }
            catch (Exception e) { Log.Write("config illisible, valeurs par défaut : " + e.Message); }
            if (c == null) c = new Config();
            if (c.Paired == null) c.Paired = new List<PairedDevice>();
            if (string.IsNullOrEmpty(c.PcId)) c.PcId = Guid.NewGuid().ToString("N");
            if (c.Fps != 30 && c.Fps != 60) c.Fps = 30;
            if (c.Mbps < 2 || c.Mbps > 50) c.Mbps = 12;
            c.Save();
            return c;
        }

        public void Save()
        {
            try
            {
                using (var ms = new MemoryStream())
                {
                    using (var w = JsonReaderWriterFactory.CreateJsonWriter(ms, Encoding.UTF8, false, true, "  "))
                        new DataContractJsonSerializer(typeof(Config)).WriteObject(w, this);
                    File.WriteAllBytes(FilePath, ms.ToArray());
                }
            }
            catch (Exception e) { Log.Write("impossible d'enregistrer la config : " + e.Message); }
        }

        public PairedDevice FindPaired(string id)
        {
            lock (Paired) return Paired.Find(p => p.Id == id);
        }
    }

    public static class Log
    {
        static readonly object Gate = new object();
        public static readonly string FilePath = Path.Combine(Config.Dir, "mirrorpad.log");

        public static void Write(string msg)
        {
            lock (Gate)
            {
                try
                {
                    Directory.CreateDirectory(Config.Dir);
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 1024 * 1024) File.Delete(FilePath);   // journal limité à 1 Mo
                    File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff ") + msg + Environment.NewLine);
                }
                catch { }
            }
        }
    }
}
