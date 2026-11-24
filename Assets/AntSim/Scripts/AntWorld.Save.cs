using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace AntSim
{
    /// <summary>A save file on disk, as listed on the start screen.</summary>
    public class SaveSlot
    {
        public string Path;
        public string Name;
        public DateTime Written;
        public string Summary;   // colony, species, time played
        public long Bytes;
    }

    /// <summary>
    /// Save and load. A save is one gzip-compressed binary file in Application.persistentDataPath/Saves:
    /// a header (version, start-screen setup, summary), the colonies, nests and farms (written field by field,
    /// by name, so adding a field doesn't break old saves), then the raw grids and the ant list.
    /// Pheromones are stored as half floats. Loading builds a fresh world of the same size and pours the data in.
    /// </summary>
    public unsafe partial class AntWorld
    {
        const string SaveMagic = "ANTSAVE";
        const int SaveVersion = 1;
        public const string SaveExtension = ".antsave";
        public const string AutosaveName = "Autosave";

        [Header("Saving")]
        [Tooltip("Seconds between autosaves (0 = off).")]
        public float autosaveInterval = 300f;

        string pendingLoad;  // set by LoadGame before Awake
        float autosaveTimer;
        public string LastSaveError { get; private set; }

        public static string SaveFolder => System.IO.Path.Combine(Application.persistentDataPath, "Saves");

        // ------------------------------------------------------------------ public API

        /// <summary>Starts a new world from a save file. Returns null and an error if the file can't be read.</summary>
        public static AntWorld LoadGame(string path, out string error)
        {
            error = null;
            GameSetup setup;
            try { setup = ReadSaveSetup(path); }
            catch (Exception e)
            {
                error = "Couldn't read that save: " + e.Message;
                return null;
            }
            var go = new GameObject("AntWorld");
            go.SetActive(false);
            var w = go.AddComponent<AntWorld>();
            w.setup = setup;
            w.pendingLoad = path;
            go.SetActive(true);
            if (w.LastSaveError != null)
            {
                error = w.LastSaveError;
                Destroy(go);
                return null;
            }
            return w;
        }

        /// <summary>The start-screen setup stored in a save (throws if the file isn't a readable save).</summary>
        public static GameSetup ReadSaveSetup(string path)
        {
            using var file = File.OpenRead(path);
            using var gz = new GZipStream(file, CompressionMode.Decompress);
            using var r = new BinaryReader(gz);
            return ReadHeader(r, out _);
        }

        /// <summary>Saves the game under a name (file name made safe). Returns null on success, or an error.</summary>
        public string SaveGame(string name)
        {
            try
            {
                Directory.CreateDirectory(SaveFolder);
                string safe = MakeSafeName(name);
                string path = System.IO.Path.Combine(SaveFolder, safe + SaveExtension);
                string tmp = path + ".tmp";
                using (var file = File.Create(tmp))
                using (var gz = new GZipStream(file, System.IO.Compression.CompressionLevel.Fastest))
                using (var w = new BinaryWriter(gz))
                {
                    WriteSave(w, name);
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                LastSaveError = null;
                return null;
            }
            catch (Exception e)
            {
                LastSaveError = "Save failed: " + e.Message;
                return LastSaveError;
            }
        }

        /// <summary>All saves, newest first.</summary>
        public static List<SaveSlot> ListSaves()
        {
            var list = new List<SaveSlot>();
            if (!Directory.Exists(SaveFolder)) return list;
            foreach (var path in Directory.GetFiles(SaveFolder, "*" + SaveExtension))
            {
                var slot = new SaveSlot
                {
                    Path = path, Name = System.IO.Path.GetFileNameWithoutExtension(path),
                    Written = File.GetLastWriteTime(path), Bytes = new FileInfo(path).Length,
                };
                try
                {
                    using var file = File.OpenRead(path);
                    using var gz = new GZipStream(file, CompressionMode.Decompress);
                    using var r = new BinaryReader(gz);
                    ReadHeader(r, out string summary);
                    slot.Summary = summary;
                }
                catch { slot.Summary = "(unreadable)"; }
                list.Add(slot);
            }
            list.Sort((a, b) => b.Written.CompareTo(a.Written));
            return list;
        }

        public static void DeleteSave(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
        }

        static string MakeSafeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) name = "Save";
            foreach (char ch in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
            return name.Trim();
        }

        /// <summary>Autosave every few minutes (called from Update).</summary>
        void UpdateAutosave(float dt)
        {
            autosaveInterval = GameSettings.AutosaveMinutes * 60f; // Esc menu, Settings
            if (autosaveInterval <= 0f || State != GameState.Playing) return;
            autosaveTimer += dt;
            if (autosaveTimer < autosaveInterval) return;
            autosaveTimer = 0f;
            string err = SaveGame(AutosaveName);
            if (err != null) Notify(err);
        }

        // ------------------------------------------------------------------ writing

        void WriteSave(BinaryWriter w, string name)
        {
            var me = Colonies[Player];
            string summary = $"{me.Name}, {SpeciesOf(Player).Name}, {(int)SimTime / 60}:{(int)SimTime % 60:00} played, " +
                             $"{me.Population:N0} ants{(Sandbox ? ", sandbox" : "")}";
            w.Write(SaveMagic);
            w.Write(SaveVersion);
            w.Write(UnsafeUtility.SizeOf<Ant>());
            w.Write(summary);
            WriteFields(w, setup);

            // World scalars.
            w.Write(SimTime); w.Write(tickIndex); w.Write(rng.state);
            w.Write(lodTimer); w.Write(foodTimer); w.Write(ecoTimer); w.Write(aiTimer); w.Write(wildTimer); w.Write(siteTimer);
            w.Write((int)State); w.Write(PeacefulStart); w.Write(MapFood); w.Write(simSpeed);
            w.Write(FocusColony); w.Write(PheromoneView); w.Write(nestHigh);
            w.Write(TerrainCover.x); w.Write(TerrainCover.y); w.Write(TerrainCover.z);

            // Colonies (species by name, so reordering species assets doesn't swap them).
            w.Write(Colonies.Length);
            foreach (var c in Colonies)
            {
                w.Write(SpeciesDef.All[c.Species].Name);
                WriteFields(w, c);
            }
            for (int k = 0; k < MaxNests; k++)
            {
                w.Write(nestInfo[k] != null);
                if (nestInfo[k] != null) WriteFields(w, nestInfo[k]);
            }
            w.Write(farms.Count);
            foreach (var f in farms) WriteFields(w, f);
            w.Write(bushes.Count);
            foreach (var b in bushes) { w.Write(b.x); w.Write(b.y); w.Write(b.z); }

            // Grids and the ants.
            WriteNative(w, ants.AsArray());
            var halfs = new NativeArray<half>(pheromones.Length, Allocator.TempJob);
            new PackHalfJob { From = pheromones, To = halfs }.Schedule(pheromones.Length, 65536).Complete();
            WriteNative(w, halfs);
            halfs.Dispose();
            WriteNative(w, food);
            WriteNative(w, leaves);
            WriteNative(w, plantCap);
            WriteNative(w, terrain);
            WriteNative(w, nests);
            WriteNative(w, hostile);
            WriteNative(w, nestSites);
            w.Write("END");
        }

        static void WriteNative<T>(BinaryWriter w, NativeArray<T> a) where T : struct
        {
            int size = UnsafeUtility.SizeOf<T>();
            w.Write(a.Length);
            w.Write(size);
            long bytes = (long)a.Length * size;
            byte* src = (byte*)a.GetUnsafeReadOnlyPtr();
            var buf = new byte[1 << 20];
            fixed (byte* dst = buf)
            {
                for (long off = 0; off < bytes; off += buf.Length)
                {
                    int n = (int)math.min(buf.Length, bytes - off);
                    UnsafeUtility.MemCpy(dst, src + off, n);
                    w.Write(buf, 0, n);
                }
            }
        }

        // ------------------------------------------------------------------ reading

        static GameSetup ReadHeader(BinaryReader r, out string summary)
        {
            if (r.ReadString() != SaveMagic) throw new InvalidDataException("not a save file");
            int version = r.ReadInt32();
            if (version != SaveVersion) throw new InvalidDataException($"save version {version}, expected {SaveVersion}");
            int antSize = r.ReadInt32();
            if (antSize != UnsafeUtility.SizeOf<Ant>()) throw new InvalidDataException("made by a different build of the game");
            summary = r.ReadString();
            var setup = new GameSetup();
            ReadFields(r, setup);
            return setup;
        }

        /// <summary>Called from Awake instead of making a new map. Allocations have already been made for the setup's size.</summary>
        void LoadState(string path)
        {
            using var file = File.OpenRead(path);
            using var gz = new GZipStream(file, CompressionMode.Decompress);
            using var r = new BinaryReader(gz);
            ReadHeader(r, out _);

            SimTime = r.ReadSingle(); tickIndex = r.ReadInt32(); rng.state = r.ReadUInt32();
            lodTimer = r.ReadSingle(); foodTimer = r.ReadSingle(); ecoTimer = r.ReadSingle(); aiTimer = r.ReadSingle();
            wildTimer = r.ReadSingle(); siteTimer = r.ReadSingle();
            State = (GameState)r.ReadInt32(); PeacefulStart = r.ReadSingle(); MapFood = r.ReadInt64(); simSpeed = r.ReadSingle();
            FocusColony = r.ReadInt32(); PheromoneView = r.ReadInt32(); nestHigh = r.ReadInt32();
            TerrainCover = new float3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

            int count = r.ReadInt32();
            if (count != colonyCount) throw new InvalidDataException("colony count mismatch");
            Colonies = new ColonyInfo[count];
            for (int c = 0; c < count; c++)
            {
                string speciesName = r.ReadString();
                var col = new ColonyInfo();
                ReadFields(r, col);
                int sp = SpeciesLibrary.IndexOf(speciesName);
                col.Species = sp >= 0 ? sp : math.clamp(col.Species, 0, SpeciesDef.All.Length - 1);
                if (col.SleepWorkerAges == null || col.SleepWorkerAges.Length != ageBins) col.SleepWorkerAges = new float[ageBins];
                if (col.SleepSoldierAges == null || col.SleepSoldierAges.Length != ageBins) col.SleepSoldierAges = new float[ageBins];
                Colonies[c] = col;
            }
            for (int k = 0; k < MaxNests; k++)
            {
                if (!r.ReadBoolean()) { nestInfo[k] = null; continue; }
                var n = new NestInfo();
                ReadFields(r, n);
                nestInfo[k] = n;
            }
            farms.Clear();
            int farmCount = r.ReadInt32();
            for (int i = 0; i < farmCount; i++)
            {
                var f = new AphidFarm();
                ReadFields(r, f);
                farms.Add(f);
            }
            bushes.Clear();
            int bushCount = r.ReadInt32();
            for (int i = 0; i < bushCount; i++) bushes.Add(new float3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));

            // The ant list.
            int antCount = PeekLength(r);
            ants.ResizeUninitialized(antCount);
            var antArray = ants.AsArray();
            ReadNative(r, antArray);
            var halfs = new NativeArray<half>(pheromones.Length, Allocator.TempJob);
            ReadNative(r, halfs);
            new UnpackHalfJob { From = halfs, To = pheromones }.Schedule(pheromones.Length, 65536).Complete();
            halfs.Dispose();
            ReadNative(r, food);
            ReadNative(r, leaves);
            ReadNative(r, plantCap);
            terrain = new NativeArray<byte>(grid.CellCount, Allocator.Persistent);
            ReadNative(r, terrain);
            ReadNative(r, nests);
            ReadNative(r, hostile);
            ReadNative(r, nestSites);
            if (r.ReadString() != "END") throw new InvalidDataException("the save is cut short");

            // Rebuild everything derived from the colonies and species.
            for (int c = 0; c < colonyCount; c++)
            {
                var col = Colonies[c];
                var sp = SpeciesOf(c);
                colonyColors[c] = new float4(col.Color.r, col.Color.g, col.Color.b, 1f);
                colorVectors[c] = col.Color;
                shapeValues[c] = sp.Shape;
                colonyStats[c] = sp.Stats;
                SetupMorphs(c, sp);
            }
            colonyStats[WildColony] = new SpeciesStats { SpeedMul = 1f, DamageMul = 1f, SenseMul = 1f, HealthMul = 1f };
            colonyColors[WildColony] = new float4(0.6f, 0.6f, 0.6f, 1f);
            colorVectors[WildColony] = new Color(0.6f, 0.6f, 0.6f);

            // Counts, queens and squad centres as of now; trail maps for the workers' fear of rivals.
            MakeCompactJob().Run();
            EnsureDiplomacy(); // saves from before diplomacy have no relation arrays
            new BusyTrailsJob { Pheromones = pheromones, Busy = busyTrails, Grid = grid, BusyW = busyW, BusyH = busyH }
                .Schedule(busyTrails.Length, 4096).Complete();
        }

        static int PeekLength(BinaryReader r)
        {
            // The next array's length is read again by ReadNative; BinaryReader on a GZip stream can't seek,
            // so remember it for one call.
            peekedLength = r.ReadInt32();
            return peekedLength;
        }
        static int peekedLength = -1;

        static void ReadNative<T>(BinaryReader r, NativeArray<T> a) where T : struct
        {
            int length = peekedLength >= 0 ? peekedLength : r.ReadInt32();
            peekedLength = -1;
            int size = r.ReadInt32();
            if (size != UnsafeUtility.SizeOf<T>()) throw new InvalidDataException($"{typeof(T).Name} size changed");
            if (length != a.Length) throw new InvalidDataException($"{typeof(T).Name} length {length}, expected {a.Length}");
            long bytes = (long)length * size;
            byte* dst = (byte*)a.GetUnsafePtr();
            var buf = new byte[1 << 20];
            fixed (byte* src = buf)
            {
                for (long off = 0; off < bytes;)
                {
                    int want = (int)math.min(buf.Length, bytes - off);
                    int got = r.Read(buf, 0, want);
                    if (got <= 0) throw new EndOfStreamException("the save is cut short");
                    UnsafeUtility.MemCpy(dst + off, src, got);
                    off += got;
                }
            }
        }

        // ------------------------------------------------------------------ field-by-field records

        const BindingFlags FieldFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        enum Tag : byte { Int, Long, Float, Bool, String, Color, Vector2, FloatArray, Brood, UInt }

        /// <summary>Writes every instance field of a plain class we know how to store, as (name, tag, value).</summary>
        static void WriteFields(BinaryWriter w, object o)
        {
            var fields = o.GetType().GetFields(FieldFlags);
            var keep = new List<(FieldInfo f, Tag t)>();
            foreach (var f in fields)
                if (TagOf(f.FieldType, out Tag t)) keep.Add((f, t));
            w.Write(keep.Count);
            foreach (var (f, t) in keep)
            {
                w.Write(f.Name);
                w.Write((byte)t);
                object v = f.GetValue(o);
                switch (t)
                {
                    case Tag.Int: w.Write((int)v); break;
                    case Tag.UInt: w.Write((uint)v); break;
                    case Tag.Long: w.Write((long)v); break;
                    case Tag.Float: w.Write((float)v); break;
                    case Tag.Bool: w.Write((bool)v); break;
                    case Tag.String: w.Write((string)v ?? ""); break;
                    case Tag.Color: var c = (Color)v; w.Write(c.r); w.Write(c.g); w.Write(c.b); w.Write(c.a); break;
                    case Tag.Vector2: var p = (Vector2)v; w.Write(p.x); w.Write(p.y); break;
                    case Tag.FloatArray:
                        var arr = (float[])v;
                        w.Write(arr?.Length ?? -1);
                        if (arr != null) foreach (float x in arr) w.Write(x);
                        break;
                    case Tag.Brood:
                        var list = (List<BroodCohort>)v;
                        w.Write(list.Count);
                        foreach (var b in list) { w.Write(b.Age); w.Write(b.Workers); w.Write(b.Soldiers); }
                        break;
                }
            }
        }

        /// <summary>Reads fields written by WriteFields into o, by name. Unknown names are skipped; missing ones keep their defaults.</summary>
        static void ReadFields(BinaryReader r, object o)
        {
            var type = o.GetType();
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                string name = r.ReadString();
                var t = (Tag)r.ReadByte();
                object v = null;
                switch (t)
                {
                    case Tag.Int: v = r.ReadInt32(); break;
                    case Tag.UInt: v = r.ReadUInt32(); break;
                    case Tag.Long: v = r.ReadInt64(); break;
                    case Tag.Float: v = r.ReadSingle(); break;
                    case Tag.Bool: v = r.ReadBoolean(); break;
                    case Tag.String: v = r.ReadString(); break;
                    case Tag.Color: v = new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); break;
                    case Tag.Vector2: v = new Vector2(r.ReadSingle(), r.ReadSingle()); break;
                    case Tag.FloatArray:
                        int len = r.ReadInt32();
                        if (len >= 0)
                        {
                            var arr = new float[len];
                            for (int k = 0; k < len; k++) arr[k] = r.ReadSingle();
                            v = arr;
                        }
                        break;
                    case Tag.Brood:
                        int bc = r.ReadInt32();
                        var list = new List<BroodCohort>(bc);
                        for (int k = 0; k < bc; k++)
                            list.Add(new BroodCohort { Age = r.ReadSingle(), Workers = r.ReadSingle(), Soldiers = r.ReadSingle() });
                        v = list;
                        break;
                    default: throw new InvalidDataException("unknown field type in save");
                }
                var f = type.GetField(name, FieldFlags);
                if (f == null || !TagOf(f.FieldType, out Tag ft) || ft != t) continue;
                if (t == Tag.Brood)
                {
                    // Readonly list: refill it in place.
                    var target = (List<BroodCohort>)f.GetValue(o);
                    target.Clear();
                    target.AddRange((List<BroodCohort>)v);
                }
                else f.SetValue(o, v);
            }
        }

        static bool TagOf(Type type, out Tag t)
        {
            t = default;
            if (type == typeof(int)) t = Tag.Int;
            else if (type == typeof(uint)) t = Tag.UInt;
            else if (type == typeof(long)) t = Tag.Long;
            else if (type == typeof(float)) t = Tag.Float;
            else if (type == typeof(bool)) t = Tag.Bool;
            else if (type == typeof(string)) t = Tag.String;
            else if (type == typeof(Color)) t = Tag.Color;
            else if (type == typeof(Vector2)) t = Tag.Vector2;
            else if (type == typeof(float[])) t = Tag.FloatArray;
            else if (type == typeof(List<BroodCohort>)) t = Tag.Brood;
            else return false;
            return true;
        }
    }

    [BurstCompile]
    struct PackHalfJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> From;
        [WriteOnly] public NativeArray<half> To;
        public void Execute(int i) => To[i] = (half)From[i];
    }

    [BurstCompile]
    struct UnpackHalfJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<half> From;
        [WriteOnly] public NativeArray<float> To;
        public void Execute(int i) => To[i] = From[i];
    }
}
