// Certificat du PC (créé une fois, clé protégée par Windows / DPAPI). L'iPad mémorise son empreinte
// au moment de l'appairage : un autre appareil ne peut donc pas se faire passer pour ce PC.
using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace bSideDesk
{
    public static class Identity
    {
        static readonly string FilePath = Path.Combine(Config.Dir, "pc-identity.bin");
        static X509Certificate2 cert;

        public static X509Certificate2 Certificate
        {
            get
            {
                if (cert != null) return cert;
                var test = Environment.GetEnvironmentVariable("BSIDEDESK_TEST_PFX");   // tests uniquement
                if (!string.IsNullOrEmpty(test)) return cert = new X509Certificate2(test, "test", X509KeyStorageFlags.Exportable);
                try
                {
                    if (File.Exists(FilePath))
                    {
                        var pfx = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser);
                        cert = new X509Certificate2(pfx, (string)null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);
                        if (cert.HasPrivateKey) return cert;
                    }
                }
                catch (Exception e) { Log.Write("certificat illisible, nouveau certificat : " + e.Message); }
                cert = Create();
                return cert;
            }
        }

        static X509Certificate2 Create()
        {
            using (var rsa = new RSACryptoServiceProvider(2048))
            {
                var req = new CertificateRequest("CN=bSideDesk " + Environment.MachineName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
                using (var tmp = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(30)))
                {
                    var pfx = tmp.Export(X509ContentType.Pfx);
                    File.WriteAllBytes(FilePath, ProtectedData.Protect(pfx, null, DataProtectionScope.CurrentUser));
                    Log.Write("nouveau certificat du PC créé");
                    return new X509Certificate2(pfx, (string)null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);
                }
            }
        }

        public static byte[] Sha256(byte[] data) { using (var h = SHA256.Create()) return h.ComputeHash(data); }

        // Code de vérification affiché sur les deux appareils : 6 chiffres tirés de SHA-256(certificat + nonce de l'iPad)
        public static string PairingCode(byte[] nonce)
        {
            var der = Certificate.RawData;
            var buf = new byte[der.Length + nonce.Length];
            Buffer.BlockCopy(der, 0, buf, 0, der.Length); Buffer.BlockCopy(nonce, 0, buf, der.Length, nonce.Length);
            var h = Sha256(buf);
            uint v = ((uint)h[0] << 24 | (uint)h[1] << 16 | (uint)h[2] << 8 | h[3]) % 1000000;
            return v.ToString("D6");
        }

        public static string RandomHex(int bytes)
        {
            var b = new byte[bytes];
            using (var r = RandomNumberGenerator.Create()) r.GetBytes(b);
            return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
        }

        public static byte[] FromHex(string hex)
        {
            if (hex == null || hex.Length % 2 != 0) return null;
            var b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return b;
        }

        // Comparaison en temps constant (jetons d'authentification)
        public static bool SafeEquals(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int d = 0; for (int i = 0; i < a.Length; i++) d |= a[i] ^ b[i];
            return d == 0;
        }
    }
}
