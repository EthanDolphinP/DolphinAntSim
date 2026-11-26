using System.Threading;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using Random = Unity.Mathematics.Random;

namespace AntSim
{
    /// <summary>
    /// Plants slowly regrow their leaves up to each cell's capacity. Fallen leaves (more than the plant there
    /// holds, e.g. under a tree) slowly rot away.
    /// </summary>
    [BurstCompile]
    public struct RegrowJob : IJobParallelFor
    {
        public NativeArray<int> Leaves;
        [ReadOnly] public NativeArray<byte> PlantCap; // 0 = bare ground
        public float Chance;  // per cell per call
        public float Rot;     // chance per fallen leaf per call to rot away
        public uint Seed;

        public void Execute(int i)
        {
            int cap = PlantCap[i];
            int have = Leaves[i];
            if (have > cap)
            {
                var r = new Random(Seed ^ (uint)(i * 747796405 + 1));
                if (r.NextFloat() < (have - cap) * Rot) Leaves[i] = have - 1;
                return;
            }
            if (cap == 0 || have >= cap) return;
            var rng = new Random(Seed ^ (uint)(i * 747796405 + 1));
            if (rng.NextFloat() < Chance) Leaves[i] = have + 1;
        }
    }

    /// <summary>
    /// Defensive buildings, one per index. Pass 0: fortified nests halve the damage their own ants took this tick.
    /// Pass 1: resin traps slow, and stinger mounds sting, every hostile ant and bug in range. Ants are found through
    /// the combat spatial hash (bucket = colony * BucketsPerColony + cell).
    /// </summary>
    [BurstCompile]
    public unsafe struct DefenceJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<Ant> Ants;
        [ReadOnly] public NativeArray<int> Sorted;
        [ReadOnly] public NativeArray<int> BucketStart;
        [ReadOnly] public NativeArray<float4> Defences; // x, y, colony * 16 + kind, radius
        [ReadOnly] public NativeArray<byte> Hostile;
        [NativeDisableParallelForRestriction] public NativeArray<CombatResult> Results;
        [NativeDisableUnsafePtrRestriction] public int* Damage;
        public HashInfo Hash;
        public int MaxColonies, Slots, WildColony;
        public int Sting; // damage x100 per tick
        public int Pass;

        public void Execute(int d)
        {
            float4 def = Defences[d];
            int owner = (int)def.z / 16, kind = (int)def.z % 16;
            bool fortify = kind == 2;
            if (fortify != (Pass == 0)) return;
            float r = def.w;
            int2 lo = Hash.Cell(def.xy - r), hi = Hash.Cell(def.xy + r);
            for (int col = 0; col < Slots; col++)
            {
                if (fortify ? col != owner : col == owner || Hostile[owner * MaxColonies + col] == 0) continue;
                int colonyBase = col * Hash.BucketsPerColony;
                for (int by = lo.y; by <= hi.y; by++)
                for (int bx = lo.x; bx <= hi.x; bx++)
                {
                    int b = colonyBase + by * Hash.BucketsX + bx;
                    int end = BucketStart[b + 1];
                    for (int k = BucketStart[b]; k < end; k++)
                    {
                        int j = Sorted[k];
                        Ant a = Ants[j];
                        if (a.Dead != 0 || math.distancesq(a.Position, def.xy) > r * r) continue;
                        // Princesses and winged males fly over traps.
                        if (a.Colony != WildColony && a.Caste >= (byte)Caste.Princess) continue;
                        if (fortify) Damage[j] /= 2; // only this nest's own ants: no other job touches them now
                        else if (kind == 0)
                        {
                            var cr = Results[j];
                            cr.Slowed = 1;
                            Results[j] = cr;
                        }
                        else Interlocked.Add(ref Damage[j], Sting);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Death smell. Per colony: fade the distress grid, then pick up to HotspotsPerColony strongest,
    /// well-separated spots. Soldiers read the hotspots instead of scanning the grid.
    /// </summary>
    [BurstCompile]
    public struct DistressJob : IJobParallelFor
    {
        [NativeDisableParallelForRestriction] public NativeArray<int> Distress;
        [NativeDisableParallelForRestriction] public NativeArray<float4> Hotspots;
        public int Width, Height;
        public float CellSize;
        public float Decay;     // multiplier for this call
        public int Threshold;   // minimum strength to raise an alarm (100 = one death)
        public int Separation;  // hotspots at least this many cells apart

        public void Execute(int colony)
        {
            int per = Width * Height;
            int baseIdx = colony * per;
            for (int i = 0; i < per; i++)
            {
                int v = Distress[baseIdx + i];
                if (v != 0) Distress[baseIdx + i] = (int)(v * Decay);
            }

            int k0 = colony * AntStepJob.HotspotsPerColony;
            int found = 0;
            for (int k = 0; k < AntStepJob.HotspotsPerColony; k++)
            {
                int best = -1, bestV = Threshold - 1;
                for (int i = 0; i < per; i++)
                {
                    int v = Distress[baseIdx + i];
                    if (v <= bestV) continue;
                    int x = i % Width, y = i / Width;
                    bool tooClose = false;
                    for (int j = 0; j < found; j++)
                    {
                        float4 h = Hotspots[k0 + j];
                        int hx = (int)(h.x / CellSize), hy = (int)(h.y / CellSize);
                        if (math.abs(hx - x) <= Separation && math.abs(hy - y) <= Separation) { tooClose = true; break; }
                    }
                    if (tooClose) continue;
                    best = i;
                    bestV = v;
                }
                if (best < 0) break;
                // Strength = deaths in a 3x3 neighbourhood, so a spread-out massacre still reads as one big call.
                int bx = best % Width, by = best / Width;
                int sum = 0;
                for (int oy = -1; oy <= 1; oy++)
                for (int ox = -1; ox <= 1; ox++)
                {
                    int x = bx + ox, y = by + oy;
                    if (x < 0 || y < 0 || x >= Width || y >= Height) continue;
                    sum += Distress[baseIdx + y * Width + x];
                }
                Hotspots[k0 + found] = new float4((bx + 0.5f) * CellSize, (by + 0.5f) * CellSize, sum, 0f);
                found++;
            }
            for (int k = found; k < AntStepJob.HotspotsPerColony; k++) Hotspots[k0 + k] = float4.zero;
        }
    }

    /// <summary>
    /// Scans the wildlife: nearest beetle/spider to each nest (for raids and the AI), counts per kind,
    /// and beetles chewing on each aphid farm.
    /// </summary>
    [BurstCompile]
    public struct WildScanJob : IJob
    {
        [ReadOnly] public NativeArray<Ant> Ants;
        [ReadOnly] public NativeArray<NestData> Nests;
        [ReadOnly] public NativeArray<float4> Farms; // x, y, colony, fullness
        public NativeArray<float4> Nearest;  // [colony * WildKinds.Count + kind] = (x, y, dist to the nearest of its nests, found)
        public NativeArray<int> BeetlesAtFarm; // per farm
        public int FarmCount;
        public int NestCount;
        public int ColonyCount;
        public int WildColony;
        public float FarmRadius;

        public void Execute()
        {
            for (int i = 0; i < Nearest.Length; i++) Nearest[i] = new float4(0f, 0f, float.MaxValue, 0f);
            for (int f = 0; f < FarmCount; f++) BeetlesAtFarm[f] = 0;

            for (int i = 0; i < Ants.Length; i++)
            {
                Ant a = Ants[i];
                if (a.Dead != 0 || a.Colony != WildColony) continue;
                int kind = a.Caste;
                for (int n = 0; n < NestCount; n++)
                {
                    NestData nest = Nests[n];
                    if (nest.Active == 0 || nest.Founding != 0) continue;
                    float d = math.distance(a.Position, nest.Position);
                    int idx = nest.Colony * WildKinds.Count + kind;
                    if (d < Nearest[idx].z) Nearest[idx] = new float4(a.Position, d, 1f);
                }
                if (kind == (int)WildKind.Beetle)
                    for (int f = 0; f < FarmCount; f++)
                        if (math.distancesq(a.Position, Farms[f].xy) < FarmRadius * FarmRadius) BeetlesAtFarm[f]++;
            }
        }
    }
}
