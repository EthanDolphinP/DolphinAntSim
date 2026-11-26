using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace AntSim
{
    /// <summary>A tree: drops leaves now and then while it lives, and a heap of them when it dies.</summary>
    public class Tree
    {
        public Vector2 Position;
        public float Radius;      // canopy, grows to MaxRadius
        public float MaxRadius;
        public float Age;
        public float Lifespan;
        [Tooltip("Wood left, 0..1. Termites eat it; at 0 the tree dies.")]
        public float Health = 1f;
        public bool Dead;
        [Tooltip("Seconds since it died. The dead trunk stands for a while, then falls.")]
        public float DeadTime;
        public float DropTimer;
        public bool Warned; // the termites living in it have been told it's running out
    }

    public enum DefenceKind
    {
        ResinTrap = 0,    // sticky patch: enemies and bugs crossing it crawl at a third of their speed
        StingerMound = 1, // a mound of guards with stings: hurts every enemy and bug close by
        Fortified = 2,    // thick nest walls: your ants inside the nest take half damage
    }

    /// <summary>A defensive building placed by a colony.</summary>
    public class Defence
    {
        public int Colony;
        public int Kind;
        public Vector2 Position;
        public float Age;
    }

    /// <summary>Trees (and the termites that live in them) and defensive buildings.</summary>
    public partial class AntWorld
    {
        public IReadOnlyList<Tree> Trees => trees;
        public IReadOnlyList<Defence> Defences => defences;
        /// <summary>Next left click places this kind of defence (-1: not placing).</summary>
        public int PlacingDefence { get; set; } = -1;

        public const float TrunkRadius = 2.6f;
        const float DeadTrunkStands = 600f;  // seconds a dead tree stands before it falls
        const float TreeWoodTime = 3000f;    // seconds one termite nest takes to eat a whole tree
        const int MaxDefences = 128;
        const int MaxTreesDrawn = 256;

        public static readonly string[] DefenceNames = { "Resin trap", "Stinger mound", "Fortified nest" };
        public static readonly string[] DefenceInfo =
        {
            "A sticky patch. Enemy ants and bugs that cross it crawl at a third of their speed. Dries up after 8 minutes.",
            "A mound of guards with stings. Every enemy ant and bug close by gets stung, a few times a second.",
            "Thick walls and narrow tunnels for a nest. Your ants inside it (the queen too) take half damage.",
        };
        static readonly float[] DefenceCosts = { 30f, 80f, 150f };
        static readonly float[] DefenceRadius = { 7f, 9f, 0f };
        static readonly int[] DefenceLimit = { 10, 6, MaxNests };
        const float TrapLife = 480f;
        const float StingPerSecond = 1.6f;

        readonly List<Tree> trees = new List<Tree>();
        readonly List<Defence> defences = new List<Defence>();
        NativeArray<float4> treeData;    // x, y, canopy radius, health (-1 dead)
        NativeArray<float4> defenceData; // x, y, colony * 16 + kind, radius
        int treeTarget;
        float saplingTimer;

        public float DefenceCost(int kind) => DefenceCosts[kind];
        public float DefenceRange(int kind) => DefenceRadius[kind];

        void AllocateNature()
        {
            treeData = new NativeArray<float4>(MaxTreesDrawn, Allocator.Persistent);
            defenceData = new NativeArray<float4>(MaxDefences, Allocator.Persistent);
        }

        void DisposeNature()
        {
            if (treeData.IsCreated) treeData.Dispose();
            if (defenceData.IsCreated) defenceData.Dispose();
        }

        // ------------------------------------------------------------------ trees

        /// <summary>Trees scattered over the map. Termite colonies start in one, with a few more within reach.</summary>
        void GenerateTrees()
        {
            treeTarget = math.max(8, grid.CellCount / 25000);
            for (int attempt = 0; attempt < treeTarget * 6 && trees.Count < treeTarget; attempt++)
            {
                float2 p = rng.NextFloat2(new float2(20f), grid.WorldSize - 20f);
                if (!TreeSiteOk(p, 30f, true)) continue;
                var t = AddTree(p, rng.NextFloat(10f, 17f));
                t.Age = rng.NextFloat(0f, 0.6f) * t.Lifespan; // so they don't all die together
            }
            for (int c = 0; c < colonyCount; c++)
            {
                float2 home = nests[Colonies[c].PrimaryNest].Position;
                if (SpeciesOf(c).TreeNester)
                {
                    // Their home tree, young and full of wood, and three more to move to later.
                    var t = AddTree(home, 15f);
                    t.Lifespan = rng.NextFloat(5400f, 7200f);
                    for (int j = 0, tries = 0; j < 3 && tries < 60; tries++)
                    {
                        float2 p = home + rng.NextFloat2Direction() * rng.NextFloat(swarm.MinSpacing * 1.2f, swarm.MaxDistance * 0.7f);
                        if (!TreeSiteOk(p, 30f, false) || NearNest(p, 40f)) continue;
                        AddTree(p, rng.NextFloat(11f, 16f));
                        j++;
                    }
                }
                else
                {
                    // A tree near every nest: leaf litter for gardens and grazers for hunting.
                    for (int tries = 0; tries < 30; tries++)
                    {
                        float2 p = home + rng.NextFloat2Direction() * rng.NextFloat(50f, 110f);
                        if (!TreeSiteOk(p, 30f, true)) continue;
                        AddTree(p, rng.NextFloat(10f, 15f));
                        break;
                    }
                }
            }
        }

        bool TreeSiteOk(float2 p, float spacing, bool awayFromNests)
        {
            if (p.x < 15f || p.y < 15f || p.x > grid.WorldSize.x - 15f || p.y > grid.WorldSize.y - 15f) return false;
            if (!AreaClear(p, sim.NestRadius * 2.5f)) return false; // room for a termite nest
            if (awayFromNests && NearNest(p, 35f)) return false;
            foreach (var t in trees) if (math.distance(p, t.Position) < spacing) return false;
            return true;
        }

        Tree AddTree(float2 p, float maxRadius)
        {
            var t = new Tree
            {
                Position = p, MaxRadius = maxRadius, Radius = maxRadius,
                Lifespan = rng.NextFloat(2400f, 6000f), DropTimer = rng.NextFloat(5f, 40f),
            };
            trees.Add(t);
            return t;
        }

        /// <summary>The tree whose trunk is within reach of p (living or a dead trunk still standing).</summary>
        public Tree TreeAt(float2 p, float within)
        {
            Tree best = null;
            float bestD = within;
            foreach (var t in trees)
            {
                float d = math.distance(p, t.Position);
                if (d <= bestD) { bestD = d; best = t; }
            }
            return best;
        }

        /// <summary>Somewhere in the leaf litter under a random tree.</summary>
        bool TryRandomTreeSpot(out float2 p)
        {
            p = default;
            if (trees.Count == 0) return false;
            var t = trees[rng.NextInt(0, trees.Count)];
            p = (float2)t.Position + rng.NextFloat2Direction() * rng.NextFloat(TrunkRadius + 1f, math.max(TrunkRadius + 2f, t.Radius));
            return true;
        }

        /// <summary>Once a second: trees grow, drop leaves, get eaten by termites, die and fall; saplings appear.</summary>
        void UpdateTrees(float dt)
        {
            for (int c = 0; c < colonyCount; c++)
            {
                Colonies[c].WoodIncome = 0f;
                Colonies[c].ExposedNests = 0;
            }

            // Termite nests eat their tree; a nest whose tree has gone is exposed and its queen suffers.
            for (int k = 0; k < nestHigh; k++)
            {
                var info = nestInfo[k];
                if (info == null || info.Founding) continue;
                int c = info.Colony;
                if (!SpeciesOf(c).TreeNester) continue;
                var col = Colonies[c];
                var t = TreeAt(nests[k].Position, 4f);
                // A dying, dead or fallen tree: the queen leads the nest to a healthy tree nearby.
                if (info.HasQueen && info.MovingTo < 0 && (col.AutoMoveTree || c != Player) && NestTreeHealth(k) < 0.25f)
                    TryMoveToNewTree(c, k, dt);
                if (t == null)
                {
                    col.ExposedNests++;
                    int qi = queenIndex[k];
                    if (qi >= 0 && qi < ants.Length)
                    {
                        Ant q = ants[qi];
                        if (q.Dead == 0 && q.Caste == (byte)Caste.Queen && q.Nest == k)
                        {
                            q.Health -= 2.5f * dt;
                            ants[qi] = q;
                        }
                    }
                    continue;
                }
                float eat = col.Abstract ? 1f : math.min(1.5f, 0.5f + info.Ants / 400f);
                if (!t.Dead) t.Health -= eat * dt / TreeWoodTime;
                col.WoodIncome += (t.Dead ? 0.25f : 0.4f) * eat;
                if (c == Player && !t.Warned && !t.Dead && NestTreeHealth(k) < 0.35f)
                {
                    t.Warned = true;
                    Notify(col.AutoMoveTree
                        ? $"The tree holding nest {info.Number} is nearly eaten out. The queen will soon lead the nest to another tree."
                        : $"The tree holding nest {info.Number} is nearly eaten out. Move the nest to another tree (Nests tab).");
                }
            }
            for (int c = 0; c < colonyCount; c++)
                if (!Colonies[c].Extinct) Colonies[c].Food += Colonies[c].WoodIncome * dt;

            int living = 0;
            for (int i = trees.Count - 1; i >= 0; i--)
            {
                var t = trees[i];
                if (t.Dead)
                {
                    t.DeadTime += dt;
                    if (t.DeadTime > DeadTrunkStands)
                    {
                        trees.RemoveAt(i);
                        TreeFell(t);
                    }
                    continue;
                }
                living++;
                t.Age += dt;
                t.Radius = math.min(t.MaxRadius, t.Radius + 0.02f * dt);
                if (t.Age > t.Lifespan || t.Health <= 0f) { KillTree(t); continue; }

                t.DropTimer -= dt;
                if (t.DropTimer <= 0f)
                {
                    t.DropTimer = rng.NextFloat(20f, 40f);
                    // Old or termite-eaten trees have thinner crowns.
                    float vigour = math.min(t.Health, math.saturate(1.3f - t.Age / t.Lifespan));
                    int n = (int)((6f + t.Radius * 0.8f) * (0.35f + 0.65f * vigour));
                    AddToArea(leaves, t.Position, (int)t.Radius, n, farming.LeafCap);
                }
            }

            // New trees take the place of fallen ones.
            saplingTimer += dt;
            if (living < treeTarget && saplingTimer > 45f)
            {
                saplingTimer = 0f;
                for (int attempt = 0; attempt < 20; attempt++)
                {
                    float2 p = rng.NextFloat2(new float2(20f), grid.WorldSize - 20f);
                    if (!TreeSiteOk(p, 30f, true)) continue;
                    var t = AddTree(p, rng.NextFloat(10f, 17f));
                    t.Radius = 4f;
                    break;
                }
            }
        }

        void KillTree(Tree t)
        {
            t.Dead = true;
            t.DeadTime = 0f;
            // Everything comes down at once: a carpet of leaves around the trunk.
            int n = (int)(t.Radius * 45f);
            AddToArea(leaves, t.Position, (int)(t.Radius * 1.3f), n, farming.LeafCap * 2);

            int k = NestInTree(t);
            if (k >= 0 && nestInfo[k].Colony == Player)
                Notify($"The tree holding nest {nestInfo[k].Number} has died. Its trunk will stand for a few more minutes: " +
                       "found a nest in another tree before it falls.");
            else if (NestNear(Player, t.Position, 200f))
                Notify("A tree near your nest has died and dropped all its leaves.");
        }

        void TreeFell(Tree t)
        {
            int k = NestInTree(t);
            if (k >= 0 && nestInfo[k].Colony == Player)
                Notify($"The tree holding nest {nestInfo[k].Number} has fallen! Its termites are exposed and the queen there is dying.");
        }

        int NestInTree(Tree t)
        {
            for (int k = 0; k < nestHigh; k++)
                if (nestInfo[k] != null && !nestInfo[k].Founding && math.distance(nests[k].Position, t.Position) < 4f) return k;
            return -1;
        }

        bool NestNear(int colony, float2 p, float dist)
        {
            for (int k = 0; k < nestHigh; k++)
                if (nestInfo[k] != null && nestInfo[k].Colony == colony && math.distance(p, nests[k].Position) < dist) return true;
            return false;
        }

        /// <summary>Wood left in the tree a nest lives in (0..1, 0 if it's dead or gone), for the HUD and the AI.</summary>
        public float NestTreeHealth(int k)
        {
            var t = TreeAt(nests[k].Position, 4f);
            return t == null || t.Dead ? 0f : t.Health * math.saturate((t.Lifespan - t.Age) / 600f);
        }

        /// <summary>Termites: a clicked spot means the tree there.</summary>
        public float2 SnapNestSite(int c, float2 p)
        {
            if (!SpeciesOf(c).TreeNester) return p;
            var t = TreeAt(p, 12f);
            return t != null ? (float2)t.Position : p;
        }

        /// <summary>Extra rules for tree nesters (null if the site is fine).</summary>
        string TreeSiteError(int c, float2 p)
        {
            if (!SpeciesOf(c).TreeNester) return null;
            var t = TreeAt(p, 3f);
            if (t == null) return "Termites can only nest inside a tree: click a tree.";
            if (t.Dead) return "That tree is dead: pick a living one.";
            if (t.Health < 0.4f || t.Lifespan - t.Age < 900f) return "That tree is too old or eaten out: pick a healthier one.";
            for (int k = 0; k < nestHigh; k++)
                if (nestInfo[k] != null && math.distance(nests[k].Position, t.Position) < 4f) return "Another nest is already in that tree.";
            return null;
        }

        /// <summary>Termites: look for a healthy tree within a queen's walk of nest k, and move there.</summary>
        void TryMoveToNewTree(int c, int k, float dt)
        {
            var info = nestInfo[k];
            info.MoveRetry -= dt;
            if (info.MoveRetry > 0f || MoveInProgress(c) >= 0) return;
            info.MoveRetry = 15f;
            Tree best = null;
            float bestScore = float.MinValue;
            foreach (var t in trees)
            {
                float2 p = t.Position;
                if (ValidateMoveSite(c, k, p) != null) continue;
                float d = math.distance(p, nests[k].Position);
                float score = t.Health * 200f + math.min(t.Lifespan - t.Age, 3600f) * 0.05f - d * 0.8f;
                for (int j = 0; j < nestHigh; j++)
                {
                    var other = nestInfo[j];
                    if (other == null || other.Colony == c || !IsAtWar(c, other.Colony)) continue;
                    float e = math.distance(p, nests[j].Position);
                    if (e < 220f) score -= (220f - e) * 3f;
                }
                if (score > bestScore) { bestScore = score; best = t; }
            }
            if (best == null)
            {
                if (c == Player && !info.NoTreeWarned)
                {
                    info.NoTreeWarned = true;
                    Notify($"There's no healthy tree within a queen's walk ({MoveMaxDistance:0}) of nest {info.Number}. " +
                           "Found a nest further away with a princess (Nests tab) before the tree falls.");
                }
                return;
            }
            MoveNest(c, k, best.Position, true);
        }

        /// <summary>The best tree for a termite colony's next nest.</summary>
        bool FindTreeSite(int c, out float2 site)
        {
            site = default;
            float best = float.MinValue;
            foreach (var t in trees)
            {
                float2 p = t.Position;
                if (ValidateNestSite(c, p) != null) continue;
                int near = NearestNest(c, p, false);
                float dist = near >= 0 ? math.distance(p, nests[near].Position) : 0f;
                float score = t.Health * 200f + math.min(t.Lifespan - t.Age, 3600f) * 0.05f - dist * 0.3f + rng.NextFloat(0f, 30f);
                for (int k = 0; k < nestHigh; k++)
                {
                    var info = nestInfo[k];
                    if (info == null || info.Colony == c || !IsAtWar(c, info.Colony)) continue;
                    float d = math.distance(p, nests[k].Position);
                    if (d < 220f) score -= (220f - d) * 3f;
                }
                if (score > best) { best = score; site = p; }
            }
            return best > float.MinValue;
        }

        int FillTreeData()
        {
            int n = math.min(trees.Count, MaxTreesDrawn);
            for (int i = 0; i < n; i++)
            {
                var t = trees[i];
                float health = t.Dead ? -1f : math.min(t.Health, math.saturate((t.Lifespan - t.Age) / 600f));
                treeData[i] = new float4(t.Position.x, t.Position.y, t.Radius, health);
            }
            return n;
        }

        // ------------------------------------------------------------------ slave-makers

        /// <summary>
        /// Slave-making species (Amazon ants) carry home a pupa for each enemy ant they kill; it hatches as their worker.
        /// Called once a second from diplomacy, before the damage tally is cleared.
        /// </summary>
        void TakeCaptives(float antHp)
        {
            for (int c = 0; c < colonyCount; c++)
            {
                var col = Colonies[c];
                float share = SpeciesOf(c).Captives;
                if (share <= 0f || col.Extinct) continue;
                float kills = 0f;
                for (int o = 0; o < colonyCount; o++)
                    if (o != c) kills += pairDamage[c * MaxColonies + o] * 0.01f / antHp;
                col.CaptiveCarry += kills * share;
                int n = (int)col.CaptiveCarry;
                if (n <= 0) continue;
                col.CaptiveCarry -= n;
                col.Captured += n;
                // Brood is kept oldest first: the pupae go in among the others of their age.
                float age = economy.EggDuration + economy.LarvaDuration;
                var brood = col.Brood;
                int at = 0;
                while (at < brood.Count && brood[at].Age >= age) at++;
                brood.Insert(at, new BroodCohort { Age = age, Workers = n });
            }
        }

        // ------------------------------------------------------------------ defences

        public int DefenceCount(int colony, int kind)
        {
            int n = 0;
            foreach (var d in defences) if (d.Colony == colony && d.Kind == kind) n++;
            return n;
        }

        public int DefenceLimitOf(int kind) => DefenceLimit[kind];

        /// <summary>Is this nest fortified?</summary>
        public bool IsFortified(int k)
        {
            var info = nestInfo[k];
            if (info == null) return false;
            foreach (var d in defences)
                if (d.Kind == (int)DefenceKind.Fortified && d.Colony == info.Colony && math.distance(d.Position, nests[k].Position) < 1f)
                    return true;
            return false;
        }

        /// <summary>Builds a defence. Returns an error message, or null on success.</summary>
        public string PlaceDefence(int colony, int kind, float2 pos)
        {
            var col = Colonies[colony];
            float cost = DefenceCosts[kind];
            if (defences.Count >= MaxDefences) return "The map can't hold more defences.";
            if (DefenceCount(colony, kind) >= DefenceLimit[kind]) return $"You can't have more than {DefenceLimit[kind]} of those.";

            if (kind == (int)DefenceKind.Fortified)
            {
                int k = NearestNest(colony, pos, false);
                if (k < 0 || nestInfo[k].Founding || math.distance(pos, nests[k].Position) > nests[k].Radius + 8f)
                    return "Click one of your nests to fortify it.";
                if (IsFortified(k)) return "That nest is already fortified.";
                if (col.Food < cost) return $"Not enough food: fortifying a nest costs {cost:0}.";
                col.Food -= cost;
                defences.Add(new Defence { Colony = colony, Kind = kind, Position = nests[k].Position });
                return null;
            }

            int near = NearestNest(colony, pos, false);
            if (near < 0 || math.distance(pos, nests[near].Position) > farming.FarmMaxDistance)
                return $"Too far from your nests (max {farming.FarmMaxDistance:0}).";
            if (IsBlocked(pos)) return "Not on rock or water.";
            foreach (var d in defences)
                if (d.Kind != (int)DefenceKind.Fortified && math.distance((float2)d.Position, pos) < 6f) return "There's already a defence here.";
            if (col.Food < cost) return $"Not enough food: a {DefenceNames[kind].ToLower()} costs {cost:0}.";
            col.Food -= cost;
            defences.Add(new Defence { Colony = colony, Kind = kind, Position = pos });
            return null;
        }

        /// <summary>Once a second: traps dry up; defences of dead colonies and abandoned nests go.</summary>
        void UpdateDefences(float dt)
        {
            for (int i = defences.Count - 1; i >= 0; i--)
            {
                var d = defences[i];
                d.Age += dt;
                bool gone = Colonies[d.Colony].Extinct
                            || (d.Kind == (int)DefenceKind.ResinTrap && d.Age > TrapLife);
                if (d.Kind == (int)DefenceKind.Fortified)
                {
                    int k = NearestNest(d.Colony, d.Position, false);
                    gone |= k < 0 || math.distance(nests[k].Position, d.Position) > 1f;
                }
                if (gone) defences.RemoveAt(i);
            }
        }

        /// <summary>Rivals put up a stinger mound or two and fortify their main nest once they can afford it.</summary>
        void RivalDefences(int c, ColonyInfo col, float reserve)
        {
            bool threatened = col.DistressAlerts > 2 || SimTime > PeacefulStart;
            if (!threatened || col.Population < 150) return;
            int want = math.min(DefenceLimit[1], 1 + col.Population / 700);
            if (DefenceCount(c, (int)DefenceKind.StingerMound) < want && col.Food > DefenceCosts[1] * 2f + reserve)
            {
                int k = col.PrimaryNest;
                for (int attempt = 0; attempt < 6; attempt++)
                {
                    float2 p = nests[k].Position + rng.NextFloat2Direction() * rng.NextFloat(10f, 22f);
                    if (PlaceDefence(c, (int)DefenceKind.StingerMound, p) == null) break;
                }
            }
            if (!IsFortified(col.PrimaryNest) && col.Food > DefenceCosts[2] * 2.5f + reserve)
                PlaceDefence(c, (int)DefenceKind.Fortified, nests[col.PrimaryNest].Position);
        }

        int FillDefenceData()
        {
            int n = math.min(defences.Count, MaxDefences);
            for (int i = 0; i < n; i++)
            {
                var d = defences[i];
                float r = d.Kind == (int)DefenceKind.Fortified ? sim.NestRadius * 1.6f : DefenceRadius[d.Kind];
                defenceData[i] = new float4(d.Position.x, d.Position.y, d.Colony * 16 + d.Kind, r);
            }
            return n;
        }

        /// <summary>
        /// After combat: fortified nests halve the damage their own ants just took, then traps slow and stinger
        /// mounds sting every hostile ant and bug in range. Uses the combat spatial hash.
        /// </summary>
        unsafe JobHandle ScheduleDefences(JobHandle dependsOn, float dt)
        {
            int count = FillDefenceData();
            if (count == 0) return dependsOn;
            var job = new DefenceJob
            {
                Ants = ants.AsArray(), Sorted = sortedAnts, BucketStart = bucketStart, Defences = defenceData,
                Hostile = hostile, Results = combatResults, Damage = (int*)damage.GetUnsafePtr(),
                Hash = hash, MaxColonies = MaxColonies, Slots = slots, WildColony = WildColony,
                Sting = (int)(StingPerSecond * dt * 100f + 0.5f), Pass = 0,
            };
            var h = job.Schedule(count, 1, dependsOn);
            job.Pass = 1;
            return job.Schedule(count, 1, h);
        }
    }
}
