using System.Diagnostics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Random = Unity.Mathematics.Random;

namespace AntSim
{
    /// <summary>
    /// Owns all simulation data and runs the fixed-tick loop:
    ///   1. Combat: spatial hash + nearest hostile (ants and wildlife)
    ///   2. AntStepJob      (parallel, every ant and bug)
    ///   3. EvaporateJob    (parallel, every pheromone cell)
    ///   4. CompactJob      (removes the dead, counts castes)
    ///   5. Colony economy  (main thread, per colony: food, farms, upkeep, egg laying)
    ///   6. Ecology         (plants, aphids, wildlife, distress calls, rival AI) - see AntWorld.Ecology.cs
    /// Wildlife lives in the ant list under the extra colony slot <see cref="WildColony"/>.
    /// Level of detail: colonies the camera can't see (and that aren't fighting) fall asleep. Their ants
    /// are removed and simulated as numbers until the camera or an enemy comes near, then re-spawned.
    /// Rendering is a single instanced draw for all ants plus one texture for the ground.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public unsafe partial class AntWorld : MonoBehaviour
    {
        public const int MaxColonies = 8;  // colony slots, including the wildlife slot
        public const int Player = 0;       // the player's colony is always slot 0

        [Tooltip("Start-screen choices. Left empty when the world is placed in a scene by hand: then the fields below are used.")]
        public GameSetup setup;

        [Header("World (used when there is no GameSetup)")]
        public int gridWidth = 1024;
        public int gridHeight = 1024;
        public float cellSize = 1f;
        public int seed = 12345;

        [Header("Colonies")]
        [Range(1, 7)] public int colonyCount = 6;
        public int startWorkers = 400;
        public float startFood = 200f;

        [Header("Simulation")]
        public int ticksPerSecond = 30;
        [Range(0f, 20f)] public float simSpeed = 1f;
        public int maxTicksPerFrame = 8;
        public SimParams sim = SimParams.Default;
        public EconomyParams economy = EconomyParams.Default;
        public CombatParams combat = CombatParams.Default;
        public LodParams lod = LodParams.Default;
        public WildlifeParams wildlife = WildlifeParams.Default;
        public FarmingParams farming = FarmingParams.Default;

        [Header("Food")]
        public float foodCheckInterval = 3f;
        public int foodBlobRadius = 6;
        public int foodPerCell = 40;
        [Tooltip("Sandbox only: new food blobs appear while total food on the map is below this.")]
        public int targetMapFood = 240000;

        [Header("Rendering")]
        public float antLength = 0.9f;

        public static readonly Color[] Palette =
        {
            new Color(0.95f, 0.30f, 0.25f), // red
            new Color(0.30f, 0.60f, 1.00f), // blue
            new Color(1.00f, 0.80f, 0.20f), // yellow
            new Color(0.75f, 0.40f, 1.00f), // purple
            new Color(0.20f, 0.90f, 0.85f), // cyan
            new Color(1.00f, 0.55f, 0.15f), // orange
            new Color(0.95f, 0.45f, 0.75f), // pink
            new Color(0.85f, 0.85f, 0.85f), // white
        };
        public static readonly string[] PaletteNames = { "Crimson", "Azure", "Amber", "Violet", "Teal", "Ember", "Rose", "Ivory" };

        // ---- Public state (read by HUD / other systems) ----
        public ColonyInfo[] Colonies { get; private set; }
        public GridInfo Grid => grid;
        public int WildColony => colonyCount;
        public bool Sandbox => setup.Sandbox;
        /// <summary>The colony your orders apply to: yours in survival, the selected one in sandbox.</summary>
        public int CommandColony => Sandbox ? FocusColony : Player;
        /// <summary>Ants that exist as individuals right now (including wildlife).</summary>
        public int AntCount => ants.IsCreated ? ants.Length : 0;
        /// <summary>All ants, including those in sleeping colonies that only exist as numbers.</summary>
        public long TotalAnts
        {
            get
            {
                long t = 0;
                if (Colonies == null) return 0;
                foreach (var c in Colonies) t += c.Population + (c.HasQueen ? 1 : 0);
                return t;
            }
        }
        public int SleepingColonies
        {
            get
            {
                int n = 0;
                if (Colonies == null) return 0;
                foreach (var c in Colonies) if (c.Abstract) n++;
                return n;
            }
        }
        /// <summary>World-space area the player is looking at. Updated from the main camera every frame.</summary>
        public Rect ViewRect { get; set; }
        public bool HasView { get; set; }
        public long MapFood { get; private set; }
        public float SimTime { get; private set; }
        public float LastTickMs { get; private set; }
        public float LastRenderMs { get; private set; }
        public int TicksLastFrame { get; private set; }
        public bool Paused { get; set; }
        public int FocusColony { get; set; }
        public int PheromoneView { get; set; } = 1; // 0 off, 1 all, 2 focused colony

        // ---- Simulation data ----
        int slots; // colonyCount + 1 (wildlife)
        NativeList<Ant> ants;
        NativeArray<float> pheromones;
        NativeArray<int> food;
        NativeArray<int> leaves;
        NativeArray<byte> plantCap;
        NativeArray<int> delivered;
        NativeArray<int> deliveredLeaves;
        NativeArray<NestData> nests;
        NativeArray<float> starveRates;
        NativeArray<int> casteCounts;
        NativeArray<int> queenIndex;
        NativeArray<SpeciesStats> colonyStats;
        NativeArray<MorphStats> morphStats;   // [slot * MaxMorphs + morph]
        NativeArray<float4> morphPick;        // [colony * 2 + (soldier ? 1 : 0)]: cumulative shares of morph 1..3
        NativeArray<int> morphCounts;         // [slot * MaxMorphs + morph], live workers and soldiers
        readonly float[] morphSizes = new float[MaxColonies * MorphStats.MaxMorphs];
        NativeReference<long> sumResult;
        // Combat
        NativeArray<int> antBucket;
        NativeArray<int> sortedAnts;
        NativeArray<int> bucketStart;
        NativeArray<int> bucketCursor;
        NativeArray<CombatResult> combatResults;
        NativeArray<int> damage;
        NativeArray<byte> hostile;      // [a * MaxColonies + b] = 1 if a attacks b
        NativeArray<int> combatDeaths;  // per colony, this tick
        NativeArray<int> coreCombatDeaths; // per colony, this tick, inside the LOD core
        // Distress calls
        NativeArray<int> distress;
        NativeArray<byte> busyTrails;
        int busyW, busyH;
        NativeArray<float4> hotspots;
        int distressW, distressH;
        const float DistressCell = 8f;
        // LOD
        NativeArray<float> terrSum;
        NativeArray<float> terrMax;
        NativeArray<int> terrCount;
        NativeArray<int> intruders;     // per colony, summed over its nests
        NativeArray<int> removedCounts;
        NativeArray<byte> frozenTrails;
        NativeArray<float> removedAges;
        int ageBins;
        NativeReference<int> takenRef;
        float lodTimer;
        int tickIndex;
        HashInfo hash;
        GridInfo grid;
        Random rng;
        float tickAccumulator;
        float foodTimer;
        readonly Stopwatch stopwatch = new Stopwatch();

        // ---- Rendering ----
        NativeArray<float4> renderData;
        NativeArray<float4> colonyColors;
        NativeArray<float> gardenLevels;
        GraphicsBuffer antBuffer;
        GraphicsBuffer queenBuffer;
        Material queenMaterial;
        readonly Vector4[] queenData = new Vector4[MaxNests * 2];
        Material antMaterial;
        Mesh antMesh;
        Texture2D groundTexture;
        Material groundMaterial;
        readonly Vector4[] colorVectors = new Vector4[16];
        readonly float[] shapeValues = new float[16];
        static readonly int AntsId = Shader.PropertyToID("_Ants");
        static readonly int ColorsId = Shader.PropertyToID("_ColonyColors");
        static readonly int ShapesId = Shader.PropertyToID("_ColonyShapes");
        static readonly int MorphSizesId = Shader.PropertyToID("_MorphSizes");
        static readonly int WildId = Shader.PropertyToID("_WildColony");
        static readonly int AntLengthId = Shader.PropertyToID("_AntLength");
        static readonly int MinSizeId = Shader.PropertyToID("_MinSize");

        // Shows the start screen when you press Play in any scene that doesn't already have a world.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoCreate()
        {
            GameSettings.Load();
            GameSettings.Apply();
            if (FindObjectOfType<AntWorld>() == null && FindObjectOfType<AntGameMenu>() == null)
                new GameObject("AntGameMenu").AddComponent<AntGameMenu>();
        }

        /// <summary>Creates a world from start-screen choices.</summary>
        public static AntWorld Create(GameSetup s)
        {
            var go = new GameObject("AntWorld");
            go.SetActive(false); // so Awake runs after the setup is assigned
            var w = go.AddComponent<AntWorld>();
            w.setup = s;
            go.SetActive(true);
            return w;
        }

        void Awake()
        {
            if (setup == null)
            {
                // Placed in a scene by hand (or a test): use the Inspector fields, sandbox rules.
                setup = new GameSetup
                {
                    Rivals = math.clamp(colonyCount, 1, 7) - 1, MapSize = gridWidth, Sandbox = true, Seed = seed,
                    ColonyName = PaletteNames[0],
                };
            }
            colonyCount = math.clamp(setup.Rivals + 1, 1, MaxColonies - 1);
            gridWidth = gridHeight = math.max(128, setup.MapSize);
            seed = setup.Seed;
            slots = colonyCount + 1;

            grid = new GridInfo(gridWidth, gridHeight, cellSize);
            rng = new Random((uint)math.max(1, seed));

            ants = new NativeList<Ant>(65536, Allocator.Persistent);
            pheromones = new NativeArray<float>(colonyCount * Pheromone.ChannelCount * grid.CellCount, Allocator.Persistent);
            food = new NativeArray<int>(grid.CellCount, Allocator.Persistent);
            leaves = new NativeArray<int>(grid.CellCount, Allocator.Persistent);
            plantCap = new NativeArray<byte>(grid.CellCount, Allocator.Persistent);
            delivered = new NativeArray<int>(slots, Allocator.Persistent);
            deliveredLeaves = new NativeArray<int>(slots, Allocator.Persistent);
            nests = new NativeArray<NestData>(MaxNests, Allocator.Persistent);
            starveRates = new NativeArray<float>(slots, Allocator.Persistent);
            // Wildlife keeps its kind in the caste field, and there are more kinds of bug than castes.
            casteCounts = new NativeArray<int>(slots * Castes.Count + WildKinds.Count, Allocator.Persistent);
            queenIndex = new NativeArray<int>(MaxNests, Allocator.Persistent);
            for (int n = 0; n < MaxNests; n++) queenIndex[n] = -1;
            AllocateNestArrays();
            colonyStats = new NativeArray<SpeciesStats>(slots, Allocator.Persistent);
            morphStats = new NativeArray<MorphStats>(slots * MorphStats.MaxMorphs, Allocator.Persistent);
            for (int i = 0; i < morphStats.Length; i++) morphStats[i] = MorphStats.Plain;
            for (int i = 0; i < morphSizes.Length; i++) morphSizes[i] = 1f;
            morphPick = new NativeArray<float4>(slots * 2, Allocator.Persistent);
            morphCounts = new NativeArray<int>(slots * MorphStats.MaxMorphs, Allocator.Persistent);
            sumResult = new NativeReference<long>(Allocator.Persistent);
            colonyColors = new NativeArray<float4>(slots, Allocator.Persistent);
            gardenLevels = new NativeArray<float>(colonyCount, Allocator.Persistent);

            hash = new HashInfo
            {
                BucketsX = (int)math.ceil(grid.WorldSize.x / combat.BucketSize),
                BucketsY = (int)math.ceil(grid.WorldSize.y / combat.BucketSize),
                InvBucketSize = 1f / combat.BucketSize,
            };
            int bucketCount = slots * hash.BucketsPerColony;
            bucketStart = new NativeArray<int>(bucketCount + 1, Allocator.Persistent);
            bucketCursor = new NativeArray<int>(bucketCount, Allocator.Persistent);
            combatDeaths = new NativeArray<int>(slots, Allocator.Persistent);
            coreCombatDeaths = new NativeArray<int>(slots, Allocator.Persistent);
            hostile = new NativeArray<byte>(MaxColonies * MaxColonies, Allocator.Persistent);
            for (int a = 0; a < slots; a++)
            for (int b = 0; b < slots; b++)
                hostile[a * MaxColonies + b] = (byte)(a != b ? 1 : 0); // everyone starts at war; bugs fight everyone

            distressW = (int)math.ceil(grid.WorldSize.x / DistressCell);
            distressH = (int)math.ceil(grid.WorldSize.y / DistressCell);
            distress = new NativeArray<int>(colonyCount * distressW * distressH, Allocator.Persistent);
            hotspots = new NativeArray<float4>(colonyCount * AntStepJob.HotspotsPerColony, Allocator.Persistent);
            busyW = (grid.Width + 1) / 2;
            busyH = (grid.Height + 1) / 2;
            busyTrails = new NativeArray<byte>(colonyCount * busyW * busyH, Allocator.Persistent);

            terrSum = new NativeArray<float>(colonyCount, Allocator.Persistent);
            terrMax = new NativeArray<float>(colonyCount, Allocator.Persistent);
            terrCount = new NativeArray<int>(colonyCount, Allocator.Persistent);
            intruders = new NativeArray<int>(colonyCount, Allocator.Persistent);
            coreOwner = new NativeArray<short>(distressW * distressH, Allocator.Persistent);
            removedCounts = new NativeArray<int>(Castes.Count, Allocator.Persistent);
            frozenTrails = new NativeArray<byte>(colonyCount, Allocator.Persistent);
            ageBins = (int)math.ceil(economy.WorkerLifespanMax * math.max(1f, economy.SoldierLifespanMultiplier)
                                     / lod.AgeBinSeconds) + 2;
            removedAges = new NativeArray<float>(Castes.Count * ageBins, Allocator.Persistent);
            takenRef = new NativeReference<int>(Allocator.Persistent);

            AllocateEcology();
            AllocateNature();
            AllocateDiplomacy();
            if (pendingLoad != null)
            {
                try { LoadState(pendingLoad); }
                catch (System.Exception e)
                {
                    LastSaveError = "Couldn't load that save: " + e.Message;
                    enabled = false;
                    return;
                }
            }
            else
            {
                CreateColonies();
                StartDiplomacy();
                SetupEcology();
            }

            SetupRendering();
            SetupCameraAndUi();
        }

        void CreateColonies()
        {
            Colonies = new ColonyInfo[colonyCount];
            float2 center = grid.WorldSize * 0.5f;
            float ring = math.cmin(grid.WorldSize) * 0.32f;
            float startAngle = rng.NextFloat(0f, math.PI * 2f);

            // Player picks a colour; rivals take the rest of the palette in order.
            int playerColor = math.clamp(setup.ColorIndex, 0, Palette.Length - 1);
            var colorOrder = new System.Collections.Generic.List<int> { playerColor };
            for (int i = 0; i < Palette.Length; i++) if (i != playerColor) colorOrder.Add(i);

            var starts = new System.Collections.Generic.List<float2>();
            for (int c = 0; c < colonyCount; c++)
            {
                float a = startAngle + c * math.PI * 2f / colonyCount;
                // A little jitter so maps don't all look like a clock face.
                float r = ring * rng.NextFloat(0.85f, 1.1f);
                a += rng.NextFloat(-0.25f, 0.25f) * math.PI / colonyCount;
                starts.Add(colonyCount == 1 ? center : center + new float2(math.cos(a), math.sin(a)) * r);
            }
            GenerateTerrain(starts);

            for (int c = 0; c < colonyCount; c++)
            {
                float2 pos = starts[c];
                int ci = colorOrder[c];
                var col = Palette[ci];
                int species = c == Player ? math.clamp(setup.Species, 0, SpeciesDef.All.Length - 1)
                    : rng.NextInt(0, SpeciesDef.All.Length);
                var sp = SpeciesDef.All[species];
                Colonies[c] = new ColonyInfo
                {
                    Name = c == Player && !string.IsNullOrWhiteSpace(setup.ColonyName) ? setup.ColonyName : PaletteNames[ci],
                    Color = col,
                    NestPosition = pos,
                    Food = startFood,
                    Species = species,
                    IsPlayer = c == Player,
                    SoldierRatio = sp.SoldierRatio,
                    GardenLevel = sp.StartGarden,
                    ForageRate = lod.DefaultForageRate,
                    SleepWorkerAges = new float[ageBins],
                    SleepSoldierAges = new float[ageBins],
                    AutoSwarm = c != Player,
                    SwarmAt = c == Player ? 1200 : 900,
                    MaxNests = c == Player ? 5 : 4,
                };
                Colonies[c].PrimaryNest = AllocNest(c, pos, false);
                colonyColors[c] = new float4(col.r, col.g, col.b, 1f);
                colorVectors[c] = col;
                shapeValues[c] = sp.Shape;
                colonyStats[c] = sp.Stats;
                SetupMorphs(c, sp);

                SpawnAnts(c, 1, Caste.Queen, 0f);
                Colonies[c].HasQueen = true;
                SpawnAnts(c, startWorkers, Caste.Worker, sim.NestRadius);
            }

            // Wildlife slot.
            colonyStats[WildColony] = new SpeciesStats { SpeedMul = 1f, DamageMul = 1f, SenseMul = 1f, HealthMul = 1f };
            colonyColors[WildColony] = new float4(0.6f, 0.6f, 0.6f, 1f);
            colorVectors[WildColony] = new Color(0.6f, 0.6f, 0.6f);
        }

        public SpeciesDef SpeciesOf(int c) => SpeciesDef.All[Colonies[c].Species];

        /// <summary>Copies a species' extra castes into the per-colony tables the jobs and shader use.</summary>
        void SetupMorphs(int c, SpeciesDef sp)
        {
            // Cumulative thresholds per base caste. A slot of the other caste gets an empty interval.
            float4 workerCum = float4.zero, soldierCum = float4.zero;
            float wSum = 0f, sSum = 0f;
            for (int m = 0; m < MorphStats.MaxMorphs; m++)
            {
                var def = sp.Morph(m);
                morphStats[c * MorphStats.MaxMorphs + m] = def != null ? def.Stats : MorphStats.Plain;
                morphSizes[c * MorphStats.MaxMorphs + m] = def != null ? math.max(0.2f, def.Size) : 1f;
                if (m == 0) continue;
                if (def != null && def.Base == Caste.Soldier) sSum = math.min(1f, sSum + def.Share);
                else if (def != null) wSum = math.min(1f, wSum + def.Share);
                workerCum[m - 1] = wSum;
                soldierCum[m - 1] = sSum;
            }
            morphPick[c * 2] = workerCum;
            morphPick[c * 2 + 1] = soldierCum;
        }

        /// <summary>Live ants of a colony per caste variant (slot 0 plain, 1..3 the species' extra castes).</summary>
        public int MorphCount(int c, int slot) => morphCounts[c * MorphStats.MaxMorphs + slot];

        /// <summary>How many of a colony's ants are of an extra caste with this perk (estimated while asleep).</summary>
        int PerkCount(int c, MorphPerk perk)
        {
            var col = Colonies[c];
            var sp = SpeciesOf(c);
            int n = 0;
            for (int m = 1; m < MorphStats.MaxMorphs; m++)
            {
                var def = sp.Morph(m);
                if (def == null || def.Perk != perk) continue;
                n += col.Abstract
                    ? (int)((def.Base == Caste.Soldier ? col.Soldiers : col.Workers) * def.Share)
                    : morphCounts[c * MorphStats.MaxMorphs + m];
            }
            return n;
        }

        // ------------------------------------------------------------------ loop

        void Update()
        {
            if (Colonies == null) return;
            HandleInput();
            if (!Paused) UpdateAutosave(Time.unscaledDeltaTime);
            UpdateViewRect();

            TicksLastFrame = 0;
            if (!Paused && State == GameState.Playing)
            {
                float tickDt = 1f / ticksPerSecond;
                tickAccumulator += math.min(Time.unscaledDeltaTime, 0.1f) * simSpeed;
                // If we fall behind, drop time rather than spiral.
                tickAccumulator = math.min(tickAccumulator, tickDt * maxTicksPerFrame);

                stopwatch.Restart();
                while (tickAccumulator >= tickDt)
                {
                    Tick(tickDt);
                    tickAccumulator -= tickDt;
                    TicksLastFrame++;
                }
                stopwatch.Stop();
                if (TicksLastFrame > 0)
                    LastTickMs = (float)stopwatch.Elapsed.TotalMilliseconds / TicksLastFrame;
            }

            stopwatch.Restart();
            Render();
            stopwatch.Stop();
            LastRenderMs = (float)stopwatch.Elapsed.TotalMilliseconds;
        }

        void UpdateViewRect()
        {
            var cam = Camera.main;
            if (cam == null || !cam.orthographic) { HasView = false; return; }
            float h = cam.orthographicSize;
            float w = h * cam.aspect;
            Vector3 p = cam.transform.position;
            ViewRect = new Rect(p.x - w, p.y - h, w * 2f, h * 2f);
            HasView = true;
        }

        void Tick(float dt)
        {
            lodTimer += dt;
            if (lodTimer >= lod.CheckInterval)
            {
                lodTimer = 0f;
                EvaluateLod();
                UpdateDistress(lod.CheckInterval);
                new BusyTrailsJob { Pheromones = pheromones, Busy = busyTrails, Grid = grid, BusyW = busyW, BusyH = busyH }
                    .Schedule(busyTrails.Length, 4096).Complete();
            }
            tickIndex++;

            int n = ants.Length;
            EnsureSimCapacity(n);
            for (int c = 0; c < colonyCount; c++)
            {
                var col = Colonies[c];
                starveRates[c] = col.Starving ? economy.StarveDeathRate : 0f;
                frozenTrails[c] = (byte)(col.Abstract ? 1 : 0);
            }
            // Colony-wide orders and rules apply to every nest the colony owns.
            for (int c = 0; c < colonyCount; c++) { Colonies[c].Squads = 0; Colonies[c].Drones = 0; Colonies[c].DroneSquads = 0; }
            for (int k = 0; k < nestHigh; k++)
            {
                var info = nestInfo[k];
                if (info == null) continue;
                var col = Colonies[info.Colony];
                var nest = nests[k];
                nest.RaidActive = (byte)(col.RaidActive ? 1 : 0);
                nest.RaidTarget = col.RaidTarget;
                nest.HasQueen = (byte)(info.HasQueen || info.Founding ? 1 : 0);
                nest.CoreRadius = math.max(lod.MinTerritory, col.TerritoryRadius * lod.CoreFraction);
                nest.GatherLeaves = (byte)(SpeciesOf(info.Colony).LeafEater
                    || (col.GardenLevel > 0 && col.Leaves < farming.LeafStorePerLevel * col.GardenLevel) ? 1 : 0);
                nest.DistressResponse = (byte)math.clamp((int)math.round(col.DistressResponse * 100f), 0, 100);
                nest.ScoutPercent = (byte)math.clamp((int)math.round(col.ScoutShare * 100f), 0, 100);
                nest.DronePercent = (byte)math.clamp((int)math.round(col.DroneShare * 100f), 0, 100);
                // Squads of about SquadSize (drones: DroneSquadSize). Only re-split when the count is clearly
                // off, so squads don't reshuffle every time a soldier is born or dies.
                int drones = nestDrones[k], guards = nestSoldiers[k] - drones;
                nest.SquadCount = Resquad(nest.SquadCount, guards, sim.SquadSize, NestData.MaxGuardSquads);
                nest.DroneSquadCount = Resquad(nest.DroneSquadCount, drones, sim.DroneSquadSize, NestData.MaxDroneSquads);
                if (!col.Abstract && guards > 0) col.Squads += nest.SquadCount;
                if (!col.Abstract && drones > 0) { col.Drones += drones; col.DroneSquads += nest.DroneSquadCount; }
                nests[k] = nest;
            }
            UnsafeUtility.MemClear(damage.GetUnsafePtr(), (long)n * sizeof(int));

            // Combat: hash ants by (colony, bucket), then every ant looks for hostile neighbours.
            JobHandle h = new AssignBucketsJob { Ants = ants.AsArray(), AntBucket = antBucket, Hash = hash }
                .Schedule(n, 2048);
            h = new BuildHashJob
            {
                AntBucket = antBucket, BucketStart = bucketStart, Cursor = bucketCursor, Sorted = sortedAnts, Count = n,
            }.Schedule(h);
            h = new CombatJob
            {
                Ants = ants.AsArray(),
                Sorted = sortedAnts,
                BucketStart = bucketStart,
                Hostile = hostile,
                Results = combatResults,
                Damage = (int*)damage.GetUnsafePtr(),
                Stats = colonyStats,
                Morphs = morphStats,
                Hash = hash,
                C = combat,
                W = wildlife,
                ColonyCount = slots,
                MaxColonies = MaxColonies,
                WildColony = WildColony,
                PairDamage = (int*)pairDamage.GetUnsafePtr(),
            }.Schedule(n, 512, h);
            h = ScheduleDefences(h, dt);

            EnsureHomeFields();
            var step = new AntStepJob
            {
                Ants = ants.AsArray(),
                HomeField = homeField,
                FieldW = fieldW,
                FieldH = fieldH,
                FieldInvScale = 1f / FieldScale,
                RaidFieldBase = MaxNests,
                Pheromones = pheromones,
                Food = (int*)food.GetUnsafePtr(),
                Leaves = (int*)leaves.GetUnsafePtr(),
                Delivered = (int*)delivered.GetUnsafePtr(),
                DeliveredLeaves = (int*)deliveredLeaves.GetUnsafePtr(),
                Nests = nests,
                Terrain = terrain,
                StarveRates = starveRates,
                Combat = combatResults,
                Damage = damage,
                Stats = colonyStats,
                Morphs = morphStats,
                CombatDeaths = (int*)combatDeaths.GetUnsafePtr(),
                CoreCombatDeaths = (int*)coreCombatDeaths.GetUnsafePtr(),
                Distress = (int*)distress.GetUnsafePtr(),
                DistressW = distressW,
                DistressH = distressH,
                DistressPerColony = distressW * distressH,
                DistressInvCell = 1f / DistressCell,
                Hotspots = hotspots,
                Grid = grid,
                P = sim,
                C = combat,
                W = wildlife,
                WildColony = WildColony,
                SquadSums = squadSums,
                Time = SimTime,
                Hostile = hostile,
                MaxColonies = MaxColonies,
                Busy = busyTrails,
                BusyW = busyW,
                BusyH = busyH,
                PrincessSpeed = swarm.PrincessSpeed,
                MaleSpeed = swarm.MaleSpeed,
                Sites = nestSites,
                QueenPos = queenPos,
                Accel = 1f - math.exp(-sim.Acceleration * dt),
                Dt = dt,
                TrailDecay = math.pow(0.5f, dt / sim.TrailHalfLife),
            };
            h = step.Schedule(n, 512, h);
            // Evaporate a quarter of the pheromone grid per tick (with 4 ticks' worth of decay).
            // Same result over time, a quarter of the memory traffic.
            const int evapSlices = 4;
            float evapDt = dt * evapSlices;
            int slice = (pheromones.Length + evapSlices - 1) / evapSlices;
            int sliceStart = (tickIndex % evapSlices) * slice;
            int sliceCount = math.min(slice, pheromones.Length - sliceStart);
            h = new EvaporateJob
            {
                Pheromones = pheromones,
                Factors = new float4(
                    math.pow(0.5f, evapDt / sim.PheromoneHalfLife),
                    math.pow(0.5f, evapDt / sim.PheromoneHalfLife),
                    math.pow(0.5f, evapDt / combat.AlarmHalfLife),
                    math.pow(0.5f, evapDt / AntStepJob.RouteHalfLife)),
                CellCount = grid.CellCount,
                Offset = sliceStart,
                Frozen = frozenTrails,
            }.Schedule(sliceCount, 16384, h);
            h = MakeCompactJob().Schedule(h);
            h.Complete();

            UpdateNests(dt);
            UpdateMoves(dt);

            for (int c = 0; c < colonyCount; c++)
                UpdateColony(c, dt);

            SimTime += dt;
            UpdateEcology(dt);

            if (Sandbox)
            {
                foodTimer += dt;
                if (foodTimer >= foodCheckInterval)
                {
                    foodTimer = 0f;
                    RespawnFood();
                }
            }
        }

        /// <summary>Colony "brain": economy, queen, brood and caste rules. Works on totals only.</summary>
        void UpdateColony(int c, float dt)
        {
            ColonyInfo col = Colonies[c];
            var sp = SpeciesOf(c);
            int countBase = c * Castes.Count;
            int liveWorkers = casteCounts[countBase + (int)Caste.Worker];
            int liveSoldiers = casteCounts[countBase + (int)Caste.Soldier];
            col.LiveAnts = liveWorkers + liveSoldiers + casteCounts[countBase + (int)Caste.Queen];
            if (col.Abstract) UpdateSleepingColony(c, col, dt);
            col.Workers = liveWorkers + (int)math.round(col.AbstractWorkers);
            col.Soldiers = liveSoldiers + (int)math.round(col.AbstractSoldiers);

            bool queenAlive = casteCounts[countBase + (int)Caste.Queen] > 0;
            if (col.HasQueen && !queenAlive && !col.JustCrownedQueen) col.QueensLost++;
            col.HasQueen = queenAlive || col.JustCrownedQueen;
            col.JustCrownedQueen = false;
            col.QueenHealth = queenAlive ? QueenHealthOf(c) : 0f;

            int got = delivered[c];
            delivered[c] = 0;
            int gotLeaves = deliveredLeaves[c];
            deliveredLeaves[c] = 0;
            col.Food += got * economy.FoodPerDelivery * sp.ForageYield;
            col.TotalDelivered += got;
            col.LeavesDelivered += gotLeaves;
            if (sp.LeafEater)
            {
                // Eaten straight away, no garden needed.
                float leafFood = gotLeaves * farming.FoodPerLeaf * sp.GardenYield;
                col.Food += leafFood;
                col.LeafIncome = math.lerp(col.LeafIncome, leafFood / dt, math.saturate(dt / 5f));
            }
            else col.Leaves += gotLeaves;
            col.CombatDeaths += combatDeaths[c];
            // Only fighting near the nest keeps a colony awake; border skirmishes don't.
            if (coreCombatDeaths[c] > 0) col.LastCombatTime = SimTime;
            combatDeaths[c] = 0;
            coreCombatDeaths[c] = 0;

            // Learn how productive this colony's foragers are while we can watch them,
            // so the sleeping model forages at a realistic rate.
            if (!col.Abstract) col.ObservedTime += dt;
            bool steady = col.ObservedTime > lod.WarmupTime && SimTime - col.LastWakeTime > lod.WakeSettleTime;
            if (!col.Abstract && liveWorkers > 0 && steady)
            {
                float sample = (got + gotLeaves) / (dt * liveWorkers);
                col.ForageRate = math.lerp(col.ForageRate, sample, math.saturate(dt / lod.ForageRateSmoothing));
            }

            // Fungus garden: gardeners turn stored leaves into food.
            // Gardener castes speed the garden up: +0.5% each, at most double.
            col.Gardeners = PerkCount(c, MorphPerk.Gardener);
            float gardenBoost = 1f + math.min(1f, col.Gardeners * 0.005f);
            float process = math.min(col.Leaves, col.GardenLevel * farming.GardenLeavesPerLevel * gardenBoost * dt);
            col.Leaves -= process;
            float gardenFood = process * farming.FoodPerLeaf * sp.GardenYield;
            col.Food += gardenFood;
            col.GardenIncome = math.lerp(col.GardenIncome, gardenFood / dt, math.saturate(dt / 5f));

            int pop = col.Population;
            col.Food -= pop * economy.UpkeepPerAntPerSecond * dt;
            col.Starving = col.Food < 0f;
            if (col.Starving) col.Food = 0f;

            UpdateBrood(c, col, dt);

            if (col.HasQueen) LayEggs(col, sp, pop, dt);
            else RaiseReplacementQueen(c, col, dt);

            // A queen alone, with no brood and too little food to lay even one egg, can never recover: she starves
            // after a minute and the colony dies out (before, such a colony lingered forever and the game never ended).
            bool hopeless = col.HasQueen && pop == 0 && col.BroodTotal < 0.5f && col.Food < economy.AntCost * sp.AntCost
                            && col.SwarmState == 0;
            col.HopelessTime = hopeless ? col.HopelessTime + dt : 0f;
            if (col.HopelessTime > 60f)
            {
                col.HopelessTime = 0f;
                new KillQueensJob { Ants = ants.AsArray(), Colony = c }.Run();
                Log($"The queen of {col.Name} starved alone.", c == Player);
            }

            col.Extinct = !col.HasQueen && pop == 0 && col.BroodTotal < 0.5f;
        }

        /// <summary>Queen lays eggs: no population cap, limited by lay rate and food.</summary>
        void LayEggs(ColonyInfo col, SpeciesDef sp, int pop, float dt)
        {
            // Every nest with a queen adds to the colony's laying (with diminishing returns).
            float queens = 1f + 0.5f * math.max(0, col.Queens - 1);
            float layRate = (economy.BaseLayRate + pop * economy.LayRatePerAnt) * sp.LayRate * queens;
            col.LayAccumulator = math.min(col.LayAccumulator + layRate * dt, layRate + 1f);

            // Budget the full cost of raising each egg (egg + larva food) on top of a reserve,
            // so a laying spree never tips the colony into starvation.
            float antCost = economy.AntCost * sp.AntCost;
            float reserve = pop * economy.UpkeepPerAntPerSecond * economy.FoodReserveSeconds
                            + col.Larvae * LarvaFoodPerSecond(sp, col) * economy.LarvaDuration;
            int canAfford = (int)math.max(0f, (col.Food - reserve) / antCost);
            int eggs = math.min((int)col.LayAccumulator, canAfford);
            if (eggs <= 0) return;

            col.LayAccumulator -= eggs;
            col.Food -= eggs * economy.EggCost;

            // Caste rule set by the player: keep roughly SoldierRatio soldiers, counting brood too.
            float future = pop + col.BroodWorkers + col.BroodSoldiers + eggs;
            int soldiersWanted = (int)math.round(future * col.SoldierRatio - col.Soldiers - col.BroodSoldiers);
            int soldiers = math.clamp(soldiersWanted, 0, eggs);

            // Eggs laid within the same second share a cohort.
            var brood = col.Brood;
            if (brood.Count > 0 && brood[brood.Count - 1].Age < 1f)
            {
                var last = brood[brood.Count - 1];
                last.Workers += eggs - soldiers;
                last.Soldiers += soldiers;
                brood[brood.Count - 1] = last;
            }
            else
            {
                brood.Add(new BroodCohort { Age = 0f, Workers = eggs - soldiers, Soldiers = soldiers });
            }
            col.Eggs += eggs;
            col.BroodWorkers += eggs - soldiers;
            col.BroodSoldiers += soldiers;
        }

        float LarvaFoodPerSecond(SpeciesDef sp, ColonyInfo col = null) =>
            math.max(0f, economy.AntCost * sp.AntCost - economy.EggCost) / math.max(0.01f, economy.LarvaDuration)
            * (col == null ? 1f : 1f - math.min(0.35f, col.Nurses * 0.002f)); // nurses: up to 35% cheaper larvae

        /// <summary>Ages brood: eggs -> larvae (must be fed) -> pupae -> adults.</summary>
        void UpdateBrood(int c, ColonyInfo col, float dt)
        {
            var brood = col.Brood;
            float larvaStart = economy.EggDuration;
            float pupaStart = larvaStart + economy.LarvaDuration;
            float hatchAge = economy.BroodDuration;

            // Feed larvae. If the stores can't cover them, some larvae die.
            col.Nurses = PerkCount(c, MorphPerk.Nurse);
            float need = col.Larvae * LarvaFoodPerSecond(SpeciesOf(c), col) * dt;
            float starveFraction = 0f;
            col.BroodStarving = false;
            if (need > 0f)
            {
                if (col.Food >= need) col.Food -= need;
                else
                {
                    starveFraction = (1f - col.Food / need) * economy.LarvaStarveRate * dt;
                    col.Food = 0f;
                    col.BroodStarving = true;
                }
            }

            col.Eggs = col.Larvae = col.Pupae = 0f;
            col.BroodWorkers = col.BroodSoldiers = 0f;
            int hatched = 0;
            for (int i = 0; i < brood.Count; i++)
            {
                var b = brood[i];
                b.Age += dt;
                bool larva = b.Age >= larvaStart && b.Age < pupaStart;
                if (larva && starveFraction > 0f)
                {
                    b.Workers *= 1f - starveFraction;
                    b.Soldiers *= 1f - starveFraction;
                }

                if (b.Age >= hatchAge)
                {
                    // Cohorts are ordered oldest first, so hatched ones are always at the front.
                    int w = (int)math.round(b.Workers);
                    int so = (int)math.round(b.Soldiers);
                    AddAdults(c, col, w, so);
                    col.Workers += w;
                    col.Soldiers += so;
                    col.TotalBorn += w + so;
                    hatched++;
                    continue;
                }

                brood[i] = b;
                float total = b.Workers + b.Soldiers;
                if (b.Age < larvaStart) col.Eggs += total;
                else if (larva) col.Larvae += total;
                else col.Pupae += total;
                col.BroodWorkers += b.Workers;
                col.BroodSoldiers += b.Soldiers;
            }
            if (hatched > 0) brood.RemoveRange(0, hatched);
        }

        /// <summary>A queenless colony can feed a larva royal jelly and raise a new queen, if the player allows it.</summary>
        void RaiseReplacementQueen(int c, ColonyInfo col, float dt)
        {
            if (!col.RaiseNewQueen || col.Population < economy.NewQueenMinPopulation)
            {
                col.NewQueenProgress = 0f;
                return;
            }
            if (col.Food < economy.NewQueenCost * 0.25f) return; // stalls while stores are low

            col.NewQueenProgress += dt / economy.NewQueenTime;
            if (col.NewQueenProgress < 1f || col.Food < economy.NewQueenCost) return;

            col.Food -= economy.NewQueenCost;
            col.NewQueenProgress = 0f;
            SpawnAntsAtNest(c, ReplacementQueenNest(c), 1, Caste.Queen, 0f);
            col.JustCrownedQueen = true; // counted by CompactJob from next tick
            col.HasQueen = true;
        }

        /// <summary>New adults from brood: real ants if the colony is awake, numbers if it's asleep.</summary>
        void AddAdults(int c, ColonyInfo col, int workers, int soldiers)
        {
            if (col.Abstract)
            {
                col.SleepWorkerAges[0] += workers;
                col.SleepSoldierAges[0] += soldiers;
                col.AbstractWorkers += workers;
                col.AbstractSoldiers += soldiers;
                return;
            }
            SpawnNewborn(c, workers, Caste.Worker);
            SpawnNewborn(c, soldiers, Caste.Soldier);
        }

        // ------------------------------------------------------------------ level of detail

        /// <summary>Decides which colonies sleep (numbers only) and which wake (real ants).</summary>
        void EvaluateLod()
        {
            new TerritoryJob
            {
                Ants = ants.AsArray(),
                Nests = nests,
                Hostile = hostile,
                CoreOwner = coreOwner,
                OwnerW = distressW,
                OwnerH = distressH,
                OwnerCell = DistressCell,
                SumDist = terrSum,
                MaxDist = terrMax,
                Count = terrCount,
                NestIntruders = nestIntruders,
                NestCount = nestHigh,
                ColonyCount = colonyCount,
                MaxColonies = MaxColonies,
                BigBugWeight = lod.WakeIntruders,
            }.Run();
            for (int c = 0; c < colonyCount; c++) intruders[c] = 0;
            for (int k = 0; k < nestHigh; k++)
                if (nestInfo[k] != null) intruders[nestInfo[k].Colony] += nestIntruders[k];

            Rect view = HasView
                ? new Rect(ViewRect.x - lod.ViewMargin, ViewRect.y - lod.ViewMargin,
                    ViewRect.width + lod.ViewMargin * 2f, ViewRect.height + lod.ViewMargin * 2f)
                : default;

            for (int c = 0; c < colonyCount; c++)
            {
                var col = Colonies[c];

                // Territory: how far the colony's ants range (only measurable while awake).
                if (!col.Abstract && terrCount[c] > 20)
                {
                    float mean = terrSum[c] / terrCount[c];
                    float measured = math.max(lod.MinTerritory, math.min(terrMax[c], mean * 2f) + 10f);
                    // Smooth it: a few lost wanderers shouldn't make the territory jump around.
                    col.TerritoryRadius = col.ObservedTime < 10f ? measured : math.lerp(col.TerritoryRadius, measured, 0.2f);
                    col.MeasuredTerritory = col.TerritoryRadius;
                }

                bool visible = !HasView;
                bool targeted = false;
                for (int k = 0; k < nestHigh; k++)
                {
                    var info = nestInfo[k];
                    if (info == null || info.Colony != c) continue;
                    float2 nest = nests[k].Position;
                    if (HasView && CircleIntersectsRect(nest, math.max(lod.MinTerritory, col.TerritoryRadius * lod.VisibleFraction), view))
                        visible = true;
                    for (int o = 0; o < colonyCount; o++)
                    {
                        var other = Colonies[o];
                        if (o != c && other.RaidActive &&
                            math.distance((float2)other.RaidTarget, nest) < col.TerritoryRadius)
                            targeted = true;
                    }
                }

                col.LodIntruders = intruders[c];
                col.LodVisible = visible;
                bool threatened = intruders[c] >= lod.WakeIntruders || targeted ||
                                  SimTime - col.LastCombatTime < lod.CombatCooldown;

                // A rival's hunting trip doesn't keep it awake (the hunt is simply called off); war raids do.
                bool raiding = col.RaidActive && (col.IsPlayer || col.AiRaidKind == 0);
                // A princess in flight is a real ant with an escort; let the swarm finish first.
                raiding |= col.SwarmState == 2;

                // The player's own colony is always simulated in full.
                if (col.Abstract)
                {
                    if (!lod.Enabled || visible || threatened || c == Player) Wake(c);
                }
                else if (lod.Enabled && c != Player && !visible && !threatened && !raiding &&
                         col.ObservedTime >= lod.MinObservedTime && SimTime - col.LastWakeTime >= lod.MinAwakeTime)
                {
                    col.SleepTimer += lod.CheckInterval;
                    if (col.SleepTimer >= lod.SleepDelay) Sleep(c);
                }
                else
                {
                    col.SleepTimer = 0f;
                }
            }
        }

        static bool CircleIntersectsRect(float2 center, float radius, Rect r)
        {
            float cx = math.clamp(center.x, r.xMin, r.xMax);
            float cy = math.clamp(center.y, r.yMin, r.yMax);
            return math.distancesq(center, new float2(cx, cy)) <= radius * radius;
        }

        /// <summary>Removes a colony's workers and soldiers from the world and keeps them as numbers.</summary>
        public void Sleep(int c)
        {
            var col = Colonies[c];
            if (col.Abstract) return;
            new RemoveColonyJob
            {
                Ants = ants.AsArray(),
                Removed = removedCounts,
                AgeBins = removedAges,
                Bins = ageBins,
                InvBinSize = 1f / lod.AgeBinSeconds,
                Colony = c,
                NestShare = nestShare,
            }.Run();
            MakeCompactJob().Run();
            StoreSleepShares(c);

            System.Array.Clear(col.SleepWorkerAges, 0, ageBins);
            System.Array.Clear(col.SleepSoldierAges, 0, ageBins);
            for (int b = 0; b < ageBins; b++)
            {
                col.SleepWorkerAges[b] = removedAges[(int)Caste.Worker * ageBins + b];
                col.SleepSoldierAges[b] = removedAges[(int)Caste.Soldier * ageBins + b];
            }
            col.AgeBinTimer = 0f;
            if (col.RaidActive && col.AiRaidKind > 0) CancelRaid(c);
            col.AbstractWorkers = removedCounts[(int)Caste.Worker];
            col.AbstractSoldiers = removedCounts[(int)Caste.Soldier];
            col.Abstract = true;
            col.SleepTimer = 0f;
            col.SleepCount++;
            col.ForageTimer = 0f;
            col.PendingForage = 0f;
        }

        /// <summary>Turns a sleeping colony's numbers back into real ants spread over its territory.</summary>
        public void Wake(int c)
        {
            var col = Colonies[c];
            if (!col.Abstract) return;
            float bin = lod.AgeBinSeconds;
            // Each nest gets back the share of ants it had when the colony fell asleep.
            for (int k = 0; k < nestHigh; k++)
            {
                var info = nestInfo[k];
                if (info == null || info.Colony != c || info.SleepShare <= 0f) continue;
                float2 nest = nests[k].Position;
                float share = info.SleepShare;
                float carryW = 0f, carryS = 0f;
                for (int b = 0; b < ageBins; b++)
                {
                    float ageLo = b * bin + col.AgeBinTimer;
                    carryW += col.SleepWorkerAges[b] * share;
                    int w = (int)carryW;
                    carryW -= w;
                    SpawnAntsAt(c, w, Caste.Worker, nest, col.TerritoryRadius * 0.85f, true, ageLo, ageLo + bin, true, k);

                    carryS += col.SleepSoldierAges[b] * share;
                    int s = (int)carryS;
                    carryS -= s;
                    SpawnAntsAt(c, s, Caste.Soldier, nest, sim.NestRadius * sim.SoldierPatrolRange, false, ageLo, ageLo + bin, false, k);
                }
            }
            System.Array.Clear(col.SleepWorkerAges, 0, ageBins);
            System.Array.Clear(col.SleepSoldierAges, 0, ageBins);
            col.AbstractWorkers = 0f;
            col.AbstractSoldiers = 0f;
            col.Abstract = false;
            col.SleepTimer = 0f;
            col.LastWakeTime = SimTime;
            col.WakeCount++;
        }

        /// <summary>Statistical model for a sleeping colony: ageing, starvation and foraging.</summary>
        void UpdateSleepingColony(int c, ColonyInfo col, float dt)
        {
            // Age-structured deaths: lifespans are uniform in [min, max], so an ant of age a dies with
            // hazard 1 / (max - a) once a > min. A young, growing colony therefore barely loses anyone.
            var sp = SpeciesOf(c);
            float starve = col.Starving ? economy.StarveDeathRate : 0f;
            float lifeMin = economy.WorkerLifespanMin * sp.Lifespan;
            float lifeMax = economy.WorkerLifespanMax * sp.Lifespan;
            float mul = economy.SoldierLifespanMultiplier;
            col.AbstractWorkers = AgeSleepingBins(col.SleepWorkerAges, col.AgeBinTimer, lifeMin, lifeMax, starve, dt);
            col.AbstractSoldiers = AgeSleepingBins(col.SleepSoldierAges, col.AgeBinTimer, lifeMin * mul, lifeMax * mul, starve, dt);
            col.AgeBinTimer += dt;
            if (col.AgeBinTimer >= lod.AgeBinSeconds)
            {
                col.AgeBinTimer -= lod.AgeBinSeconds;
                ShiftBins(col.SleepWorkerAges);
                ShiftBins(col.SleepSoldierAges);
            }

            col.PendingForage += col.AbstractWorkers * col.ForageRate * dt;
            col.ForageTimer += dt;
            if (col.ForageTimer < lod.ForageInterval) return;
            col.ForageTimer = 0f;

            int want = (int)col.PendingForage;
            if (want <= 0) return;
            col.PendingForage -= want; // anything not found on the map is simply lost
            int taken = ForageAllNests(food, c, want);
            col.Food += taken * economy.FoodPerDelivery * sp.ForageYield;
            col.TotalDelivered += taken;

            // Leafcutters fill the rest of the trips with leaves, while the garden has room.
            int leafTaken = 0;
            if (taken < want && col.GardenLevel > 0 && col.Leaves < farming.LeafStorePerLevel * col.GardenLevel)
            {
                leafTaken = ForageAllNests(leaves, c, want - taken);
                col.Leaves += leafTaken;
                col.LeavesDelivered += leafTaken;
            }
            int total = taken + leafTaken;
            col.ForageWanted += want;
            col.ForageTaken += total;

            // Live ants that run out of nearby food range further; mimic that, within limits.
            float maxR = col.MeasuredTerritory * lod.MaxTerritoryGrowth;
            if (total < want * 0.8f) col.TerritoryRadius = math.min(maxR, col.TerritoryRadius * 1.05f);
            else if (total >= want) col.TerritoryRadius = math.max(col.MeasuredTerritory, col.TerritoryRadius * 0.995f);
        }

        /// <summary>Sleeping foragers search around every nest, in proportion to how many ants live there.</summary>
        int ForageAllNests(NativeArray<int> grid01, int c, int want)
        {
            int taken = 0;
            for (int k = 0; k < nestHigh; k++)
            {
                var info = nestInfo[k];
                if (info == null || info.Colony != c || info.Founding || info.SleepShare <= 0f) continue;
                int part = (int)math.ceil(want * info.SleepShare);
                taken += ForageFrom(grid01, nests[k].Position, Colonies[c].TerritoryRadius, math.min(part, want - taken));
            }
            return taken;
        }

        int ForageFrom(NativeArray<int> grid01, float2 center, float radius, int want)
        {
            new ForageAbstractJob
            {
                Food = grid01,
                Taken = takenRef,
                Grid = grid,
                Center = center,
                Radius = radius,
                Want = want,
                Seed = rng.NextUInt(1, uint.MaxValue),
            }.Run();
            return takenRef.Value;
        }

        float AgeSleepingBins(float[] bins, float timer, float lifeMin, float lifeMax, float starve, float dt)
        {
            float bin = lod.AgeBinSeconds;
            float total = 0f;
            for (int b = 0; b < bins.Length; b++)
            {
                float n = bins[b];
                if (n <= 0f) continue;
                float age = b * bin + timer + bin * 0.5f;
                float hazard = age < lifeMin ? 0f : age >= lifeMax ? 1f / dt : 1f / math.max(dt, lifeMax - age);
                n *= math.max(0f, 1f - (hazard + starve) * dt);
                bins[b] = n;
                total += n;
            }
            return total;
        }

        static void ShiftBins(float[] bins)
        {
            for (int b = bins.Length - 1; b > 0; b--) bins[b] = bins[b - 1];
            bins[0] = 0f;
        }

        /// <summary>Adds newborn ants to a sleeping colony (also handy for tests and scenario setup).</summary>
        public void AddSleepingAnts(int c, Caste caste, float count)
        {
            var col = Colonies[c];
            if (!col.Abstract) { SpawnAnts(c, (int)count, caste, sim.NestRadius); return; }
            if (caste == Caste.Soldier) { col.SleepSoldierAges[0] += count; col.AbstractSoldiers += count; }
            else { col.SleepWorkerAges[0] += count; col.AbstractWorkers += count; }
        }

        // ------------------------------------------------------------------ commands (callable from UI / input)

        /// <summary>Adds ants at the colony's main nest.</summary>
        public void SpawnAnts(int colony, int count, Caste caste, float scatter)
        {
            SpawnAntsAtNest(colony, Colonies[colony].PrimaryNest, count, caste, scatter);
        }

        public void SpawnAntsAtNest(int colony, int nest, int count, Caste caste, float scatter)
        {
            SpawnAntsAt(colony, count, caste, nests[nest].Position, scatter, false, 0f, 0f, false, nest);
        }

        void SpawnAntsAt(int colony, int count, Caste caste, float2 center, float radius, bool trailFromCenter,
            float ageMin, float ageMax, bool onTrails = false, int nest = -1, byte state = AntState.Searching)
        {
            if (count <= 0) return;
            if (nest < 0) nest = Colonies[colony].PrimaryNest;
            var sp = SpeciesOf(colony);
            float lifeMul = (caste == Caste.Soldier ? economy.SoldierLifespanMultiplier : 1f) * sp.Lifespan;
            float health = caste == Caste.Queen ? combat.QueenHealth
                : caste == Caste.Princess ? swarm.PrincessHealth
                : caste == Caste.Soldier ? combat.SoldierHealth : combat.WorkerHealth;
            float lifeMin = economy.WorkerLifespanMin * lifeMul, lifeMax = economy.WorkerLifespanMax * lifeMul;
            if (caste == Caste.Male) { lifeMin = swarm.MaleLife * 0.6f; lifeMax = swarm.MaleLife; }
            new SpawnAntsJob
            {
                Ants = ants,
                Colony = colony,
                Nest = nest,
                State = state,
                Count = count,
                Caste = (byte)caste,
                Center = center,
                Radius = radius,
                WorldMax = grid.WorldSize,
                Seed = rng.NextUInt(1, uint.MaxValue),
                LifeMin = lifeMin,
                LifeMax = lifeMax,
                Health = health * sp.Health,
                MorphPick = caste == Caste.Worker ? morphPick[colony * 2]
                    : caste == Caste.Soldier ? morphPick[colony * 2 + 1] : new float4(-1f),
                MorphHealth = new float4(1f, MorphHealthOf(colony, 1), MorphHealthOf(colony, 2), MorphHealthOf(colony, 3)),
                AgeMin = ageMin,
                AgeMax = ageMax,
                TrailFromCenter = trailFromCenter,
                Speed = sim.WorkerSpeed,
                TrailHalfLife = sim.TrailHalfLife,
                OnTrails = onTrails,
                Pheromones = pheromones,
                Terrain = terrain,
                Grid = grid,
            }.Run();
        }

        float MorphHealthOf(int colony, int slot) => morphStats[colony * MorphStats.MaxMorphs + slot].HealthMul;

        /// <summary>Adds bugs of one kind around a point.</summary>
        public void SpawnWildlife(WildKind kind, int count, float2 center, float radius)
        {
            if (count <= 0) return;
            int k = (int)kind;
            float life = wildlife.Life(k);
            new SpawnAntsJob
            {
                Ants = ants,
                Colony = WildColony,
                Count = count,
                Caste = (byte)kind,
                Center = center,
                Radius = radius,
                WorldMax = grid.WorldSize,
                Seed = rng.NextUInt(1, uint.MaxValue),
                LifeMin = life * 0.6f,
                LifeMax = life,
                Health = wildlife.Health(k),
                MorphPick = new float4(-1f),
                Speed = sim.WorkerSpeed,
                TrailHalfLife = sim.TrailHalfLife,
                Pheromones = pheromones,
                Terrain = terrain,
                Grid = grid,
            }.Run();
        }

        public bool IsAtWar(int a, int b) => hostile[a * MaxColonies + b] != 0;

        /// <summary>Diplomacy: war or peace is symmetric for now.</summary>
        public void SetWar(int a, int b, bool war)
        {
            if (a == b) return;
            hostile[a * MaxColonies + b] = (byte)(war ? 1 : 0);
            hostile[b * MaxColonies + a] = (byte)(war ? 1 : 0);
        }

        /// <summary>One-off command: this colony's soldiers march on a point and fight whatever they find.</summary>
        public void OrderRaid(int colony, float2 target)
        {
            Colonies[colony].RaidActive = true;
            Colonies[colony].RaidTarget = target;
            Colonies[colony].RaidTimer = 0f;
        }

        public void CancelRaid(int colony)
        {
            Colonies[colony].RaidActive = false;
            Colonies[colony].AiRaidKind = 0;
        }

        void EnsureSimCapacity(int n)
        {
            if (antBucket.IsCreated && antBucket.Length >= n) return;
            int cap = math.ceilpow2(math.max(n, 4096));
            if (antBucket.IsCreated) antBucket.Dispose();
            if (sortedAnts.IsCreated) sortedAnts.Dispose();
            if (combatResults.IsCreated) combatResults.Dispose();
            if (damage.IsCreated) damage.Dispose();
            antBucket = new NativeArray<int>(cap, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            sortedAnts = new NativeArray<int>(cap, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            combatResults = new NativeArray<CombatResult>(cap, Allocator.Persistent);
            damage = new NativeArray<int>(cap, Allocator.Persistent);
        }

        public void PlaceFood(float2 worldPos, int radiusCells, int amountPerCell)
        {
            int cx = (int)(worldPos.x * grid.InvCellSize);
            int cy = (int)(worldPos.y * grid.InvCellSize);
            for (int y = cy - radiusCells; y <= cy + radiusCells; y++)
            for (int x = cx - radiusCells; x <= cx + radiusCells; x++)
            {
                if (x < 0 || y < 0 || x >= grid.Width || y >= grid.Height) continue;
                int dx = x - cx, dy = y - cy;
                if (dx * dx + dy * dy > radiusCells * radiusCells) continue;
                int idx = y * grid.Width + x;
                if (TerrainKind.Blocks(terrain[idx])) continue;
                food[idx] = math.max(food[idx], amountPerCell);
            }
        }

        void SpawnRandomFoodBlob(int radius)
        {
            // Keep food away from nests so ants actually have to forage.
            for (int attempt = 0; attempt < 20; attempt++)
            {
                float2 p = rng.NextFloat2(new float2(radius * 2f), grid.WorldSize - radius * 2f);
                bool tooClose = false;
                if (NearNest(p, sim.NestRadius * 8f)) tooClose = true;
                if (tooClose || IsBlocked(p)) continue;
                PlaceFood(p, radius, foodPerCell);
                return;
            }
        }

        void RespawnFood()
        {
            int maxBlobs = math.max(4, grid.CellCount / 65536);
            for (int i = 0; i < maxBlobs && MapFood < targetMapFood; i++)
            {
                SpawnRandomFoodBlob(foodBlobRadius);
                MapFood += (long)(math.PI * foodBlobRadius * foodBlobRadius * foodPerCell);
            }
        }

        // ------------------------------------------------------------------ input

        void HandleInput()
        {
            if (AntSimHud.IsTyping || AntSimHud.MenuOpen) return; // a text box or the Esc menu has the keyboard
            if (Input.GetKeyDown(KeyCode.F5)) Notify(SaveGame("Quicksave") ?? "Game saved (Quicksave).");
            if (Input.GetKeyDown(KeyCode.F9))
            {
                string quick = System.IO.Path.Combine(SaveFolder, "Quicksave" + SaveExtension);
                if (System.IO.File.Exists(quick)) { AntGameMenu.LoadSave(this, quick); return; }
                Notify("No quicksave yet: press F5 to make one.");
            }
            if (Input.GetKeyDown(KeyCode.Space)) Paused = !Paused;
            if (Input.GetKeyDown(KeyCode.Equals) || Input.GetKeyDown(KeyCode.KeypadPlus)) simSpeed = math.min(simSpeed * 2f, 16f);
            if (Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.KeypadMinus)) simSpeed = math.max(simSpeed * 0.5f, 0.125f);
            if (Input.GetKeyDown(KeyCode.Tab)) FocusColony = (FocusColony + 1) % colonyCount;
            if (Input.GetKeyDown(KeyCode.P)) PheromoneView = (PheromoneView + 1) % 3;
            if (Input.GetKeyDown(KeyCode.X)) CancelRaid(CommandColony);
            if (Input.GetKeyDown(KeyCode.F)) { PlacingFarm = !PlacingFarm; PlacingNest = false; PlacingDefence = -1; PlacingMove = -1; }
            if (Input.GetKeyDown(KeyCode.N)) { PlacingNest = !PlacingNest; PlacingFarm = false; PlacingDefence = -1; PlacingMove = -1; }
            if (Input.GetKeyDown(KeyCode.G)) Notify(UpgradeGarden(CommandColony) ?? "Fungus garden upgraded.");
            if (Input.GetKeyDown(KeyCode.B))
            {
                string err = HuntNearestOf(CommandColony, WildKinds.BigPrey, "No big prey around right now.", out var prey);
                Notify(err ?? $"Soldiers sent to hunt a {WildKinds.Name(prey)}.");
            }
            if (Sandbox)
            {
                if (Input.GetKeyDown(KeyCode.T)) SpawnAnts(FocusColony, 25000, Caste.Worker, 40f);
                if (Input.GetKeyDown(KeyCode.Y)) SpawnAnts(FocusColony, 5000, Caste.Soldier, 30f);
            }

            var cam = Camera.main;
            if (cam == null) return;
            Vector3 mw = cam.ScreenToWorldPoint(Input.mousePosition);
            float2 mouse = math.clamp(new float2(mw.x, mw.y), 0f, grid.WorldSize - 0.01f);

            if (Input.GetKeyDown(KeyCode.R)) OrderRaid(CommandColony, mouse);

            if (AntSimHud.IsMouseOverUi()) return;
            if (PlacingMove >= 0 && Input.GetMouseButtonDown(0))
            {
                string err = MoveNest(CommandColony, PlacingMove, SnapNestSite(CommandColony, mouse));
                if (err != null) Notify(err);
                else PlacingMove = -1;
            }
            else if (PlacingDefence >= 0 && Input.GetMouseButtonDown(0))
            {
                string err = PlaceDefence(CommandColony, PlacingDefence, mouse);
                Notify(err ?? $"{DefenceNames[PlacingDefence]} built.");
                if (err == null && !Input.GetKey(KeyCode.LeftShift)) PlacingDefence = -1;
            }
            else if (PlacingNest && Input.GetMouseButtonDown(0))
            {
                string err = StartSwarm(CommandColony, SnapNestSite(CommandColony, mouse));
                Notify(err ?? "Raising a princess. When she's ready she'll fly to the new site with an escort.");
                if (err == null) PlacingNest = false;
            }
            else if (PlacingFarm && Input.GetMouseButtonDown(0))
            {
                string err = PlaceAphidFarm(CommandColony, mouse);
                Notify(err ?? "Aphid farm placed. Your workers will collect its honeydew.");
                if (err == null && !Input.GetKey(KeyCode.LeftShift)) PlacingFarm = false;
            }
            else if (!PlacingFarm && !PlacingNest && PlacingDefence < 0 && PlacingMove < 0 && Sandbox && Input.GetMouseButton(0))
            {
                PlaceFood(mouse, 3, foodPerCell);
            }
        }

        // ------------------------------------------------------------------ rendering

        void SetupRendering()
        {
            var antShader = Resources.Load<Shader>("AntInstanced");
            var groundShader = Resources.Load<Shader>("AntGround");
            if (antShader == null || groundShader == null)
            {
                UnityEngine.Debug.LogError("AntSim: shaders not found in Resources folder.");
                enabled = false;
                return;
            }

            antMaterial = new Material(antShader);
            queenMaterial = new Material(antShader) { renderQueue = antMaterial.renderQueue + 1 };
            queenBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, MaxNests * 2, sizeof(float) * 4);
            antMesh = BuildQuad(new Vector3(-0.5f, -0.5f), new Vector3(0.5f, 0.5f));

            groundTexture = new Texture2D(grid.Width, grid.Height, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            groundMaterial = new Material(groundShader) { mainTexture = groundTexture };

            var ground = new GameObject("Ground");
            ground.transform.SetParent(transform, false);
            ground.transform.position = new Vector3(0f, 0f, 1f);
            ground.AddComponent<MeshFilter>().sharedMesh =
                BuildQuad(Vector3.zero, new Vector3(grid.WorldSize.x, grid.WorldSize.y));
            var mr = ground.AddComponent<MeshRenderer>();
            mr.sharedMaterial = groundMaterial;
            mr.sortingOrder = -100;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }

        void SetupCameraAndUi()
        {
            var cam = Camera.main;
            if (cam == null)
            {
                cam = new GameObject("Main Camera").AddComponent<Camera>();
                cam.tag = "MainCamera";
            }
            cam.orthographic = true;
            // Start zoomed in on your colony; the rest of the world runs asleep until you look at it.
            Vector2 home = Colonies[Player].NestPosition;
            cam.orthographicSize = math.min(90f, grid.WorldSize.y * 0.55f);
            cam.transform.position = new Vector3(home.x, home.y, -10f);
            cam.backgroundColor = new Color(0.04f, 0.04f, 0.05f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            var controller = cam.GetComponent<AntCameraController>();
            if (controller == null) controller = cam.gameObject.AddComponent<AntCameraController>();
            controller.world = this;

            gameObject.AddComponent<AntSimHud>().world = this;
        }

        /// <summary>Moves the camera to a world position (used by the HUD's "go to" buttons).</summary>
        public void LookAt(Vector2 p)
        {
            var cam = Camera.main;
            if (cam != null) cam.transform.position = new Vector3(p.x, p.y, cam.transform.position.z);
        }

        void Render()
        {
            if (antMaterial == null) return;
            int n = ants.Length;

            if (n > 0)
            {
                EnsureRenderCapacity(n);
                new BuildRenderDataJob
                {
                    Ants = ants.AsArray(), Nests = nests, Output = renderData, WildColony = WildColony,
                    Lead = Paused || State != GameState.Playing ? 0f : tickAccumulator,
                }.Schedule(n, 4096).Complete();
                antBuffer.SetData(renderData, 0, 0, n);

                var cam = Camera.main;
                float pixelWorld = cam != null ? cam.orthographicSize * 2f / math.max(1, Screen.height) : 1f;
                SetAntMaterial(antMaterial, antBuffer, pixelWorld);

                var bounds = new Bounds(
                    new Vector3(grid.WorldSize.x * 0.5f, grid.WorldSize.y * 0.5f, 0f),
                    new Vector3(grid.WorldSize.x + 10f, grid.WorldSize.y + 10f, 10f));
                Graphics.DrawMeshInstancedProcedural(antMesh, 0, antMaterial, bounds, n, null,
                    ShadowCastingMode.Off, false, 0, null, LightProbeUsage.Off);

                // Queens and princesses again on top so they're never buried under the crowd.
                int queens = 0;
                for (int k = 0; k < nestHigh; k++)
                {
                    if (nestInfo[k] == null) continue;
                    AddRoyal(queenIndex[k], Caste.Queen, k, n, ref queens);
                    AddRoyal(princessIndex[k], Caste.Princess, k, n, ref queens);
                }
                if (queens > 0)
                {
                    queenBuffer.SetData(queenData, 0, 0, queens);
                    SetAntMaterial(queenMaterial, queenBuffer, pixelWorld);
                    Graphics.DrawMeshInstancedProcedural(antMesh, 0, queenMaterial, bounds, queens, null,
                        ShadowCastingMode.Off, false, 0, null, LightProbeUsage.Off);
                }
            }

            for (int c = 0; c < colonyCount; c++)
                gardenLevels[c] = Colonies[c].GardenLevel / (float)FarmingParams.MaxGarden;
            int farmCount = FillFarmData();
            int treeCount = FillTreeData();
            int defenceCount = FillDefenceData();

            var pixels = groundTexture.GetPixelData<Color32>(0);
            var overlay = new OverlayJob
            {
                Pheromones = pheromones,
                Food = food,
                Leaves = leaves,
                PlantCap = plantCap,
                Terrain = terrain,
                ColonyColors = colonyColors,
                Pixels = pixels,
                Grid = grid,
                ColonyCount = colonyCount,
                Mode = PheromoneView,
                Focus = FocusColony,
                FoodPerCellFull = foodPerCell,
                LeafCap = farming.LeafCap,
            }.Schedule(grid.CellCount, 1024);
            new StampJob
            {
                Pixels = pixels,
                Nests = nests,
                ColonyColors = colonyColors,
                Farms = farmData,
                GardenLevels = gardenLevels,
                Trees = treeData,
                Defences = defenceData,
                Grid = grid,
                NestCount = nestHigh,
                FarmCount = farmCount,
                TreeCount = treeCount,
                DefenceCount = defenceCount,
                Focus = FocusColony,
                TrunkRadius = TrunkRadius,
            }.Schedule(overlay).Complete();
            groundTexture.Apply(false);
        }

        void AddRoyal(int index, Caste caste, int nest, int n, ref int count)
        {
            if (index < 0 || index >= n) return;
            Ant q = ants[index];
            if (q.Caste != (byte)caste || q.Dead != 0 || q.Nest != nest) return;
            float lead = Paused || State != GameState.Playing ? 0f : tickAccumulator;
            float2 p = q.Position + new float2(math.cos(q.Angle), math.sin(q.Angle)) * (q.Speed * lead);
            queenData[count++] = new Vector4(p.x, p.y, q.Angle, q.Colony | ((int)caste << 4));
        }

        void SetAntMaterial(Material m, GraphicsBuffer buffer, float pixelWorld)
        {
            m.SetBuffer(AntsId, buffer);
            m.SetVectorArray(ColorsId, colorVectors);
            m.SetFloatArray(ShapesId, shapeValues);
            m.SetFloatArray(MorphSizesId, morphSizes);
            m.SetFloat(WildId, WildColony);
            m.SetFloat(AntLengthId, antLength);
            m.SetFloat(MinSizeId, pixelWorld * 2.5f);
        }

        void EnsureRenderCapacity(int n)
        {
            if (renderData.IsCreated && renderData.Length >= n) return;
            int cap = math.ceilpow2(math.max(n, 1024));
            if (renderData.IsCreated) renderData.Dispose();
            renderData = new NativeArray<float4>(cap, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            antBuffer?.Release();
            antBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, cap, sizeof(float) * 4);
        }

        static Mesh BuildQuad(Vector3 min, Vector3 max)
        {
            var m = new Mesh();
            m.vertices = new[]
            {
                new Vector3(min.x, min.y, 0f), new Vector3(max.x, min.y, 0f),
                new Vector3(max.x, max.y, 0f), new Vector3(min.x, max.y, 0f),
            };
            m.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            m.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            m.RecalculateBounds();
            return m;
        }

        void OnDestroy()
        {
            if (ants.IsCreated) ants.Dispose();
            if (pheromones.IsCreated) pheromones.Dispose();
            if (food.IsCreated) food.Dispose();
            if (leaves.IsCreated) leaves.Dispose();
            DisposeTerrain();
            if (plantCap.IsCreated) plantCap.Dispose();
            if (delivered.IsCreated) delivered.Dispose();
            if (deliveredLeaves.IsCreated) deliveredLeaves.Dispose();
            if (nests.IsCreated) nests.Dispose();
            if (starveRates.IsCreated) starveRates.Dispose();
            if (casteCounts.IsCreated) casteCounts.Dispose();
            if (queenIndex.IsCreated) queenIndex.Dispose();
            if (colonyStats.IsCreated) colonyStats.Dispose();
            if (morphStats.IsCreated) morphStats.Dispose();
            if (morphPick.IsCreated) morphPick.Dispose();
            if (morphCounts.IsCreated) morphCounts.Dispose();
            if (sumResult.IsCreated) sumResult.Dispose();
            if (renderData.IsCreated) renderData.Dispose();
            if (colonyColors.IsCreated) colonyColors.Dispose();
            if (gardenLevels.IsCreated) gardenLevels.Dispose();
            if (antBucket.IsCreated) antBucket.Dispose();
            if (sortedAnts.IsCreated) sortedAnts.Dispose();
            if (bucketStart.IsCreated) bucketStart.Dispose();
            if (bucketCursor.IsCreated) bucketCursor.Dispose();
            if (combatResults.IsCreated) combatResults.Dispose();
            if (damage.IsCreated) damage.Dispose();
            if (hostile.IsCreated) hostile.Dispose();
            if (combatDeaths.IsCreated) combatDeaths.Dispose();
            if (coreCombatDeaths.IsCreated) coreCombatDeaths.Dispose();
            if (distress.IsCreated) distress.Dispose();
            if (busyTrails.IsCreated) busyTrails.Dispose();
            if (hotspots.IsCreated) hotspots.Dispose();
            if (terrSum.IsCreated) terrSum.Dispose();
            if (terrMax.IsCreated) terrMax.Dispose();
            if (terrCount.IsCreated) terrCount.Dispose();
            if (intruders.IsCreated) intruders.Dispose();
            if (coreOwner.IsCreated) coreOwner.Dispose();
            DisposeNests();
            if (removedCounts.IsCreated) removedCounts.Dispose();
            if (frozenTrails.IsCreated) frozenTrails.Dispose();
            if (removedAges.IsCreated) removedAges.Dispose();
            if (takenRef.IsCreated) takenRef.Dispose();
            DisposeEcology();
            DisposeNature();
            DisposeDiplomacy();
            antBuffer?.Release();
            antBuffer = null;
            queenBuffer?.Release();
            queenBuffer = null;
        }
    }
}
