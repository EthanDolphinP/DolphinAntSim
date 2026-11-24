using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Random = Unity.Mathematics.Random;

namespace AntSim
{
    /// <summary>
    /// Per colony: how far its ants range from their home nests (territory size). Per nest: how many
    /// hostile ants are inside its core. Runs a couple of times per second, not every tick.
    /// A coarse grid marks which nest's core each cell belongs to, so the cost doesn't grow with nests.
    /// </summary>
    [BurstCompile]
    public struct TerritoryJob : IJob
    {
        [ReadOnly] public NativeArray<Ant> Ants;
        [ReadOnly] public NativeArray<NestData> Nests;
        [ReadOnly] public NativeArray<byte> Hostile;
        public NativeArray<short> CoreOwner; // coarse grid: nest index + 1 whose core covers the cell, or 0
        public int OwnerW, OwnerH;
        public float OwnerCell;
        public NativeArray<float> SumDist;
        public NativeArray<float> MaxDist;
        public NativeArray<int> Count;
        public NativeArray<int> NestIntruders; // per nest
        public int NestCount;
        public int ColonyCount; // real colonies; wildlife lives in slot ColonyCount
        public int MaxColonies;
        [UnityEngine.Tooltip("A beetle or spider in a colony's core counts as this many intruders.")]
        public int BigBugWeight;

        public void Execute()
        {
            for (int c = 0; c < ColonyCount; c++)
            {
                SumDist[c] = 0f;
                MaxDist[c] = 0f;
                Count[c] = 0;
            }
            for (int i = 0; i < CoreOwner.Length; i++) CoreOwner[i] = 0;
            float inv = 1f / OwnerCell;
            for (int n = 0; n < NestCount; n++)
            {
                NestIntruders[n] = 0;
                NestData nest = Nests[n];
                if (nest.Active == 0) continue;
                float r = nest.CoreRadius;
                int x0 = math.max(0, (int)((nest.Position.x - r) * inv)), x1 = math.min(OwnerW - 1, (int)((nest.Position.x + r) * inv));
                int y0 = math.max(0, (int)((nest.Position.y - r) * inv)), y1 = math.min(OwnerH - 1, (int)((nest.Position.y + r) * inv));
                for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    CoreOwner[y * OwnerW + x] = (short)(n + 1);
            }

            for (int i = 0; i < Ants.Length; i++)
            {
                Ant a = Ants[i];
                if (a.Dead != 0) continue;
                bool wild = a.Colony >= ColonyCount;
                if (!wild && a.Caste >= (byte)Caste.Queen) continue; // queens, princesses and winged males
                if (wild && a.Caste == (byte)WildKind.Mite) continue;

                if (!wild)
                {
                    float d = math.distance(a.Position, Nests[a.Nest].Position);
                    SumDist[a.Colony] += d;
                    MaxDist[a.Colony] = math.max(MaxDist[a.Colony], d);
                    Count[a.Colony]++;
                }

                int ox = math.clamp((int)(a.Position.x * inv), 0, OwnerW - 1);
                int oy = math.clamp((int)(a.Position.y * inv), 0, OwnerH - 1);
                int owner = CoreOwner[oy * OwnerW + ox] - 1;
                if (owner < 0) continue;
                NestData on = Nests[owner];
                if (Hostile[a.Colony * MaxColonies + on.Colony] == 0) continue;
                if (math.distancesq(a.Position, on.Position) < on.CoreRadius * on.CoreRadius)
                    NestIntruders[owner] += wild ? BigBugWeight : 1;
            }
        }
    }

    /// <summary>Puts a colony to sleep: marks its workers and soldiers dead (no corpses) and counts them.</summary>
    [BurstCompile]
    public struct RemoveColonyJob : IJob
    {
        public NativeArray<Ant> Ants;
        public NativeArray<int> Removed;     // by caste
        public NativeArray<float> AgeBins;   // [caste * Bins + bin], ages histogram of removed ants
        public NativeArray<float> NestShare; // per nest: ants removed from it (caller clears)
        public int Bins;
        public float InvBinSize;
        public int Colony;

        public void Execute()
        {
            for (int k = 0; k < Removed.Length; k++) Removed[k] = 0;
            for (int k = 0; k < AgeBins.Length; k++) AgeBins[k] = 0f;
            for (int i = 0; i < Ants.Length; i++)
            {
                Ant a = Ants[i];
                // Queens and princesses stay real ants even while the colony sleeps.
                if (a.Dead != 0 || a.Colony != Colony || a.Caste == (byte)Caste.Queen || a.Caste == (byte)Caste.Princess) continue;
                if (a.Nest < NestShare.Length) NestShare[a.Nest] += 1f;
                a.Dead = 1;
                Ants[i] = a;
                Removed[a.Caste]++;
                int bin = math.clamp((int)(a.Age * InvBinSize), 0, Bins - 1);
                AgeBins[a.Caste * Bins + bin] += 1f;
            }
        }
    }

    /// <summary>
    /// Appends ants to the list. Used for births, stress tests and waking a sleeping colony
    /// (hundreds of thousands at once, so it's Burst-compiled).
    /// </summary>
    [BurstCompile]
    public struct SpawnAntsJob : IJob
    {
        public NativeList<Ant> Ants;
        public int Colony;
        public int Nest;
        public byte State;
        public int Count;
        public byte Caste;
        public float2 Center;
        public float Radius;
        public float2 WorldMax;
        public uint Seed;
        public float LifeMin;
        public float LifeMax;
        public float Health;
        // Caste variants: cumulative shares of morph slots 1..3, and their health multipliers (slot 0..3).
        public float4 MorphPick;
        public float4 MorphHealth;
        // Ages are drawn from [AgeMin, AgeMax); lifespans are conditioned on surviving to that age.
        public float AgeMin;
        public float AgeMax;
        // Trail strength as if the ant had walked straight out from Center (keeps the home gradient sane).
        public bool TrailFromCenter;
        public float Speed;
        public float TrailHalfLife;
        // Place ants preferentially on the colony's existing pheromone trails (used when waking).
        public bool OnTrails;
        [ReadOnly] public NativeArray<float> Pheromones;
        [ReadOnly] public NativeArray<byte> Terrain;
        public GridInfo Grid;

        public void Execute()
        {
            var rng = new Random(Seed == 0 ? 1u : Seed);
            int layer = Colony * Pheromone.ChannelCount * Grid.CellCount;
            for (int i = 0; i < Count; i++)
            {
                float age = AgeMax > AgeMin ? rng.NextFloat(AgeMin, AgeMax) : AgeMin;
                float life;
                if (Caste == (byte)AntSim.Caste.Queen || Caste == (byte)AntSim.Caste.Princess) { life = float.MaxValue; age = 0f; }
                else
                {
                    float lo = math.max(LifeMin, age + 0.5f);
                    life = lo < LifeMax ? rng.NextFloat(lo, LifeMax) : age + 0.5f;
                }
                float2 offset = rng.NextFloat2Direction() * (math.sqrt(rng.NextFloat()) * Radius);
                if (OnTrails)
                {
                    // Rejection sampling: accept a spot with probability ~ trail strength there.
                    for (int attempt = 0; attempt < 24; attempt++)
                    {
                        float2 p = math.clamp(Center + offset, 0f, WorldMax - 0.01f);
                        int cell = Grid.CellIndex(p);
                        float here = Pheromones[layer + cell] + Pheromones[layer + Grid.CellCount + cell];
                        if (rng.NextFloat() < 0.03f + math.saturate(here * 2f)) break;
                        offset = rng.NextFloat2Direction() * (math.sqrt(rng.NextFloat()) * Radius);
                    }
                }
                // Never put an ant inside rock or water.
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    float2 p = math.clamp(Center + offset, 0f, WorldMax - 0.01f);
                    if (!TerrainKind.Blocks(Terrain[Grid.CellIndex(p)])) break;
                    offset = attempt == 7 ? float2.zero : rng.NextFloat2Direction() * (math.sqrt(rng.NextFloat()) * Radius);
                }
                byte morph = MorphStats.Pick(MorphPick, rng.NextFloat());
                float trail = 1f;
                if (TrailFromCenter)
                    trail = math.pow(0.5f, math.length(offset) / math.max(0.01f, Speed) / math.max(0.01f, TrailHalfLife));

                Ants.Add(new Ant
                {
                    Position = math.clamp(Center + offset, 0f, WorldMax - 0.01f),
                    Angle = rng.NextFloat(0f, math.PI * 2f),
                    Age = age,
                    Lifespan = life,
                    Trail = trail,
                    Health = Health * (morph == 0 ? 1f : MorphHealth[morph]),
                    Morph = morph,
                    Rng = rng.NextUInt(1, uint.MaxValue),
                    Colony = (byte)Colony,
                    Caste = Caste,
                    State = State,
                    Nest = (byte)Nest,
                    Squad = (byte)rng.NextInt(0, 256),
                    Role = (byte)rng.NextInt(0, 100),
                });
            }
        }
    }

    /// <summary>
    /// A sleeping colony "forages" by removing food from the map inside its territory,
    /// proportionally from every food cell, so food sources shrink evenly as if ants were carrying it off.
    /// </summary>
    [BurstCompile]
    public struct ForageAbstractJob : IJob
    {
        public NativeArray<int> Food;
        public NativeReference<int> Taken;
        public GridInfo Grid;
        public float2 Center;
        public float Radius;
        public int Want;
        public uint Seed;

        public void Execute()
        {
            Taken.Value = 0;
            if (Want <= 0) return;

            int x0 = math.max(0, (int)((Center.x - Radius) * Grid.InvCellSize));
            int x1 = math.min(Grid.Width - 1, (int)((Center.x + Radius) * Grid.InvCellSize));
            int y0 = math.max(0, (int)((Center.y - Radius) * Grid.InvCellSize));
            int y1 = math.min(Grid.Height - 1, (int)((Center.y + Radius) * Grid.InvCellSize));
            float r2 = Radius * Radius;

            long total = 0;
            for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                int idx = y * Grid.Width + x;
                if (Food[idx] <= 0) continue;
                if (math.distancesq(Grid.CellCenter(idx), Center) > r2) continue;
                total += Food[idx];
            }
            if (total == 0) return;

            float fraction = math.min(1f, Want / (float)total);
            var rng = new Random(Seed == 0 ? 1u : Seed);
            int taken = 0;
            for (int y = y0; y <= y1 && taken < Want; y++)
            for (int x = x0; x <= x1 && taken < Want; x++)
            {
                int idx = y * Grid.Width + x;
                int f = Food[idx];
                if (f <= 0) continue;
                if (math.distancesq(Grid.CellCenter(idx), Center) > r2) continue;

                float t = f * fraction;
                int take = (int)t;
                if (rng.NextFloat() < t - take) take++;
                take = math.min(math.min(take, f), Want - taken);
                Food[idx] = f - take;
                taken += take;
            }
            Taken.Value = taken;
        }
    }

    /// <summary>Moves ants from one nest to another: swarm escorts, failed swarms and abandoned nests.</summary>
    [BurstCompile]
    public struct ReassignNestJob : IJob
    {
        public NativeArray<Ant> Ants;
        public int Colony;
        public int FromNest;
        public int ToNest;
        [UnityEngine.Tooltip("Only ants with Role >= 100 - Percent move (100 = everyone).")]
        public int Percent;
        [UnityEngine.Tooltip("Also remove the princess and winged males flying to FromNest (a failed swarm).")]
        public bool KillFlyers;
        public NativeReference<int> Count;

        public void Execute()
        {
            int moved = 0;
            int minRole = 100 - Percent;
            for (int i = 0; i < Ants.Length; i++)
            {
                Ant a = Ants[i];
                if (a.Dead != 0 || a.Colony != Colony || a.Nest != FromNest) continue;
                if (a.Caste >= (byte)Caste.Princess && KillFlyers) { a.Dead = 1; Ants[i] = a; continue; }
                if (a.Caste > (byte)Caste.Soldier || a.Role < minRole) continue;
                a.Nest = (byte)ToNest;
                a.State = AntState.Migrating;
                a.Carrying = Cargo.None;
                a.Explore = default;
                a.Stuck = default;
                Ants[i] = a;
                moved++;
            }
            Count.Value = moved;
        }
    }
}
