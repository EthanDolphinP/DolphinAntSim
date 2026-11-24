using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace AntSim
{
    /// <summary>What a map cell is made of. Rock and water block walking ants and bugs (flyers pass over).</summary>
    public static class TerrainKind
    {
        public const byte Open = 0;
        public const byte Rock = 1;
        public const byte Water = 2;
        public const byte Sand = 3;   // walkable but slow

        public static bool Blocks(byte t) => t == Rock || t == Water;
    }

    /// <summary>Map styles on the start screen.</summary>
    public enum MapStyle { Meadow = 0, Rocky = 1, Lakes = 2, Mixed = 3 }

    /// <summary>Fills the terrain from layered noise: rock outcrops, ponds and sandy patches.</summary>
    [BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
    public struct TerrainNoiseJob : IJobParallelFor
    {
        [WriteOnly] public NativeArray<byte> Terrain;
        public int Width;
        public float2 Offset;
        public float RockThreshold;  // higher = less rock (above 1 = none)
        public float WaterThreshold; // higher = less water
        public float SandThreshold;

        static float Fbm(float2 p)
        {
            float v = noise.snoise(p) + 0.5f * noise.snoise(p * 2.03f + 17.1f) + 0.25f * noise.snoise(p * 4.1f + 41.7f);
            return v / 1.75f;
        }

        public void Execute(int i)
        {
            float2 p = new float2(i % Width, i / Width) + Offset;
            float water = Fbm(p / 110f);
            if (water > WaterThreshold) { Terrain[i] = TerrainKind.Water; return; }
            // Rocks: ridged noise gives broken outcrops rather than round blobs.
            float rock = 1f - math.abs(noise.snoise(p / 38f + 300f));
            rock = rock * 0.7f + 0.3f * noise.snoise(p / 9f + 900f);
            if (rock > RockThreshold) { Terrain[i] = TerrainKind.Rock; return; }
            float sand = Fbm(p / 60f + 500f);
            Terrain[i] = sand > SandThreshold ? TerrainKind.Sand : TerrainKind.Open;
        }
    }

    /// <summary>Open ground right next to water becomes sand (a beach), so shores read clearly and slow ants a little.</summary>
    [BurstCompile]
    public struct ShoreJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<byte> Source;
        [WriteOnly] public NativeArray<byte> Result;
        public int Width, Height;

        public void Execute(int i)
        {
            byte t = Source[i];
            Result[i] = t;
            if (t != TerrainKind.Open) return;
            int x = i % Width, y = i / Width;
            for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= Width || ny >= Height) continue;
                if (Source[ny * Width + nx] == TerrainKind.Water) { Result[i] = TerrainKind.Sand; return; }
            }
        }
    }

    /// <summary>Marks every walkable cell reachable from a start cell (4-connected flood fill).</summary>
    [BurstCompile]
    public struct FloodFillJob : IJob
    {
        [ReadOnly] public NativeArray<byte> Terrain;
        public NativeArray<byte> Reached;
        public int Width, Height, Start;

        public void Execute()
        {
            for (int i = 0; i < Reached.Length; i++) Reached[i] = 0;
            if (TerrainKind.Blocks(Terrain[Start])) return;
            var stack = new NativeList<int>(65536, Allocator.Temp);
            stack.Add(Start);
            Reached[Start] = 1;
            while (stack.Length > 0)
            {
                int c = stack[stack.Length - 1];
                stack.RemoveAt(stack.Length - 1);
                int x = c % Width, y = c / Width;
                if (x > 0) Visit(c - 1, ref stack);
                if (x < Width - 1) Visit(c + 1, ref stack);
                if (y > 0) Visit(c - Width, ref stack);
                if (y < Height - 1) Visit(c + Width, ref stack);
            }
            stack.Dispose();
        }

        void Visit(int c, ref NativeList<int> stack)
        {
            if (Reached[c] != 0 || TerrainKind.Blocks(Terrain[c])) return;
            Reached[c] = 1;
            stack.Add(c);
        }
    }

    /// <summary>
    /// "Way home" for one nest: walking distance (x10, 8-way, no corner cutting) from every coarse cell to the nest,
    /// going round rock and water. Coarse cells are Scale x Scale map cells; one is walkable if at least half of it is.
    /// Ants use it to head for fords and gaps instead of walking straight at a nest across a river.
    /// </summary>
    [BurstCompile]
    public struct HomeFieldJob : IJob
    {
        [ReadOnly] public NativeArray<byte> Terrain;
        [NativeDisableContainerSafetyRestriction] public NativeArray<ushort> Field; // all nests; this one at Offset
        public int Offset, Width, Height, Scale, CW, CH;
        public int2 Start; // coarse cell of the nest

        public const ushort Unreachable = ushort.MaxValue;

        bool Walkable(int cx, int cy)
        {
            int open = 0;
            for (int y = cy * Scale; y < math.min(Height, (cy + 1) * Scale); y++)
            for (int x = cx * Scale; x < math.min(Width, (cx + 1) * Scale); x++)
                if (!TerrainKind.Blocks(Terrain[y * Width + x])) open++;
            return open * 2 >= Scale * Scale; // half open keeps narrow fords and dug paths connected
        }

        public void Execute()
        {
            int n = CW * CH;
            var open = new NativeArray<byte>(n, Allocator.Temp);
            for (int cy = 0; cy < CH; cy++)
            for (int cx = 0; cx < CW; cx++)
                open[cy * CW + cx] = (byte)(Walkable(cx, cy) ? 1 : 0);
            for (int i = 0; i < n; i++) Field[Offset + i] = Unreachable;
            int s = math.clamp(Start.y, 0, CH - 1) * CW + math.clamp(Start.x, 0, CW - 1);
            open[s] = 1;

            // Dijkstra with a binary heap of (cost, cell).
            var heap = new NativeList<int2>(4096, Allocator.Temp);
            Field[Offset + s] = 0;
            Push(ref heap, new int2(0, s));
            while (heap.Length > 0)
            {
                int2 top = Pop(ref heap);
                int c = top.y;
                if (top.x > Field[Offset + c]) continue;
                int x = c % CW, y = c / CW;
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= CW || ny >= CH) continue;
                    int nc = ny * CW + nx;
                    if (open[nc] == 0) continue;
                    bool diag = dx != 0 && dy != 0;
                    if (diag && (open[y * CW + nx] == 0 || open[ny * CW + x] == 0)) continue; // no corner cutting
                    int cost = top.x + (diag ? 14 : 10);
                    if (cost >= Unreachable || cost >= Field[Offset + nc]) continue;
                    Field[Offset + nc] = (ushort)cost;
                    Push(ref heap, new int2(cost, nc));
                }
            }
            heap.Dispose();
            open.Dispose();
        }

        static void Push(ref NativeList<int2> h, int2 v)
        {
            h.Add(v);
            int i = h.Length - 1;
            while (i > 0)
            {
                int p = (i - 1) >> 1;
                if (h[p].x <= h[i].x) break;
                (h[p], h[i]) = (h[i], h[p]);
                i = p;
            }
        }

        static int2 Pop(ref NativeList<int2> h)
        {
            int2 top = h[0];
            h[0] = h[h.Length - 1];
            h.RemoveAt(h.Length - 1);
            int i = 0;
            while (true)
            {
                int l = i * 2 + 1, r = l + 1, m = i;
                if (l < h.Length && h[l].x < h[m].x) m = l;
                if (r < h.Length && h[r].x < h[m].x) m = r;
                if (m == i) break;
                (h[m], h[i]) = (h[i], h[m]);
                i = m;
            }
            return top;
        }
    }

    /// <summary>Walkable pockets nobody can reach become rock, so no food or bugs end up stranded there.</summary>
    [BurstCompile]
    public struct FillPocketsJob : IJobParallelFor
    {
        public NativeArray<byte> Terrain;
        [ReadOnly] public NativeArray<byte> Reached;

        public void Execute(int i)
        {
            if (Reached[i] == 0 && !TerrainKind.Blocks(Terrain[i])) Terrain[i] = TerrainKind.Rock;
        }
    }
}
