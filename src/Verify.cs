// Verify - spot-check a volume image against the live volume.
// Reads only sectors; no filesystem parsing, no names, no content interpretation.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

static class Program
{
    const uint GENERIC_READ = 0x80000000;
    const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
    const uint OPEN_EXISTING = 3;
    const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
    const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadFile(SafeFileHandle h, IntPtr buf, uint n, out uint read, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetFilePointerEx(SafeFileHandle h, long dist, out long np, uint method);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize, IntPtr outBuf, int outSize, out int ret, IntPtr ov);

    static int Main(string[] args)
    {
        string src = @"\\.\E:";
        string img = @"D:\PlayGround\backup\E-volume.raw";
        int samples = 300;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--src") src = args[++i];
            else if (args[i] == "--img") img = args[++i];
            else if (args[i] == "--samples") samples = int.Parse(args[++i]);
        }

        int chunk = 1024 * 1024;
        SafeFileHandle vh = CreateFileW(src, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_NO_BUFFERING, IntPtr.Zero);
        if (vh.IsInvalid) { Console.WriteLine("cannot open volume: " + Marshal.GetLastWin32Error()); return 2; }

        IntPtr outb = Marshal.AllocHGlobal(8);
        int got;
        DeviceIoControl(vh, IOCTL_DISK_GET_LENGTH_INFO, IntPtr.Zero, 0, outb, 8, out got, IntPtr.Zero);
        long total = Marshal.ReadInt64(outb);
        Marshal.FreeHGlobal(outb);

        IntPtr raw = Marshal.AllocHGlobal(chunk + 4096);
        IntPtr buf = new IntPtr((raw.ToInt64() + 4095L) & ~4095L);
        byte[] sbuf = new byte[chunk];
        byte[] ibuf = new byte[chunk];

        using (var fs = new FileStream(img, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20))
        {
            var rnd = new Random(20261118);
            long ok = 0, mismatch = 0, srcBad = 0, zeroHole = 0;
            for (int s = 0; s < samples; s++)
            {
                long off = (long)(rnd.NextDouble() * (total - chunk));
                off -= off % 1048576L;
                long np;
                SetFilePointerEx(vh, off, out np, 0);
                uint rd;
                if (!ReadFile(vh, buf, (uint)chunk, out rd, IntPtr.Zero))
                {
                    srcBad++; // expected inside recorded bad ranges
                    continue;
                }
                Marshal.Copy(buf, sbuf, 0, chunk);
                fs.Seek(off, SeekOrigin.Begin);
                int read = 0;
                while (read < chunk)
                {
                    int n = fs.Read(ibuf, read, chunk - read);
                    if (n <= 0) break;
                    read += n;
                }
                bool allZero = true;
                for (int i = 0; i < chunk; i++) if (ibuf[i] != 0) { allZero = false; break; }
                bool equal = true;
                for (int i = 0; i < chunk; i++) if (sbuf[i] != ibuf[i]) { equal = false; break; }
                if (equal) { ok++; if (allZero) zeroHole++; }
                else { mismatch++; Console.WriteLine("MISMATCH at " + off); }
            }
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "verify: samples={0} ok={1} mismatch={2} sourceUnreadable={3} (of ok, allZero={4})",
                samples, ok, mismatch, srcBad, zeroHole));
            return mismatch == 0 ? 0 : 1;
        }
    }
}
