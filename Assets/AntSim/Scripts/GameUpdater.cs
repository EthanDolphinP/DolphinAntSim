using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace AntSim
{
    /// <summary>
    /// Updates the game in place from the newest GitHub release, downloading only the files that changed.
    ///
    /// The release zip ends with a list of every file in it (the zip's central directory) with its size and CRC-32.
    /// The updater downloads just that list, compares it with the files in the game folder, then downloads only the
    /// parts of the zip that hold the changed files (GitHub serves byte ranges), unpacks them into "_update" and
    /// checks them. The running game can't overwrite its own files, so a small batch script waits for the game to
    /// close, moves the new files over the old ones and starts the game again. Saves and settings are elsewhere
    /// (AppData and the registry) and are never touched.
    /// </summary>
    public class GameUpdater : MonoBehaviour
    {
        public enum State { Idle, Working, Ready, Failed }

        public static State Current { get; private set; }
        /// <summary>What the updater is doing, for the start screen.</summary>
        public static string Status { get; private set; } = "";
        /// <summary>The version that is downloaded and waiting for a restart.</summary>
        public static string ReadyVersion { get; private set; }

        const string StageName = "_update";
        const string FailedMarker = "update-failed.txt";
        const string UserAgent = "DolphinAntSimulator";

        public static bool Supported => Application.platform == RuntimePlatform.WindowsPlayer;
        static string InstallDir => Path.GetDirectoryName(Application.dataPath);
        static string StageDir => Path.Combine(InstallDir, StageName);

        static GameUpdater instance;
        static bool applyLaunched;

        class Entry
        {
            public string Path;   // relative to the game folder, with '/'
            public int Method;    // 0 stored, 8 deflate
            public uint Crc;
            public long CompSize, Size, Offset, End;   // End: where the next file starts in the zip
        }

        string url;
        long zipSize;
        byte[] fullZip;          // set if the server ignored the byte range and sent the whole zip
        byte[] fetched;
        string fetchError;
        long fetchingNow;

        /// <summary>
        /// Call once at start: removes what a finished update left behind. Returns a message if the last update
        /// could not be installed, else null.
        /// </summary>
        public static string Cleanup()
        {
            if (!Supported) return null;
            string msg = null;
            try
            {
                string marker = Path.Combine(InstallDir, FailedMarker);
                if (File.Exists(marker))
                {
                    File.Delete(marker);
                    msg = "The last update couldn't be installed (a game file was locked). Try again, or download it from the release page.";
                }
                if (Directory.Exists(StageDir)) Directory.Delete(StageDir, true);
            }
            catch { /* not important */ }
            return msg;
        }

        /// <summary>Starts downloading the newer version that UpdateCheck found.</summary>
        public static void Begin()
        {
            if (Current == State.Working || Current == State.Ready) return;
            if (!Supported) { Fail("Updating in place only works in the Windows game."); return; }
            if (string.IsNullOrEmpty(UpdateCheck.ZipUrl)) { Fail("That release has no Windows download attached."); return; }
            if (instance == null)
            {
                var go = new GameObject("GameUpdater");
                DontDestroyOnLoad(go);
                instance = go.AddComponent<GameUpdater>();
            }
            Current = State.Working;
            Status = "Starting...";
            instance.StartCoroutine(instance.Run(UpdateCheck.ZipUrl, UpdateCheck.ZipSize, UpdateCheck.NewerVersion));
        }

        /// <summary>Closes the game, installs the downloaded update and starts the game again.</summary>
        public static void RestartToUpdate()
        {
            if (Current != State.Ready) return;
            if (LaunchApply(true)) Application.Quit();
            else Fail("Couldn't start the installer. Download the update from the release page instead.");
        }

        static void Fail(string msg)
        {
            Current = State.Failed;
            Status = msg;
            Debug.LogWarning("GameUpdater: " + msg);
        }

        // If the player quits with an update downloaded, install it on the way out (without restarting).
        void OnApplicationQuit()
        {
            if (Current == State.Ready && !applyLaunched) LaunchApply(false);
        }

        IEnumerator Run(string zipUrl, long size, string version)
        {
            installDirCached = InstallDir;   // Application.dataPath can only be read on the main thread

            // Can we write to the game folder at all (it could be somewhere protected, like Program Files)?
            try
            {
                if (Directory.Exists(StageDir)) Directory.Delete(StageDir, true);
                Directory.CreateDirectory(StageDir);
                File.WriteAllText(Path.Combine(StageDir, "test"), "ok");
                File.Delete(Path.Combine(StageDir, "test"));
            }
            catch
            {
                Fail($"Can't write to the game folder ({InstallDir}). Download the update from the release page instead.");
                yield break;
            }

            url = zipUrl;
            zipSize = size;
            fullZip = null;
            Status = "Looking at the new version...";
            yield return ResolveUrl();
            if (zipSize <= 0)
            {
                Fail("Couldn't find the size of the download.");
                yield break;
            }

            // 1. The file list at the end of the zip.
            long tailStart = Math.Max(0, zipSize - 128 * 1024);
            yield return Fetch(tailStart, zipSize - 1);
            if (fetched == null) { Fail("Download failed: " + fetchError); yield break; }
            byte[] tail = fetched;
            List<Entry> entries;
            try
            {
                int eocd = FindEndRecord(tail);
                if (eocd < 0) throw new Exception("no end record");
                int count = U16(tail, eocd + 10);
                long cdSize = U32(tail, eocd + 12), cdOffset = U32(tail, eocd + 16);
                if (count == 0xFFFF || cdOffset == 0xFFFFFFFF) throw new Exception("zip64 isn't supported");
                byte[] cd;
                if (cdOffset >= tailStart)
                {
                    cd = new byte[cdSize];
                    Array.Copy(tail, cdOffset - tailStart, cd, 0, cdSize);
                }
                else cd = null;
                entries = cd != null ? ParseDirectory(cd, count, cdOffset) : null;
                if (entries == null) pendingDirectory = (cdOffset, cdSize, count);
            }
            catch (Exception e) { Fail("The download isn't a zip the updater understands (" + e.Message + ")."); yield break; }

            if (entries == null)
            {
                var (cdOffset, cdSize, count) = pendingDirectory;
                yield return Fetch(cdOffset, cdOffset + cdSize - 1);
                if (fetched == null) { Fail("Download failed: " + fetchError); yield break; }
                try { entries = ParseDirectory(fetched, count, cdOffset); }
                catch (Exception e) { Fail("The download isn't a zip the updater understands (" + e.Message + ")."); yield break; }
            }

            // 2. Which of the player's files differ? (Hashing ~80 MB takes a moment, so off the main thread.)
            Status = "Checking your game files...";
            var check = Task.Run(() => FindChanged(entries));
            while (!check.IsCompleted) yield return null;
            if (check.IsFaulted) { Fail("Couldn't check the game files: " + check.Exception?.GetBaseException().Message); yield break; }
            List<Entry> changed = check.Result;
            long total = 0;
            foreach (var e in changed) total += e.End - e.Offset;

            // 3. Download the changed files, a run of neighbours per request.
            long done = 0;
            int unpacked = 0;
            foreach (var group in Group(changed))
            {
                long a = group[0].Offset, b = group[group.Count - 1].End - 1;
                var fetch = Fetch(a, b);
                while (fetch.MoveNext())
                {
                    Status = $"Downloading {version}: {Mb(done + fetchingNow)} of {Mb(total)} MB ({changed.Count} changed files)";
                    yield return fetch.Current;
                }
                if (fetched == null) { Fail("Download failed: " + fetchError); yield break; }
                done += b - a + 1;
                Status = $"Downloading {version}: {Mb(done)} of {Mb(total)} MB ({changed.Count} changed files)";
                byte[] data = fetched;
                var unpack = Task.Run(() => { foreach (var e in group) Unpack(data, a, e); });
                while (!unpack.IsCompleted) yield return null;
                if (unpack.IsFaulted) { Fail("A downloaded file was damaged: " + unpack.Exception?.GetBaseException().Message); yield break; }
                unpacked += group.Count;
            }

            ReadyVersion = version;
            Current = State.Ready;
            Status = changed.Count == 0
                ? $"Your files already match {version}. Restart the game to finish."
                : $"{version} is downloaded ({changed.Count} files, {Mb(total)} MB). Restart the game to finish updating.";
            Debug.Log($"GameUpdater: {unpacked} files ready in {StageDir}");
        }

        (long, long, int) pendingDirectory;

        static string Mb(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.0");

        // ---------------------------------------------------------------- downloading

        /// <summary>GitHub sends release downloads on to a signed storage link; ask for it once so ranges go straight there.</summary>
        IEnumerator ResolveUrl()
        {
            using (var r = UnityWebRequest.Head(url))
            {
                r.redirectLimit = 0;
                r.SetRequestHeader("User-Agent", UserAgent);
                r.timeout = 20;
                yield return r.SendWebRequest();
                string loc = r.GetResponseHeader("Location");
                if (!string.IsNullOrEmpty(loc) && r.responseCode >= 300 && r.responseCode < 400) url = loc;
                else if (zipSize <= 0 && long.TryParse(r.GetResponseHeader("Content-Length"), out long len)) zipSize = len;
            }
            if (zipSize <= 0)
            {
                using (var r = UnityWebRequest.Head(url))
                {
                    r.SetRequestHeader("User-Agent", UserAgent);
                    r.timeout = 20;
                    yield return r.SendWebRequest();
                    if (long.TryParse(r.GetResponseHeader("Content-Length"), out long len)) zipSize = len;
                }
            }
        }

        /// <summary>Downloads bytes a..b of the zip into <see cref="fetched"/> (null and fetchError on failure).</summary>
        IEnumerator Fetch(long a, long b)
        {
            fetched = null;
            fetchError = null;
            long want = b - a + 1;
            if (fullZip != null) { fetched = Slice(fullZip, a, want); yield break; }
            for (int attempt = 0; attempt < 3; attempt++)
            {
                using (var r = UnityWebRequest.Get(url))
                {
                    r.SetRequestHeader("Range", $"bytes={a}-{b}");
                    r.SetRequestHeader("User-Agent", UserAgent);
                    r.timeout = 300;
                    var op = r.SendWebRequest();
                    while (!op.isDone)
                    {
                        fetchingNow = (long)r.downloadedBytes;
                        yield return null;
                    }
                    fetchingNow = 0;
                    if (r.result == UnityWebRequest.Result.Success)
                    {
                        byte[] d = r.downloadHandler.data;
                        if (r.responseCode == 206 && d != null && d.Length == want) { fetched = d; yield break; }
                        if (r.responseCode == 200 && d != null && d.Length == zipSize)
                        {
                            fullZip = d;   // no byte ranges: we have the whole zip, which works too
                            fetched = Slice(d, a, want);
                            yield break;
                        }
                        fetchError = $"unexpected reply ({r.responseCode}, {d?.Length ?? 0} bytes)";
                        yield break;
                    }
                    fetchError = r.error;
                }
                // The signed link may have expired (they last a few minutes): get a fresh one and try again.
                url = UpdateCheck.ZipUrl;
                yield return ResolveUrl();
            }
        }

        static byte[] Slice(byte[] src, long start, long len)
        {
            var d = new byte[len];
            Array.Copy(src, start, d, 0, len);
            return d;
        }

        /// <summary>Splits the changed files into runs that sit close together in the zip, one download each.</summary>
        static List<List<Entry>> Group(List<Entry> changed)
        {
            var groups = new List<List<Entry>>();
            const long Gap = 256 * 1024, MaxRun = 16L * 1024 * 1024;
            foreach (var e in changed)   // already sorted by offset
            {
                var last = groups.Count > 0 ? groups[groups.Count - 1] : null;
                if (last != null && e.Offset - last[last.Count - 1].End <= Gap && e.End - last[0].Offset <= MaxRun) last.Add(e);
                else groups.Add(new List<Entry> { e });
            }
            return groups;
        }

        // ---------------------------------------------------------------- zip reading

        static int U16(byte[] b, long i) => b[i] | (b[i + 1] << 8);
        static long U32(byte[] b, long i) => (uint)(b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24));

        static int FindEndRecord(byte[] tail)
        {
            for (int i = tail.Length - 22; i >= 0; i--)
                if (tail[i] == 0x50 && tail[i + 1] == 0x4b && tail[i + 2] == 0x05 && tail[i + 3] == 0x06) return i;
            return -1;
        }

        static List<Entry> ParseDirectory(byte[] cd, int count, long cdOffset)
        {
            var all = new List<Entry>();
            long p = 0;
            for (int n = 0; n < count; n++)
            {
                if (U32(cd, p) != 0x02014b50) throw new Exception("bad file list");
                int nameLen = U16(cd, p + 28), extraLen = U16(cd, p + 30), commentLen = U16(cd, p + 32);
                string name = Encoding.UTF8.GetString(cd, (int)p + 46, nameLen).Replace('\\', '/');
                all.Add(new Entry
                {
                    Path = name,
                    Method = U16(cd, p + 10),
                    Crc = (uint)U32(cd, p + 16),
                    CompSize = U32(cd, p + 20),
                    Size = U32(cd, p + 24),
                    Offset = U32(cd, p + 42),
                });
                p += 46 + nameLen + extraLen + commentLen;
            }

            // Each file's bytes run up to where the next one starts.
            all.Sort((x, y) => x.Offset.CompareTo(y.Offset));
            for (int i = 0; i < all.Count; i++) all[i].End = i + 1 < all.Count ? all[i + 1].Offset : cdOffset;

            // The zip holds one top folder ("DolphinAntSimulator/..."): paths are relative to inside it.
            string prefix = null;
            foreach (var e in all)
            {
                int slash = e.Path.IndexOf('/');
                string top = slash < 0 ? "" : e.Path.Substring(0, slash + 1);
                if (prefix == null) prefix = top;
                else if (prefix != top) { prefix = ""; break; }
            }
            var files = new List<Entry>();
            foreach (var e in all)
            {
                if (e.Path.EndsWith("/")) continue;   // a folder
                if (!string.IsNullOrEmpty(prefix)) e.Path = e.Path.Substring(prefix.Length);
                if (e.Path.Length == 0 || e.Path.StartsWith("/") || e.Path.Contains(":") || Array.IndexOf(e.Path.Split('/'), "..") >= 0)
                    throw new Exception("unsafe path " + e.Path);
                if (e.Method != 0 && e.Method != 8) throw new Exception("unknown compression in " + e.Path);
                files.Add(e);
            }
            if (files.Count == 0) throw new Exception("no files");
            return files;
        }

        static List<Entry> FindChanged(List<Entry> entries)
        {
            var changed = new List<Entry>();
            foreach (var e in entries)
            {
                string local = Path.Combine(InstallDirCached, e.Path);
                bool same = false;
                try
                {
                    var info = new FileInfo(local);
                    if (info.Exists && info.Length == e.Size)
                        using (var f = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                            same = Crc32.Of(f) == e.Crc;
                }
                catch { /* unreadable: download it */ }
                if (!same) changed.Add(e);
            }
            return changed;
        }

        static string installDirCached;
        static string InstallDirCached => installDirCached ?? (installDirCached = InstallDir);

        /// <summary>Unpacks one file from a downloaded run of the zip (which starts at zip offset runStart) into _update.</summary>
        static void Unpack(byte[] run, long runStart, Entry e)
        {
            long p = e.Offset - runStart;
            if (U32(run, p) != 0x04034b50) throw new Exception("bad header for " + e.Path);
            long dataStart = p + 30 + U16(run, p + 26) + U16(run, p + 28);
            if (dataStart + e.CompSize > run.Length) throw new Exception("short data for " + e.Path);

            string dest = Path.Combine(StageDirCached, e.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            using (var input = new MemoryStream(run, (int)dataStart, (int)e.CompSize, false))
            using (var output = new FileStream(dest, FileMode.Create, FileAccess.Write))
            {
                Stream src = e.Method == 8 ? new DeflateStream(input, CompressionMode.Decompress) : (Stream)input;
                var crc = new Crc32();
                var buf = new byte[81920];
                long written = 0;
                int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    crc.Add(buf, n);
                    output.Write(buf, 0, n);
                    written += n;
                }
                if (written != e.Size || crc.Value != e.Crc) throw new Exception(e.Path + " doesn't match its checksum");
            }
        }

        static string StageDirCached => Path.Combine(InstallDirCached, StageName);

        // ---------------------------------------------------------------- installing

        /// <summary>Writes and starts the script that installs _update once the game has closed.</summary>
        static bool LaunchApply(bool relaunch)
        {
            try
            {
                string dir = InstallDir;
                string dataName = Path.GetFileName(Application.dataPath);   // "Dolphin Ant Simulator_Data"
                string exe = Path.Combine(dir, dataName.Substring(0, dataName.Length - "_Data".Length) + ".exe");
                int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                string Q(string s) => s.Replace("%", "%%");

                var sb = new StringBuilder();
                sb.AppendLine("@echo off");
                sb.AppendLine("chcp 65001 >nul");
                sb.AppendLine("set n=0");
                sb.AppendLine(":wait");   // until the game has closed (at most a minute)
                sb.AppendLine($"tasklist /FI \"PID eq {pid}\" 2>nul | find \" {pid} \" >nul");
                sb.AppendLine("if errorlevel 1 goto copy");
                sb.AppendLine("set /a n+=1");
                sb.AppendLine("if %n% geq 60 goto copy");
                sb.AppendLine("ping -n 2 127.0.0.1 >nul");
                sb.AppendLine("goto wait");
                sb.AppendLine(":copy");
                sb.AppendLine($"robocopy \"{Q(Path.Combine(dir, StageName))}\" \"{Q(dir)}\" /E /MOVE /R:15 /W:1 /NP /NFL /NDL /NJH /NJS >nul");
                sb.AppendLine($"if errorlevel 8 (echo failed> \"{Q(Path.Combine(dir, FailedMarker))}\")");
                if (relaunch) sb.AppendLine($"start \"\" /D \"{Q(dir)}\" \"{Q(exe)}\"");
                sb.AppendLine("del \"%~f0\"");

                string bat = Path.Combine(Path.GetTempPath(), $"DolphinAntSimulator-update-{pid}.bat");
                File.WriteAllText(bat, sb.ToString(), new UTF8Encoding(false));
                var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c \"\"{bat}\"\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                };
                System.Diagnostics.Process.Start(psi);
                applyLaunched = true;
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("GameUpdater: couldn't start the installer: " + e);
                return false;
            }
        }

        /// <summary>CRC-32 as used by zip files.</summary>
        class Crc32
        {
            static readonly uint[] Table = MakeTable();
            uint crc = 0xFFFFFFFF;
            public uint Value => crc ^ 0xFFFFFFFF;

            static uint[] MakeTable()
            {
                var t = new uint[256];
                for (uint i = 0; i < 256; i++)
                {
                    uint c = i;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                    t[i] = c;
                }
                return t;
            }

            public void Add(byte[] b, int n)
            {
                uint c = crc;
                for (int i = 0; i < n; i++) c = Table[(c ^ b[i]) & 0xFF] ^ (c >> 8);
                crc = c;
            }

            public static uint Of(Stream s)
            {
                var c = new Crc32();
                var buf = new byte[1 << 16];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0) c.Add(buf, n);
                return c.Value;
            }
        }
    }
}
