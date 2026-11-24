using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace AntSim
{
    public enum GameState { Playing, Won, Lost }

    /// <summary>
    /// The living world around the colonies: plants, aphid farms, fungus gardens, wildlife,
    /// distress calls, rival AI and win/lose.
    /// </summary>
    public partial class AntWorld
    {
        public GameState State { get; private set; } = GameState.Playing;
        /// <summary>Next left click places an aphid farm.</summary>
        public bool PlacingFarm { get; set; }
        public string Toast { get; private set; }
        public float ToastTime { get; private set; } = -100f;
        public IReadOnlyList<AphidFarm> Farms => farms;
        public int WildCount(WildKind k) => casteCounts.IsCreated ? casteCounts[WildColony * Castes.Count + (int)k] : 0;

        const int MaxFarms = 256;
        /// <summary>Seconds at the start of a game during which rival colonies don't launch war raids.</summary>
        public float PeacefulStart = 600f;
        readonly List<AphidFarm> farms = new List<AphidFarm>();
        readonly List<float3> bushes = new List<float3>(); // x, y, radius
        NativeArray<float4> farmData;
        NativeArray<float4> nearestWild;  // [colony * 3 + kind] = (x, y, dist, found)
        NativeArray<int> beetlesAtFarm;
        float ecoTimer;
        float aiTimer;
        float wildTimer;

        public void Notify(string message)
        {
            Toast = message;
            ToastTime = Time.unscaledTime;
        }

        // ------------------------------------------------------------------ setup

        void AllocateEcology()
        {
            farmData = new NativeArray<float4>(MaxFarms, Allocator.Persistent);
            nearestWild = new NativeArray<float4>(colonyCount * 3, Allocator.Persistent);
            beetlesAtFarm = new NativeArray<int>(MaxFarms, Allocator.Persistent);
        }

        void SetupEcology()
        {
            GeneratePlants();
            PlaceStarterFood();
            for (int k = 0; k < 3; k++)
            {
                int target = WildTarget((WildKind)k);
                for (int i = 0; i < target; i++) SpawnWildAtRandom((WildKind)k);
            }
            new SumJob { Values = food, Result = sumResult }.Run();
            MapFood = sumResult.Value;
        }

        /// <summary>Bushes scattered over the map, with a couple guaranteed near every nest.</summary>
        void GeneratePlants()
        {
            int count = math.max(12, grid.CellCount / 6000);
            for (int i = 0; i < count; i++)
            {
                float2 p = rng.NextFloat2(new float2(12f), grid.WorldSize - 12f);
                if (NearNest(p, 14f) || IsBlocked(p)) continue;
                AddBush(p, rng.NextFloat(4f, 10f));
            }
            for (int c = 0; c < colonyCount; c++)
                for (int j = 0; j < 2; j++)
                {
                    float a = rng.NextFloat(0f, math.PI * 2f);
                    float2 p = nests[c].Position + new float2(math.cos(a), math.sin(a)) * rng.NextFloat(28f, 60f);
                    AddBush(math.clamp(p, 12f, grid.WorldSize - 12f), rng.NextFloat(6f, 9f));
                }
        }

        void AddBush(float2 center, float radius)
        {
            bushes.Add(new float3(center, radius));
            int r = (int)math.ceil(radius);
            int cx = (int)center.x, cy = (int)center.y;
            for (int y = cy - r; y <= cy + r; y++)
            for (int x = cx - r; x <= cx + r; x++)
            {
                if (x < 0 || y < 0 || x >= grid.Width || y >= grid.Height) continue;
                float d = math.distance(new float2(x + 0.5f, y + 0.5f), center);
                // Ragged edges so bushes don't look like perfect discs.
                if (d > radius * rng.NextFloat(0.75f, 1.05f)) continue;
                int idx = y * grid.Width + x;
                if (TerrainKind.Blocks(terrain[idx])) continue;
                int cap = (int)(farming.LeafCap * (1f - 0.5f * d / radius));
                if (cap > plantCap[idx]) plantCap[idx] = (byte)math.min(cap, 255);
                leaves[idx] = plantCap[idx];
            }
        }

        /// <summary>
        /// A fair start: each nest gets two caches of food nearby, plus a little scattered elsewhere.
        /// After that, food has to be earned (hunting, aphids, fungus) unless this is a sandbox game.
        /// </summary>
        void PlaceStarterFood()
        {
            for (int c = 0; c < colonyCount; c++)
                for (int j = 0; j < 2; j++)
                {
                    // Somewhere the ants can actually get to.
                    float2 p = nests[c].Position;
                    for (int attempt = 0; attempt < 20; attempt++)
                    {
                        float a = rng.NextFloat(0f, math.PI * 2f);
                        p = math.clamp(nests[c].Position + new float2(math.cos(a), math.sin(a)) * rng.NextFloat(30f, 65f),
                            8f, grid.WorldSize - 8f);
                        if (AreaClear(p, 4f)) break;
                    }
                    PlaceFood(p, 5, foodPerCell);
                }
            int scattered = math.max(4, grid.CellCount / 50000);
            for (int i = 0; i < scattered; i++) SpawnRandomFoodBlob(4);
            if (Sandbox)
            {
                int blobs = math.max(10, grid.CellCount / 26000);
                for (int i = 0; i < blobs; i++) SpawnRandomFoodBlob(foodBlobRadius);
            }
        }

        bool NearNest(float2 p, float dist)
        {
            for (int k = 0; k < nestHigh; k++)
                if (nestInfo[k] != null && math.distance(p, nests[k].Position) < dist) return true;
            return false;
        }

        // ------------------------------------------------------------------ per tick

        void UpdateEcology(float dt)
        {
            ecoTimer += dt;
            if (ecoTimer >= 1f)
            {
                float step = ecoTimer;
                ecoTimer = 0f;
                new SumJob { Values = food, Result = sumResult }.Run();
                MapFood = sumResult.Value;
                new RegrowJob
                {
                    Leaves = leaves, PlantCap = plantCap,
                    Chance = farming.LeafRegrow * step, Seed = rng.NextUInt(1, uint.MaxValue),
                }.Schedule(grid.CellCount, 8192).Complete();
                int fc = FillFarmData();
                new WildScanJob
                {
                    Ants = ants.AsArray(), Nests = nests, Farms = farmData, Nearest = nearestWild,
                    BeetlesAtFarm = beetlesAtFarm, FarmCount = fc, NestCount = nestHigh, ColonyCount = colonyCount,
                    WildColony = WildColony, FarmRadius = 6f,
                }.Run();
                UpdateFarms(step);
                UpdateRaids(step);
                UpdateSwarms(step);
                UpdateGuardSites(step);
                UpdateSleepWars(step);
                UpdateDiplomacy(step);
                CheckGameState();
            }

            wildTimer += dt;
            if (wildTimer >= 3f)
            {
                wildTimer = 0f;
                for (int k = 0; k < 3; k++)
                {
                    int missing = WildTarget((WildKind)k) - WildCount((WildKind)k);
                    for (int i = 0; i < math.min(missing, 3); i++) SpawnWildAtRandom((WildKind)k);
                }
            }

            aiTimer += dt;
            if (aiTimer >= 2f)
            {
                for (int c = 0; c < colonyCount; c++)
                    if (c != Player) RivalAI(c, aiTimer);
                aiTimer = 0f;
            }
        }

        // ------------------------------------------------------------------ distress calls

        void UpdateDistress(float dt)
        {
            new DistressJob
            {
                Distress = distress, Hotspots = hotspots, Width = distressW, Height = distressH,
                CellSize = DistressCell, Decay = math.pow(0.5f, dt / 30f), Threshold = 100, Separation = 4,
            }.Schedule(colonyCount, 1).Complete();

            for (int c = 0; c < colonyCount; c++)
            {
                var col = Colonies[c];
                float4 h = hotspots[c * AntStepJob.HotspotsPerColony];
                col.DistressAlerts = (int)(h.z / 100f);
                if (h.z > 0f) col.DistressAt = h.xy;
            }
        }

        public float4 Hotspot(int colony, int k) => hotspots[colony * AntStepJob.HotspotsPerColony + k];

        // ------------------------------------------------------------------ wildlife

        int WildTarget(WildKind k)
        {
            float mul = setup.Wildlife == 0 ? 0f : setup.Wildlife == 1 ? 1f : 2.5f;
            return (int)(wildlife.Density((int)k) * grid.WorldSize.x * grid.WorldSize.y / 10000f * mul);
        }

        void SpawnWildAtRandom(WildKind k)
        {
            for (int attempt = 0; attempt < 12; attempt++)
            {
                float2 p;
                if (k != WildKind.Spider && bushes.Count > 0)
                {
                    // Grazers turn up in the vegetation.
                    float3 b = bushes[rng.NextInt(0, bushes.Count)];
                    p = b.xy + rng.NextFloat2Direction() * rng.NextFloat(0f, b.z);
                }
                else p = rng.NextFloat2(new float2(4f), grid.WorldSize - 4f);
                if (NearNest(p, k == WildKind.Mite ? 25f : 70f) || IsBlocked(p)) continue;
                SpawnWildlife(k, 1, p, 0f);
                return;
            }
        }

        /// <summary>Nearest beetle/spider to a colony's nest, from the last scan.</summary>
        public bool TryGetNearestWild(int colony, WildKind k, out Vector2 pos, out float dist)
        {
            float4 v = nearestWild[colony * 3 + (int)k];
            pos = v.xy;
            dist = v.z;
            return v.w > 0f;
        }

        /// <summary>Send soldiers after the nearest beetle or spider. Returns an error message, or null on success.</summary>
        public string HuntNearest(int colony, WildKind k)
        {
            if (!TryGetNearestWild(colony, k, out var p, out _)) return $"No {k.ToString().ToLower()}s around right now.";
            if (Colonies[colony].Soldiers < 5) return "You need some soldiers to hunt (raise the soldier ratio).";
            OrderRaid(colony, p);
            Colonies[colony].AiRaidKind = 1 + (int)k;
            return null;
        }

        /// <summary>Hunts follow the prey around and end when it's dead; war raids time out.</summary>
        void UpdateRaids(float dt)
        {
            for (int c = 0; c < colonyCount; c++)
            {
                var col = Colonies[c];
                if (!col.RaidActive) { col.AiRaidKind = 0; continue; }
                col.RaidTimer += dt;
                if (col.AiRaidKind >= 1)
                {
                    var kind = (WildKind)(col.AiRaidKind - 1);
                    bool found = TryGetNearestWild(c, kind, out var p, out _);
                    // Track the prey while it's near the hunt; give up when it's gone.
                    if (found && math.distance((float2)p, (float2)col.RaidTarget) < 40f) col.RaidTarget = p;
                    else if (col.RaidTimer > 8f) CancelRaid(c);
                    if (col.RaidTimer > 90f) CancelRaid(c);
                }
                else if (!col.IsPlayer && col.RaidTimer > 70f) CancelRaid(c);
            }
        }

        // ------------------------------------------------------------------ farming

        /// <summary>Builds or upgrades the fungus garden. Returns an error message, or null on success.</summary>
        public string UpgradeGarden(int colony)
        {
            var col = Colonies[colony];
            if (col.GardenLevel >= FarmingParams.MaxGarden) return "The fungus garden is already at its biggest.";
            float cost = FarmingParams.GardenCost(col.GardenLevel + 1);
            if (col.Food < cost) return $"Not enough food: the garden needs {cost:0}.";
            col.Food -= cost;
            col.GardenLevel++;
            return null;
        }

        public float FarmCost(int colony) => farming.AphidFarmCost * SpeciesOf(colony).FarmCost;

        /// <summary>Places an aphid herd on a plant near the nest. Returns an error message, or null on success.</summary>
        public string PlaceAphidFarm(int colony, float2 pos)
        {
            var col = Colonies[colony];
            if (farms.Count >= MaxFarms) return "The map can't hold more farms.";
            float cost = FarmCost(colony);
            if (col.Food < cost) return $"Not enough food: an aphid farm costs {cost:0}.";
            int near = NearestNest(colony, pos, false);
            if (near < 0 || math.distance(pos, nests[near].Position) > farming.FarmMaxDistance)
                return $"Too far from your nests (max {farming.FarmMaxDistance:0}).";
            foreach (var f in farms)
                if (math.distance((float2)f.Position, pos) < 10f) return "There's already a farm here.";
            if (LeavesAround(pos, 4) < 40) return "Aphids need a plant: place the farm on a green bush.";

            col.Food -= cost;
            farms.Add(new AphidFarm { Colony = colony, Position = pos, Aphids = 20f });
            return null;
        }

        int LeavesAround(float2 p, int r)
        {
            int sum = 0;
            int cx = (int)p.x, cy = (int)p.y;
            for (int y = cy - r; y <= cy + r; y++)
            for (int x = cx - r; x <= cx + r; x++)
            {
                if (x < 0 || y < 0 || x >= grid.Width || y >= grid.Height) continue;
                sum += leaves[y * grid.Width + x];
            }
            return sum;
        }

        /// <summary>Aphids eat the plant, breed, get eaten by beetles, and drip honeydew (food) around the farm.</summary>
        void UpdateFarms(float dt)
        {
            for (int c = 0; c < colonyCount; c++) { Colonies[c].AphidFarms = 0; Colonies[c].FarmIncome = 0f; }

            for (int i = farms.Count - 1; i >= 0; i--)
            {
                var f = farms[i];
                var owner = Colonies[f.Colony];
                float2 p = f.Position;

                // Feed: take leaves from the plant underneath.
                int want = (int)math.ceil(f.Aphids * farming.AphidLeafAppetite * dt);
                int got = TakeFromArea(leaves, p, 5, want);
                float fed = want > 0 ? got / (float)want : 1f;

                float growth = farming.AphidGrowth * f.Aphids * (1f - f.Aphids / farming.AphidCap) * fed
                               - (1f - fed) * 0.04f * f.Aphids;
                int beetles = i < MaxFarms ? beetlesAtFarm[i] : 0;
                f.Eaten = beetles * wildlife.BeetleAphidAppetite * dt;
                f.Aphids = math.max(0f, f.Aphids + growth * dt - f.Eaten);

                // Honeydew pools on the ground around the herd. Whoever gets there first takes it.
                float honey = f.Aphids * farming.HoneydewPerAphid * SpeciesOf(f.Colony).Honeydew * dt;
                int drops = (int)honey + (rng.NextFloat() < honey - (int)honey ? 1 : 0);
                AddToArea(food, p, 3, drops, farming.HoneydewCellCap);

                if (f.Aphids < 0.5f || owner.Extinct)
                {
                    farms.RemoveAt(i);
                    if (f.Colony == Player && !owner.Extinct) Notify("One of your aphid farms has died out.");
                    continue;
                }
                owner.AphidFarms++;
                owner.FarmIncome += honey / dt;
            }
        }

        int TakeFromArea(NativeArray<int> g, float2 p, int r, int want)
        {
            int taken = 0;
            int cx = (int)p.x, cy = (int)p.y;
            for (int y = cy - r; y <= cy + r && taken < want; y++)
            for (int x = cx - r; x <= cx + r && taken < want; x++)
            {
                if (x < 0 || y < 0 || x >= grid.Width || y >= grid.Height) continue;
                int idx = y * grid.Width + x;
                int v = g[idx];
                if (v <= 0) continue;
                int t = math.min(v, math.max(1, (want - taken) / 8));
                g[idx] = v - t;
                taken += t;
            }
            return taken;
        }

        void AddToArea(NativeArray<int> g, float2 p, int r, int amount, int cellCap)
        {
            int cx = (int)p.x, cy = (int)p.y;
            for (int attempt = 0; attempt < amount * 3 && amount > 0; attempt++)
            {
                int x = cx + rng.NextInt(-r, r + 1), y = cy + rng.NextInt(-r, r + 1);
                if (x < 0 || y < 0 || x >= grid.Width || y >= grid.Height) continue;
                int idx = y * grid.Width + x;
                if (g[idx] >= cellCap || TerrainKind.Blocks(terrain[idx])) continue;
                g[idx] = g[idx] + 1;
                amount--;
            }
        }

        int FillFarmData()
        {
            int n = math.min(farms.Count, MaxFarms);
            for (int i = 0; i < n; i++)
            {
                var f = farms[i];
                farmData[i] = new float4(f.Position.x, f.Position.y, f.Colony, f.Aphids / farming.AphidCap);
            }
            return n;
        }

        // ------------------------------------------------------------------ rival AI

        /// <summary>
        /// Simple rival colonies: build a garden and farms when they can afford it, hunt beetles,
        /// keep soldiers up when attacked, and raid weaker neighbours once they're strong.
        /// </summary>
        void RivalAI(int c, float dt)
        {
            var col = Colonies[c];
            if (col.Extinct || !col.HasQueen) return;
            var sp = SpeciesOf(c);
            int pop = col.Population;
            float reserve = pop * economy.UpkeepPerAntPerSecond * economy.FoodReserveSeconds;

            // Soldiers: more when under attack.
            float wantRatio = sp.SoldierRatio + (col.DistressAlerts > 2 ? 0.1f : 0f);
            col.SoldierRatio = math.lerp(col.SoldierRatio, wantRatio, 0.2f);

            // Fungus garden grows with the colony.
            int wantGarden = math.min(FarmingParams.MaxGarden, 1 + pop / 500);
            if (sp.GardenYield >= 1f && col.GardenLevel < wantGarden &&
                col.Food > FarmingParams.GardenCost(col.GardenLevel + 1) + reserve)
                UpgradeGarden(c);

            // Aphid farms on bushes near home.
            int wantFarms = 1 + pop / 350;
            if (col.AphidFarms < wantFarms && col.Food > FarmCost(c) * 1.5f + reserve)
            {
                float best = float.MaxValue;
                float2 spot = default;
                foreach (var b in bushes)
                {
                    int nn = NearestNest(c, b.xy, false);
                    if (nn < 0) continue;
                    float d = math.distance(b.xy, nests[nn].Position);
                    if (d > farming.FarmMaxDistance * 0.8f || d >= best) continue;
                    bool taken = false;
                    foreach (var f in farms) if (math.distance((float2)f.Position, b.xy) < 12f) { taken = true; break; }
                    if (taken || LeavesAround(b.xy, 4) < 60) continue;
                    best = d;
                    spot = b.xy;
                }
                if (best < float.MaxValue) PlaceAphidFarm(c, spot);
            }

            if (col.RaidActive || col.StatRaidTarget >= 0) return;

            // Hunt: a beetle close to home is a feast (only awake colonies hunt; bugs don't exist for sleepers).
            if (!col.Abstract && col.Soldiers >= 25 && TryGetNearestWild(c, WildKind.Beetle, out _, out float bd) && bd < 140f)
            {
                HuntNearest(c, WildKind.Beetle);
                return;
            }
            // Spiders near the nest get mobbed.
            if (!col.Abstract && col.Soldiers >= 30 && TryGetNearestWild(c, WildKind.Spider, out _, out float sd) && sd < 70f)
            {
                HuntNearest(c, WildKind.Spider);
                return;
            }

            // War: once strong, raid a weaker neighbour now and then. Nobody starts wars during the
            // opening minutes, so every colony gets time to find food and build up.
            col.AiTimer += dt;
            if (SimTime > PeacefulStart && col.Soldiers > 120 && col.AiTimer > 150f)
            {
                int target = -1;
                float bestD = 600f;
                for (int o = 0; o < colonyCount; o++)
                {
                    var other = Colonies[o];
                    if (o == c || other.Extinct || !IsAtWar(c, o)) continue;
                    if (other.Soldiers > col.Soldiers * 0.7f) continue;
                    float d = math.distance(NestPos(o), NestPos(c));
                    if (d < bestD) { bestD = d; target = o; }
                }
                if (target >= 0)
                {
                    col.AiTimer = 0f;
                    if (col.Abstract) { SleepingRaid(c, target); return; }
                    // Hit the target's nest closest to home.
                    int tn = NearestNest(target, NestPos(c), false);
                    OrderRaid(c, tn >= 0 ? nests[tn].Position : NestPos(target));
                    if (target == Player) Notify($"{col.Name} is raiding your nest!");
                }
            }
        }

        // ------------------------------------------------------------------ win / lose

        void CheckGameState()
        {
            if (Sandbox || State != GameState.Playing) return;
            if (Colonies[Player].Extinct) { State = GameState.Lost; return; }
            bool rivalsLeft = false;
            for (int c = 0; c < colonyCount; c++)
                if (c != Player && !Colonies[c].Extinct) rivalsLeft = true;
            if (!rivalsLeft && colonyCount > 1) State = GameState.Won;
        }

        void DisposeEcology()
        {
            if (farmData.IsCreated) farmData.Dispose();
            if (nearestWild.IsCreated) nearestWild.Dispose();
            if (beetlesAtFarm.IsCreated) beetlesAtFarm.Dispose();
        }
    }
}
