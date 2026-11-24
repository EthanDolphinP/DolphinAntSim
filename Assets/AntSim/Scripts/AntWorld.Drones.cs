using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace AntSim
{
    /// <summary>
    /// Drones: a rare kind of soldier that escorts the workers. Every couple of seconds each nest gets a short
    /// list of places to guard: the food sources its workers are busiest harvesting, and the colony's aphid
    /// farms near it. Each drone squad takes one of them.
    /// </summary>
    public partial class AntWorld
    {
        const float SiteCell = 8f;          // coarse cells used to find harvest sites
        const float SiteSpacing = 20f;      // two food sites at least this far apart
        const float SiteMinScore = 0.04f;
        const int MaxFarmSites = 3;         // per nest; the rest of the slots go to food sources

        NativeArray<float4> nestSites;      // [nest * NestData.MaxSites + i] = (x, y, kind 0 food / 1 farm, score)
        NativeArray<float> harvestScore;    // [colony * siteW * siteH + cell]
        int siteW, siteH;
        float siteTimer;
        readonly List<float3> siteCandidates = new List<float3>();   // (score, x, y)
        readonly List<float2> colonySites = new List<float2>();
        readonly List<float4> nestSiteScratch = new List<float4>();

        /// <summary>Drone posts of a nest, for the HUD: (x, y, kind 0 food / 1 farm, score).</summary>
        public int GetGuardSites(int nest, List<float4> into)
        {
            into.Clear();
            if (nest < 0 || nest >= MaxNests || nestInfo[nest] == null) return 0;
            for (int i = 0; i < nests[nest].SiteCount; i++) into.Add(nestSites[nest * NestData.MaxSites + i]);
            return into.Count;
        }

        void AllocateDroneArrays()
        {
            siteW = (int)math.ceil(grid.WorldSize.x / SiteCell);
            siteH = (int)math.ceil(grid.WorldSize.y / SiteCell);
            nestSites = new NativeArray<float4>(MaxNests * NestData.MaxSites, Allocator.Persistent);
            harvestScore = new NativeArray<float>(colonyCount * siteW * siteH, Allocator.Persistent);
        }

        void DisposeDroneArrays()
        {
            if (nestSites.IsCreated) nestSites.Dispose();
            if (harvestScore.IsCreated) harvestScore.Dispose();
        }

        static byte Resquad(byte have, int members, int size, int max)
        {
            int want = math.clamp((int)math.round(members / (float)math.max(1, size)), 1, max);
            int h = math.max(1, (int)have);
            return math.abs(want - h) >= math.max(1, h / 4) ? (byte)want : (byte)h;
        }

        void UpdateGuardSites(float dt)
        {
            siteTimer += dt;
            if (siteTimer < 2f) return;
            siteTimer = 0f;

            bool anyDrones = false;
            for (int k = 0; k < nestHigh && !anyDrones; k++) anyDrones = nestInfo[k] != null && nestDrones[k] > 0;
            if (!anyDrones)
            {
                for (int c = 0; c < colonyCount; c++) { Colonies[c].GuardedTrails = 0; Colonies[c].GuardedFarms = 0; }
                for (int k = 0; k < nestHigh; k++) if (nestInfo[k] != null) SetSiteCount(k, 0);
                return;
            }

            new HarvestSitesJob
            {
                Food = food, Busy = busyTrails, Score = harvestScore, Grid = grid,
                BusyW = busyW, BusyH = busyH, W = siteW, H = siteH, Colonies = colonyCount, Cell = SiteCell,
            }.Schedule(siteW * siteH, 256).Complete();

            for (int c = 0; c < colonyCount; c++) { Colonies[c].GuardedTrails = 0; Colonies[c].GuardedFarms = 0; }
            int per = siteW * siteH;
            for (int c = 0; c < colonyCount; c++)
            {
                var col = Colonies[c];
                bool any = false;
                for (int k = 0; k < nestHigh; k++)
                    if (nestInfo[k] != null && nestInfo[k].Colony == c && nestDrones[k] > 0) { any = true; break; }
                if (col.Abstract || !any)
                {
                    for (int k = 0; k < nestHigh; k++)
                        if (nestInfo[k] != null && nestInfo[k].Colony == c) SetSiteCount(k, 0);
                    continue;
                }

                // The colony's best harvest sites, spread out and not right on top of a nest.
                siteCandidates.Clear();
                for (int cell = 0; cell < per; cell++)
                {
                    float s = harvestScore[c * per + cell];
                    if (s < SiteMinScore) continue;
                    siteCandidates.Add(new float3(s, ((cell % siteW) + 0.5f) * SiteCell, ((cell / siteW) + 0.5f) * SiteCell));
                }
                siteCandidates.Sort((x, y) => y.x.CompareTo(x.x));
                colonySites.Clear();
                float keepOut = sim.NestRadius * 5f;
                foreach (var cand in siteCandidates)
                {
                    if (colonySites.Count >= 24) break;
                    float2 p = cand.yz;
                    bool ok = true;
                    foreach (var q in colonySites)
                        if (math.distancesq(p, q) < SiteSpacing * SiteSpacing) { ok = false; break; }
                    if (!ok) continue;
                    // Honeydew around a farm is the farm's business, not a separate food source.
                    foreach (var fm in farms)
                        if (fm.Colony == c && math.distancesq((float2)fm.Position, p) < 18f * 18f) { ok = false; break; }
                    if (!ok) continue;
                    int nn = NearestNest(c, p, false);
                    if (nn < 0 || math.distancesq(nests[nn].Position, p) < keepOut * keepOut) continue;
                    colonySites.Add(p);
                }

                for (int k = 0; k < nestHigh; k++)
                {
                    var info = nestInfo[k];
                    if (info == null || info.Colony != c) continue;
                    if (info.Founding) { SetSiteCount(k, 0); continue; }
                    float2 home = nests[k].Position;
                    nestSiteScratch.Clear();

                    // Farms that this nest is the nearest to.
                    foreach (var f in farms)
                    {
                        if (f.Colony != c) continue;
                        float2 fp = f.Position;
                        if (NearestNest(c, fp, false) != k) continue;
                        nestSiteScratch.Add(new float4(fp, 1f, -math.distance(home, fp)));
                    }
                    nestSiteScratch.Sort((x, y) => y.w.CompareTo(x.w));
                    if (nestSiteScratch.Count > MaxFarmSites) nestSiteScratch.RemoveRange(MaxFarmSites, nestSiteScratch.Count - MaxFarmSites);
                    int farmsHere = nestSiteScratch.Count;

                    // Food sources: the busiest ones (in colony order) this nest is closest to and within range.
                    for (int i = 0; i < colonySites.Count && nestSiteScratch.Count < NestData.MaxSites; i++)
                    {
                        float2 p = colonySites[i];
                        if (math.distance(home, p) > sim.DroneRange) continue;
                        if (NearestNest(c, p, false) != k) continue;
                        nestSiteScratch.Add(new float4(p, 0f, colonySites.Count - i));
                    }
                    col.GuardedFarms += farmsHere;
                    col.GuardedTrails += nestSiteScratch.Count - farmsHere;
                    StoreSitesStable(k);
                }
            }
        }

        /// <summary>
        /// Writes the new site list for a nest, keeping each site that's still there (within a few cells) in the
        /// same slot, so drone squads keep their post rather than all swapping every update.
        /// </summary>
        void StoreSitesStable(int k)
        {
            int baseI = k * NestData.MaxSites;
            int oldCount = nests[k].SiteCount;
            var slots = new float4[NestData.MaxSites];
            var used = new bool[nestSiteScratch.Count];
            int count = 0;
            for (int j = 0; j < oldCount; j++)
            {
                float4 old = nestSites[baseI + j];
                int match = -1;
                float best = 16f * 16f;
                for (int i = 0; i < nestSiteScratch.Count; i++)
                {
                    if (used[i] || nestSiteScratch[i].z != old.z) continue;
                    float d = math.distancesq(nestSiteScratch[i].xy, old.xy);
                    if (d < best) { best = d; match = i; }
                }
                if (match >= 0) { used[match] = true; slots[j] = nestSiteScratch[match]; }
                else slots[j] = new float4(0, 0, -1f, 0); // empty: filled below
            }
            count = oldCount;
            // Fill gaps, then append.
            for (int i = 0; i < nestSiteScratch.Count; i++)
            {
                if (used[i]) continue;
                int gap = -1;
                for (int j = 0; j < count; j++) if (slots[j].z < 0f) { gap = j; break; }
                if (gap >= 0) slots[gap] = nestSiteScratch[i];
                else if (count < NestData.MaxSites) slots[count++] = nestSiteScratch[i];
                used[i] = true;
            }
            // Drop any gaps left over by moving the last sites into them.
            for (int j = 0; j < count; j++)
                while (j < count && slots[j].z < 0f) slots[j] = slots[--count];
            for (int j = 0; j < count; j++) nestSites[baseI + j] = slots[j];
            SetSiteCount(k, count);
        }

        void SetSiteCount(int k, int count)
        {
            var n = nests[k];
            n.SiteCount = (byte)count;
            nests[k] = n;
        }
    }
}
