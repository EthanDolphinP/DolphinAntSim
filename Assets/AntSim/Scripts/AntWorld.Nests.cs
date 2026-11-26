using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace AntSim
{
    /// <summary>Main-thread view of one nest (the Burst jobs use <see cref="NestData"/>).</summary>
    public class NestInfo
    {
        public int Index;
        public int Colony;
        public int Number;          // 1, 2, 3... within the colony, for the HUD
        public Vector2 Position;
        public bool Founding;       // a princess is flying here
        public bool HasQueen;
        public float QueenHealth;
        public int Ants;            // workers + soldiers living here (awake colonies only)
        public float QueenlessTime;
        public float SleepShare;    // fraction of the colony's ants that live here, while it sleeps
        public float FoundedAt;
        public int MovingFrom = -1; // moving house: this is the new site, and the queen is walking here from that nest
        public int MovingTo = -1;   // moving house: this is the old nest, being left for that one
        public float MoveTime;      // seconds since the move started
        public float MoveRetry;     // termites: seconds until the next look for a tree to move to
        public bool NoTreeWarned;
    }

    /// <summary>
    /// Multiple nests per colony, and swarming: a colony raises a princess, who flies to a new site
    /// with winged males and an escort of workers. If she gets there she becomes the new nest's queen.
    /// Food and brood are pooled across a colony's nests; each ant belongs to one nest.
    /// </summary>
    public partial class AntWorld
    {
        public const int MaxNests = 64;

        [Header("Nests & swarming")]
        public SwarmParams swarm = SwarmParams.Default;

        /// <summary>Next left click picks a site for a new nest.</summary>
        public bool PlacingNest { get; set; }
        /// <summary>Nest slots in use are all below this.</summary>
        public int NestSlots => nestHigh;
        public NestInfo GetNest(int index) => index >= 0 && index < MaxNests ? nestInfo[index] : null;

        readonly NestInfo[] nestInfo = new NestInfo[MaxNests];
        int nestHigh;
        NativeArray<int> princessIndex;
        NativeArray<int> nestCounts;
        NativeArray<int> nestSoldiers;
        NativeArray<int> nestDrones;
        NativeArray<float4> squadSums;
        NativeArray<int> nestIntruders;
        NativeArray<short> coreOwner;
        NativeArray<float> nestShare;
        NativeReference<int> reassignCount;
        NativeArray<float2> queenPos; // per nest: where its queen is (read by soldiers escorting a moving queen)

        void AllocateNestArrays()
        {
            princessIndex = new NativeArray<int>(MaxNests, Allocator.Persistent);
            for (int n = 0; n < MaxNests; n++) princessIndex[n] = -1;
            nestCounts = new NativeArray<int>(MaxNests, Allocator.Persistent);
            nestSoldiers = new NativeArray<int>(MaxNests, Allocator.Persistent);
            nestDrones = new NativeArray<int>(MaxNests, Allocator.Persistent);
            AllocateDroneArrays();
            squadSums = new NativeArray<float4>(MaxNests * NestData.MaxSquads, Allocator.Persistent);
            nestIntruders = new NativeArray<int>(MaxNests, Allocator.Persistent);
            nestShare = new NativeArray<float>(MaxNests, Allocator.Persistent);
            reassignCount = new NativeReference<int>(Allocator.Persistent);
            queenPos = new NativeArray<float2>(MaxNests, Allocator.Persistent);
        }

        void DisposeNests()
        {
            if (princessIndex.IsCreated) princessIndex.Dispose();
            if (nestCounts.IsCreated) nestCounts.Dispose();
            if (nestSoldiers.IsCreated) nestSoldiers.Dispose();
            if (nestDrones.IsCreated) nestDrones.Dispose();
            DisposeDroneArrays();
            if (squadSums.IsCreated) squadSums.Dispose();
            if (nestIntruders.IsCreated) nestIntruders.Dispose();
            if (nestShare.IsCreated) nestShare.Dispose();
            if (reassignCount.IsCreated) reassignCount.Dispose();
            if (queenPos.IsCreated) queenPos.Dispose();
        }

        CompactJob MakeCompactJob() => new CompactJob
        {
            Ants = ants, CasteCounts = casteCounts, QueenIndex = queenIndex,
            PrincessIndex = princessIndex, NestCounts = nestCounts, NestSoldiers = nestSoldiers, NestDrones = nestDrones, MorphCounts = morphCounts,
            SquadSums = squadSums, Nests = nests, WildColony = WildColony,
        };

        // ------------------------------------------------------------------ nest slots

        int AllocNest(int colony, float2 pos, bool founding)
        {
            for (int k = 0; k < MaxNests; k++)
            {
                if (nestInfo[k] != null) continue;
                var col = Colonies[colony];
                nests[k] = new NestData
                {
                    Position = pos, Radius = sim.NestRadius, Colony = (byte)colony, Active = 1,
                    Founding = (byte)(founding ? 1 : 0), SquadCount = 1,
                    CoreRadius = math.max(lod.MinTerritory, col.TerritoryRadius * lod.CoreFraction),
                };
                nestInfo[k] = new NestInfo
                {
                    Index = k, Colony = colony, Number = ++col.NestSerial, Position = pos,
                    Founding = founding, FoundedAt = SimTime,
                };
                queenIndex[k] = -1;
                princessIndex[k] = -1;
                nestCounts[k] = 0;
                nestSoldiers[k] = 0;
                nestHigh = math.max(nestHigh, k + 1);
                return k;
            }
            return -1;
        }

        void FreeNest(int k)
        {
            nests[k] = default;
            nestInfo[k] = null;
            queenIndex[k] = -1;
            princessIndex[k] = -1;
            while (nestHigh > 0 && nestInfo[nestHigh - 1] == null) nestHigh--;
        }

        /// <summary>The colony's main nest (where the camera goes, and the default home for new ants).</summary>
        public float2 NestPos(int c) => nests[Colonies[c].PrimaryNest].Position;

        /// <summary>The colony's nest closest to a point, or -1. Founding sites don't count.</summary>
        public int NearestNest(int colony, float2 p, bool needQueen, int exclude = -1)
        {
            int best = -1;
            float bestD = float.MaxValue;
            for (int k = 0; k < nestHigh; k++)
            {
                var info = nestInfo[k];
                if (info == null || info.Colony != colony || info.Founding || k == exclude) continue;
                if (needQueen && !info.HasQueen) continue;
                float d = math.distancesq(nests[k].Position, p);
                if (d < bestD) { bestD = d; best = k; }
            }
            return best;
        }

        int ReassignNest(int colony, int from, int to, int percent, bool killFlyers)
        {
            new ReassignNestJob
            {
                Ants = ants.AsArray(), Colony = colony, FromNest = from, ToNest = to, Percent = percent,
                KillFlyers = killFlyers, Count = reassignCount,
            }.Run();
            return reassignCount.Value;
        }

        // ------------------------------------------------------------------ per tick

        /// <summary>After compaction: who has a queen, how many ants live where, abandoned nests.</summary>
        void UpdateNests(float dt)
        {
            for (int c = 0; c < colonyCount; c++) { Colonies[c].NestCount = 0; Colonies[c].Queens = 0; }

            for (int k = 0; k < nestHigh; k++)
            {
                var info = nestInfo[k];
                if (info == null) continue;
                var col = Colonies[info.Colony];
                int qi = queenIndex[k];
                bool hasQueen = qi >= 0 && qi < ants.Length;
                if (info.HasQueen && !hasQueen && !info.Founding && info.MovingTo < 0 && info.Colony == Player)
                    Notify($"The queen of nest {info.Number} has died.");
                info.HasQueen = hasQueen;
                info.QueenHealth = hasQueen ? ants[qi].Health : 0f;
                if (!col.Abstract) info.Ants = nestCounts[k];
                if (info.Founding) continue;
                col.NestCount++;
                if (hasQueen) col.Queens++;
            }

            for (int k = 0; k < nestHigh; k++)
            {
                var info = nestInfo[k];
                if (info == null || info.Founding) continue;
                var col = Colonies[info.Colony];
                // A queenless nest is given up once another of the colony's nests has a queen:
                // its ants move there. (With no queen anywhere, the colony tries to raise a new one.)
                if (info.HasQueen || col.Queens == 0 || col.NestCount < 2) { info.QueenlessTime = 0f; continue; }
                info.QueenlessTime += dt;
                if (info.QueenlessTime >= swarm.AbandonTime) AbandonNest(k);
            }

            for (int c = 0; c < colonyCount; c++)
            {
                var col = Colonies[c];
                var primary = GetNest(col.PrimaryNest);
                if (primary == null || primary.Founding || primary.Colony != c)
                {
                    int p = NearestNest(c, col.NestPosition, true);
                    if (p < 0) p = NearestNest(c, col.NestPosition, false);
                    if (p >= 0) col.PrimaryNest = p;
                }
                if (GetNest(col.PrimaryNest) != null) col.NestPosition = (Vector2)nests[col.PrimaryNest].Position;
            }
        }

        void AbandonNest(int k)
        {
            var info = nestInfo[k];
            int c = info.Colony;
            var col = Colonies[c];
            int to = NearestNest(c, nests[k].Position, true, k);
            if (to < 0) return;
            if (col.Abstract)
            {
                nestInfo[to].SleepShare += info.SleepShare;
            }
            else
            {
                ReassignNest(c, k, to, 100, true);
            }
            if (c == Player) Notify($"Nest {info.Number} had no queen and was abandoned. Its ants moved to nest {nestInfo[to].Number}.");
            FreeNest(k);
        }

        float QueenHealthOf(int c)
        {
            var primary = GetNest(Colonies[c].PrimaryNest);
            if (primary != null && primary.HasQueen) return primary.QueenHealth;
            float best = 0f;
            for (int k = 0; k < nestHigh; k++)
                if (nestInfo[k] != null && nestInfo[k].Colony == c) best = math.max(best, nestInfo[k].QueenHealth);
            return best;
        }

        /// <summary>Where a replacement queen is raised: the nest with the most ants.</summary>
        int ReplacementQueenNest(int c)
        {
            int best = Colonies[c].PrimaryNest, most = -1;
            for (int k = 0; k < nestHigh; k++)
            {
                var info = nestInfo[k];
                if (info == null || info.Colony != c || info.Founding) continue;
                if (info.Ants > most) { most = info.Ants; best = k; }
            }
            return best;
        }

        /// <summary>New adults hatch in the nests that have queens, shared out evenly.</summary>
        void SpawnNewborn(int c, int count, Caste caste)
        {
            if (count <= 0) return;
            var col = Colonies[c];
            if (col.Queens <= 1)
            {
                int home = NearestNest(c, col.NestPosition, true);
                SpawnAntsAtNest(c, home >= 0 ? home : col.PrimaryNest, count, caste, sim.NestRadius * 0.8f);
                return;
            }
            int left = count, queensLeft = col.Queens;
            for (int k = 0; k < nestHigh && left > 0; k++)
            {
                var info = nestInfo[k];
                if (info == null || info.Colony != c || info.Founding || !info.HasQueen) continue;
                int share = queensLeft <= 1 ? left : (left + queensLeft - 1) / queensLeft;
                SpawnAntsAtNest(c, k, share, caste, sim.NestRadius * 0.8f);
                left -= share;
                queensLeft--;
            }
            if (left > 0) SpawnAntsAtNest(c, col.PrimaryNest, left, caste, sim.NestRadius * 0.8f);
        }

        /// <summary>After RemoveColonyJob: remember what share of the ants lived in each nest.</summary>
        void StoreSleepShares(int c)
        {
            float total = 0f;
            for (int k = 0; k < nestHigh; k++)
                if (nestInfo[k] != null && nestInfo[k].Colony == c) total += nestShare[k];
            for (int k = 0; k < nestHigh; k++)
            {
                var info = nestInfo[k];
                if (info == null || info.Colony != c) continue;
                info.SleepShare = total > 0f ? nestShare[k] / total : (k == Colonies[c].PrimaryNest ? 1f : 0f);
            }
            for (int k = 0; k < MaxNests; k++) nestShare[k] = 0f;
        }

        // ------------------------------------------------------------------ swarming

        /// <summary>Checks a site for a new nest. Returns an error message, or null if it's fine.</summary>
        public string ValidateNestSite(int c, float2 p)
        {
            float margin = sim.NestRadius * 3f;
            if (p.x < margin || p.y < margin || p.x > grid.WorldSize.x - margin || p.y > grid.WorldSize.y - margin)
                return "Too close to the edge of the map.";
            if (!AreaClear(p, sim.NestRadius * 2.5f)) return "Ants can't nest on rock or water.";
            string treeErr = TreeSiteError(c, p);
            if (treeErr != null) return treeErr;
            float nearestOwn = float.MaxValue;
            for (int k = 0; k < nestHigh; k++)
            {
                var info = nestInfo[k];
                if (info == null) continue;
                float d = math.distance(p, nests[k].Position);
                if (info.Colony == c)
                {
                    if (d < swarm.MinSpacing) return $"Too close to one of your nests (min {swarm.MinSpacing:0}).";
                    if (!info.Founding) nearestOwn = math.min(nearestOwn, d);
                }
                else if (d < swarm.EnemyClearance) return "Too close to another colony's nest.";
            }
            if (nearestOwn > swarm.MaxDistance) return $"Too far from your nests (max {swarm.MaxDistance:0}).";
            return null;
        }

        /// <summary>Why the colony can't swarm right now, or null if it can (ignoring the site).</summary>
        public string SwarmBlocker(int c)
        {
            var col = Colonies[c];
            if (col.SwarmState == 1) return "Already raising a princess.";
            if (col.SwarmState == 2) return "A princess is already on her way to a new nest.";
            if (!col.HasQueen) return "You need a queen to raise a princess.";
            if (col.NestCount >= swarm.MaxNestsPerColony) return $"A colony can't hold more than {swarm.MaxNestsPerColony} nests.";
            if (col.Population < swarm.MinPopulation) return $"Too few ants: swarming needs {swarm.MinPopulation}.";
            if (col.Food < swarm.Cost) return $"Not enough food: a princess costs {swarm.Cost:0}.";
            return null;
        }

        /// <summary>Starts raising a princess who'll found a nest at the site. Returns an error message, or null.</summary>
        public string StartSwarm(int c, float2 site)
        {
            string err = SwarmBlocker(c) ?? ValidateNestSite(c, site);
            if (err != null) return err;
            var col = Colonies[c];
            col.Food -= swarm.Cost;
            col.SwarmState = 1;
            col.SwarmProgress = 0f;
            col.SwarmTarget = (Vector2)site;
            col.SwarmParent = NearestNest(c, site, true);
            col.SwarmNest = -1;
            return null;
        }

        /// <summary>Calls off a swarm that hasn't flown yet (half the food comes back).</summary>
        public void CancelSwarm(int c)
        {
            var col = Colonies[c];
            if (col.SwarmState != 1) return;
            col.Food += swarm.Cost * 0.5f;
            col.SwarmState = 0;
            col.SwarmProgress = 0f;
        }

        void UpdateSwarms(float dt)
        {
            for (int c = 0; c < colonyCount; c++)
            {
                var col = Colonies[c];
                if (col.Extinct) { col.SwarmState = 0; continue; }
                col.SwarmCooldown = math.max(0f, col.SwarmCooldown - dt);
                switch (col.SwarmState)
                {
                    case 0: AutoSwarm(c, col); break;
                    case 1: RaisePrincess(c, col, dt); break;
                    case 2: FollowFlight(c, col, dt); break;
                }
            }
        }

        /// <summary>Nesting rule: found a new nest when every nest is crowded.</summary>
        void AutoSwarm(int c, ColonyInfo col)
        {
            if (!col.AutoSwarm || col.SwarmCooldown > 0f) return;
            int cap = math.min(col.MaxNests, swarm.MaxNestsPerColony);
            if (col.NestCount >= cap || col.Population < col.SwarmAt * col.NestCount) return;
            float reserve = col.Population * economy.UpkeepPerAntPerSecond * economy.FoodReserveSeconds;
            if (col.Food < swarm.Cost + reserve) return;
            col.SwarmCooldown = 20f; // don't search for a site every second
            if (!FindNestSite(c, out float2 site)) return;
            if (StartSwarm(c, site) == null && c == Player)
                Notify("Your colony is crowded: raising a princess to found a new nest.");
        }

        void RaisePrincess(int c, ColonyInfo col, float dt)
        {
            if (!col.HasQueen) return; // stalls until there's a queen again
            col.SwarmProgress += dt / swarm.PrincessTime;
            if (col.SwarmProgress < 1f) return;

            // The site may have been taken while she grew up.
            string err = ValidateNestSite(c, col.SwarmTarget);
            int parent = NearestNest(c, col.SwarmTarget, true);
            if (err != null || parent < 0)
            {
                if (FindNestSite(c, out float2 alt)) col.SwarmTarget = (Vector2)alt;
                else { FailSwarm(c, col, "There was nowhere left to found a nest."); return; }
                parent = NearestNest(c, col.SwarmTarget, true);
                if (parent < 0) return;
            }
            col.SwarmParent = parent;
            LaunchSwarm(c, col);
        }

        void LaunchSwarm(int c, ColonyInfo col)
        {
            int parent = col.SwarmParent;
            int k = AllocNest(c, col.SwarmTarget, true);
            if (k < 0) { FailSwarm(c, col, "The world is full of nests."); return; }
            col.SwarmNest = k;
            col.SwarmTimer = 0f;
            col.SwarmProgress = 1f;

            if (col.Abstract)
            {
                // Nobody's watching: the flight just succeeds and a share of the colony moves in.
                SpawnAntsAtNest(c, k, 1, Caste.Queen, 0f);
                var from = nestInfo[parent];
                float moved = from.SleepShare * swarm.EscortPercent / 100f;
                from.SleepShare -= moved;
                nestInfo[k].SleepShare = moved;
                FoundNest(c, col, k);
                return;
            }

            float2 start = nests[parent].Position;
            SpawnAntsAt(c, 1, Caste.Princess, start, 0f, false, 0f, 0f, false, k);
            SpawnAntsAt(c, swarm.Males, Caste.Male, start, sim.NestRadius, false, 0f, 0f, false, k);
            int escort = ReassignNest(c, parent, k, swarm.EscortPercent, false);
            col.SwarmState = 2;
            if (c == Player) Notify($"The princess has taken flight with {escort} ants. Keep her safe!");
        }

        void FollowFlight(int c, ColonyInfo col, float dt)
        {
            int k = col.SwarmNest;
            var info = GetNest(k);
            if (info == null) { col.SwarmState = 0; return; }
            col.SwarmTimer += dt;
            int pi = princessIndex[k];
            bool alive = pi >= 0 && pi < ants.Length && ants[pi].Dead == 0 && ants[pi].Caste == (byte)Caste.Princess;
            if (!alive) { FailSwarm(c, col, "The princess was killed on her flight."); return; }

            Ant p = ants[pi];
            if (math.distance(p.Position, nests[k].Position) < sim.NestRadius)
            {
                // She's home: crown her.
                p.Caste = (byte)Caste.Queen;
                p.Health = combat.QueenHealth * SpeciesOf(c).Health;
                ants[pi] = p;
                queenIndex[k] = pi;
                info.HasQueen = true;
                col.JustCrownedQueen = true;
                FoundNest(c, col, k);
                return;
            }
            if (col.SwarmTimer > swarm.FlightTimeout) FailSwarm(c, col, "The princess got lost and never found the site.");
        }

        void FoundNest(int c, ColonyInfo col, int k)
        {
            var info = nestInfo[k];
            info.Founding = false;
            info.FoundedAt = SimTime;
            var nd = nests[k];
            nd.Founding = 0;
            nests[k] = nd;
            col.NestsFounded++;
            col.SwarmState = 0;
            col.SwarmProgress = 0f;
            col.SwarmNest = -1;
            col.SwarmCooldown = 60f;
            if (c == Player) Notify($"New nest founded! Nest {info.Number} has a queen of its own.");
        }

        void FailSwarm(int c, ColonyInfo col, string why)
        {
            int k = col.SwarmNest;
            if (k >= 0 && nestInfo[k] != null && nestInfo[k].Founding)
            {
                int back = NearestNest(c, nests[k].Position, true);
                if (back < 0) back = col.PrimaryNest;
                ReassignNest(c, k, back, 100, true);
                FreeNest(k);
            }
            col.SwarmState = 0;
            col.SwarmProgress = 0f;
            col.SwarmNest = -1;
            col.SwarmsFailed++;
            col.SwarmCooldown = 90f;
            if (c == Player) Notify("Swarm failed: " + why);
        }

        /// <summary>Scouts look for a good spot: plants and food nearby, away from enemies, not too far from home.</summary>
        public bool FindNestSite(int c, out float2 site)
        {
            site = default;
            if (SpeciesOf(c).TreeNester) return FindTreeSite(c, out site);
            float bestScore = float.MinValue;
            int ownNests = 0;
            for (int k = 0; k < nestHigh; k++)
                if (nestInfo[k] != null && nestInfo[k].Colony == c && !nestInfo[k].Founding) ownNests++;
            if (ownNests == 0) return false;

            for (int attempt = 0; attempt < 80; attempt++)
            {
                // Start from a random nest of ours and look outward.
                int pick = rng.NextInt(0, ownNests), from = -1;
                for (int k = 0; k < nestHigh; k++)
                    if (nestInfo[k] != null && nestInfo[k].Colony == c && !nestInfo[k].Founding && pick-- == 0) { from = k; break; }
                if (from < 0) continue;
                float dist = rng.NextFloat(swarm.MinSpacing * 1.15f, swarm.MaxDistance * 0.85f);
                float2 p = nests[from].Position + rng.NextFloat2Direction() * dist;
                if (ValidateNestSite(c, p) != null) continue;

                float score = SumAround(leaves, p, 14) * 0.15f + SumAround(food, p, 14) * 0.5f - dist * 0.4f;
                for (int k = 0; k < nestHigh; k++)
                {
                    var info = nestInfo[k];
                    if (info == null || info.Colony == c || !IsAtWar(c, info.Colony)) continue;
                    float d = math.distance(p, nests[k].Position);
                    if (d < 220f) score -= (220f - d) * 3f;
                }
                score += rng.NextFloat(0f, 40f);
                if (score > bestScore) { bestScore = score; site = p; }
            }
            return bestScore > float.MinValue;
        }

        // ------------------------------------------------------------------ moving house

        public const float MoveCost = 60f;
        public const float MoveMaxDistance = 180f;
        const float MoveTimeout = 420f;

        /// <summary>Next left click picks the new site for this nest (-1: not placing).</summary>
        public int PlacingMove { get; set; } = -1;

        /// <summary>The new site this colony is moving a nest to, or -1.</summary>
        public int MoveInProgress(int c)
        {
            for (int k = 0; k < nestHigh; k++)
                if (nestInfo[k] != null && nestInfo[k].Colony == c && nestInfo[k].MovingFrom >= 0) return k;
            return -1;
        }

        /// <summary>Why this nest can't move house right now, or null.</summary>
        public string MoveBlocker(int c, int k, bool free = false)
        {
            var info = GetNest(k);
            if (info == null || info.Colony != c) return "That isn't one of your nests.";
            if (info.Founding) return "That nest isn't settled yet.";
            if (!info.HasQueen) return "Only a nest with a queen can move: she leads the way.";
            if (MoveInProgress(c) >= 0) return "Your colony is already moving a nest.";
            if (!free && Colonies[c].Food < MoveCost) return $"Not enough food: moving costs {MoveCost:0}.";
            return null;
        }

        /// <summary>Checks a new site for nest k. Returns an error message, or null if it's fine.</summary>
        public string ValidateMoveSite(int c, int k, float2 p)
        {
            float margin = sim.NestRadius * 3f;
            if (p.x < margin || p.y < margin || p.x > grid.WorldSize.x - margin || p.y > grid.WorldSize.y - margin)
                return "Too close to the edge of the map.";
            if (!AreaClear(p, sim.NestRadius * 2.5f)) return "Ants can't nest on rock or water.";
            float from = math.distance(p, nests[k].Position);
            if (from < 15f) return "That's where the nest already is.";
            if (from > MoveMaxDistance) return $"Too far: a queen can walk at most {MoveMaxDistance:0}.";
            string treeErr = TreeSiteError(c, p);
            if (treeErr != null) return treeErr;
            for (int j = 0; j < nestHigh; j++)
            {
                var info = nestInfo[j];
                if (info == null || j == k) continue;
                float d = math.distance(p, nests[j].Position);
                if (info.Colony == c) { if (d < swarm.MinSpacing) return $"Too close to another of your nests (min {swarm.MinSpacing:0})."; }
                else if (d < swarm.EnemyClearance) return "Too close to another colony's nest.";
            }
            return null;
        }

        /// <summary>
        /// Moves a nest house: the queen walks to the new site (faster than her usual crawl) with the soldiers round
        /// her, the workers go ahead to set it up, and the old nest is given up when she gets there.
        /// Returns an error message, or null.
        /// </summary>
        public string MoveNest(int c, int k, float2 site, bool free = false)
        {
            string err = MoveBlocker(c, k, free) ?? ValidateMoveSite(c, k, site);
            if (err != null) return err;
            int qi = queenIndex[k];
            if (qi < 0 || qi >= ants.Length) return "That nest's queen can't be found.";
            int n = AllocNest(c, site, true);
            if (n < 0) return "The world is full of nests.";
            var col = Colonies[c];
            if (!free) col.Food -= MoveCost;

            var from = nestInfo[k];
            var to = nestInfo[n];
            to.MovingFrom = k;
            to.MoveTime = 0f;
            to.Number = from.Number; // it's the same nest, moving: keep its number
            col.NestSerial--;
            from.MovingTo = n;
            var nd = nests[n];
            nd.Moving = 1;
            nd.SquadCount = nests[k].SquadCount;
            nd.DistressResponse = nests[k].DistressResponse;
            nests[n] = nd;

            Ant q = ants[qi];
            q.Nest = (byte)n;
            ants[qi] = q;
            queenIndex[k] = -1;
            queenIndex[n] = qi;
            queenPos[n] = q.Position;
            int guards = col.Abstract ? 0 : nestSoldiers[k];
            if (!col.Abstract) ReassignNest(c, k, n, 100, false);
            if (c == Player)
                Notify($"Nest {from.Number} is moving house. The queen is walking to the new site" +
                       (guards > 0 ? $" with {guards} soldiers round her" : ", with no soldiers to guard her") + ": keep her safe!");
            return null;
        }

        /// <summary>Each tick: follow moving queens; settle the new nest when she arrives (or the move fails).</summary>
        void UpdateMoves(float dt)
        {
            for (int n = 0; n < nestHigh; n++)
            {
                var info = nestInfo[n];
                if (info == null || info.MovingFrom < 0) continue;
                info.MoveTime += dt;
                int qi = queenIndex[n];
                bool alive = qi >= 0 && qi < ants.Length && ants[qi].Dead == 0 && ants[qi].Caste == (byte)Caste.Queen && ants[qi].Nest == n;
                if (!alive) { FinishMove(n, false); continue; }
                Ant q = ants[qi];
                queenPos[n] = q.Position;
                bool there = math.distance(q.Position, nests[n].Position) < sim.NestRadius;
                if (!there && info.MoveTime > MoveTimeout)
                {
                    // Stuck somewhere: she finds her way in the end.
                    q.Position = nests[n].Position;
                    ants[qi] = q;
                    there = true;
                }
                if (there) FinishMove(n, true);
            }
        }

        void FinishMove(int n, bool queenArrived)
        {
            var info = nestInfo[n];
            int c = info.Colony;
            var col = Colonies[c];
            int k = info.MovingFrom;
            var old = GetNest(k);
            if (old != null && old.Colony == c && old.MovingTo == n)
            {
                // Everyone left behind (and anyone hatched there meanwhile) follows.
                if (col.Abstract) { info.SleepShare += old.SleepShare; old.SleepShare = 0f; }
                else ReassignNest(c, k, n, 100, true);
                bool primary = col.PrimaryNest == k;
                FreeNest(k);
                if (primary) col.PrimaryNest = n;
            }
            info.MovingFrom = -1;
            info.Founding = false;
            info.FoundedAt = SimTime;
            info.MoveRetry = 60f;
            var nd = nests[n];
            nd.Founding = 0;
            nd.Moving = 0;
            nests[n] = nd;
            if (c == Player)
                Notify(queenArrived ? $"Nest {info.Number} has moved into its new home."
                    : $"The queen of nest {info.Number} was killed on the way. Her ants settle at the new site without her.");
        }

        int SumAround(NativeArray<int> g, float2 p, int r)
        {
            int sum = 0;
            int cx = (int)(p.x * grid.InvCellSize), cy = (int)(p.y * grid.InvCellSize);
            for (int y = math.max(0, cy - r); y <= math.min(grid.Height - 1, cy + r); y++)
            for (int x = math.max(0, cx - r); x <= math.min(grid.Width - 1, cx + r); x++)
                sum += g[y * grid.Width + x];
            return sum;
        }
    }
}
