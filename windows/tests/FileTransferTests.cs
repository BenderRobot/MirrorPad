using System;
using System.IO;
using System.Text;
using bSideDesk;

namespace bSideDesk
{
    // Only the transport and the application's directory/logging are replaced.
    public class Session { public bool IsClosed { get { return false; } } public void SendFrame(byte t, byte[] b, int o, int n) { } }
    public static class Log { public static void Write(string s) { } }
    public static class Inbox { public static string Dir; }
}

class FileTransferTests
{
    static byte[] Frame(uint id, byte flags, string name, string data)
    {
        using (var ms = new MemoryStream())
        {
            var b = BitConverter.GetBytes(id); ms.Write(b, 0, 4); ms.WriteByte(flags);
            if ((flags & 1) != 0) { b = Encoding.UTF8.GetBytes(name); ms.WriteByte((byte)(b.Length >> 8)); ms.WriteByte((byte)b.Length); ms.Write(b, 0, b.Length); }
            b = Encoding.UTF8.GetBytes(data); ms.Write(b, 0, b.Length); return ms.ToArray();
        }
    }
    static void Check(bool ok) { if (!ok) throw new Exception("File transfer assertion failed"); }
    static void Main()
    {
        Inbox.Dir = Path.Combine(Path.GetTempPath(), "bsidedesk-transfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Inbox.Dir);
        var transfer = new FileTransfer(); int completed = 0;
        transfer.Received = path => completed++;
        try
        {
            transfer.Receive(Frame(1, 1, "result.txt", "hello"));
            Check(!File.Exists(Path.Combine(Inbox.Dir, "result.txt")) && completed == 0);
            transfer.Receive(Frame(2, 1, "other.txt", "partial"));
            transfer.CancelAll(); transfer.CancelAll();
            Check(Directory.GetFiles(Inbox.Dir).Length == 0 && completed == 0);
            transfer.Receive(Frame(1, 2, null, "stale tail"));
            Check(Directory.GetFiles(Inbox.Dir).Length == 0);
            transfer.Receive(Frame(1, 1, "result.txt", "old partial"));
            transfer.Receive(Frame(1, 1, "result.txt", "new")); // reused ID closes and deletes old partial
            Check(Directory.GetFiles(Inbox.Dir).Length == 1);
            transfer.Receive(Frame(1, 2, null, " contents"));
            Check(File.ReadAllText(Path.Combine(Inbox.Dir, "result.txt")) == "new contents" && completed == 1);
            transfer.CancelAll();
            Check(File.Exists(Path.Combine(Inbox.Dir, "result.txt")));
            transfer.Receive(Frame(3, 3, "result.txt", "second"));
            Check(File.ReadAllText(Path.Combine(Inbox.Dir, "result (2).txt")) == "second" && completed == 2);
            // A destination created during transfer must never be overwritten.
            transfer.Receive(Frame(4, 1, "collision.txt", "incoming"));
            File.WriteAllText(Path.Combine(Inbox.Dir, "collision.txt"), "existing");
            try { transfer.Receive(Frame(4, 2, null, "tail")); throw new Exception("Expected collision failure"); }
            catch (IOException) { }
            Check(File.ReadAllText(Path.Combine(Inbox.Dir, "collision.txt")) == "existing");
            Check(Directory.GetFiles(Inbox.Dir, "*.part").Length == 0);
            Console.WriteLine("PASS: disconnect cleanup, reused ID, stale tail, completion, collision safety");
        }
        finally { transfer.CancelAll(); Directory.Delete(Inbox.Dir, true); }
    }
}
