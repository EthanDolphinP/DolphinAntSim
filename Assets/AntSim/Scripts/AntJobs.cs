using System.Threading;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Random = Unity.Mathematics.Random;

namespace AntSim
{
    /// <summary>
    /// The per-ant brain. Runs in parallel across all cores, once per simulation tick.
    /// Ants only read/write the shared grids; there is no per-ant pathfinding.
    /// Wildlife (mites, beetles, spiders) lives in the same list under colony slot WildColony.
    /// </summary>
    [BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
    public unsafe struct AntStepJob : IJobParallelFor
    {
        public NativeArray<Ant> Ants;

        // Pheromone layout: [(colony * ChannelCount + channel) * CellCount + cell]. Wildlife has no layer.
        // Many ants write here concurrently. Writes are "max(old, new)" so races only lose a little strength.
        [NativeDisableParallelForRestriction] public NativeArray<float> Pheromones;

        // Food and leaves per cell, decremented atomically so nothing is ever duplicated.
        [NativeDisableUnsafePtrRestriction] public int* Food;
        [NativeDisableUnsafePtrRestriction] public int* Leaves;
        // Delivered per colony this tick, incremented atomically.
        [NativeDisableUnsafePtrRestriction] public int* Delivered;
        [NativeDisableUnsafePtrRestriction] public int* DeliveredLeaves;

        [ReadOnly] public NativeArray<NestData> Nests;
        [ReadOnly] public NativeArray<byte> Terrain; // per cell, see TerrainKind
        [ReadOnly] public NativeArray<ushort> HomeField; // per nest: walking distance home on a coarse grid
        public int FieldW, FieldH;
        public int RaidFieldBase; // field slot of nest k's raid target = RaidFieldBase + k
        public float FieldInvScale;
        [ReadOnly] public NativeArray<float> StarveRates;
        [ReadOnly] public NativeArray<CombatResult> Combat;
        [ReadOnly] public NativeArray<int> Damage; // fixed point x100, from CombatJob
        [ReadOnly] public NativeArray<SpeciesStats> Stats; // per colony slot
        [ReadOnly] public NativeArray<MorphStats> Morphs;  // [colony * MaxMorphs + morph]
        [NativeDisableUnsafePtrRestriction] public int* CombatDeaths; // per colony
        [NativeDisableUnsafePtrRestriction] public int* CoreCombatDeaths; // per colony, deaths near the nest

        // Distress ("death smell"): a coarse per-colony grid. Ants killed in battle add to it;
        // the main thread turns it into a few hotspots that soldiers can smell from far away.
        [NativeDisableUnsafePtrRestriction] public int* Distress;
        public int DistressW, DistressH, DistressPerColony;
        public float DistressInvCell;
        [ReadOnly] public NativeArray<float4> Hotspots; // [colony * HotspotsPerColony + k] = (x, y, strength, 0)
        // Squads: last tick's position sums per [nest * MaxSquads + squad] = (sum x, sum y, members, 0).
        [ReadOnly] public NativeArray<float4> SquadSums;
        // Drone posts: [nest * NestData.MaxSites + i] = (x, y, kind 0 food source / 1 farm, score).
        [ReadOnly] public NativeArray<float4> Sites;
        public float Time;
        [ReadOnly] public NativeArray<byte> Hostile; // [colony * MaxColonies + other]
        public int MaxColonies;
        // Busy trails: per colony, a half-resolution map of its food-trail strength (0..255), refreshed twice a second.
        [ReadOnly] public NativeArray<byte> Busy;
        public int BusyW, BusyH;

        public GridInfo Grid;
        public SimParams P;
        public CombatParams C;
        public WildlifeParams W;
        public int WildColony;
        public float PrincessSpeed;
        public float MaleSpeed;
        public float Accel; // 1 - exp(-Acceleration * Dt)
        public float Dt;
        public float TrailDecay;

        public const int HotspotsPerColony = 4;

        public void Execute(int i)
        {
            Ant a = Ants[i];
            if (a.Dead != 0) return;

            var rng = new Random(a.Rng);
            bool wild = a.Colony == WildColony;

            // --- Combat results ---
            // A woodlouse rolled up in its armour (it rolls up as soon as it's attacked) shrugs off most bites.
            bool rolled = wild && a.Caste == (byte)WildKind.Woodlouse && Combat[i].Engaged != 0;
            a.Health -= Damage[i] * (rolled ? 0.004f : 0.01f);
            if (a.Health <= 0f)
            {
                a.Dead = 1;
                // The body becomes food for whoever drags it home.
                int corpse = wild ? W.Food(a.Caste)
                    : a.Caste == (byte)Caste.Worker ? C.WorkerCorpseFood : C.SoldierCorpseFood;
                Interlocked.Add(ref Food[Grid.CellIndex(a.Position)], corpse);
                if (!wild)
                {
                    Interlocked.Increment(ref CombatDeaths[a.Colony]);
                    NestData home = Nests[a.Nest];
                    if (math.distancesq(a.Position, home.Position) < home.CoreRadius * home.CoreRadius)
                        Interlocked.Increment(ref CoreCombatDeaths[a.Colony]);
                    // Death smell: soldiers far away will come to investigate.
                    int dx = math.clamp((int)(a.Position.x * DistressInvCell), 0, DistressW - 1);
                    int dy = math.clamp((int)(a.Position.y * DistressInvCell), 0, DistressH - 1);
                    Interlocked.Add(ref Distress[a.Colony * DistressPerColony + dy * DistressW + dx], 100);
                }
                Ants[i] = a;
                return;
            }
            CombatResult cr = Combat[i];
            a.Cooldown = cr.Attacked != 0 ? C.AttackInterval : math.max(0f, a.Cooldown - Dt);

            a.Age += Dt;
            // Queens don't age out and are fed last, so only combat kills them.
            bool queen = !wild && a.Caste == (byte)Caste.Queen;
            bool royal = queen || (!wild && a.Caste == (byte)Caste.Princess);
            if (!royal && (a.Age > a.Lifespan || (!wild && rng.NextFloat() < StarveRates[a.Colony] * Dt)))
            {
                a.Dead = 1;
                Ants[i] = a;
                return;
            }

            float speed;
            bool detour = false; // heading home round an obstacle, guided by the nest's way-home field
            float2 navGoal = default; // soldiers: where they are walking to (for edge-following round obstacles)
            bool hasNav = false;
            if (wild)
            {
                speed = StepWildlife(ref a, ref rng, cr);
            }
            else
            {
                NestData nest = Nests[a.Nest];
                float2 toNest = nest.Position - a.Position;
                float nestDistSq = math.lengthsq(toNest);
                // Heading home round rock and water: toNest points along the way home, not through the river.
                float2 straightHome = toNest;
                toNest = WayHome(a.Position, a.Nest, toNest, nestDistSq);
                detour = math.any(toNest != straightHome);
                float nestAngle = math.atan2(toNest.y, toNest.x);
                bool soldier = a.Caste == (byte)Caste.Soldier;
                bool princess = a.Caste == (byte)Caste.Princess;
                bool male = a.Caste == (byte)Caste.Male;
                speed = (queen ? P.QueenSpeed : soldier ? P.SoldierSpeed : princess ? PrincessSpeed
                    : male ? MaleSpeed : P.WorkerSpeed) * Stats[a.Colony].SpeedMul
                    * Morphs[a.Colony * MorphStats.MaxMorphs + a.Morph].SpeedMul;

                // Workers run from predators away from home (and always from a centipede).
                bool fleeSpider = !soldier && !queen && !princess && cr.Engaged != 0 && cr.TargetKind > 0 &&
                                  WildKinds.IsPredator(cr.TargetKind - 1) &&
                                  (nestDistSq > nest.Radius * nest.Radius * 9f || cr.TargetKind == 1 + (int)WildKind.Centipede);
                if (male)
                {
                    // Winged males swirl around the swarm's flight path, then drop out and die.
                    if (nestDistSq > 400f) a.Angle = TurnTowards(a.Angle, nestAngle, P.TurnSpeed * 0.5f * Dt);
                    a.Angle += rng.NextFloat(-1f, 1f) * P.Wander * 3f * Dt;
                }
                else if (princess)
                {
                    // Fly straight to the new nest site and wait there to be crowned. She ignores fights.
                    if (nestDistSq > nest.Radius * nest.Radius * 0.25f)
                        a.Angle = TurnTowards(a.Angle, nestAngle, P.TurnSpeed * 2f * Dt);
                    else speed *= 0.15f;
                    a.Angle += rng.NextFloat(-1f, 1f) * P.Wander * 0.3f * Dt;
                }
                else if (fleeSpider)
                {
                    // Workers don't take on predators away from home: run, and shout for the soldiers.
                    a.Angle = TurnTowards(a.Angle, cr.TargetAngle + math.PI, P.TurnSpeed * 2f * Dt);
                    Deposit(a.Colony, Pheromone.Alarm, Grid.CellIndex(a.Position), 1f);
                }
                else if (cr.Engaged != 0)
                {
                    // Fight: face the enemy, charge, and shout for help.
                    a.Angle = TurnTowards(a.Angle, cr.TargetAngle, P.TurnSpeed * 2f * Dt);
                    if (cr.InRange != 0) speed *= C.EngagedSpeed;
                    Deposit(a.Colony, Pheromone.Alarm, Grid.CellIndex(a.Position), 1f);
                    if (!soldier) a.Trail *= TrailDecay;
                }
                else if (queen)
                {
                    // Stay in the heart of the nest.
                    if (nestDistSq > nest.Radius * nest.Radius * 0.25f)
                        a.Angle = TurnTowards(a.Angle, math.atan2(toNest.y, toNest.x), P.TurnSpeed * Dt);
                    a.Angle += rng.NextFloat(-1f, 1f) * P.Wander * Dt;
                }
                else if (soldier)
                {
                    StepSoldier(ref a, ref rng, toNest, nestDistSq, nest, ref speed, ref navGoal, ref hasNav);
                }
                else
                {
                    StepWorker(ref a, ref rng, toNest, nestDistSq, nest, ref speed, detour);
                }
            }

            // Sand slows walkers; princesses and winged males fly over everything.
            int hereCell = Grid.CellIndex(a.Position);
            bool flyer = !wild && (a.Caste == (byte)Caste.Princess || a.Caste == (byte)Caste.Male);
            if (!flyer && Terrain[hereCell] == TerrainKind.Sand && !(wild && a.Caste == (byte)WildKind.Antlion)) speed *= P.SandSpeed;
            // Stuck in an enemy's resin trap.
            if (cr.Slowed != 0) speed *= 0.3f;

            // Ease toward the wanted speed so ants don't jerk between walking, waiting and charging.
            a.Speed += (speed - a.Speed) * Accel;

            // Move, bouncing off the world edges.
            math.sincos(a.Angle, out float s, out float c);
            float2 np = a.Position + new float2(c, s) * (a.Speed * Dt);
            if (a.Detour != 0) a.Detour = (byte)((a.Detour & 0x7F) > 1 ? a.Detour - 1 : 0);
            if (!flyer && !TerrainKind.Blocks(Terrain[hereCell]))
            {
                // Look a little further ahead than one step so ants turn before they touch the edge.
                float step = math.max(0.3f, a.Speed * Dt);
                float2 probe = a.Position + new float2(c, s) * math.max(step, 1.2f);
                bool blocked = Grid.Contains(np) && TerrainKind.Blocks(Terrain[Grid.CellIndex(np)]);
                bool near = Grid.Contains(probe) && TerrainKind.Blocks(Terrain[Grid.CellIndex(probe)]);
                // Ordinary searching workers and bugs have nowhere particular to be: they turn away from rock and
                // water (as from the map edge) and spread back over open ground instead of lining the shore.
                // Scouts, ants with somewhere to go, and soldiers follow the edge round (see below).
                bool wanderer = wild || (a.State == AntState.Searching && a.Caste == (byte)Caste.Worker
                                         && a.Role >= Nests[a.Nest].ScoutPercent && a.Detour == 0);
                bool turned = false;
                if ((blocked || near) && wanderer)
                {
                    for (int t = 0; t < 4 && !turned; t++)
                    {
                        float ang = a.Angle + math.PI + rng.NextFloat(-1.1f, 1.1f);
                        if (!Free(a.Position, ang, 1.2f)) continue;
                        math.sincos(ang, out float s2, out float c2);
                        float2 q = a.Position + new float2(c2, s2) * step;
                        if (!Grid.Contains(q) || TerrainKind.Blocks(Terrain[Grid.CellIndex(q)])) continue;
                        a.Angle = ang;
                        np = q;
                        turned = true;
                    }
                }
                if ((blocked || near) && !turned)
                {
                    // Rock or water ahead: follow its edge. The first time, take whichever way round is the smaller turn;
                    // then keep going that way for a few seconds (wall-following), even though the ant's goal keeps
                    // pulling it back toward the obstacle. Otherwise ants on a stair-stepped shore flip left/right
                    // every cell and jitter in place instead of walking round to a ford or the end of the rock.
                    float side;
                    bool guided = detour && a.State == AntState.Returning;
                    if (guided)
                    {
                        // The way-home field already knows the route (e.g. into a ford): take whichever way round
                        // turns least from it, every time, rather than a remembered side.
                        side = 0f;
                        for (int k = 1; k <= 9 && side == 0f; k++)
                        {
                            if (Free(a.Position, a.Angle + k * 0.35f, 1.2f)) side = 1f;
                            else if (Free(a.Position, a.Angle - k * 0.35f, 1.2f)) side = -1f;
                        }
                        if (side == 0f) side = 1f;
                    }
                    else if (a.Detour != 0) side = (a.Detour & 0x80) != 0 ? 1f : -1f;
                    else
                    {
                        // A glancing touch (only one side is clear within a small turn): go that way. Otherwise each ant
                        // always goes round the same way (half the colony left, half right). With a random choice every
                        // time, ants on a long shore just random-walk along it and pile up opposite home; with a fixed
                        // one they drift steadily along it and reach the ford or the end of the ridge.
                        float mine = (a.Role & 1) == 0 ? 1f : -1f;
                        bool l = Free(a.Position, a.Angle + 0.35f, 1.2f) || Free(a.Position, a.Angle + 0.7f, 1.2f);
                        bool r = Free(a.Position, a.Angle - 0.35f, 1.2f) || Free(a.Position, a.Angle - 0.7f, 1.2f);
                        side = l && !r ? 1f : r && !l ? -1f : mine;
                    }
                    bool found = false;
                    for (int k = 1; k <= 9 && !found; k++)
                    {
                        float ang = a.Angle + side * k * 0.35f;
                        if (!Free(a.Position, ang, 1.2f) && k < 9) continue;
                        math.sincos(ang, out float s2, out float c2);
                        float2 q = a.Position + new float2(c2, s2) * step;
                        if (Grid.Contains(q) && !TerrainKind.Blocks(Terrain[Grid.CellIndex(q)])) { a.Angle = ang; np = q; found = true; }
                    }
                    // Keep following the edge for a while after losing touch: 4 s for searchers (they have nowhere
                    // particular to be), 2 s for everyone else.
                    if (found) a.Detour = guided ? (byte)0 : (byte)((side > 0f ? 0x80 : 0) | (a.State == AntState.Searching ? 120 : 60));
                    else if (blocked) { np = a.Position; a.Angle += math.PI; a.Detour = 0; }
                }
                else if (a.Detour != 0)
                {
                    // Still hugging the edge: keep turning gently toward it so the ant follows the shore round corners
                    // instead of being steered straight back into it next tick.
                    float side = (a.Detour & 0x80) != 0 ? 1f : -1f;
                    if ((a.Detour & 0x7F) > 20 && Free(a.Position, a.Angle - side * 1.2f, 2.5f)) a.Angle -= side * 0.15f;
                }
                // Soldiers going somewhere keep following the edge until the way to their goal is clear (the "bug"
                // rule robots use). Giving up after a couple of seconds let the goal pull them straight back into a
                // U-shaped bay of rock or shore, where they paced forever.
                if (hasNav && a.Detour != 0 && (a.Detour & 0x7F) < 40)
                {
                    float2 dg = navGoal - a.Position;
                    float gl = math.length(dg);
                    if (gl > 3f && !LineClear(a.Position, a.Position + dg / gl * math.min(8f, gl)))
                        a.Detour = (byte)((a.Detour & 0x80) | 40);
                }
            }
            if (np.x < 0f || np.x >= Grid.WorldSize.x)
            {
                a.Angle = math.PI - a.Angle;
                np.x = math.clamp(np.x, 0f, Grid.WorldSize.x - 0.01f);
            }
            if (np.y < 0f || np.y >= Grid.WorldSize.y)
            {
                a.Angle = -a.Angle;
                np.y = math.clamp(np.y, 0f, Grid.WorldSize.y - 0.01f);
            }
            a.Position = np;

            // Keep the angle small so sin/cos stay precise.
            if (math.abs(a.Angle) > 64f) a.Angle = math.fmod(a.Angle, 2f * math.PI);

            a.Rng = rng.state;
            Ants[i] = a;
        }

        // ------------------------------------------------------------------ wildlife

        float StepWildlife(ref Ant a, ref Random rng, CombatResult cr)
        {
            int kind = a.Caste;
            float speed = W.Speed(kind);
            int cell = Grid.CellIndex(a.Position);

            // Antlions never leave their pit: they just turn to face whatever stumbles in.
            if (kind == (int)WildKind.Antlion)
            {
                if (cr.Engaged != 0) a.Angle = TurnTowards(a.Angle, cr.TargetAngle, P.TurnSpeed * 2f * Dt);
                return 0f;
            }

            if (cr.Engaged != 0)
            {
                switch (kind)
                {
                    case (int)WildKind.Mite:
                        // Prey: scatter away from ants.
                        a.Angle = TurnTowards(a.Angle, cr.TargetAngle + math.PI, P.TurnSpeed * Dt);
                        speed *= 1.3f;
                        break;
                    case (int)WildKind.Cricket:
                        // Hops away in jumps: a burst of speed in a slightly random direction.
                        a.Angle = TurnTowards(a.Angle, cr.TargetAngle + math.PI + rng.NextFloat(-0.6f, 0.6f), P.TurnSpeed * 2f * Dt);
                        speed *= rng.NextFloat() < 0.3f ? 2.2f : 1f;
                        break;
                    case (int)WildKind.Caterpillar:
                        // Tries to crawl off, too slowly to get away.
                        a.Angle = TurnTowards(a.Angle, cr.TargetAngle + math.PI, P.TurnSpeed * 0.3f * Dt);
                        speed *= 0.8f;
                        break;
                    case (int)WildKind.Woodlouse:
                        // Rolls up into a ball and waits for the ants to give up.
                        speed = 0f;
                        break;
                    default:
                        // Beetles defend themselves; spiders, assassin bugs and centipedes pounce.
                        a.Angle = TurnTowards(a.Angle, cr.TargetAngle, P.TurnSpeed * 1.5f * Dt);
                        if (cr.InRange != 0) speed *= C.EngagedSpeed;
                        break;
                }
                return speed;
            }

            if (kind == (int)WildKind.Spider || kind == (int)WildKind.Centipede)
            {
                // Hunters roam; centipedes in long, purposeful sweeps.
                a.Angle += rng.NextFloat(-1f, 1f) * P.Wander * (kind == (int)WildKind.Centipede ? 0.5f : 1f) * Dt;
                return speed;
            }
            if (kind == (int)WildKind.Assassin)
            {
                // Creeps about, then sits still for a while in ambush.
                a.Angle += rng.NextFloat(-1f, 1f) * P.Wander * 0.6f * Dt;
                return math.frac(a.Age / 40f) < 0.45f ? 0f : speed;
            }

            // Grazers: drift toward plants and fallen leaves and linger on them.
            float l = SenseLeaves(a.Position, a.Angle + P.SensorAngle);
            float f = SenseLeaves(a.Position, a.Angle);
            float r = SenseLeaves(a.Position, a.Angle - P.SensorAngle);
            SteerByScores(ref a, ref rng, l, f, r);
            if (kind == (int)WildKind.Cricket) speed *= rng.NextFloat() < 0.05f ? 1.5f : 0.35f; // mostly walks, sometimes hops
            if (Leaves[cell] > 0)
            {
                speed *= 0.35f;
                // Beetles and caterpillars strip plants as they go; woodlice tidy up fallen leaves.
                float chew = kind == (int)WildKind.Beetle ? 2f : kind == (int)WildKind.Caterpillar ? 1.2f
                    : kind == (int)WildKind.Woodlouse ? 0.5f : 0f;
                if (chew > 0f && rng.NextFloat() < chew * Dt) TryTake(Leaves, cell);
            }
            return speed;
        }

        float SenseLeaves(float2 pos, float angle)
        {
            math.sincos(angle, out float s, out float c);
            float2 p = pos + new float2(c, s) * (P.SensorDistance * 2f);
            if (!Grid.Contains(p)) return -1f;
            int cell = Grid.CellIndex(p);
            return TerrainKind.Blocks(Terrain[cell]) ? -1f : Leaves[cell];
        }

        // ------------------------------------------------------------------ workers

        void StepWorker(ref Ant a, ref Random rng, float2 toNest, float nestDistSq, NestData nest, ref float speed, bool detour)
        {
            int cell = Grid.CellIndex(a.Position);
            float nestRadius = nest.Radius;
            bool atNest = nestDistSq < nestRadius * nestRadius;
            bool leaves = nest.GatherLeaves != 0;

            if (a.State == AntState.Migrating)
            {
                // Swarm escort: walk straight to the new home, laying no trail (it would point the wrong way).
                if (atNest)
                {
                    a.State = AntState.Searching;
                    a.Carrying = Cargo.None;
                    a.Trail = 1f;
                    a.Stuck = default;
                }
                else
                {
                    a.Angle = TurnTowards(a.Angle, math.atan2(toNest.y, toNest.x), P.TurnSpeed * Dt);
                    a.Angle += rng.NextFloat(-1f, 1f) * P.Wander * 0.5f * Dt;
                    a.Trail = 0f;
                }
                return;
            }

            MorphStats morph = Morphs[a.Colony * MorphStats.MaxMorphs + a.Morph];
            if (morph.StaysHome != 0)
            {
                // Gardeners and nurses: potter about inside the nest.
                float inner = nestRadius * 0.75f;
                if (nestDistSq > inner * inner)
                    a.Angle = TurnTowards(a.Angle, math.atan2(toNest.y, toNest.x), P.TurnSpeed * Dt);
                a.Angle += rng.NextFloat(-1f, 1f) * P.Wander * 1.5f * Dt;
                speed *= 0.4f;
                a.Trail = 1f;
                a.State = AntState.Searching;
                return;
            }

            // Scouts are a few workers (chosen by Role) who go out looking and lay the trails.
            bool scout = a.Role < nest.ScoutPercent;
            if (scout) speed *= P.ScoutSpeed;
            // Scouts' trails fade half as fast with distance, so they range twice as far.
            float decay = scout ? math.sqrt(TrailDecay) : TrailDecay;

            if (a.State == AntState.Searching)
            {
                if (atNest) a.Trail = 1f;

                byte got = TryTake(Food, cell) ? Cargo.Food
                    : leaves && TryTake(Leaves, cell) ? Cargo.Leaf : Cargo.None;
                if (got != Cargo.None)
                {
                    // Big castes carry more than one crumb.
                    if (got == Cargo.Food)
                        for (int k = 1; k < morph.Carry; k++) TryTake(Food, cell);
                    a.Carrying = got;
                    a.State = AntState.Returning;
                    a.Trail = 1f;
                    a.Angle += math.PI;
                    a.Explore = default;
                    a.Stuck = default;
                }
                else if (scout)
                {
                    // Scouts look for new food: they see it from far off and prefer ground nobody has walked.
                    float l = SenseScout(a.Colony, a.Position, a.Angle + P.SensorAngle, leaves);
                    float f = SenseScout(a.Colony, a.Position, a.Angle, leaves);
                    float r = SenseScout(a.Colony, a.Position, a.Angle - P.SensorAngle, leaves);
                    SteerByScores(ref a, ref rng, l, f, r);
                    if (a.Trail < P.GiveUpTrail) a.State = AntState.Returning;
                }
                else if (a.Explore > 0f)
                {
                    // Just found a dead end: ignore trails for a moment so it doesn't walk straight back up it.
                    a.Explore = (half)(a.Explore - Dt);
                    Dawdle(ref a, ref rng, ref speed, nestDistSq, toNest, nest, leaves);
                    if (a.Trail < P.GiveUpTrail) a.State = AntState.Returning;
                }
                else
                {
                    int foodIdx = (a.Colony * Pheromone.ChannelCount + Pheromone.ToFood) * Grid.CellCount;
                    float here = Pheromones[foodIdx + cell];
                    int cl = SensorCell(a.Position, a.Angle + P.SensorAngle, P.SensorDistance);
                    int cf = SensorCell(a.Position, a.Angle, P.SensorDistance);
                    int cr = SensorCell(a.Position, a.Angle - P.SensorAngle, P.SensorDistance);
                    float ownL = cl < 0 ? -1f : Pheromones[foodIdx + cl];
                    float ownF = cf < 0 ? -1f : Pheromones[foodIdx + cf];
                    float ownR = cr < 0 ? -1f : Pheromones[foodIdx + cr];
                    float bestOwn = math.max(ownF, math.max(ownL, ownR));

                    if (bestOwn > P.TrailFollowMin || here > P.TrailFollowMin)
                    {
                        // On a trail: follow it closely, steering hard and hardly wandering.
                        float l = TrailScore(ownL, cl, a.Colony, leaves);
                        float f = TrailScore(ownF, cf, a.Colony, leaves);
                        float r = TrailScore(ownR, cr, a.Colony, leaves);
                        float turn = P.TurnSpeed * 1.5f * Dt;
                        if (f >= l && f >= r) { }
                        else if (l > r) a.Angle += turn;
                        else if (r > l) a.Angle -= turn;
                        a.Angle += rng.NextFloat(-1f, 1f) * P.Wander * 0.35f * Dt;

                        // Dead end: on a food trail, nothing uphill, and no food in sight. The source is gone.
                        float best = math.max(f, math.max(l, r));
                        if (here > 0.08f && best < 5f && bestOwn <= here * 0.97f) a.Stuck = (half)(a.Stuck + Dt);
                        else a.Stuck = (half)math.max(0f, a.Stuck - Dt * 0.3f);

                        if (a.Stuck > P.DeadEndTime)
                        {
                            // Wipe the stale trail end so others stop coming, then wander off for a bit.
                            int cx = cell % Grid.Width, cy = cell / Grid.Width;
                            for (int oy = -2; oy <= 2; oy++)
                            for (int ox = -2; ox <= 2; ox++)
                            {
                                int x = cx + ox, y = cy + oy;
                                if (x < 0 || y < 0 || x >= Grid.Width || y >= Grid.Height) continue;
                                Pheromones[foodIdx + y * Grid.Width + x] *= 0.1f;
                            }
                            a.Stuck = default;
                            a.Explore = (half)3f;
                            a.Angle += rng.NextFloat(-2f, 2f);
                        }
                    }
                    else
                    {
                        Dawdle(ref a, ref rng, ref speed, nestDistSq, toNest, nest, leaves);
                    }

                    // Wandered too far without luck: give up and go home.
                    if (a.Trail < P.GiveUpTrail) a.State = AntState.Returning;
                }

                Deposit(a.Colony, Pheromone.ToHome, cell, a.Trail);
            }
            else // Returning
            {
                if (atNest)
                {
                    if (a.Carrying == Cargo.Food) Interlocked.Add(ref Delivered[a.Colony], math.max(1, (int)morph.Carry));
                    else if (a.Carrying == Cargo.Leaf) Interlocked.Increment(ref DeliveredLeaves[a.Colony]);
                    a.Carrying = Cargo.None;
                    a.State = AntState.Searching;
                    a.Trail = 1f;
                    a.Stuck = default;
                    a.Explore = default;
                    a.Angle += math.PI + rng.NextFloat(-0.6f, 0.6f);
                }
                else
                {
                    float homing = nestRadius * P.NestHomingRange;
                    float nestAngle = math.atan2(toNest.y, toNest.x);
                    if (nestDistSq < homing * homing || detour)
                    {
                        // Close to home, or home is round an obstacle (no home trail there): head along the way home.
                        a.Angle = TurnTowards(a.Angle, nestAngle, P.TurnSpeed * Dt);
                    }
                    else
                    {
                        Steer(ref a, ref rng, Pheromone.ToHome);
                        // Real ants path-integrate: a gentle pull home keeps lost ants from wandering forever.
                        a.Angle = TurnTowards(a.Angle, nestAngle, P.HomeBias * Dt);
                    }

                    if (a.Carrying != Cargo.None) Deposit(a.Colony, Pheromone.ToFood, cell, a.Trail);
                }
            }

            a.Trail *= decay;
        }

        /// <summary>
        /// A worker with no trail to follow is a bit hopeless: it wanders almost at random, only notices
        /// food right in front of it, stays fairly close to home and keeps off rival colonies' busy trails.
        /// </summary>
        void Dawdle(ref Ant a, ref Random rng, ref float speed, float nestDistSq, float2 toNest, NestData nest, bool leaves)
        {
            float d = P.SensorDistance * 0.5f;
            float l = NearScore(SensorCell(a.Position, a.Angle + P.SensorAngle, d), a.Colony, leaves);
            float f = NearScore(SensorCell(a.Position, a.Angle, d), a.Colony, leaves);
            float r = NearScore(SensorCell(a.Position, a.Angle - P.SensorAngle, d), a.Colony, leaves);
            SteerByScores(ref a, ref rng, l, f, r);
            a.Angle += rng.NextFloat(-1f, 1f) * P.Wander * 1.5f * Dt;
            float roam = nest.Radius * P.WorkerRoam;
            if (nestDistSq > roam * roam)
                a.Angle = TurnTowards(a.Angle, math.atan2(toNest.y, toNest.x), P.TurnSpeed * 0.5f * Dt);
            speed *= 0.8f;
        }

        /// <summary>
        /// Direction home. In the open it's the straight line; when the nest's distance field says the walk is
        /// clearly longer than the straight line (a river or ridge in between), it's toward the neighbouring field
        /// cell that is closest to home, so ants walk along the shore to a ford instead of at the water.
        /// </summary>
        float2 WayHome(float2 pos, int nestSlot, float2 straight, float distSq)
        {
            if (distSq < 400f) return straight;
            int cx = math.clamp((int)(pos.x * FieldInvScale), 0, FieldW - 1);
            int cy = math.clamp((int)(pos.y * FieldInvScale), 0, FieldH - 1);
            int baseIdx = nestSlot * FieldW * FieldH;
            ushort here = HomeField[baseIdx + cy * FieldW + cx];
            float scale = 0.1f / FieldInvScale; // field units -> world units
            if (here != HomeFieldJob.Unreachable && here * scale < math.sqrt(distSq) * 1.15f + 8f) return straight;
            int best = here, bx = -1, by = -1;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = cx + dx, ny = cy + dy;
                if ((dx == 0 && dy == 0) || nx < 0 || ny < 0 || nx >= FieldW || ny >= FieldH) continue;
                int d = HomeField[baseIdx + ny * FieldW + nx];
                if (d < best) { best = d; bx = nx; by = ny; }
            }
            if (bx < 0) return straight; // nowhere better (cut off, or already in the right cell)
            // Aim at the middle of the open part of that cell: a half-open cell's centre may be in the water.
            int cellSize = (int)math.round(1f / FieldInvScale);
            float2 sum = 0f;
            int open = 0;
            for (int y = by * cellSize; y < math.min(Grid.Height, (by + 1) * cellSize); y++)
            for (int x = bx * cellSize; x < math.min(Grid.Width, (bx + 1) * cellSize); x++)
                if (!TerrainKind.Blocks(Terrain[y * Grid.Width + x])) { sum += new float2(x, y) + 0.5f; open++; }
            float2 target = open > 0 ? sum / open : (new float2(bx, by) + 0.5f) / FieldInvScale;
            return target - pos;
        }

        /// <summary>
        /// A guard post near the nest that the squad can actually walk to more or less directly. Posts are picked at
        /// random around the nest, so some land in a pond or across a river; soldiers sent there followed the shore,
        /// gave up, turned back toward the post and paced the same nook forever. Walk back toward the nest until the
        /// nest's way-home field says the spot is open and not much further to walk than to fly.
        /// </summary>
        float2 ReachablePost(int nestSlot, float2 nestPos, float2 post)
        {
            if (FieldW <= 0) return post;
            int baseIdx = nestSlot * FieldW * FieldH;
            float scale = 0.1f / FieldInvScale;
            for (int k = 0; k < 8; k++)
            {
                float2 p = math.lerp(nestPos, post, 1f - k * 0.12f);
                // Open ground with a little room around it: guards mill about at their post.
                if (!Open(p) || !Open(p + new float2(4f, 0f)) || !Open(p - new float2(4f, 0f))
                    || !Open(p + new float2(0f, 4f)) || !Open(p - new float2(0f, 4f))) continue;
                int cx = math.clamp((int)(p.x * FieldInvScale), 0, FieldW - 1);
                int cy = math.clamp((int)(p.y * FieldInvScale), 0, FieldH - 1);
                ushort d = HomeField[baseIdx + cy * FieldW + cx];
                if (d != HomeFieldJob.Unreachable && d * scale <= math.distance(nestPos, p) * 1.3f + 8f) return p;
            }
            return nestPos;
        }

        bool Open(float2 p) => Grid.Contains(p) && !TerrainKind.Blocks(Terrain[Grid.CellIndex(p)]);

        /// <summary>Is the walk home from here much longer than the straight line (on the far side of water or rock)?</summary>
        bool FarRoundHome(float2 pos, int nestSlot)
        {
            if (FieldW <= 0) return false;
            int cx = math.clamp((int)(pos.x * FieldInvScale), 0, FieldW - 1);
            int cy = math.clamp((int)(pos.y * FieldInvScale), 0, FieldH - 1);
            ushort d = HomeField[nestSlot * FieldW * FieldH + cy * FieldW + cx];
            if (d == HomeFieldJob.Unreachable) return false;
            return d * (0.1f / FieldInvScale) > math.distance(pos, Nests[nestSlot].Position) * 1.3f + 8f;
        }

        /// <summary>Is the straight line between two points free of rock and water (checked every couple of units)?</summary>
        bool LineClear(float2 from, float2 to)
        {
            float len = math.distance(from, to);
            int n = math.min(16, (int)(len * 0.5f));
            for (int i = 1; i <= n; i++)
            {
                float2 p = math.lerp(from, to, i / (float)(n + 1));
                if (!Grid.Contains(p) || TerrainKind.Blocks(Terrain[Grid.CellIndex(p)])) return false;
            }
            return true;
        }

        /// <summary>The goal itself if it is on open ground, else the last open ground on the straight line to it.</summary>
        float2 DryGoal(float2 from, float2 goal)
        {
            if (!Grid.Contains(goal)) goal = math.clamp(goal, 0.5f, Grid.WorldSize - 0.5f);
            if (!TerrainKind.Blocks(Terrain[Grid.CellIndex(goal)])) return goal;
            float2 d = from - goal;
            float len = math.length(d);
            if (len < 1e-3f) return goal;
            d /= len;
            for (float t = 1f; t < len; t += 1f)
            {
                float2 p = goal + d * t;
                if (!TerrainKind.Blocks(Terrain[Grid.CellIndex(p)])) return p + d * 2f;
            }
            return from;
        }

        /// <summary>Is the ground dist ahead along this heading walkable (and on the map)?</summary>
        bool Free(float2 pos, float angle, float dist)
        {
            math.sincos(angle, out float s, out float c);
            float2 q = pos + new float2(c, s) * dist;
            return Grid.Contains(q) && !TerrainKind.Blocks(Terrain[Grid.CellIndex(q)]);
        }

        int SensorCell(float2 pos, float angle, float dist)
        {
            math.sincos(angle, out float s, out float c);
            float2 p = pos + new float2(c, s) * dist;
            if (!Grid.Contains(p)) return -1;
            int cell = Grid.CellIndex(p);
            return TerrainKind.Blocks(Terrain[cell]) ? -1 : cell;
        }

        /// <summary>Following a trail: own trail, food and leaves, minus a little fear of rival highways.</summary>
        float TrailScore(float own, int cell, int colony, bool senseLeaves)
        {
            if (cell < 0) return -1f;
            float v = own;
            if (Food[cell] > 0) v += 10f;
            if (senseLeaves && Leaves[cell] > 0) v += 6f;
            return v - Foreign(colony, cell) * P.ForeignFearOnTrail;
        }

        /// <summary>Off the trails: only food right in front counts, and rival highways are avoided.</summary>
        float NearScore(int cell, int colony, bool senseLeaves)
        {
            if (cell < 0) return -1f;
            float v = 0f;
            if (Food[cell] > 0) v += 10f;
            if (senseLeaves && Leaves[cell] > 0) v += 6f;
            return v - Foreign(colony, cell) * P.ForeignFear;
        }

        /// <summary>Scouts: food and leaves seen from far off, fresh ground preferred, rival trails mildly avoided.</summary>
        float SenseScout(int colony, float2 pos, float angle, bool senseLeaves)
        {
            math.sincos(angle, out float s, out float c);
            float2 dir = new float2(c, s);
            float2 near = pos + dir * P.SensorDistance;
            if (!Grid.Contains(near)) return -1f;
            int cell = Grid.CellIndex(near);
            if (TerrainKind.Blocks(Terrain[cell])) return -1f;
            float v = -0.3f * Pheromones[(colony * Pheromone.ChannelCount + Pheromone.ToHome) * Grid.CellCount + cell];
            if (Food[cell] > 0) v += 10f;
            if (senseLeaves && Leaves[cell] > 0) v += 6f;
            float2 far = pos + dir * (P.SensorDistance * P.ScoutSight);
            if (Grid.Contains(far))
            {
                int fc = Grid.CellIndex(far);
                if (Food[fc] > 0) v += 8f;
                if (senseLeaves && Leaves[fc] > 0) v += 4f;
            }
            return v - Foreign(colony, cell) * P.ForeignFear * 0.2f;
        }

        /// <summary>How busy the food trails of colonies we're at war with are at a cell (above a threshold).</summary>
        float Foreign(int colony, int cell)
        {
            int x = (cell % Grid.Width) >> 1, y = (cell / Grid.Width) >> 1;
            int idx = math.min(y, BusyH - 1) * BusyW + math.min(x, BusyW - 1);
            int per = BusyW * BusyH;
            int sum = 0;
            int row = colony * MaxColonies;
            for (int o = 0; o < WildColony; o++)
            {
                if (o == colony || Hostile[row + o] == 0) continue;
                sum += Busy[o * per + idx];
            }
            return math.max(0f, sum * (1f / 255f) - P.ForeignFearMin);
        }

        // ------------------------------------------------------------------ soldiers

        void StepSoldier(ref Ant a, ref Random rng, float2 toNest, float nestDistSq, NestData nest, ref float speed,
            ref float2 navGoal, ref bool hasNav)
        {
            if (a.State == AntState.Migrating)
            {
                if (nestDistSq < nest.Radius * nest.Radius) a.State = AntState.Searching;
                else a.Angle = TurnTowards(a.Angle, math.atan2(toNest.y, toNest.x), P.TurnSpeed * Dt);
                a.Angle += rng.NextFloat(-1f, 1f) * P.Wander * 0.5f * Dt;
                return;
            }
            int sq = NestData.SquadOf(a, nest, out bool drone);
            if (drone)
            {
                StepDrone(ref a, ref rng, nest, sq, ref speed, ref navGoal, ref hasNav);
                return;
            }
            bool raiding = nest.RaidActive != 0;

            // Squads: soldiers move in groups. Each squad holds its own sector around the nest;
            // squad 0 is the home guard and stays by the queen when there are enough squads.
            int squads = math.max(1, (int)nest.SquadCount);
            uint sqHash = math.hash(new uint2((uint)a.Nest * 64u + (uint)sq, 0x5A17u));
            bool homeGuard = sq == 0 && squads > 2;
            float sector = (sq + 0.5f) / squads * 2f * math.PI + (sqHash & 1023) / 1023f * 0.5f;
            float2 sectorDir = new float2(math.cos(sector), math.sin(sector));

            // Death smell: whole squads answer (or don't), and different squads pick different calls.
            bool hasCall = false;
            float2 call = default;
            if (!raiding && !homeGuard && sqHash % 100u < nest.DistressResponse)
            {
                float bestScore = 0f;
                int baseK = a.Colony * HotspotsPerColony;
                for (int k = 0; k < HotspotsPerColony; k++)
                {
                    float4 h = Hotspots[baseK + k];
                    if (h.z <= 0f) continue;
                    float d = math.distance(h.xy, a.Position);
                    if (d > P.DistressRange) continue;
                    float jitter = 0.6f + 0.8f * ((math.hash(new uint2(sqHash, (uint)k)) & 1023) / 1023f);
                    float score = h.z / (1f + d * 0.01f) * jitter;
                    if (score > bestScore) { bestScore = score; call = h.xy; hasCall = true; }
                }
            }
            bool atCall = hasCall && math.distancesq(call, a.Position) < 30f * 30f;

            // 1. Answer alarms: follow the alarm gradient into the fight (squads break up to fight).
            float leash = nest.Radius * C.SoldierLeash;
            if ((raiding && !homeGuard) || atCall || nestDistSq < leash * leash)
            {
                float l = Sense(a.Colony, Pheromone.Alarm, a.Position, a.Angle + P.SensorAngle, false, false);
                float f = Sense(a.Colony, Pheromone.Alarm, a.Position, a.Angle, false, false);
                float r = Sense(a.Colony, Pheromone.Alarm, a.Position, a.Angle - P.SensorAngle, false, false);
                if (math.max(f, math.max(l, r)) > C.AlarmThreshold)
                {
                    SteerByScores(ref a, ref rng, l, f, r);
                    return;
                }
            }

            // 2. Where is the squad going?
            float2 goal;
            float roam;
            bool nearHome = homeGuard; // the goal is near the nest and reachable from it
            if (raiding && !homeGuard)
            {
                // Squads converge on the target from different sides.
                goal = nest.RaidTarget + sectorDir * (squads > 1 ? 10f + 1.5f * squads : 0f);
                roam = 8f;
            }
            else if (hasCall)
            {
                goal = call + sectorDir * 6f;
                roam = 10f;
            }
            else if (homeGuard)
            {
                goal = nest.Position;
                roam = nest.Radius * 1.5f;
            }
            else
            {
                // Patrol: hold a post in the squad's sector, moving to a new one now and then.
                float patrol = nest.Radius * P.SoldierPatrolRange;
                uint epoch = (uint)((Time + (sqHash & 255) * 0.1f) / math.max(1f, P.SquadPostTime));
                uint ph = math.hash(new uint3(sqHash, epoch, 7u));
                float ang = sector + ((ph & 1023) / 1023f - 0.5f) * (2f * math.PI / squads) * 0.8f;
                float rad = patrol * (0.45f + 0.55f * (((ph >> 10) & 1023) / 1023f));
                goal = nest.Position + new float2(math.cos(ang), math.sin(ang)) * rad;
                goal = ReachablePost(a.Nest, nest.Position, goal);
                roam = 4f;
                nearHome = true;
            }
            if (raiding && !homeGuard)
            {
                // Round lakes and ridges to the target, along its field (like the way home).
                float2 toGoal = goal - a.Position;
                float2 way = WayHome(a.Position, RaidFieldBase + a.Nest, toGoal, math.lengthsq(toGoal));
                if (math.any(way != toGoal) && math.lengthsq(way) > 1f)
                    goal = a.Position + math.normalize(way) * math.min(20f, math.length(toGoal));
            }
            SquadMove(ref a, ref rng, ref speed, goal, roam, sq, ref navGoal, ref hasNav, -1, nearHome);
        }

        /// <summary>
        /// Drones: escort soldiers. Their squads walk the food trails out to the places workers are
        /// harvesting, wait there a while and walk back; or stand guard around an aphid farm.
        /// </summary>
        void StepDrone(ref Ant a, ref Random rng, NestData nest, int sq, ref float speed, ref float2 navGoal, ref bool hasNav)
        {
            // Any fight nearby comes first: that's what they're there for.
            float l = Sense(a.Colony, Pheromone.Alarm, a.Position, a.Angle + P.SensorAngle, false, false);
            float f = Sense(a.Colony, Pheromone.Alarm, a.Position, a.Angle, false, false);
            float r = Sense(a.Colony, Pheromone.Alarm, a.Position, a.Angle - P.SensorAngle, false, false);
            if (math.max(f, math.max(l, r)) > C.AlarmThreshold)
            {
                SteerByScores(ref a, ref rng, l, f, r);
                return;
            }

            int dsq = sq - NestData.DroneSquadBase;
            uint h = math.hash(new uint2((uint)a.Nest * 64u + (uint)sq, 0xD20Eu));
            float2 goal;
            float roam;
            int trail = -1;
            bool nearHome = false;
            if (nest.SiteCount > 0)
            {
                float4 site = Sites[a.Nest * NestData.MaxSites + dsq % nest.SiteCount];
                if (site.z > 0.5f)
                {
                    // Farm: circle it slowly, a post at a time.
                    float t = (Time + (h & 1023) * 0.01f) / 9f;
                    uint ph = math.hash(new uint2(h, (uint)t));
                    float ang = (ph & 1023) / 1023f * 2f * math.PI;
                    goal = site.xy + new float2(math.cos(ang), math.sin(ang)) * 5f;
                    roam = 3f;
                }
                else
                {
                    // Food source: out along the trail, stay a while, back to the near end of the trail, repeat.
                    float d = math.distance(nest.Position, site.xy);
                    float cycle = d * 1.1f / math.max(1f, P.SoldierSpeed * 0.7f) + 30f;
                    float phase = math.frac((Time + (h & 1023) / 1023f * cycle) / cycle);
                    bool outbound = phase < 0.65f;
                    float jit = ((h >> 10) & 1023) / 1023f * 2f * math.PI;
                    goal = outbound
                        ? site.xy + new float2(math.cos(jit), math.sin(jit)) * 3f
                        : math.lerp(nest.Position, site.xy, 0.35f);
                    roam = outbound ? 6f : 5f;
                    trail = outbound ? Pheromone.ToFood : Pheromone.ToHome;
                }
            }
            else
            {
                // Nothing to guard yet: wait near home in a small group.
                int n = math.max(1, (int)nest.DroneSquadCount);
                float ang = (dsq + 0.5f) / n * 2f * math.PI + (h & 1023) / 1023f;
                goal = nest.Position + new float2(math.cos(ang), math.sin(ang)) * nest.Radius * 2.5f;
                goal = ReachablePost(a.Nest, nest.Position, goal);
                roam = 3f;
                nearHome = true;
            }
            SquadMove(ref a, ref rng, ref speed, goal, roam, sq, ref navGoal, ref hasNav, trail, nearHome);
        }

        /// <summary>
        /// Moves a soldier with its squad: stragglers catch up with the squad's centre, the squad marches
        /// on the goal at the pace of its slowest members, and mills around tightly once it's there.
        /// Everything blends smoothly so squads don't lurch between those modes. With a trail channel,
        /// marching ants walk along that trail when there is one going their way.
        /// </summary>
        void SquadMove(ref Ant a, ref Random rng, ref float speed, float2 goal, float roam, int sq,
            ref float2 navGoal, ref bool hasNav, int trail = -1, bool nearHome = false)
        {
            // A goal in the water or on rock (a raid on a spot in a pond, a distress call from the far shore) can't be
            // reached: aim for the nearest dry ground on the way to it instead of pacing the shore forever.
            goal = DryGoal(a.Position, goal);
            if (nearHome && FarRoundHome(a.Position, a.Nest))
            {
                // A post by the nest, but the ant is on the wrong side of an inlet or a ridge (the walk home is much
                // longer than the straight line). Every post can be walked to from the nest, so head home round the
                // obstacle first, then out to the post.
                float2 straight = Nests[a.Nest].Position - a.Position;
                float2 way = WayHome(a.Position, a.Nest, straight, math.lengthsq(straight));
                if (math.lengthsq(way) > 1f) goal = a.Position + math.normalize(way) * math.min(8f, math.length(way));
            }
            float4 s = SquadSums[a.Nest * NestData.MaxSquads + sq];
            float2 centre = s.z > 0.5f ? s.xy / s.z : a.Position;
            // The squad's centre can be out over water (half the squad on each shore). Don't try to walk to it then:
            // a soldier pressing toward its squad across a pond never gets anywhere.
            if (!LineClear(a.Position, centre)) centre = a.Position;
            float blob = 2f + math.sqrt(math.max(1f, s.z)) * 0.9f;
            float2 toC = centre - a.Position;
            float dC = math.length(toC);
            float2 dirC = dC > 1e-3f ? toC / dC : float2.zero;
            float centreToGoal = math.distance(centre, goal);
            float2 toGoal = goal - a.Position;
            float dG = math.length(toGoal);
            float2 dirG = dG > 1e-3f ? toGoal / dG : float2.zero;

            float catchUp = math.smoothstep(blob * 1.2f, blob * 2f, dC);      // 1 = straggler
            float march = math.smoothstep(roam * 0.7f, roam * 1.3f, centreToGoal); // 1 = on the move
            float hold = math.smoothstep(blob * 0.7f, blob * 1.2f, dC);       // drifting out of the group

            // Marching pace: those ahead of the pack ease off, those behind hurry.
            float2 marchDir = centreToGoal > 1e-3f ? (goal - centre) / centreToGoal : float2.zero;
            float ahead = math.clamp(math.dot(a.Position - centre, marchDir) / (blob * 0.6f), -1f, 1f);
            float pace = math.lerp(0.45f, 0.85f - 0.3f * ahead, march);
            pace = math.lerp(pace, 1.15f, catchUp);

            float2 want = dirG * march + dirC * (hold * (1f - march));
            want = math.lerp(want, dirC, catchUp);
            float wander = math.lerp(math.lerp(1.5f, 0.4f, march), 0.3f, catchUp);

            bool onTrail = false;
            if (trail >= 0 && march > 0.5f && catchUp < 0.5f)
            {
                float l = Sense(a.Colony, trail, a.Position, a.Angle + P.SensorAngle, false, false);
                float f = Sense(a.Colony, trail, a.Position, a.Angle, false, false);
                float r = Sense(a.Colony, trail, a.Position, a.Angle - P.SensorAngle, false, false);
                float m = math.max(f, math.max(l, r));
                if (m > P.TrailFollowMin)
                {
                    // Follow the workers' trail, but only the way it leads toward the goal.
                    float g = math.atan2(dirG.y, dirG.x);
                    float bias = m * 0.6f;
                    l += bias * math.cos(a.Angle + P.SensorAngle - g);
                    f += bias * math.cos(a.Angle - g);
                    r += bias * math.cos(a.Angle - P.SensorAngle - g);
                    SteerByScores(ref a, ref rng, l, f, r);
                    onTrail = true;
                    wander = 0.25f;
                }
            }
            if (!onTrail && math.lengthsq(want) > 1e-4f)
                a.Angle = TurnTowards(a.Angle, math.atan2(want.y, want.x), P.TurnSpeed * Dt);
            if (catchUp > 0.5f) { navGoal = centre; hasNav = true; }
            else if (march > 0.5f) { navGoal = goal; hasNav = true; }
            a.Angle += rng.NextFloat(-1f, 1f) * P.Wander * wander * Dt;
            speed *= pace;
        }

        // ------------------------------------------------------------------ helpers

        void Steer(ref Ant a, ref Random rng, int channel)
        {
            float l = Sense(a.Colony, channel, a.Position, a.Angle + P.SensorAngle, false, false);
            float f = Sense(a.Colony, channel, a.Position, a.Angle, false, false);
            float r = Sense(a.Colony, channel, a.Position, a.Angle - P.SensorAngle, false, false);
            SteerByScores(ref a, ref rng, l, f, r);
        }

        void SteerByScores(ref Ant a, ref Random rng, float l, float f, float r)
        {
            float turn = P.TurnSpeed * Dt;
            if (f >= l && f >= r) { }
            else if (l > r) a.Angle += turn;
            else if (r > l) a.Angle -= turn;

            a.Angle += rng.NextFloat(-1f, 1f) * P.Wander * Dt;
        }

        float Sense(int colony, int channel, float2 pos, float angle, bool senseFood, bool senseLeaves)
        {
            math.sincos(angle, out float s, out float c);
            float2 p = pos + new float2(c, s) * P.SensorDistance;
            if (!Grid.Contains(p)) return -1f; // walls are unattractive
            int cell = Grid.CellIndex(p);
            if (TerrainKind.Blocks(Terrain[cell])) return -1f; // so are rock and water
            float v = Pheromones[(colony * Pheromone.ChannelCount + channel) * Grid.CellCount + cell];
            if (senseFood && Food[cell] > 0) v += 10f;
            if (senseLeaves && Leaves[cell] > 0) v += 6f;
            return v;
        }

        void Deposit(int colony, int channel, int cell, float strength)
        {
            int idx = (colony * Pheromone.ChannelCount + channel) * Grid.CellCount + cell;
            if (Pheromones[idx] < strength) Pheromones[idx] = strength;
        }

        static bool TryTake(int* grid, int cell)
        {
            int* p = grid + cell;
            while (true)
            {
                int cur = *p;
                if (cur <= 0) return false;
                if (Interlocked.CompareExchange(ref *p, cur - 1, cur) == cur) return true;
            }
        }

        static float TurnTowards(float angle, float target, float maxTurn)
        {
            float d = target - angle;
            d = math.atan2(math.sin(d), math.cos(d)); // wrap to [-pi, pi]
            return angle + math.clamp(d, -maxTurn, maxTurn);
        }
    }

    /// <summary>Pheromone evaporation over every colony/channel/cell.</summary>
    [BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
    public struct EvaporateJob : IJobParallelFor
    {
        [NativeDisableParallelForRestriction] public NativeArray<float> Pheromones;
        public float3 Factors; // per channel: to-home, to-food, alarm
        public int CellCount;
        public int Offset;     // processes [Offset, Offset + jobLength)
        [ReadOnly] public NativeArray<byte> Frozen; // per colony: sleeping colonies keep their trails

        public void Execute(int j)
        {
            int i = Offset + j;
            int layer = i / CellCount;
            if (Frozen[layer / Pheromone.ChannelCount] != 0) return;
            int channel = layer % Pheromone.ChannelCount;
            float v = Pheromones[i] * Factors[channel];
            Pheromones[i] = v < 0.001f ? 0f : v;
        }
    }

    /// <summary>Removes dead ants (swap-free, order preserving) and counts the living by colony and caste.</summary>
    [BurstCompile]
    public struct CompactJob : IJob
    {
        public NativeList<Ant> Ants;
        public NativeArray<int> CasteCounts; // [colony * Castes.Count + caste]
        public NativeArray<int> QueenIndex;    // per nest, index of a living queen or -1
        public NativeArray<int> PrincessIndex; // per nest, index of a princess flying to it or -1
        public NativeArray<int> NestCounts;    // per nest, workers + soldiers that live there
        public NativeArray<int> NestSoldiers;  // per nest (guards and drones)
        public NativeArray<int> NestDrones;    // per nest
        public NativeArray<int> MorphCounts;   // [colony * MaxMorphs + morph], workers and soldiers
        public NativeArray<float4> SquadSums;  // [nest * MaxSquads + squad] = (sum x, sum y, members, 0)
        [ReadOnly] public NativeArray<NestData> Nests;
        public int WildColony;

        public void Execute()
        {
            for (int i = 0; i < CasteCounts.Length; i++) CasteCounts[i] = 0;
            for (int i = 0; i < MorphCounts.Length; i++) MorphCounts[i] = 0;
            for (int i = 0; i < QueenIndex.Length; i++) { QueenIndex[i] = -1; PrincessIndex[i] = -1; NestCounts[i] = 0; NestSoldiers[i] = 0; NestDrones[i] = 0; }
            for (int i = 0; i < SquadSums.Length; i++) SquadSums[i] = float4.zero;

            int w = 0;
            int n = Ants.Length;
            for (int i = 0; i < n; i++)
            {
                Ant a = Ants[i];
                if (a.Dead != 0) continue;
                if (a.Colony != WildColony)
                {
                    if (a.Caste == (byte)Caste.Queen) QueenIndex[a.Nest] = w;
                    else if (a.Caste == (byte)Caste.Princess) PrincessIndex[a.Nest] = w;
                    else if (a.Caste <= (byte)Caste.Soldier)
                    {
                        NestCounts[a.Nest]++;
                        MorphCounts[a.Colony * MorphStats.MaxMorphs + a.Morph]++;
                    }
                    if (a.Caste == (byte)Caste.Soldier)
                    {
                        NestSoldiers[a.Nest]++;
                        int sq = NestData.SquadOf(a, Nests[a.Nest], out bool drone);
                        if (drone) NestDrones[a.Nest]++;
                        SquadSums[a.Nest * NestData.MaxSquads + sq] += new float4(a.Position, 1f, 0f);
                    }
                }
                Ants[w++] = a;
                CasteCounts[a.Colony * Castes.Count + a.Caste]++;
            }
            Ants.ResizeUninitialized(w);
        }
    }

    [BurstCompile]
    public struct SumJob : IJob
    {
        [ReadOnly] public NativeArray<int> Values;
        public NativeReference<long> Result;

        public void Execute()
        {
            long sum = 0;
            for (int i = 0; i < Values.Length; i++) sum += Values[i];
            Result.Value = sum;
        }
    }

    /// <summary>Packs ants into float4s for the GPU: xy = position, z = angle, w = colony | caste &lt;&lt; 4 | carrying &lt;&lt; 8 | scout &lt;&lt; 10.</summary>
    [BurstCompile]
    public struct BuildRenderDataJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<Ant> Ants;
        [ReadOnly] public NativeArray<NestData> Nests;
        [WriteOnly] public NativeArray<float4> Output;
        public int WildColony;
        public float Lead; // sim seconds since the last tick: ants are drawn that far along their way

        public void Execute(int i)
        {
            Ant a = Ants[i];
            bool ours = a.Colony != WildColony;
            uint scout = ours && a.Caste == (byte)Caste.Worker && a.Role < Nests[a.Nest].ScoutPercent ? 1u : 0u;
            uint drone = ours && a.Caste == (byte)Caste.Soldier && a.Role < Nests[a.Nest].DronePercent ? 1u : 0u;
            uint morph = ours ? (uint)(a.Morph & 3) : 0u;
            uint packed = a.Colony | ((uint)a.Caste << 4) | ((uint)a.Carrying << 8) | (scout << 10) | (drone << 11) | (morph << 12);
            math.sincos(a.Angle, out float s, out float c);
            Output[i] = new float4(a.Position + new float2(c, s) * (a.Speed * Lead), a.Angle, packed);
        }
    }

    /// <summary>Paints the ground texture: soil, plants, pheromone trails and food. StampJob draws nests and farms on top.</summary>
    [BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
    public struct OverlayJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> Pheromones;
        [ReadOnly] public NativeArray<int> Food;
        [ReadOnly] public NativeArray<int> Leaves;
        [ReadOnly] public NativeArray<byte> PlantCap;
        [ReadOnly] public NativeArray<byte> Terrain;
        [ReadOnly] public NativeArray<float4> ColonyColors;
        [WriteOnly] public NativeArray<Color32> Pixels;

        public GridInfo Grid;
        public int ColonyCount;
        public int Mode;  // 0 = no pheromones, 1 = all colonies, 2 = focused colony only
        public int Focus;
        public float FoodPerCellFull;
        public float LeafCap;

        public void Execute(int cell)
        {
            // Ground texture: a little per-cell grain.
            uint h = math.hash(new uint2((uint)cell, 0x7E77u));
            float grain = (h & 255) / 255f;
            byte t = Terrain[cell];
            if (t == TerrainKind.Rock)
            {
                // Rock: grey with lit top edges.
                bool edge = cell >= Grid.Width && Terrain[cell - Grid.Width] != TerrainKind.Rock;
                float3 rock = new float3(0.34f, 0.33f, 0.32f) * (0.85f + 0.25f * grain) * (edge ? 0.75f : 1f);
                bool top = cell + Grid.Width < Grid.CellCount && Terrain[cell + Grid.Width] != TerrainKind.Rock;
                if (top) rock *= 1.3f;
                rock = math.saturate(rock) * 255f;
                Pixels[cell] = new Color32((byte)rock.x, (byte)rock.y, (byte)rock.z, 255);
                return;
            }
            if (t == TerrainKind.Water)
            {
                float3 water = new float3(0.08f, 0.19f, 0.32f) + new float3(0.02f, 0.03f, 0.04f) * grain;
                water = math.saturate(water) * 255f;
                Pixels[cell] = new Color32((byte)water.x, (byte)water.y, (byte)water.z, 255);
                return;
            }
            float3 col = t == TerrainKind.Sand
                ? new float3(0.29f, 0.24f, 0.15f) * (0.92f + 0.14f * grain)
                : new float3(0.12f, 0.09f, 0.07f) * (0.9f + 0.2f * grain);

            int leaf = Leaves[cell];
            if (leaf > 0)
            {
                // Living plants are green; fallen leaves (on bare ground, or more than the plant holds) autumn brown.
                int cap = PlantCap[cell];
                float green = cap > 0 ? math.saturate(cap / (float)leaf) : 0f;
                float3 leafCol = math.lerp(new float3(0.50f, 0.28f, 0.08f) * (0.85f + 0.3f * ((h >> 8) & 255) / 255f),
                    new float3(0.10f, 0.38f, 0.10f), green);
                col = math.lerp(col, leafCol, 0.45f + 0.55f * math.saturate(leaf / LeafCap));
            }

            if (Mode != 0)
            {
                for (int c = 0; c < ColonyCount; c++)
                {
                    if (Mode == 2 && c != Focus) continue;
                    int baseIdx = c * Pheromone.ChannelCount * Grid.CellCount + cell;
                    float home = Pheromones[baseIdx];
                    float food = Pheromones[baseIdx + Grid.CellCount];
                    float alarm = Pheromones[baseIdx + 2 * Grid.CellCount];
                    float s = math.saturate(home * 0.25f + food * 0.8f);
                    col += ColonyColors[c].xyz * (s * 0.55f);
                    col += new float3(0.9f, 0.1f, 0.05f) * math.saturate(alarm) * 0.7f;
                }
            }

            int f = Food[cell];
            if (f > 0)
                col = math.lerp(col, new float3(0.98f, 0.72f, 0.22f), 0.35f + 0.65f * math.saturate(f / FoodPerCellFull));

            col = math.saturate(col) * 255f;
            Pixels[cell] = new Color32((byte)col.x, (byte)col.y, (byte)col.z, 255);
        }
    }

    /// <summary>
    /// Draws the few small things on top of the ground: nests (with fungus gardens), aphid farms,
    /// raid targets and the sites princesses are flying to. Touches only the pixels it needs.
    /// </summary>
    [BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
    public struct StampJob : IJob
    {
        public NativeArray<Color32> Pixels;
        [ReadOnly] public NativeArray<NestData> Nests;
        [ReadOnly] public NativeArray<float4> ColonyColors;
        [ReadOnly] public NativeArray<float4> Farms;       // x, y, colony, aphid fullness 0..1
        [ReadOnly] public NativeArray<float> GardenLevels; // per colony, 0..1 of max
        [ReadOnly] public NativeArray<float4> Trees;       // x, y, canopy radius, health 0..1 (-1 dead)
        [ReadOnly] public NativeArray<float4> Defences;    // x, y, colony * 16 + kind, radius
        public GridInfo Grid;
        public int NestCount;
        public int FarmCount;
        public int TreeCount;
        public int DefenceCount;
        public int Focus;
        public float TrunkRadius;

        public void Execute()
        {
            for (int k = 0; k < TreeCount; k++) DrawTree(Trees[k]);

            for (int k = 0; k < DefenceCount; k++)
            {
                float4 d = Defences[k];
                int colony = (int)d.z / 16, kind = (int)d.z % 16;
                if (kind == 2) continue; // fortified nests are drawn with the nest
                float3 dc = ColonyColors[colony].xyz;
                float r = d.w;
                Bounds(d.xy, r, out int x0, out int y0, out int x1, out int y1);
                for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    int cell = y * Grid.Width + x;
                    float2 p = Grid.CellCenter(cell);
                    float2 o = p - d.xy;
                    float dist = math.length(o);
                    if (dist > r) continue;
                    if (kind == 0)
                    {
                        // Resin trap: glossy amber blobs, with a dashed rim in the owner's colour.
                        if (dist > r - 0.9f) { if ((int)((math.atan2(o.y, o.x) + math.PI) * 4f) % 2 == 0) Set(cell, dc); }
                        else
                        {
                            // Soft, uneven amber: thicker towards the middle, patchy at the edge.
                            float n = (math.hash(new int2(x, y)) & 255) / 255f;
                            float a = (0.18f + 0.3f * n) * (1f - 0.5f * dist / r);
                            Set(cell, math.lerp(Get(cell), new float3(0.8f, 0.52f, 0.14f), a));
                        }
                    }
                    else
                    {
                        // Stinger mound: a raised earth mound, its sting range a faint ring.
                        if (dist < 2.6f) Set(cell, math.lerp(new float3(0.42f, 0.3f, 0.18f), dc, dist < 1.2f ? 0.7f : 0.25f) * (1.2f - 0.25f * dist));
                        else if (dist > r - 0.8f) Set(cell, math.lerp(Get(cell), dc, 0.45f));
                    }
                }
            }

            for (int k = 0; k < FarmCount; k++)
            {
                float4 farm = Farms[k];
                float3 fc = ColonyColors[(int)farm.z].xyz;
                Bounds(farm.xy, 4.6f, out int x0, out int y0, out int x1, out int y1);
                for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    int cell = y * Grid.Width + x;
                    float2 p = Grid.CellCenter(cell);
                    float d = math.distance(p, farm.xy);
                    if (d > 4.6f) continue;
                    if (d > 3.8f) Set(cell, fc);
                    else if (d < 2.6f && ((int)(p.x * 1.7f + p.y * 2.3f) % 3 == 0) && farm.w > 0.05f)
                        Set(cell, math.lerp(Get(cell), new float3(0.75f, 1f, 0.45f), 0.4f + 0.6f * farm.w)); // aphids
                }
            }

            for (int n = 0; n < NestCount; n++)
            {
                NestData nest = Nests[n];
                if (nest.Active == 0) continue;
                int c = nest.Colony;
                float3 nc = ColonyColors[c].xyz;
                float r = nest.Radius;
                float g = GardenLevels[c];
                float3 rim = nest.HasQueen == 0 ? new float3(0.35f, 0.35f, 0.35f) : nc; // queenless: grey rim
                if (c == Focus) rim = math.lerp(rim, new float3(1f, 1f, 1f), 0.5f);
                bool fortified = false;
                for (int f = 0; f < DefenceCount; f++)
                {
                    float4 d = Defences[f];
                    if ((int)d.z % 16 == 2 && (int)d.z / 16 == c && math.distancesq(d.xy, nest.Position) < 1f) fortified = true;
                }
                if (fortified) r += 1.2f; // thick stone walls round the nest

                Bounds(nest.Position, r + 1f, out int x0, out int y0, out int x1, out int y1);
                for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    int cell = y * Grid.Width + x;
                    float2 p = Grid.CellCenter(cell);
                    float2 o = p - nest.Position;
                    float d = math.length(o);
                    if (nest.Founding != 0)
                    {
                        // Dashed ring where a princess is heading.
                        if (d > r - 1.2f && d < r + 0.6f && (int)((math.atan2(o.y, o.x) + math.PI) * 3f) % 2 == 0)
                            Set(cell, nc);
                        continue;
                    }
                    if (d >= r) continue;
                    if (fortified && d > r - 2.4f) { Set(cell, (x + y) % 2 == 0 ? new float3(0.55f, 0.53f, 0.5f) : rim); continue; }
                    if (d > r - 1.2f) { Set(cell, rim); continue; }
                    // Fungus garden: pale blotches inside the nest, more with each level.
                    if (g > 0f && ((int)(p.x * 3.1f + p.y * 1.3f) % 4 == 0) && d < r * (0.4f + 0.6f * g))
                        Set(cell, new float3(0.92f, 0.88f, 0.75f));
                    else Set(cell, nc * 0.45f);
                }

                // Raid target marker: a ring in the colony's colour (one per colony; all its nests share it).
                if (nest.RaidActive != 0)
                {
                    Bounds(nest.RaidTarget, 7.5f, out x0, out y0, out x1, out y1);
                    for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        int cell = y * Grid.Width + x;
                        float rd = math.distance(Grid.CellCenter(cell), nest.RaidTarget);
                        if (rd > 6f && rd < 7.5f) Set(cell, nc);
                    }
                }
            }
        }

        /// <summary>A tree from above: dappled shade under the crown, the trunk in the middle. Dead: a grey snag.</summary>
        void DrawTree(float4 t)
        {
            bool dead = t.w < 0f;
            float r = dead ? TrunkRadius + 0.5f : t.z;
            // Leaves yellow as the tree runs out of wood or years.
            float3 shade = math.lerp(new float3(0.35f, 0.30f, 0.05f), new float3(0.02f, 0.16f, 0.05f), math.saturate(t.w * 2f));
            Bounds(t.xy, r, out int x0, out int y0, out int x1, out int y1);
            for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                int cell = y * Grid.Width + x;
                float2 o = Grid.CellCenter(cell) - t.xy;
                float d = math.length(o);
                if (d > r) continue;
                if (d < TrunkRadius)
                {
                    // Bark: rings round the middle.
                    float ring = math.frac(d * 1.3f);
                    float3 bark = dead ? new float3(0.33f, 0.30f, 0.27f) : new float3(0.36f, 0.22f, 0.11f);
                    Set(cell, bark * (ring < 0.25f ? 0.7f : 1f) * (d > TrunkRadius - 0.7f ? 0.75f : 1f));
                    continue;
                }
                if (dead) continue;
                // Canopy shadow, broken up by patches of light.
                uint hh = math.hash(new int2(x / 2, y / 2));
                float dapple = (hh & 255) / 255f;
                float edge = math.saturate((r - d) / 2.5f);
                float a = (dapple < 0.3f ? 0.12f : 0.38f) * edge;
                Set(cell, math.lerp(Get(cell), shade, a));
            }
        }

        void Bounds(float2 center, float radius, out int x0, out int y0, out int x1, out int y1)
        {
            x0 = math.clamp((int)((center.x - radius) * Grid.InvCellSize), 0, Grid.Width - 1);
            y0 = math.clamp((int)((center.y - radius) * Grid.InvCellSize), 0, Grid.Height - 1);
            x1 = math.clamp((int)((center.x + radius) * Grid.InvCellSize), 0, Grid.Width - 1);
            y1 = math.clamp((int)((center.y + radius) * Grid.InvCellSize), 0, Grid.Height - 1);
        }

        float3 Get(int cell)
        {
            Color32 c = Pixels[cell];
            return new float3(c.r, c.g, c.b) * (1f / 255f);
        }

        void Set(int cell, float3 col)
        {
            col = math.saturate(col) * 255f;
            Pixels[cell] = new Color32((byte)col.x, (byte)col.y, (byte)col.z, 255);
        }
    }

    /// <summary>
    /// Builds the half-resolution "busy trails" map per colony: the strongest food-trail value in each
    /// 2x2 block, as a byte. Workers read it to keep off rival highways without touching every colony's layers.
    /// </summary>
    [BurstCompile]
    public struct BusyTrailsJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> Pheromones;
        [WriteOnly] public NativeArray<byte> Busy;
        public GridInfo Grid;
        public int BusyW, BusyH;

        public void Execute(int i) // i = colony * BusyW * BusyH + block
        {
            int per = BusyW * BusyH;
            int colony = i / per, block = i % per;
            int bx = (block % BusyW) * 2, by = (block / BusyW) * 2;
            int layer = (colony * Pheromone.ChannelCount + Pheromone.ToFood) * Grid.CellCount;
            float m = 0f;
            for (int y = by; y < math.min(by + 2, Grid.Height); y++)
            for (int x = bx; x < math.min(bx + 2, Grid.Width); x++)
                m = math.max(m, Pheromones[layer + y * Grid.Width + x]);
            Busy[i] = (byte)(math.saturate(m) * 255f);
        }
    }

    /// <summary>
    /// Scores each coarse cell for each colony as a place where its workers are harvesting:
    /// food on the ground there times how busy the colony's food trail is there.
    /// </summary>
    [BurstCompile]
    public struct HarvestSitesJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<int> Food;
        [ReadOnly] public NativeArray<byte> Busy;
        [NativeDisableParallelForRestriction, WriteOnly] public NativeArray<float> Score; // [colony * W * H + cell]
        public GridInfo Grid;
        public int BusyW, BusyH, W, H, Colonies;
        public float Cell;

        public void Execute(int cell)
        {
            int per = W * H;
            int cx = cell % W, cy = cell / W;
            int x0 = (int)(cx * Cell * Grid.InvCellSize), y0 = (int)(cy * Cell * Grid.InvCellSize);
            int x1 = math.min(Grid.Width, (int)((cx + 1) * Cell * Grid.InvCellSize));
            int y1 = math.min(Grid.Height, (int)((cy + 1) * Cell * Grid.InvCellSize));
            int foodCells = 0;
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
                if (Food[y * Grid.Width + x] > 0) foodCells++;
            if (foodCells == 0)
            {
                for (int c = 0; c < Colonies; c++) Score[c * per + cell] = 0f;
                return;
            }
            float amount = math.min(foodCells, 16) / 16f;
            int bx0 = x0 >> 1, by0 = y0 >> 1;
            int bx1 = math.min(BusyW, (x1 + 1) >> 1), by1 = math.min(BusyH, (y1 + 1) >> 1);
            int busyPer = BusyW * BusyH;
            for (int c = 0; c < Colonies; c++)
            {
                int m = 0;
                for (int y = by0; y < by1; y++)
                for (int x = bx0; x < bx1; x++)
                    m = math.max(m, Busy[c * busyPer + y * BusyW + x]);
                Score[c * per + cell] = m / 255f * amount;
            }
        }
    }
}
