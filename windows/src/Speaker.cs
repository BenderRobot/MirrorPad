// Son du PC vers l'iPad : capture « loopback » WASAPI (ce que Windows joue sur la sortie par défaut),
// converti en PCM 16 bits stéréo et envoyé par paquets d'environ 20 ms (trame type 7).
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace MirrorPad
{
    public class PcAudio
    {
        public Action<byte[]> Packet;        // PCM s16 stéréo entrelacé
        public Action<int> Format;           // fréquence d'échantillonnage, envoyée avant le premier paquet
        Thread thread; volatile bool running; volatile int gen;
        public bool Running { get { return running; } }

        public void Start()
        {
            if (running) return;
            running = true;
            int g = ++gen;
            thread = new Thread(() => Loop(g)) { IsBackground = true, Name = "son PC" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }
        public void Stop() { if (running) Log.Write("son du PC vers l'iPad arrêté"); running = false; }

        void Loop(int g)
        {
            IAudioClient client = null; IAudioCaptureClient capture = null; IntPtr mix = IntPtr.Zero;
            try
            {
                var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                IMMDevice dev;
                Check(en.GetDefaultAudioEndpoint(0 /* eRender */, 0 /* eConsole */, out dev));
                object o; var iid = typeof(IAudioClient).GUID;
                Check(dev.Activate(ref iid, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out o));
                client = (IAudioClient)o;
                Check(client.GetMixFormat(out mix));
                // WAVEFORMATEX : wFormatTag, nChannels, nSamplesPerSec, nAvgBytesPerSec, nBlockAlign, wBitsPerSample, cbSize (+ extensible)
                int tag = Marshal.ReadInt16(mix, 0) & 0xFFFF, ch = Marshal.ReadInt16(mix, 2), rate = Marshal.ReadInt32(mix, 4), bits = Marshal.ReadInt16(mix, 14);
                bool isFloat = tag == 3 || (tag == 0xFFFE && bits == 32 && IsFloatSubFormat(mix));
                Check(client.Initialize(0 /* partagé */, 0x00020000 /* LOOPBACK */, 2000000 /* 200 ms */, 0, mix, IntPtr.Zero));
                object c; var ciid = typeof(IAudioCaptureClient).GUID;
                Check(client.GetService(ref ciid, out c));
                capture = (IAudioCaptureClient)c;
                Check(client.Start());
                if (Format != null) Format(rate);
                Log.Write("son du PC vers l'iPad (" + rate + " Hz, " + ch + " canaux, " + (isFloat ? "float" : bits + " bits") + ")");

                var outBuf = new System.Collections.Generic.List<byte>(rate / 50 * 4 * 2);
                int packetBytes = rate / 50 * 4;   // 20 ms en s16 stéréo
                float[] fbuf = new float[0]; short[] sbuf = new short[0]; int[] ibuf = new int[0];
                while (running && g == gen)
                {
                    uint next;
                    if (capture.GetNextPacketSize(out next) != 0) break;
                    if (next == 0) { Thread.Sleep(8); continue; }
                    IntPtr data; uint frames, flags; ulong pos, qpc;
                    if (capture.GetBuffer(out data, out frames, out flags, out pos, out qpc) != 0) break;
                    bool silent = (flags & 0x2) != 0;
                    int n = (int)frames * ch;
                    if (!silent)
                    {
                        if (isFloat) { if (fbuf.Length < n) fbuf = new float[n]; Marshal.Copy(data, fbuf, 0, n); }
                        else if (bits == 16) { if (sbuf.Length < n) sbuf = new short[n]; Marshal.Copy(data, sbuf, 0, n); }
                        else if (bits == 32) { if (ibuf.Length < n) ibuf = new int[n]; Marshal.Copy(data, ibuf, 0, n); }
                    }
                    for (int i = 0; i < frames; i++)
                    {
                        for (int k = 0; k < 2; k++)   // on garde les 2 premiers canaux (gauche / droite)
                        {
                            int idx = i * ch + Math.Min(k, ch - 1);
                            short v = 0;
                            if (!silent)
                            {
                                if (isFloat) v = (short)Math.Max(-32768, Math.Min(32767, (int)(fbuf[idx] * 32767)));
                                else if (bits == 16) v = sbuf[idx];
                                else if (bits == 32) v = (short)(ibuf[idx] >> 16);
                            }
                            outBuf.Add((byte)v); outBuf.Add((byte)(v >> 8));
                        }
                    }
                    capture.ReleaseBuffer(frames);
                    while (outBuf.Count >= packetBytes)
                    {
                        var pkt = outBuf.GetRange(0, packetBytes).ToArray();
                        outBuf.RemoveRange(0, packetBytes);
                        if (Packet != null) Packet(pkt);
                    }
                }
            }
            catch (Exception e) { Log.Write("son du PC : " + e.Message); }
            finally
            {
                try { if (client != null) client.Stop(); } catch { }
                if (mix != IntPtr.Zero) Marshal.FreeCoTaskMem(mix);
                if (g == gen) running = false;
            }
        }

        static bool IsFloatSubFormat(IntPtr fmt)
        {
            // WAVEFORMATEXTENSIBLE : SubFormat (GUID) à l'octet 24 ; KSDATAFORMAT_SUBTYPE_IEEE_FLOAT commence par 3
            return Marshal.ReadInt32(fmt, 24) == 3;
        }
        static void Check(int hr) { if (hr != 0) throw new COMException("WASAPI", hr); }

        // ---- WASAPI (COM) : méthodes déclarées dans l'ordre exact de la vtable ----
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int flow, int mask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int ctx, IntPtr par, [MarshalAs(UnmanagedType.IUnknown)] out object itf);
        }

        [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioClient
        {
            [PreserveSig] int Initialize(int shareMode, int flags, long bufferDuration, long periodicity, IntPtr format, IntPtr session);
            [PreserveSig] int GetBufferSize(out uint frames);
            [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out uint frames);
            [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
            [PreserveSig] int GetMixFormat(out IntPtr format);
            [PreserveSig] int GetDevicePeriod(out long def, out long min);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr h);
            [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object itf);
        }

        [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioCaptureClient
        {
            [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
            [PreserveSig] int ReleaseBuffer(uint frames);
            [PreserveSig] int GetNextPacketSize(out uint frames);
        }
    }
}
