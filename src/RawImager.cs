// RawImager - blind, block-level, bad-sector tolerant volume imager.
// Does NOT parse any filesystem structure, does NOT read file names,
// does NOT interpret contents. It only copies sectors and records ranges
// that cannot be read.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

static class Native
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint FILE_SHARE_READ = 1;
    public const uint FILE_SHARE_WRITE = 2;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
    public const uint FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;
    public const uint FSCTL_SET_SPARSE = 0x000900C4;
    public const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeFileHandle CreateFileW(string name, uint access, uint share,
        IntPtr sa, uint disp, uint flags, IntPtr tmpl);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadFile(SafeFileHandle h, IntPtr buf, uint n, out uint read, IntPtr ov);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetFilePointerEx(SafeFileHandle h, long dist, out long np, uint method);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetDiskFreeSpaceExW(string dir, out ulong freeAvail, out ulong totalBytes, out ulong totalFree);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize,
        IntPtr outBuf, int outSize, out int ret, IntPtr ov);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool FlushFileBuffers(SafeFileHandle h);
}

sealed class RawVolume : IDisposable
{
    readonly SafeFileHandle _h;
    readonly IntPtr _buf;
    readonly int _chunk;
    public string LastError = "";

    public RawVolume(string path, int chunk)
    {
        _chunk = chunk;
        _h = Native.CreateFileW(path, Native.GENERIC_READ,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero,
            Native.OPEN_EXISTING, Native.FILE_FLAG_NO_BUFFERING | Native.FILE_FLAG_SEQUENTIAL_SCAN,
            IntPtr.Zero);
        if (_h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "open " + path);
        IntPtr raw = Marshal.AllocHGlobal(chunk + 4096);
        long a = (raw.ToInt64() + 4095L) & ~4095L;
        _buf = new IntPtr(a);
    }

    public long Length
    {
        get
        {
            IntPtr outBuf = Marshal.AllocHGlobal(8);
            try
            {
                int got;
                if (!Native.DeviceIoControl(_h, Native.IOCTL_DISK_GET_LENGTH_INFO, IntPtr.Zero, 0,
                        outBuf, 8, out got, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "IOCTL_DISK_GET_LENGTH_INFO");
                return Marshal.ReadInt64(outBuf);
            }
            finally { Marshal.FreeHGlobal(outBuf); }
        }
    }

    // Returns bytes read, or throws Win32Exception (e.g. ERROR_CRC = 23) on unreadable sectors.
    public int ReadAt(long offset, int len)
    {
        long np;
        if (!Native.SetFilePointerEx(_h, offset, out np, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "seek");
        uint got;
        if (!Native.ReadFile(_h, _buf, (uint)len, out got, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "read");
        if ((int)got != len) throw new Win32Exception(38, "short read");
        return (int)got;
    }

    public byte[] CopyBuffer(int len)
    {
        if (len > _chunk) throw new ArgumentOutOfRangeException("len");
        byte[] a = new byte[len];
        Marshal.Copy(_buf, a, 0, len);
        return a;
    }

    public void Dispose()
    {
        if (!_h.IsInvalid) _h.Dispose();
    }
}

sealed class Program
{
    static long _total;
    static long _pos;
    static long _skippedZero;     // bytes not written because all zero
    static long _written;         // bytes written into the image
    static long _badP1;           // 4 MiB chunks that failed in pass 1
    static long _badBytes;        // bytes never readable (final)
    static readonly List<long> BadChunks = new List<long>();
    static StreamWriter _map;
    static string _statePath;
    static string _mapPath;
    static FileStream _dst;
    static RawVolume _src;
    static int _chunk;
    static Stopwatch _runClock = new Stopwatch();
    static Stopwatch _p2Clock = new Stopwatch();
    static long _startPos;
    static long _lastProgTicks;
    static long _stopAfterBytes = long.MaxValue;
    static double _p2BudgetSec = 3600;
    static int _p2Min = 65536;
    static int _rounds = 3;
    static double _cooldownSec = 90;
    static double _prewaitSec = 0;
    static int _exitCode;

    static void Main(string[] args)
    {
        string srcPath = @"\\.\E:";
        string dstPath = @"D:\PlayGround\backup\E-volume.raw";
        string mapPath = @"D:\PlayGround\backup\bad-sectors.txt";
        string logPath = @"D:\PlayGround\backup\image-progress.log";
        _statePath = @"D:\PlayGround\backup\.E-volume.state";
        _mapPath = mapPath;
        bool fresh = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--src": srcPath = args[++i]; break;
                case "--dst": dstPath = args[++i]; break;
                case "--map": mapPath = args[++i]; _mapPath = mapPath; break;
                case "--log": logPath = args[++i]; break;
                case "--state": _statePath = args[++i]; break;
                case "--fresh": fresh = true; break;
                case "--maxbytes": _stopAfterBytes = long.Parse(args[++i]); break;
                case "--p2min": _p2Min = int.Parse(args[++i]); break;
                case "--p2budget": _p2BudgetSec = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--rounds": _rounds = int.Parse(args[++i]); break;
                case "--cooldown": _cooldownSec = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--prewait": _prewaitSec = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(dstPath));
        _chunk = 4 * 1024 * 1024;
        _src = new RawVolume(srcPath, _chunk);
        _total = _src.Length;

        if (fresh)
        {
            if (File.Exists(_statePath)) File.Delete(_statePath);
            if (File.Exists(mapPath)) File.Delete(mapPath);
            if (File.Exists(@"D:\PlayGround\backup\STOP.flag")) File.Delete(@"D:\PlayGround\backup\STOP.flag");
            if (File.Exists(@"D:\PlayGround\backup\STATUS.txt")) File.Delete(@"D:\PlayGround\backup\STATUS.txt");
        }

        long resume = 0;
        if (File.Exists(_statePath))
        {
            foreach (string ln in File.ReadAllLines(_statePath))
                if (ln.StartsWith("POS ")) resume = long.Parse(ln.Substring(4), CultureInfo.InvariantCulture);
        }
        _pos = resume;

        // reload the pass-1 failure list so a resumed run still finishes recovery:
        // BAD4M = single failed chunk, BADRUN = [start,start+len) contiguous failure
        // (its interior is stored only as a run, so expand it), minus already recovered
        if (File.Exists(mapPath))
        {
            var badSet = new HashSet<long>();
            var recSet = new HashSet<long>();
            foreach (string ln in File.ReadAllLines(mapPath))
            {
                string[] p = ln.Split(' ');
                if (p.Length < 2) continue;
                long o;
                if (!long.TryParse(p[1], out o)) continue;
                if (p[0] == "BAD4M") badSet.Add(o);
                else if (p[0] == "RECOVERED") recSet.Add(o);
                else if (p[0] == "BADRUN" && p.Length > 2)
                {
                    long e = o + long.Parse(p[2], CultureInfo.InvariantCulture);
                    for (long q = o; q < e; q += _chunk) badSet.Add(q);
                }
            }
            foreach (long o in badSet) if (!recSet.Contains(o)) BadChunks.Add(o);
            BadChunks.Sort();
        }

        _map = new StreamWriter(mapPath, true, Encoding.ASCII) { AutoFlush = true };

        _dst = new FileStream(dstPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1 << 20);
        int got;
        Native.DeviceIoControl(_dst.SafeFileHandle, Native.FSCTL_SET_SPARSE, IntPtr.Zero, 0, IntPtr.Zero, 0, out got, IntPtr.Zero);
        if (_dst.Length != _total) _dst.SetLength(_total);

        foreach (string ln in File.Exists(_statePath) ? File.ReadAllLines(_statePath) : new string[0])
        {
            if (ln.StartsWith("WRITTEN ")) _written = long.Parse(ln.Substring(8), CultureInfo.InvariantCulture);
            if (ln.StartsWith("ZEROSKIP ")) _skippedZero = long.Parse(ln.Substring(9), CultureInfo.InvariantCulture);
            if (ln.StartsWith("BADP1 ")) _badP1 = long.Parse(ln.Substring(6), CultureInfo.InvariantCulture);
        }

        Log("=== start " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===");
        Log(string.Format(CultureInfo.InvariantCulture,
            "source={0} volume={1} bytes ({2:N2} GiB) chunk={3} resumeAt={4} ({5:N2} GiB) knownBad4M={6}",
            srcPath, _total, _total / 1073741824.0, _chunk, _pos, _pos / 1073741824.0, BadChunks.Count));
        Log("no filesystem parsing: this run only moves sectors");

        _runClock.Start();
        _startPos = _pos;
        _lastProgTicks = Stopwatch.GetTimestamp();

        try
        {
            Pass1();
            if (_pos < _total)
            {
                _exitCode = 3;
                Log("STOPPED before completion (space guard or stop flag). Rerun with the same --state to resume.");
                WriteStatus("PARTIAL");
            }
            else
            {
                Flush();
                Log(string.Format(CultureInfo.InvariantCulture, "pass1 complete: bad4M={0} written={1:N2} GiB zeroSkipped={2:N2} GiB",
                    _badP1, _written / 1073741824.0, _skippedZero / 1073741824.0));
                Log(string.Format(CultureInfo.InvariantCulture, "pass2 budget={0:N0}s granularityFloor={1} B", _p2BudgetSec, _p2Min));
                _p2Clock.Start();
                Pass2();
                Flush();
                WriteFinalReport();
                WriteStatus("COMPLETE");
            }
        }
        finally
        {
            try { _dst.Flush(true); } catch { }
            _dst.Dispose();
            _map.Dispose();
            _src.Dispose();
        }
        Environment.ExitCode = _exitCode;
    }

    static void Pass1()
    {
        byte[] buf = null;
        long lastFlush = _pos;
        while (_pos < _total && _pos < _stopAfterBytes)
        {
            if (File.Exists("D:\\PlayGround\\backup\\STOP.flag"))
            {
                Log("stop flag detected, saving state");
                break;
            }
            int len = (int)Math.Min(_chunk, _total - _pos);
            int err;
            // single attempt here; retries happen in pass 2 after a cool-down
            if (TryReadBlock(_pos, len, 1, out err))
            {
                buf = _src.CopyBuffer(len);
                if (IsZero(buf, len))
                {
                    _skippedZero += len;
                }
                else
                {
                    _dst.Seek(_pos, SeekOrigin.Begin);
                    _dst.Write(buf, 0, len);
                    _written += len;
                }
                _pos += len;
            }
            else
            {
                _badP1++;
                BadChunks.Add(_pos);
                _map.WriteLine("BAD4M {0} {1} err={2}", _pos, len, err);
                long runEnd = FindDeadRunEnd(_pos, len);
                if (runEnd > _pos + len)
                {
                    for (long p = _pos + len; p + len <= runEnd; p += len)
                    {
                        BadChunks.Add(p);
                        _badP1++;
                    }
                    _map.WriteLine("BADRUN {0} {1}", _pos, runEnd - _pos);
                    Log(string.Format(CultureInfo.InvariantCulture,
                        "dead run from {0:N0} spanning {1:N1} MiB - fast-skipped, pass 2 retries it",
                        _pos, (runEnd - _pos) / 1048576.0));
                    _pos = runEnd;
                }
                else
                {
                    _pos += len;
                }
                Flush();
            }
            if (_pos - lastFlush >= 256L * 1024 * 1024 || _pos >= _total)
            {
                lastFlush = _pos;
                Flush();
                double free = FreeD();
                if (free >= 0 && free < 25)
                {
                    Log(string.Format(CultureInfo.InvariantCulture, "LOW SPACE on D: ({0:N1} GiB free) - stopping safely, state saved", free));
                    break;
                }
            }
            long now = Stopwatch.GetTimestamp();
            if (now - _lastProgTicks > Stopwatch.Frequency * 10)
            {
                _lastProgTicks = now;
                Progress();
            }
        }
        Flush();
        Progress();
        SaveState();
    }

    static bool TryReadBlock(long off, int len, int attempts, out int err)
    {
        err = 0;
        for (int a = 0; a < attempts; a++)
        {
            try { _src.ReadAt(off, len); return true; }
            catch (Win32Exception we) { err = we.NativeErrorCode; }
        }
        return false;
    }

    // A 4 MiB block just failed. Probe exponentially forward to find where
    // reads start working again, then binary-search the boundary. This turns
    // a multi-hundred-MB dead zone from thousands of costly failing reads
    // into a handful of probes; pass 2 later retries the range block by block.
    static long FindDeadRunEnd(long badStart, int blockLen)
    {
        long firstAfter = badStart + blockLen;
        if (firstAfter >= _total) return _total;

        long lastBad = firstAfter;
        long good = -1;
        long step = blockLen;
        while (true)
        {
            long probe = lastBad + step;
            if (probe > _total - blockLen) probe = _total - blockLen;
            if (probe <= lastBad)
            {
                good = _total;
                break;
            }
            int e;
            if (TryReadBlock(probe, blockLen, 1, out e)) { good = probe; break; }
            lastBad = probe;
            if (step < (256L << 20)) step *= 2;
            if (lastBad + blockLen >= _total) { good = _total; break; }
        }
        // binary search between lastBad (failing) and good (working)
        long lo = lastBad;
        while (good - lo > blockLen)
        {
            long mid = lo + (good - lo) / 2;
            mid -= mid % blockLen;
            if (mid <= lo) mid = lo + blockLen;
            if (mid >= good) break;
            int e;
            if (TryReadBlock(mid, blockLen, 1, out e)) good = mid; else lo = mid;
        }
        return good;
    }

    // Retry failed 4 MiB chunks in rounds with cool-downs: the drive throws
    // intermittent CRC errors under load and recovers after a rest, so a
    // later attempt often succeeds. Only after all rounds do we split the
    // still-failing chunk into smaller pieces to salvage partial data.
    static void Pass2()
    {
        if (BadChunks.Count == 0) { Log("pass2: nothing to recover"); return; }
        Log(string.Format(CultureInfo.InvariantCulture,
            "pass2: {0} failed 4MiB chunks (rounds={1}, cooldown={2}s, prewait={3}s)",
            BadChunks.Count, _rounds, _cooldownSec, _prewaitSec));
        if (_prewaitSec > 0)
        {
            Log("pre-wait " + _prewaitSec + "s to let the drive rest");
            System.Threading.Thread.Sleep((int)(_prewaitSec * 1000));
        }
        var pending = new List<long>(BadChunks);
        for (int round = 1; round <= _rounds && pending.Count > 0; round++)
        {
            if (File.Exists("D:\\PlayGround\\backup\\STOP.flag"))
            {
                Log("stop flag present - not starting round " + round);
                break;
            }
            if (round > 1) { Log("cool-down " + _cooldownSec + "s before round " + round); System.Threading.Thread.Sleep((int)(_cooldownSec * 1000)); }
            var still = new List<long>();
            int recovered = 0, attempted = 0, carried = 0;
            var sw = Stopwatch.StartNew();
            double lastLog = 0;
            foreach (long off in pending)
            {
                if (File.Exists("D:\\PlayGround\\backup\\STOP.flag") || sw.Elapsed.TotalSeconds > 1800)
                {
                    still.Add(off); carried++; continue;
                }
                int len = (int)Math.Min(_chunk, _total - off);
                attempted++;
                try
                {
                    _src.ReadAt(off, len);
                    byte[] b = _src.CopyBuffer(len);
                    if (!IsZero(b, len))
                    {
                        _dst.Seek(off, SeekOrigin.Begin);
                        _dst.Write(b, 0, len);
                        _written += len;
                    }
                    _map.WriteLine("RECOVERED {0} {1} round={2}", off, len, round);
                    recovered++;
                }
                catch (Win32Exception) { still.Add(off); }
                if (sw.Elapsed.TotalSeconds - lastLog > 30)
                {
                    lastLog = sw.Elapsed.TotalSeconds;
                    Log(string.Format(CultureInfo.InvariantCulture,
                        "  round {0}: attempted={1} recovered={2} failing={3} elapsed={4:N0}s",
                        round, attempted, recovered, attempted - recovered, sw.Elapsed.TotalSeconds));
                }
            }
            pending = still;
            Log(string.Format(CultureInfo.InvariantCulture,
                "pass2 round {0}: attempted={1} recovered={2} stillFailing={3} carriedOver={4}",
                round, attempted, recovered, pending.Count, carried));
            if (recovered == 0 && attempted >= 200)
            {
                Log("no chunk recovered in a full round - drive is not responding; stopping salvage, image is already consistent");
                break;
            }
        }

        if (pending.Count == 0) { Log("pass2: all previously failed chunks recovered"); return; }

        // final attempt: binary-split each remaining chunk to salvage partial data
        Log("pass2: splitting " + pending.Count + " stubborn chunks down to " + _p2Min + " B (budget " + _p2BudgetSec + "s)");
        for (int k = 0; k < pending.Count; k++)
        {
            long off = pending[k];
            if (File.Exists("D:\\PlayGround\\backup\\STOP.flag") || _p2Clock.Elapsed.TotalSeconds > _p2BudgetSec)
            {
                Log("split stopped at chunk " + k + " of " + pending.Count);
                for (int j = k; j < pending.Count; j++)
                {
                    _badBytes += _chunk;
                    _map.WriteLine("BAD {0} {1}", pending[j], _chunk);
                }
                break;
            }
            byte[] buf = null;
            Recover(off, (int)Math.Min(_chunk, _total - off), ref buf);
        }
    }

    static void Recover(long off, int len, ref byte[] buf)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                _src.ReadAt(off, len);
                buf = _src.CopyBuffer(len);
                if (!IsZero(buf, len))
                {
                    _dst.Seek(off, SeekOrigin.Begin);
                    _dst.Write(buf, 0, len);
                    _written += len;
                    _map.WriteLine("RECOVERED {0} {1}", off, len);
                }
                return;
            }
            catch (Win32Exception) { }
        }
        bool canSplit = len > _p2Min && _p2Clock.Elapsed.TotalSeconds < _p2BudgetSec;
        if (canSplit)
        {
            int half = len / 2;
            Recover(off, half, ref buf);
            Recover(off + half, len - half, ref buf);
        }
        else
        {
            _badBytes += len;
            _map.WriteLine("BAD {0} {1}", off, len);
        }
    }

    static bool IsZero(byte[] b, int n)
    {
        int i = 0;
        for (; i + 8 <= n; i += 8)
            if (BitConverter.ToInt64(b, i) != 0) return false;
        for (; i < n; i++) if (b[i] != 0) return false;
        return true;
    }

    static void Progress()
    {
        double secs = _runClock.Elapsed.TotalSeconds;
        double mbps = secs > 0.5 ? (double)(_pos - _startPos) / 1048576.0 / secs : 0;
        string msg = string.Format(CultureInfo.InvariantCulture,
            "{0} pos={1:N2} GiB ({2:N1}%) rate={3:N1} MB/s written={4:N2} GiB zeroSkip={5:N2} GiB bad4M={6} badBytes={7:N2} MiB freeD={8:N1} GiB",
            DateTime.Now.ToString("HH:mm:ss"), _pos / 1073741824.0, 100.0 * _pos / _total, mbps,
            _written / 1073741824.0, _skippedZero / 1073741824.0, _badP1, _badBytes / 1048576.0, FreeD());
        Log(msg);
    }

    static double FreeD()
    {
        try { return new DriveInfo("D").AvailableFreeSpace / 1073741824.0; }
        catch { return -1; }
    }

    static void WriteStatus(string state)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendFormat("STATUS {0}\n", state);
            sb.AppendFormat("UPDATED {0:yyyy-MM-dd HH:mm:ss}\n", DateTime.Now);
            sb.AppendFormat("VOLUME_BYTES {0}\n", _total);
            sb.AppendFormat("COPIED_POS {0}\n", _pos);
            sb.AppendFormat("DATA_WRITTEN {0}\n", _written);
            sb.AppendFormat("ZERO_HOLES {0}\n", _skippedZero);
            sb.AppendFormat("BAD_4M_CHUNKS {0}\n", _badP1);
            sb.AppendFormat("UNREADABLE_BYTES_MIN {0}\n", _badBytes);
            sb.AppendFormat("D_FREE_BYTES {0}\n", (long)(FreeD() * 1073741824.0));
            File.WriteAllText(@"D:\PlayGround\backup\STATUS.txt", sb.ToString());
        }
        catch { }
    }

    static void Flush()
    {
        try { _dst.Flush(true); } catch { }
        try { Native.FlushFileBuffers(_dst.SafeFileHandle); } catch { }
        SaveState();
        _map.Flush();
    }

    static void SaveState()
    {
        var sb = new StringBuilder();
        sb.AppendFormat("POS {0}\n", _pos);
        sb.AppendFormat("WRITTEN {0}\n", _written);
        sb.AppendFormat("ZEROSKIP {0}\n", _skippedZero);
        sb.AppendFormat("BADP1 {0}\n", _badP1);
        sb.AppendFormat("TOTAL {0}\n", _total);
        File.WriteAllText(_statePath, sb.ToString());
    }

    static void WriteFinalReport()
    {
        double free = FreeD();
        Log(string.Format("FINAL volume={0:N0} bytes; copied={1:N2} GiB; zeroHoles={2:N2} GiB; unreadable={3:N2} MiB in {4} ranges; D: free {5:N1} GiB",
            _total, _written / 1073741824.0, _skippedZero / 1073741824.0, _badBytes / 1048576.0, _badP1, free));
        _map.WriteLine("SUMMARY total={0} written={1} zero={2} badbytes={3} badchunks={4}", _total, _written, _skippedZero, _badBytes, _badP1);
    }

    static void Log(string s)
    {
        Console.WriteLine(s);
        try
        {
            File.AppendAllText(@"D:\PlayGround\backup\image-progress.log", s + Environment.NewLine);
        }
        catch { }
    }
}
