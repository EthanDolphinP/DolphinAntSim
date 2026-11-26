using System.Threading;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace AntSim
{
    /// <summary>
    /// Spatial hash layout: one bucket grid per colony, so an ant only ever scans buckets that
    /// belong to colonies it is hostile to. Friendly crowds (e.g. 200k ants around one nest)
    /// cost nothing to search through.
    ///   bucket = colony * BucketsPerColony + by * BucketsX + bx
    /// </summary>
    public struct HashInfo
    {
        public int BucketsX;
        public int BucketsY;
        public float InvBucketSize;
        public int BucketsPerColony => BucketsX * BucketsY;

        public int2 Cell(float2 p) => new int2(
            math.clamp((int)(p.x * InvBucketSize), 0, BucketsX - 1),
            math.clamp((int)(p.y * InvBucketSize), 0, BucketsY - 1));
    }

    [BurstCompile]
    public struct AssignBucketsJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<Ant> Ants;
        [WriteOnly] public NativeArray<int> AntBucket;
        public HashInfo Hash;

        public void Execute(int i)
        {
            Ant a = Ants[i];
            int2 c = Hash.Cell(a.Position);
            AntBucket[i] = a.Colony * Hash.BucketsPerColony + c.y * Hash.BucketsX + c.x;
        }
    }

    /// <summary>Counting sort of ant indices by bucket. Single-threaded but linear and Burst-fast.</summary>
    [BurstCompile]
    public struct BuildHashJob : IJob
    {
        [ReadOnly] public NativeArray<int> AntBucket;
        public NativeArray<int> BucketStart; // length = bucketCount + 1
        public NativeArray<int> Cursor;      // length = bucketCount
        public NativeArray<int> Sorted;
        public int Count;

        public void Execute()
        {
            int buckets = Cursor.Length;
            for (int b = 0; b <= buckets; b++) BucketStart[b] = 0;
            for (int i = 0; i < Count; i++) BucketStart[AntBucket[i] + 1]++;
            for (int b = 1; b <= buckets; b++) BucketStart[b] += BucketStart[b - 1];
            for (int b = 0; b < buckets; b++) Cursor[b] = BucketStart[b];
            for (int i = 0; i < Count; i++)
            {
                int b = AntBucket[i];
                Sorted[Cursor[b]++] = i;
            }
        }
    }

    /// <summary>
    /// Every ant looks for the nearest hostile ant within its sense radius and bites it if close enough.
    /// Damage goes into a shared int array (fixed point, x100) with atomic adds; AntStepJob applies it.
    /// </summary>
    [BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
    public unsafe struct CombatJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<Ant> Ants;
        [ReadOnly] public NativeArray<int> Sorted;
        [ReadOnly] public NativeArray<int> BucketStart;
        [ReadOnly] public NativeArray<byte> Hostile; // [colony * MaxColonies + other]
        [WriteOnly] public NativeArray<CombatResult> Results;
        [NativeDisableUnsafePtrRestriction] public int* Damage;
        [NativeDisableUnsafePtrRestriction] public int* PairDamage; // [attacker * MaxColonies + victim], for diplomacy

        [ReadOnly] public NativeArray<SpeciesStats> Stats;
        [ReadOnly] public NativeArray<MorphStats> Morphs; // [colony * MaxMorphs + morph]
        public HashInfo Hash;
        public CombatParams C;
        public WildlifeParams W;
        public int ColonyCount; // colony slots, including wildlife
        public int MaxColonies;
        public int WildColony;

        public void Execute(int i)
        {
            Ant a = Ants[i];
            var r = new CombatResult();

            bool wild = a.Colony == WildColony;
            // Winged males and princesses on their mating flight don't fight.
            if (!wild && a.Caste >= (byte)Caste.Princess) { Results[i] = r; return; }
            bool soldier = !wild && a.Caste == (byte)Caste.Soldier;
            bool queen = !wild && a.Caste == (byte)Caste.Queen;
            SpeciesStats st = Stats[a.Colony];
            float sense = wild ? W.Sense(a.Caste)
                : (soldier ? C.SoldierSense : queen ? C.WorkerSense * 2f : C.WorkerSense) * st.SenseMul;
            int2 lo = Hash.Cell(a.Position - sense);
            int2 hi = Hash.Cell(a.Position + sense);

            float bestD = sense * sense;
            int best = -1;
            int checks = 0;
            int row = a.Colony * MaxColonies;

            for (int other = 0; other < ColonyCount; other++)
            {
                if (Hostile[row + other] == 0) continue;
                int colonyBase = other * Hash.BucketsPerColony;

                for (int by = lo.y; by <= hi.y; by++)
                for (int bx = lo.x; bx <= hi.x; bx++)
                {
                    int b = colonyBase + by * Hash.BucketsX + bx;
                    int end = BucketStart[b + 1];
                    for (int k = BucketStart[b]; k < end; k++)
                    {
                        int j = Sorted[k];
                        Ant o = Ants[j];
                        if (o.Caste == (byte)Caste.Male && o.Colony != WildColony) continue; // too quick to catch
                        float d = math.distancesq(o.Position, a.Position);
                        if (d < bestD) { bestD = d; best = j; }
                        if (++checks >= C.MaxChecks) goto Done;
                    }
                }
            }

            Done:
            if (best >= 0)
            {
                r.Engaged = 1;
                float2 to = Ants[best].Position - a.Position;
                r.TargetAngle = math.atan2(to.y, to.x);
                Ant target = Ants[best];
                r.TargetKind = (byte)(target.Colony == WildColony ? 1 + target.Caste : 0);
                // Big bugs have long legs: they reach a bit further. Antlions and centipedes further still.
                float reach = wild ? Reach(a.Caste) : C.AttackRange;
                if (target.Colony == WildColony) reach = math.max(reach, Reach(target.Caste));
                if (bestD <= reach * reach)
                {
                    r.InRange = 1;
                    if (a.Cooldown <= 0f)
                    {
                        float dmg = wild ? W.Damage(a.Caste)
                            : (soldier ? C.SoldierDamage : queen ? C.QueenDamage : C.WorkerDamage) * st.DamageMul
                              * Morphs[a.Colony * MorphStats.MaxMorphs + a.Morph].DamageMul;
                        Interlocked.Add(ref Damage[best], (int)(dmg * 100f));
                        if (!wild && target.Colony != WildColony)
                            Interlocked.Add(ref PairDamage[a.Colony * MaxColonies + target.Colony], (int)(dmg * 100f));
                        r.Attacked = 1;
                    }
                }
            }

            Results[i] = r;
        }

        float Reach(int kind) =>
            WildKinds.IsSmall(kind) ? C.AttackRange
            : kind == (int)WildKind.Antlion || kind == (int)WildKind.Centipede ? C.AttackRange * 2.4f
            : C.AttackRange * 1.8f;
    }
}
