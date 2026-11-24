using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace AntSim
{
    /// <summary>
    /// Terrain and map generation: rock outcrops and water block ants and bugs; sand slows them.
    /// The generator makes clearings around the starting nests, can run a river with fords across the map,
    /// and makes sure every nest can walk to every other one. Unreachable pockets are filled in.
    /// </summary>
    public partial class AntWorld
    {
        public static readonly string[] MapStyleNames = { "Meadow", "Rocky", "Lakes", "Mixed" };

        NativeArray<byte> terrain;

        /// <summary>The terrain kind at a world position (rock and water outside the map).</summary>
        public byte TerrainAt(float2 p) => grid.Contains(p) ? terrain[grid.CellIndex(p)] : TerrainKind.Rock;
        public bool IsBlocked(float2 p) => TerrainKind.Blocks(TerrainAt(p));
        /// <summary>Share of the map covered by rock, water and sand (for tests and the HUD).</summary>
        public float3 TerrainCover { get; private set; }

        /// <summary>True if every cell within a radius of p is walkable.</summary>
        public bool AreaClear(float2 p, float radius)
        {
            int r = (int)math.ceil(radius);
            int cx = (int)p.x, cy = (int)p.y;
            for (int y = cy - r; y <= cy + r; y += 2)
            for (int x = cx - r; x <= cx + r; x += 2)
            {
                if ((x - cx) * (x - cx) + (y - cy) * (y - cy) > r * r) continue;
                if (x < 0 || y < 0 || x >= grid.Width || y >= grid.Height) return false;
                if (TerrainKind.Blocks(terrain[y * grid.Width + x])) return false;
            }
            return true;
        }

        void GenerateTerrain(List<float2> starts)
        {
            terrain = new NativeArray<byte>(grid.CellCount, Allocator.Persistent);
            var style = (MapStyle)math.clamp(setup.MapStyle, 0, 3);
            float rockT, waterT, sandT = 0.5f;
            bool river;
            switch (style)
            {
                case MapStyle.Meadow: rockT = 0.82f; waterT = 2f; river = false; break;
                case MapStyle.Rocky: rockT = 0.64f; waterT = 2f; river = false; break;
                case MapStyle.Lakes: rockT = 0.84f; waterT = 0.3f; river = true; break;
                default: rockT = 0.73f; waterT = 0.45f; river = rng.NextFloat() < 0.6f; break;
            }

            new TerrainNoiseJob
            {
                Terrain = terrain, Width = grid.Width, Offset = rng.NextFloat2(new float2(-5000f), new float2(5000f)),
                RockThreshold = rockT, WaterThreshold = waterT, SandThreshold = sandT,
            }.Schedule(grid.CellCount, 4096).Complete();

            if (river) CarveRiver();

            // Shores become sand.
            var tmp = new NativeArray<byte>(grid.CellCount, Allocator.TempJob);
            new ShoreJob { Source = terrain, Result = tmp, Width = grid.Width, Height = grid.Height }
                .Schedule(grid.CellCount, 4096).Complete();
            tmp.CopyTo(terrain);
            tmp.Dispose();

            // A clear border so nothing gets pinned against the map edge, and clearings around the nests.
            for (int x = 0; x < grid.Width; x++)
            for (int b = 0; b < 3; b++)
            {
                SetOpen(x, b); SetOpen(x, grid.Height - 1 - b);
            }
            for (int y = 0; y < grid.Height; y++)
            for (int b = 0; b < 3; b++)
            {
                SetOpen(b, y); SetOpen(grid.Width - 1 - b, y);
            }
            foreach (var s in starts) Paint(s, 26f, TerrainKind.Open, true);

            EnsureConnected(starts);

            int rock = 0, water = 0, sand = 0;
            for (int i = 0; i < terrain.Length; i++)
            {
                byte t = terrain[i];
                if (t == TerrainKind.Rock) rock++;
                else if (t == TerrainKind.Water) water++;
                else if (t == TerrainKind.Sand) sand++;
            }
            TerrainCover = new float3(rock, water, sand) / terrain.Length;
        }

        void SetOpen(int x, int y)
        {
            int i = y * grid.Width + x;
            if (TerrainKind.Blocks(terrain[i])) terrain[i] = TerrainKind.Open;
        }

        /// <summary>Paints a disc. With onlyBlocked, only rock and water are replaced (sand and grass stay).</summary>
        void Paint(float2 c, float radius, byte kind, bool onlyBlocked)
        {
            int r = (int)math.ceil(radius);
            int cx = (int)c.x, cy = (int)c.y;
            for (int y = cy - r; y <= cy + r; y++)
            for (int x = cx - r; x <= cx + r; x++)
            {
                if (x < 0 || y < 0 || x >= grid.Width || y >= grid.Height) continue;
                if ((x - cx) * (x - cx) + (y - cy) * (y - cy) > radius * radius) continue;
                int i = y * grid.Width + x;
                if (onlyBlocked && !TerrainKind.Blocks(terrain[i])) continue;
                terrain[i] = kind;
            }
        }

        /// <summary>A meandering river from one side of the map to the other, with shallow sandy fords.</summary>
        void CarveRiver()
        {
            bool horizontal = rng.NextFloat() < 0.5f;
            float2 size = grid.WorldSize;
            float2 p = horizontal ? new float2(0f, rng.NextFloat(0.3f, 0.7f) * size.y)
                                  : new float2(rng.NextFloat(0.3f, 0.7f) * size.x, 0f);
            float baseAngle = horizontal ? 0f : math.PI * 0.5f;
            float angle = baseAngle, turn = 0f;
            float width = rng.NextFloat(4f, 7f);
            float nextFord = rng.NextFloat(60f, 120f), travelled = 0f;
            while (p.x >= -10f && p.y >= -10f && p.x <= size.x + 10f && p.y <= size.y + 10f)
            {
                turn = math.clamp(turn + rng.NextFloat(-0.08f, 0.08f), -0.05f, 0.05f);
                angle += turn;
                // Keep heading across the map.
                angle = baseAngle + math.clamp(angle - baseAngle, -0.9f, 0.9f);
                p += new float2(math.cos(angle), math.sin(angle)) * 2f;
                travelled += 2f;
                bool ford = travelled > nextFord && travelled < nextFord + 10f;
                if (travelled >= nextFord + 10f) nextFord = travelled + rng.NextFloat(110f, 190f);
                Paint(p, width, ford ? TerrainKind.Sand : TerrainKind.Water, false);
            }
        }

        /// <summary>
        /// Every nest must reach every other: if one can't, dig a sandy path (a ford over water) between them.
        /// Afterwards, walkable pockets that can't be reached become rock.
        /// </summary>
        void EnsureConnected(List<float2> starts)
        {
            var reached = new NativeArray<byte>(grid.CellCount, Allocator.TempJob);
            int start = grid.CellIndex(starts[0]);
            for (int pass = 0; pass < starts.Count + 1; pass++)
            {
                new FloodFillJob { Terrain = terrain, Reached = reached, Width = grid.Width, Height = grid.Height, Start = start }.Run();
                int cut = -1;
                for (int k = 1; k < starts.Count; k++)
                    if (reached[grid.CellIndex(starts[k])] == 0) { cut = k; break; }
                if (cut < 0) break;
                // Dig from the stranded nest toward the nearest reached cell along the way to nest 0.
                float2 a = starts[cut], b = starts[0];
                float len = math.distance(a, b);
                for (float t = 0f; t <= len; t += 1.5f)
                {
                    float2 q = math.lerp(a, b, t / len);
                    Paint(q, 2.5f, TerrainKind.Sand, true);
                }
            }
            new FillPocketsJob { Terrain = terrain, Reached = reached }.Schedule(grid.CellCount, 4096).Complete();
            reached.Dispose();
        }

        /// <summary>A random walkable point at least margin from the edge (or the fallback if none is found).</summary>
        float2 RandomOpenPoint(float margin, float2 fallback)
        {
            for (int attempt = 0; attempt < 30; attempt++)
            {
                float2 p = rng.NextFloat2(new float2(margin), grid.WorldSize - margin);
                if (!IsBlocked(p)) return p;
            }
            return fallback;
        }

        // ------------------------------------------------------------------ way home

        /// <summary>Map cells per side of a "way home" field cell.</summary>
        const int FieldScale = 4;
        NativeArray<ushort> homeField;  // [nest * fieldW * fieldH + coarse cell]
        readonly float2[] homeFieldFor = new float2[MaxNests];
        // Fields MaxNests.. 2*MaxNests-1: the way to each nest's raid or hunt target, so squads go round lakes to it.
        readonly float2[] raidFieldFor = new float2[MaxNests];
        readonly float[] raidFieldTime = new float[MaxNests];
        int fieldW, fieldH;

        /// <summary>
        /// Every active nest has a field of walking distances home around rock and water. Fields are made when a
        /// nest first appears (or after loading) and kept until the slot holds a nest somewhere else.
        /// </summary>
        void EnsureHomeFields()
        {
            if (!homeField.IsCreated)
            {
                fieldW = (grid.Width + FieldScale - 1) / FieldScale;
                fieldH = (grid.Height + FieldScale - 1) / FieldScale;
                homeField = new NativeArray<ushort>(2 * MaxNests * fieldW * fieldH, Allocator.Persistent);
                for (int k = 0; k < MaxNests; k++) { homeFieldFor[k] = new float2(-1f); raidFieldFor[k] = new float2(-1000f); }
            }
            for (int k = 0; k < nestHigh; k++)
            {
                var n = nests[k];
                if (n.Active == 0 || n.RaidActive == 0) continue;
                // Recompute when the target has moved (hunted bugs walk), at most once a second.
                if (math.distance(raidFieldFor[k], n.RaidTarget) < 12f || SimTime - raidFieldTime[k] < 1f) continue;
                new HomeFieldJob
                {
                    Terrain = terrain, Field = homeField, Offset = (MaxNests + k) * fieldW * fieldH,
                    Width = grid.Width, Height = grid.Height, Scale = FieldScale, CW = fieldW, CH = fieldH,
                    Start = new int2((int)(n.RaidTarget.x / FieldScale), (int)(n.RaidTarget.y / FieldScale)),
                }.Run();
                raidFieldFor[k] = n.RaidTarget;
                raidFieldTime[k] = SimTime;
            }
            for (int k = 0; k < nestHigh; k++)
            {
                var n = nests[k];
                if (n.Active == 0 || math.all(homeFieldFor[k] == n.Position)) continue;
                new HomeFieldJob
                {
                    Terrain = terrain, Field = homeField, Offset = k * fieldW * fieldH,
                    Width = grid.Width, Height = grid.Height, Scale = FieldScale, CW = fieldW, CH = fieldH,
                    Start = new int2((int)(n.Position.x / FieldScale), (int)(n.Position.y / FieldScale)),
                }.Run();
                homeFieldFor[k] = n.Position;
            }
        }

        /// <summary>Walking distance home from p (world units), or -1 if there's no way (for tests).</summary>
        public float HomeDistance(int nest, float2 p)
        {
            EnsureHomeFields();
            int cx = math.clamp((int)(p.x / FieldScale), 0, fieldW - 1), cy = math.clamp((int)(p.y / FieldScale), 0, fieldH - 1);
            ushort d = homeField[nest * fieldW * fieldH + cy * fieldW + cx];
            return d == HomeFieldJob.Unreachable ? -1f : d * 0.1f * FieldScale;
        }

        void DisposeTerrain()
        {
            if (terrain.IsCreated) terrain.Dispose();
            if (homeField.IsCreated) homeField.Dispose();
        }
    }
}
