using System;
using Unity.Mathematics;
using UnityEngine;

namespace AntSim
{
    /// <summary>Ant archetypes. Keep values small: they are packed into 4 bits for rendering.</summary>
    public enum Caste : byte
    {
        Worker = 0,
        Soldier = 1,
        Queen = 2,
        Princess = 3, // young winged queen on her way to found a new nest
        Male = 4,     // winged male; flies with the princess on the swarm, then dies
    }

    public static class Castes
    {
        public const int Count = 5;
    }

    /// <summary>
    /// Other bugs. They live in the same ant list under a special "wildlife" colony slot
    /// (index = colonyCount), with their kind stored in the Caste field.
    /// </summary>
    public enum WildKind : byte
    {
        Mite = 0,        // small, harmless prey that flees
        Beetle = 1,      // big, armoured, slow; eats plants and aphids; a feast if you can kill it
        Spider = 2,      // predator that hunts ants
        Caterpillar = 3, // slow, soft, harmless leaf-eater: easy big prey
        Cricket = 4,     // quick prey that hops away from ants
        Woodlouse = 5,   // armoured scavenger of fallen leaves; rolls up when attacked
        Antlion = 6,     // easy predator: sits in a pit in the sand and grabs whatever walks in
        Assassin = 7,    // assassin bug: a slow stalker that picks off stragglers
        Centipede = 8,   // the hardest predator: fast, armoured, deadly
    }

    public static class WildKinds
    {
        public const int Count = 9;

        public static bool IsPredator(int k) =>
            k == (int)WildKind.Spider || k == (int)WildKind.Antlion || k == (int)WildKind.Assassin || k == (int)WildKind.Centipede;

        /// <summary>Little prey that doesn't count as a threat (LOD) and has short reach.</summary>
        public static bool IsSmall(int k) => k == (int)WildKind.Mite || k == (int)WildKind.Cricket;

        /// <summary>Eats plants and fallen leaves (and turns up near them).</summary>
        public static bool IsGrazer(int k) => !IsPredator(k);

        public static string Name(WildKind k)
        {
            switch (k)
            {
                case WildKind.Mite: return "mite";
                case WildKind.Beetle: return "beetle";
                case WildKind.Spider: return "spider";
                case WildKind.Caterpillar: return "caterpillar";
                case WildKind.Cricket: return "cricket";
                case WildKind.Woodlouse: return "woodlouse";
                case WildKind.Antlion: return "antlion";
                case WildKind.Assassin: return "assassin bug";
                default: return "centipede";
            }
        }

        public static string Plural(WildKind k) => k == WildKind.Woodlouse ? "woodlice" : Name(k) + "s";

        /// <summary>Prey worth sending soldiers after, and the predators, for the hunt buttons and the AI.</summary>
        public static readonly WildKind[] BigPrey = { WildKind.Beetle, WildKind.Caterpillar, WildKind.Woodlouse, WildKind.Cricket };
        public static readonly WildKind[] Predators = { WildKind.Spider, WildKind.Assassin, WildKind.Antlion, WildKind.Centipede };
    }

    /// <summary>What an ant carries in its mandibles.</summary>
    public static class Cargo
    {
        public const byte None = 0;
        public const byte Food = 1;
        public const byte Leaf = 2; // for the fungus garden
    }

    /// <summary>
    /// Brood laid in the same second, aged together. Brood lives inside the nest as numbers,
    /// not as individual ants, so a colony can hold huge nurseries for free.
    /// </summary>
    public struct BroodCohort
    {
        public float Age;
        public float Workers;
        public float Soldiers;
    }

    public static class AntState
    {
        public const byte Searching = 0; // looking for food, laying "to home" pheromone
        public const byte Returning = 1; // heading home (with or without food), laying "to food" if carrying
        public const byte Migrating = 2; // moving to a new home nest (swarm escort); beelines, lays nothing
    }

    public static class Pheromone
    {
        public const int ToHome = 0;
        public const int ToFood = 1;
        public const int Alarm = 2;  // laid by fighting ants; soldiers follow it to the battle
        public const int Route = 3;  // laid by scouts coming home with food; fades over many minutes
        public const int ChannelCount = 4;
        public const int OldChannelCount = 3; // saves before version 3
    }

    /// <summary>
    /// One ant. Plain blittable struct so millions of them can live in a NativeList and be
    /// processed by Burst jobs. No references, no inheritance.
    /// </summary>
    public struct Ant
    {
        public float2 Position;
        public float Angle;     // heading in radians
        public float Age;       // seconds of sim time
        public float Lifespan;  // dies when Age > Lifespan
        public float Trail;     // strength of pheromone this ant lays; resets at nest/food, decays while walking
        public float Health;
        public float Cooldown;  // seconds until it can bite again
        public uint Rng;        // per-ant random state (never 0)
        public byte Colony;
        public byte Caste;
        public byte State;
        public byte Carrying;
        public byte Dead;
        public byte Role;       // 0..99, fixed at birth: decides who answers distress calls and who joins a swarm
        public byte Nest;       // home nest (global nest index, < 64); unused for wildlife
        public byte Squad;      // soldiers: random at birth; squad = Squad % the nest's squad count
        public byte Morph;      // workers/soldiers: species caste variant (0 plain, 1..3 = SpeciesDef.Castes)
        public byte Detour;     // following rock/water: bit 7 = which way round, bits 0-6 = ticks left (fills padding; size stays 56)
        public half Explore;    // seconds left exploring (ignoring food trails) after hitting a dead end
        public half Stuck;      // seconds spent at the top of a food trail with no food in sight
        public half Clock;      // seconds since the last memory checkpoint (fills padding)
        public float Speed;     // current walking speed (eases toward the wanted speed; also used to draw between ticks)

        // Memory (see AntStepJob.Remember). Positions are half precision: plenty for "roughly where".
        public half2 Mark;      // where it was at the last checkpoint (every couple of seconds)
        public half2 Back;      // the last checkpoint out on open ground: the way out if it gets stuck
        public half2 Avoid0;    // two remembered bad spots (a dead end, a crevice it got stuck in)
        public half2 Avoid1;
        public half Forget0;    // seconds left remembering each of them
        public half Forget1;
        public byte WallTicks;  // ticks spent touching rock or water since the last checkpoint
        public byte Escape;     // ticks left backing out toward Back after getting stuck
    }

    /// <summary>One nest. A colony can own several; colony-wide fields are copied into each of its nests every tick.</summary>
    public struct NestData
    {
        public float2 Position;
        public byte Colony;
        public byte Active;   // slot in use
        public byte Founding; // a princess is on her way; no queen yet
        public float Radius;
        public float2 RaidTarget;
        public byte RaidActive;
        public byte HasQueen;
        public float CoreRadius; // LOD: fights inside this radius keep the colony awake
        public byte GatherLeaves; // colony has a fungus garden with room for more leaves
        public byte DistressResponse; // % of soldier squads that answer distress calls
        public byte SquadCount;       // soldiers are split into this many squads (1..MaxSquads)
        public byte ScoutPercent;     // workers with Role below this are scouts
        public byte DronePercent;     // soldiers with Role below this are drones (escorts)
        public byte DroneSquadCount;  // drones are split into this many squads (1..MaxDroneSquads)
        public byte SiteCount;        // harvest sites and farms the drones guard (in AntWorld's site table)

        public const int MaxSquads = 32;      // squad slots per nest: guard squads first, then drone squads
        public const int MaxGuardSquads = 24;
        public const int DroneSquadBase = 24;
        public const int MaxDroneSquads = 8;
        public const int MaxSites = 6;

        /// <summary>The squad slot a soldier belongs to; drones use the slots from DroneSquadBase up.</summary>
        public static int SquadOf(in Ant a, in NestData n, out bool drone)
        {
            drone = a.Role < n.DronePercent;
            return drone
                ? DroneSquadBase + a.Squad % math.max(1, (int)n.DroneSquadCount)
                : a.Squad % math.max(1, (int)n.SquadCount);
        }
    }

    /// <summary>Written by CombatJob for each ant (at the ant's own index), read by AntStepJob.</summary>
    public struct CombatResult
    {
        public float TargetAngle; // heading toward the nearest hostile ant
        public byte Engaged;      // an enemy is within sensing range
        public byte InRange;      // close enough to bite
        public byte Attacked;     // bit this tick, so reset cooldown
        public byte TargetKind;   // 0 = ant, 1 + WildKind for wildlife
        public byte Slowed;       // stuck in an enemy's resin trap (DefenceJob)
    }

    /// <summary>Combat tuning. Passed by value into Burst jobs.</summary>
    [Serializable]
    public struct CombatParams
    {
        public float WorkerHealth;
        public float SoldierHealth;
        public float WorkerDamage;
        public float SoldierDamage;
        public float QueenHealth;
        public float QueenDamage;
        public float AttackRange;
        [Tooltip("Seconds between bites.")]
        public float AttackInterval;
        [Tooltip("Workers only notice enemies this close.")]
        public float WorkerSense;
        [Tooltip("Soldiers spot enemies this far away.")]
        public float SoldierSense;
        [Tooltip("Max neighbours inspected per ant per tick. Bounds cost in dense crowds.")]
        public int MaxChecks;
        public float AlarmHalfLife;
        public float AlarmThreshold;
        [Tooltip("Soldiers answer alarms within NestRadius * this of the nest.")]
        public float SoldierLeash;
        [Tooltip("Movement speed multiplier while biting an enemy.")]
        public float EngagedSpeed;
        [Tooltip("Food left on the ground by a dead worker / soldier. Ants eat their enemies.")]
        public int WorkerCorpseFood;
        public int SoldierCorpseFood;
        [Tooltip("Spatial hash bucket size in world units.")]
        public float BucketSize;

        public static CombatParams Default => new CombatParams
        {
            WorkerHealth = 10f,
            SoldierHealth = 40f,
            WorkerDamage = 2f,
            SoldierDamage = 7f,
            QueenHealth = 600f,
            QueenDamage = 10f,
            AttackRange = 1.2f,
            AttackInterval = 0.6f,
            WorkerSense = 1.6f,
            SoldierSense = 6f,
            MaxChecks = 48,
            AlarmHalfLife = 4f,
            AlarmThreshold = 0.05f,
            SoldierLeash = 30f,
            EngagedSpeed = 0.1f,
            WorkerCorpseFood = 2,
            SoldierCorpseFood = 6,
            BucketSize = 4f,
        };
    }

    public struct GridInfo
    {
        public int Width;
        public int Height;
        public float CellSize;
        public float InvCellSize;
        public float2 WorldSize;

        public int CellCount => Width * Height;

        public GridInfo(int width, int height, float cellSize)
        {
            Width = width;
            Height = height;
            CellSize = cellSize;
            InvCellSize = 1f / cellSize;
            WorldSize = new float2(width * cellSize, height * cellSize);
        }

        public bool Contains(float2 p) => p.x >= 0f && p.y >= 0f && p.x < WorldSize.x && p.y < WorldSize.y;

        public int CellIndex(float2 p)
        {
            int x = math.clamp((int)(p.x * InvCellSize), 0, Width - 1);
            int y = math.clamp((int)(p.y * InvCellSize), 0, Height - 1);
            return y * Width + x;
        }

        public float2 CellCenter(int index)
        {
            int y = index / Width;
            int x = index - y * Width;
            return new float2((x + 0.5f) * CellSize, (y + 0.5f) * CellSize);
        }
    }

    /// <summary>Per-ant behaviour tuning. Passed by value into Burst jobs.</summary>
    [Serializable]
    public struct SimParams
    {
        public float WorkerSpeed;
        public float SoldierSpeed;
        [Tooltip("Max steering rate in radians/second when following pheromone.")]
        public float TurnSpeed;
        [Tooltip("Random heading jitter in radians/second.")]
        public float Wander;
        public float SensorAngle;
        public float SensorDistance;
        [Tooltip("Seconds for an ant's laid-trail strength to halve.")]
        public float TrailHalfLife;
        [Tooltip("Searching ants whose trail drops below this give up and head home.")]
        public float GiveUpTrail;
        [Tooltip("Path-integration pull toward the nest while returning (radians/second).")]
        public float HomeBias;
        [Tooltip("Returning ants beeline for the nest within NestRadius * this.")]
        public float NestHomingRange;
        [Tooltip("Seconds for pheromone on the ground to halve.")]
        public float PheromoneHalfLife;
        [Tooltip("Soldiers patrol within NestRadius * this.")]
        public float SoldierPatrolRange;
        public float NestRadius;
        public float QueenSpeed;
        [Tooltip("Seconds a searching ant must circle the top of a food trail (no food) before it decides the source is gone.")]
        public float DeadEndTime;
        [Tooltip("Seconds an ant explores fresh ground (ignoring food trails) after finding a dead end or coming home empty.")]
        public float ExploreTime;
        [Tooltip("Soldiers answer worker distress calls from this far away.")]
        public float DistressRange;
        [Tooltip("Scouts run this much faster than workers.")]
        public float ScoutSpeed;
        [Tooltip("Scouts see food this many times further away than workers.")]
        public float ScoutSight;
        [Tooltip("Off the trails, workers stay within NestRadius * this of home.")]
        public float WorkerRoam;
        [Tooltip("Own food-trail strength a worker needs to notice and follow a trail.")]
        public float TrailFollowMin;
        [Tooltip("How strongly searching workers avoid hostile colonies' busy food trails (off their own trails).")]
        public float ForeignFear;
        [Tooltip("The same, while following their own trail.")]
        public float ForeignFearOnTrail;
        [Tooltip("Rival trail strength below this isn't 'busy' and is ignored.")]
        public float ForeignFearMin;
        [Tooltip("Soldiers are organised in squads of about this many.")]
        public int SquadSize;
        [Tooltip("Seconds a patrolling squad holds a post before moving to another spot in its sector.")]
        public float SquadPostTime;
        [Tooltip("Drones (escort soldiers) are organised in squads of about this many.")]
        public int DroneSquadSize;
        [Tooltip("Drones guard food sources at most this far from their nest.")]
        public float DroneRange;
        [Tooltip("How quickly ants reach their wanted speed (per second). Lower is smoother.")]
        public float Acceleration;
        [Tooltip("Speed multiplier on sand.")]
        public float SandSpeed;

        public static SimParams Default => new SimParams
        {
            QueenSpeed = 1.5f,
            WorkerSpeed = 8f,
            SoldierSpeed = 6f,
            TurnSpeed = 7f,
            Wander = 3f,
            SensorAngle = 0.6f,
            SensorDistance = 3f,
            TrailHalfLife = 18f,
            GiveUpTrail = 0.03f,
            HomeBias = 0.35f,
            NestHomingRange = 4f,
            PheromoneHalfLife = 25f,
            SoldierPatrolRange = 7f,
            NestRadius = 4f,
            DeadEndTime = 0.6f,
            ExploreTime = 12f,
            DistressRange = 320f,
            ScoutSpeed = 1.3f,
            ScoutSight = 2.5f,
            WorkerRoam = 12f,
            TrailFollowMin = 0.05f,
            ForeignFear = 1.5f,
            ForeignFearOnTrail = 0.4f,
            ForeignFearMin = 0.15f,
            SquadSize = 20,
            SquadPostTime = 25f,
            DroneSquadSize = 10,
            DroneRange = 260f,
            Acceleration = 8f,
            SandSpeed = 0.6f,
        };
    }

    /// <summary>Colony-level economy tuning (main thread only).</summary>
    [Serializable]
    public struct EconomyParams
    {
        public float AntCost;
        public float UpkeepPerAntPerSecond;
        [Tooltip("Eggs per second for a tiny colony.")]
        public float BaseLayRate;
        [Tooltip("Extra eggs per second per living ant (nurses/brood care). Growth has no cap, only food limits it.")]
        public float LayRatePerAnt;
        public float WorkerLifespanMin;
        public float WorkerLifespanMax;
        public float SoldierLifespanMultiplier;
        [Tooltip("Fraction of ants dying per second while the colony has no food.")]
        public float StarveDeathRate;
        public float FoodPerDelivery;
        [Tooltip("Seconds of upkeep the colony keeps in store before spending food on eggs.")]
        public float FoodReserveSeconds;

        [Header("Brood")]
        [Tooltip("Food the queen spends per egg. The rest of AntCost is eaten by the larva while it grows.")]
        public float EggCost;
        public float EggDuration;
        public float LarvaDuration;
        public float PupaDuration;
        [Tooltip("Fraction of larvae dying per second when the colony can't feed them.")]
        public float LarvaStarveRate;

        [Header("Queen")]
        public float NewQueenCost;
        [Tooltip("Seconds a queenless colony needs to raise a replacement queen.")]
        public float NewQueenTime;
        public int NewQueenMinPopulation;

        public float BroodDuration => EggDuration + LarvaDuration + PupaDuration;

        public static EconomyParams Default => new EconomyParams
        {
            AntCost = 4f,
            UpkeepPerAntPerSecond = 0.004f,
            BaseLayRate = 3f,
            LayRatePerAnt = 0.01f,
            WorkerLifespanMin = 180f,
            WorkerLifespanMax = 360f,
            SoldierLifespanMultiplier = 1.5f,
            StarveDeathRate = 0.03f,
            FoodPerDelivery = 1f,
            FoodReserveSeconds = 60f,
            EggCost = 1f,
            EggDuration = 10f,
            LarvaDuration = 25f,
            PupaDuration = 15f,
            LarvaStarveRate = 0.25f,
            NewQueenCost = 150f,
            NewQueenTime = 60f,
            NewQueenMinPopulation = 40,
        };
    }

    /// <summary>
    /// Level-of-detail tuning. Colonies the player can't see, that aren't fighting, are "asleep":
    /// their ants exist only as numbers and forage/die statistically, costing almost nothing.
    /// </summary>
    [Serializable]
    public struct LodParams
    {
        public bool Enabled;
        [Tooltip("World units around the camera view that still count as visible.")]
        public float ViewMargin;
        [Tooltip("Seconds between sleep/wake checks.")]
        public float CheckInterval;
        [Tooltip("A colony must be eligible this long before it falls asleep.")]
        public float SleepDelay;
        [Tooltip("Colonies stay awake this long after losing an ant in battle near their nest (inside the core).")]
        public float CombatCooldown;
        [Tooltip("Hostile ants inside this fraction of a colony's territory keep it awake / wake it.")]
        public float CoreFraction;
        [Tooltip("A colony counts as on-screen when this fraction of its territory (around the nest) is in view.")]
        public float VisibleFraction;
        public float MinTerritory;
        [Tooltip("Seconds between statistical foraging pulls for sleeping colonies.")]
        public float ForageInterval;
        [Tooltip("Food per worker per second assumed before a colony has been measured.")]
        public float DefaultForageRate;
        [Tooltip("Seconds over which the live forage rate is averaged.")]
        public float ForageRateSmoothing;
        [Tooltip("A colony must have been simulated awake this long (so its territory and forage rate are measured) before it may sleep.")]
        public float MinObservedTime;
        [Tooltip("Forage rate isn't measured during a new colony's first seconds (trails still forming).")]
        public float WarmupTime;
        [Tooltip("...nor for this long after a colony wakes (ants re-finding trails).")]
        public float WakeSettleTime;
        [Tooltip("A sleeping colony short on food ranges further, up to this multiple of its measured territory.")]
        public float MaxTerritoryGrowth;
        [Tooltip("Hostile ants inside a sleeping colony's core needed to wake it (a few stray foragers don't).")]
        public int WakeIntruders;
        [Tooltip("A colony that woke stays awake at least this long, to avoid sleep/wake churn.")]
        public float MinAwakeTime;
        [Tooltip("Width of the age bins sleeping ants are stored in (seconds).")]
        public float AgeBinSeconds;

        public static LodParams Default => new LodParams
        {
            Enabled = true,
            ViewMargin = 40f,
            CheckInterval = 0.5f,
            SleepDelay = 3f,
            CombatCooldown = 10f,
            CoreFraction = 0.35f,
            VisibleFraction = 0.5f,
            MinTerritory = 30f,
            ForageInterval = 1f,
            DefaultForageRate = 0.03f,
            ForageRateSmoothing = 60f,
            MinObservedTime = 120f,
            WarmupTime = 90f,
            WakeSettleTime = 30f,
            MaxTerritoryGrowth = 2f,
            WakeIntruders = 10,
            MinAwakeTime = 60f,
            AgeBinSeconds = 10f,
        };
    }

    /// <summary>
    /// Managed, colony-level state. This is where the "colony brain" and player rules live:
    /// it runs once per tick on the main thread and only deals with totals, never individual ants.
    /// </summary>
    [Serializable]
    public class ColonyInfo
    {
        public string Name;
        public Color Color;
        public Vector2 NestPosition;
        public float Food;
        public int Workers;
        public int Soldiers;
        public bool Starving;
        public long TotalBorn;
        public long TotalDelivered;
        public long CombatDeaths;

        // --- Queen & brood ---
        public bool HasQueen;
        public float QueenHealth;
        public int QueensLost;
        public float NewQueenProgress; // 0..1 while raising a replacement
        public float Eggs, Larvae, Pupae;
        public float BroodWorkers, BroodSoldiers;
        public bool BroodStarving;
        public bool Extinct;
        [NonSerialized] public bool JustCrownedQueen;
        [NonSerialized] public readonly System.Collections.Generic.List<BroodCohort> Brood =
            new System.Collections.Generic.List<BroodCohort>();

        // --- Level of detail ---
        [Tooltip("Asleep: this colony's ants are simulated as numbers, not individuals.")]
        public bool Abstract;
        public float AbstractWorkers;
        public float AbstractSoldiers;
        public int LiveAnts;
        public float TerritoryRadius = 60f;
        [Tooltip("Measured food delivered per worker per second while awake.")]
        public float ForageRate = 0.03f;
        [NonSerialized] public float PendingForage;
        [NonSerialized] public float ForageTimer;
        [NonSerialized] public float SleepTimer;
        [NonSerialized] public float LastCombatTime = -1000f;
        [NonSerialized] public float ObservedTime; // seconds spent awake (territory/forage rate measured)
        [NonSerialized] public float LastWakeTime = -1000f;
        [NonSerialized] public long ForageWanted, ForageTaken; // diagnostics for the sleeping forage model
        [NonSerialized] public int SleepCount, WakeCount;
        [NonSerialized] public int LodIntruders; // hostile ants in the core at the last LOD check
        [NonSerialized] public bool LodVisible;
        [NonSerialized] public float MeasuredTerritory = 60f; // territory as last measured while awake
        // Sleeping ants by age: bin b holds ages in [b*bin + AgeBinTimer, (b+1)*bin + AgeBinTimer).
        [NonSerialized] public float[] SleepWorkerAges;
        [NonSerialized] public float[] SleepSoldierAges;
        [NonSerialized] public float AgeBinTimer;

        // --- Player rules (semi-automatic control) ---
        [Range(0f, 0.6f)] public float SoldierRatio = 0.1f;
        [Tooltip("If the queen dies, workers try to raise a new one (costs food and time).")]
        public bool RaiseNewQueen = true;

        // --- One-off commands ---
        public bool RaidActive;
        public Vector2 RaidTarget;

        [NonSerialized] public float LayAccumulator;

        // --- Game ---
        public int Species;
        public bool IsPlayer;
        [Range(0f, 1f)] public float DistressResponse = 0.6f;
        [Tooltip("Share of workers that are scouts: they explore and lay the trails other workers follow.")]
        [Range(0f, 0.3f)] public float ScoutShare = 0.05f;
        [Tooltip("Share of soldiers that are drones: they escort workers along food trails and guard farms.")]
        [Range(0f, 0.5f)] public float DroneShare = 0.1f;
        [Tooltip("Fungus garden level (0 = none). Turns leaves into food.")]
        public int GardenLevel;
        public float Leaves;
        public long LeavesDelivered;
        public int AphidFarms;
        public float FarmIncome;   // food/s from fungus + honeydew stored directly (for the HUD)
        public float GardenIncome;
        [NonSerialized] public float LeafIncome;  // leaf eaters (termites): food/s from leaves eaten directly
        [NonSerialized] public float WoodIncome;  // tree nesters (termites): food/s from their trees' wood
        [NonSerialized] public int ExposedNests;  // tree nesters: nests whose tree has fallen
        [Tooltip("Slave-makers: captured workers not yet hatched (fractions carry over).")]
        public float CaptiveCarry;
        public long Captured;
        [NonSerialized] public float AiTimer;
        [NonSerialized] public float RaidTimer;
        [NonSerialized] public int AiRaidKind; // 0 = war raid / player order, 1 + WildKind = hunt
        [NonSerialized] public int DistressAlerts;

        // --- Nests & swarming ---
        public int NestCount = 1;
        public int Queens = 1;
        [NonSerialized] public int PrimaryNest;
        [Tooltip("Nesting rule: found new nests automatically when the colony is crowded.")]
        public bool AutoSwarm;
        [Tooltip("Nesting rule: swarm when there are more than this many ants per nest.")]
        public int SwarmAt = 1200;
        [Tooltip("Nesting rule: never own more nests than this.")]
        public int MaxNests = 5;
        public int SwarmState;            // 0 none, 1 raising a princess, 2 princess in flight
        public float SwarmProgress;       // 0..1 while raising
        public Vector2 SwarmTarget;
        [NonSerialized] public int SwarmNest = -1;   // nest being founded
        [NonSerialized] public int SwarmParent = -1;
        [NonSerialized] public float SwarmTimer;
        [NonSerialized] public float SwarmCooldown;
        [NonSerialized] public int Squads; // guard squads over all nests
        [NonSerialized] public int Drones;
        [NonSerialized] public int DroneSquads;
        [NonSerialized] public int GuardedTrails; // food sources with a drone squad assigned
        [NonSerialized] public int GuardedFarms;
        [NonSerialized] public int Gardeners;   // gardener-caste ants (speed up the fungus garden)
        [NonSerialized] public int Nurses;      // nurse-caste ants (cheaper larvae)
        [NonSerialized] public int NestSerial;
        public int NestsFounded;
        public int SwarmsFailed;

        // --- Diplomacy (arrays are indexed by the other colony) ---
        public float[] Opinion;    // how this colony feels about each other one: -100 hatred .. 100 friendship
        public float[] TruceLeft;  // seconds of truce left
        public float[] LossesTo;   // own ants recently killed by each colony (fades over a few minutes)
        public float[] WarSince;   // sim time the current war started
        public int TruceOfferFrom = -1; // player only: a rival offering a truce
        public float TruceOfferTime;
        public int StatRaidTarget = -1; // asleep: raiding this (sleeping) colony by numbers
        public float StatRaidLeft;
        /// <summary>Seconds the queen has been alone with no ants, no brood and no food to lay with.</summary>
        public float HopelessTime;
        [NonSerialized] public float[] Contact;       // 0..1 territory overlap with each colony
        [NonSerialized] public float[] FrontLosses;   // ants/s lost in sleeping battles against each colony
        [NonSerialized] public float[] OfferCooldown; // seconds before a truce can be asked of / by this colony again
        [NonSerialized] public float BattleCarry;
        [NonSerialized] public Vector2 DistressAt;

        public int Population => Workers + Soldiers;
        public float BroodTotal => Eggs + Larvae + Pupae;
    }
}
