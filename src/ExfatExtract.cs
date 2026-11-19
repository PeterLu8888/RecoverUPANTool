// ExfatExtract - read-only exFAT extractor for a raw volume image.
// Parses the exFAT boot sector, FAT and directory entries inside an image file
// and copies files out. The source image is only ever read.
// Console output contains counters only, never file names.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

sealed class Entry
{
    public string Name = "?";
    public bool IsDirectory;
    public uint FirstCluster;
    public long Size;
    public long ValidDataLength;
}

sealed class ExfatImage : IDisposable
{
    readonly FileStream _fs;
    public int BytesPerSector;
    public int SectorsPerClusterShift;
    public int ClusterSize;
    public long FatOffsetBytes;
    public long ClusterHeapOffsetBytes;
    public long VolumeLengthBytes;
    public uint ClusterCount;
    public uint RootCluster;
    public int NumberOfFats;

    public ExfatImage(string path)
    {
        _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20);
        byte[] bs = new byte[512];
        ReadAt(0, bs, 512);
        string sig = Encoding.ASCII.GetString(bs, 3, 8);
        if (sig != "EXFAT   ") throw new InvalidDataException("not an exFAT volume (signature '" + sig.Trim() + "')");
        VolumeLengthBytes = BitConverter.ToInt64(bs, 0x48) * 512L;
        FatOffsetBytes = BitConverter.ToInt32(bs, 0x50) * 512L;
        ClusterHeapOffsetBytes = BitConverter.ToInt32(bs, 0x58) * 512L;
        ClusterCount = BitConverter.ToUInt32(bs, 0x5C);
        RootCluster = BitConverter.ToUInt32(bs, 0x60);
        BytesPerSector = 1 << bs[0x6C];
        SectorsPerClusterShift = bs[0x6D];
        NumberOfFats = bs[0x6E];
        ClusterSize = BytesPerSector << SectorsPerClusterShift;
        if (ClusterSize <= 0 || ClusterSize > 32 * 1024 * 1024) throw new InvalidDataException("bad cluster size");
    }

    void ReadAt(long off, byte[] buf, int len)
    {
        _fs.Seek(off, SeekOrigin.Begin);
        int done = 0;
        while (done < len)
        {
            int n = _fs.Read(buf, done, len - done);
            if (n <= 0) break;
            done += n;
        }
        if (done < len) throw new EndOfStreamException("short read at " + off);
    }

    public long ClusterOffset(uint c)
    {
        return ClusterHeapOffsetBytes + (long)(c - 2) * ClusterSize;
    }

    readonly byte[] _fatBlock = new byte[1 << 16];
    long _fatBlockStart = -1;

    public uint FatNext(uint c)
    {
        long off = 4L * c;
        if (_fatBlockStart < 0 || off < _fatBlockStart || off + 4 > _fatBlockStart + _fatBlock.Length)
        {
            long blockStart = off - (off % _fatBlock.Length);
            ReadAt(FatOffsetBytes + blockStart, _fatBlock, _fatBlock.Length);
            _fatBlockStart = blockStart;
        }
        return BitConverter.ToUInt32(_fatBlock, (int)(off - _fatBlockStart));
    }

    public IEnumerable<uint> Chain(uint first, long dataLen)
    {
        uint c = first;
        long done = 0, guard = 0;
        long max = ClusterCount + 2L;
        if (dataLen >= 0) max = Math.Min(max, dataLen / ClusterSize + 2);
        var seen = new HashSet<uint>();
        while (c >= 2 && c < ClusterCount + 2 && guard++ < max)
        {
            if (!seen.Add(c)) yield break;       // loop in a damaged FAT
            yield return c;
            done += ClusterSize;
            if (dataLen >= 0 && done >= dataLen) yield break;
            uint next = FatNext(c);
            if (next >= 0xFFFFFFF8 || next == 0) yield break;
            c = next;
        }
    }

    public List<Entry> ReadDirectory(uint firstCluster, out bool truncated)
    {
        truncated = false;
        var list = new List<Entry>();
        long cap = 512L * 1024 * 1024;
        var ms = new MemoryStream();
        foreach (uint c in Chain(firstCluster, 256L * 1024 * 1024))
        {
            if (c * ClusterSize > VolumeLengthBytes) { truncated = true; break; }
            byte[] buf = new byte[ClusterSize];
            ReadAt(ClusterOffset(c), buf, ClusterSize);
            ms.Write(buf, 0, ClusterSize);
            if (ms.Length > cap) { truncated = true; break; }
        }
        byte[] data = ms.ToArray();
        bool dbg = Environment.GetEnvironmentVariable("EXFAT_DEBUG") == "1";
        if (dbg)
        {
            var sb = new StringBuilder();
            for (int j = 0; j * 32 + 2 < data.Length && j < 24; j++)
                sb.AppendFormat("{0:X2}", data[j * 32]).Append(' ');
            Console.WriteLine("DBG types: " + sb);
        }
        int i = 0;
        while (i + 32 <= data.Length)
        {
            byte t = data[i];
            if (t == 0x00) break;
            if (t == 0x85)
            {
                // Scan secondaries by type instead of trusting SecondaryCount:
                // a damaged or non-standard volume may have it wrong.
                if (Environment.GetEnvironmentVariable("EXFAT_DEBUG") == "1" && list.Count < 6) Console.WriteLine("DBG c0=" + BitConverter.ToString(data, i + 32, 32));
                uint first = 0;
                long size = 0, vdl = 0;
                int nameLen = 0;
                var name = new StringBuilder();
                int j = i + 32;
                bool isDir = (BitConverter.ToUInt16(data, i + 4) & 0x10) != 0;
                while (j + 32 <= data.Length && (data[j] == 0xC0 || data[j] == 0xC1))
                {
                    int o = j;
                    if (data[o] == 0xC0)
                    {
                        vdl = BitConverter.ToInt64(data, o + 8);
                        first = BitConverter.ToUInt32(data, o + 20);
                        size = BitConverter.ToInt64(data, o + 24);
                        nameLen = data[o + 3];
                    }
                    else
                    {
                        name.Append(Encoding.Unicode.GetString(data, o + 2, 30));
                    }
                    j += 32;
                }
                string nm = name.ToString();
                // names are NUL-padded inside the 15-char chunks; trust the
                // padding over NameLength (a damaged entry may have it wrong)
                int cut = nm.IndexOf('\0');
                if (cut < 0) cut = nm.IndexOf('\uFFFF');
                if (cut > 0) nm = nm.Substring(0, cut);
                // NameLength on this volume proved unreliable; rely on padding trimming
                nm = nm.Replace("\0", "").Trim();
                if (nm.Length == 0) nm = "?";
                if (dbg)
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "DBG ent nameLen={0} chars={1} decoded={2} dir={3} cluster={4} size={5}",
                        nameLen, name.Length, nm.Length, isDir, first, size));
                list.Add(new Entry { Name = nm, IsDirectory = isDir, FirstCluster = first, Size = size, ValidDataLength = vdl });
                i = j;
                continue;
            }
            i += 32;
        }
        return list;
    }

    // Returns true if the chain covered the full file, false if it was short.
    public bool CopyFile(Entry e, string destPath, out bool allZero, out bool truncated)
    {
        allZero = true;
        truncated = false;
        long size = e.Size;
        if (size < 0 || size > VolumeLengthBytes) { size = Math.Min(VolumeLengthBytes, Math.Max(0, size)); truncated = true; }
        long remaining = size;
        using (var outFs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            const int Batch = 4096;                  // up to 16 MiB per flush at 4 KiB clusters
            var batch = new uint[Batch];
            int cnt = 0;
            long queued = 0;
            foreach (uint c in Chain(e.FirstCluster, size))
            {
                batch[cnt++] = c;
                queued += ClusterSize;
                if (cnt == Batch || queued >= size)
                {
                    FlushBatch(outFs, batch, cnt, ref remaining, ref allZero);
                    cnt = 0;
                    if (remaining <= 0) break;
                }
            }
            if (cnt > 0) FlushBatch(outFs, batch, cnt, ref remaining, ref allZero);
            if (remaining > 0) truncated = true;
        }
        return !truncated;
    }

    void FlushBatch(FileStream outFs, uint[] batch, int cnt, ref long remaining, ref bool allZero)
    {
        int i = 0;
        while (i < cnt && remaining > 0)
        {
            int j = i + 1;
            while (j < cnt && batch[j] == batch[j - 1] + 1) j++;
            long bytesWanted = Math.Min((long)(j - i) * ClusterSize, remaining);
            byte[] run = new byte[bytesWanted];
            ReadAt(ClusterOffset(batch[i]), run, (int)bytesWanted);
            for (int k = 0; k < run.Length && allZero; k++) if (run[k] != 0) allZero = false;
            outFs.Write(run, 0, run.Length);
            remaining -= bytesWanted;
            i = j;
        }
    }
    public void Dispose() { _fs.Dispose(); }
}



static class Program
{
    static ExfatImage img;
    static long filesCopied, bytesCopied, filesSkipped, allZeroFiles, truncatedFiles, dirsCreated;
    static long budget = long.MaxValue;
    static StreamWriter listWriter;
    static long listCount, maxList = long.MaxValue;
    static int maxDepth = 64;

    static int Main(string[] args)
    {
        string imagePath = null, outList = null, copySrc = null, copyDst = null;
        bool info = false, copyAll = false;
        string probe = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--img": imagePath = args[++i]; break;
                case "--info": info = true; break;
                case "--list": outList = args[++i]; break;
                case "--copy": copySrc = args[++i]; copyDst = args[++i]; break;
                case "--all": copyAll = true; copyDst = args[++i]; break;
                case "--maxbytes": budget = long.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--maxlist": maxList = long.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--maxdepth": maxDepth = int.Parse(args[++i]); break;
                case "--probe": probe = args[++i]; break;
            }
        }
        if (imagePath == null) { Console.WriteLine("usage: --img <image> [--info] [--list out.txt] [--copy <path> <dest>] [--all <dest>]"); return 2; }

        img = new ExfatImage(imagePath);
        if (info)
        {
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "volume={0:N0} bytes  clusterSize={1:N0}  clusters={2:N0}  rootCluster={3}  fats={4}",
                img.VolumeLengthBytes, img.ClusterSize, img.ClusterCount, img.RootCluster, img.NumberOfFats));
        }

        int rc = 0;
        if (outList != null)
        {
            listWriter = new StreamWriter(outList, false, new UTF8Encoding(false));
            listWriter.WriteLine("# path\tsize\tfirstCluster\ttype");
            Walk(img.RootCluster, "", 0);
            listWriter.Dispose();
            Console.WriteLine("listed " + listCount + " entries -> " + outList);
        }
        if (copySrc != null)
        {
            Directory.CreateDirectory(copyDst);
            var e = FindByPath(copySrc);
            if (e == null) { Console.WriteLine("path not found"); rc = 3; }
            else if (e.IsDirectory) CopyDir(e, copyDst, 0);
            else CopyOne(e, copyDst);
        }
        if (copyAll)
        {
            Directory.CreateDirectory(copyDst);
            bool tr;
            var root = img.ReadDirectory(img.RootCluster, out tr);
            foreach (var e in root)
            {
                if (bytesCopied >= budget) { filesSkipped++; continue; }
                if (e.IsDirectory) CopyDir(e, Path.Combine(copyDst, Safe(e.Name)), 1);
                else CopyOne(e, copyDst);
            }
        }

        if (copySrc != null || copyAll)
        {
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "copied files={0} bytes={1:N0} dirs={2} skipped={3} allZeroFiles={4} truncatedFiles={5}",
                filesCopied, bytesCopied, dirsCreated, filesSkipped, allZeroFiles, truncatedFiles));
        }
        img.Dispose();
        return rc;
    }

    static string Safe(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Replace('\\', '_');
        if (name.Length > 180) name = name.Substring(0, 180);
        return name;
    }

    static void Walk(uint dirCluster, string path, int depth)
    {
        if (listCount >= maxList) return;
        bool truncated;
        var entries = img.ReadDirectory(dirCluster, out truncated);
        foreach (var e in entries)
        {
            string p = path + "\\" + e.Name;
            if (listWriter != null && listCount < maxList)
            {
                listWriter.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0}\t{1}\t{2}\t{3}",
                    p, e.Size, e.FirstCluster, e.IsDirectory ? "D" : "F"));
                listCount++;
            }
            if (e.IsDirectory && depth < maxDepth) Walk(e.FirstCluster, p, depth + 1);
        }
        if (truncated) Console.WriteLine("warning: directory truncated (damaged chain)");
    }

    static Entry FindByPath(string path)
    {
        var parts = path.Replace('/', '\\').Trim('\\').Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 0 && parts[0].Equals("$Root", StringComparison.OrdinalIgnoreCase))
        {
            var tmp = new string[parts.Length - 1];
            Array.Copy(parts, 1, tmp, 0, tmp.Length);
            parts = tmp;
        }
        if (parts.Length == 0)
            return new Entry { Name = "$Root", IsDirectory = true, FirstCluster = img.RootCluster, Size = 0 };
        uint cluster = img.RootCluster;
        Entry cur = null;
        foreach (var part in parts)
        {
            bool tr;
            var list = img.ReadDirectory(cluster, out tr);
            if (Environment.GetEnvironmentVariable("EXFAT_DEBUG") == "1") Console.WriteLine(string.Format("DBG find partLen={0} dirEntries={1}", part.Length, list.Count));
            cur = list.Find(x => string.Equals(x.Name, part, StringComparison.OrdinalIgnoreCase));
            if (cur == null) return null;
            cluster = cur.FirstCluster;
        }
        return cur;
    }

    static void CopyDir(Entry dir, string destDir, int depth)
    {
        Directory.CreateDirectory(destDir);
        dirsCreated++;
        bool tr;
        var entries = img.ReadDirectory(dir.FirstCluster, out tr);
        foreach (var e in entries)
        {
            if (bytesCopied >= budget) { filesSkipped++; continue; }
            if (e.IsDirectory)
            {
                if (depth < maxDepth) CopyDir(e, Path.Combine(destDir, Safe(e.Name)), depth + 1);
            }
            else
            {
                CopyOne(e, destDir);
            }
        }
    }

    static void CopyOne(Entry e, string destDir)
    {
        if (bytesCopied + e.Size > budget) { filesSkipped++; return; }
        string target = Path.Combine(destDir, Safe(e.Name));
        try
        {
            bool allZero, truncated;
            img.CopyFile(e, target, out allZero, out truncated);
            filesCopied++;
            bytesCopied += e.Size;
            if (allZero && e.Size > 0) allZeroFiles++;
            if (truncated) truncatedFiles++;
        }
        catch (Exception)
        {
            filesSkipped++;
            try { if (File.Exists(target)) File.Delete(target); } catch { }
        }
        if (filesCopied % 500 == 0)
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "  {0} files, {1:N2} GiB", filesCopied, bytesCopied / 1073741824.0));
    }
}
