using System;
using Unity.Mathematics;
using UnityEngine;

namespace AntSim
{
    /// <summary>Per-colony multipliers the Burst jobs need. Indexed by colony slot.</summary>
    public struct SpeciesStats
    {
        public float SpeedMul;
        public float DamageMul;
        public float SenseMul;
        public float HealthMul;
    }

    /// <summary>Per colony and morph slot ([colony * MaxMorphs + slot]): what the Burst jobs need to know about a caste variant.</summary>
    public struct MorphStats
    {
        public float SpeedMul;
        public float HealthMul;
        public float DamageMul;
        public float Size;
        public byte Carry;
        public byte StaysHome;

        public const int MaxMorphs = 4; // slot 0 = plain worker/soldier, 1..3 = the species' extra castes
        public static MorphStats Plain => new MorphStats { SpeedMul = 1f, HealthMul = 1f, DamageMul = 1f, Size = 1f, Carry = 1 };

        /// <summary>Picks a morph slot for a newborn from cumulative shares (x, y, z for slots 1, 2, 3).</summary>
        public static byte Pick(float4 cumulative, float r) =>
            (byte)(r < cumulative.x ? 1 : r < cumulative.y ? 2 : r < cumulative.z ? 3 : 0);
    }

    /// <summary>One kind of bug's numbers.</summary>
    [Serializable]
    public struct WildStat
    {
        public float Health, Damage, Speed, Sense;
        [Tooltip("Food left by the body.")]
        public int Food;
        [Tooltip("Seconds it lives.")]
        public float Life;
        [Tooltip("Animals per 10,000 square units at 'Normal' density.")]
        public float Density;

        public WildStat(float health, float damage, float speed, float sense, int food, float life, float density)
        {
            Health = health; Damage = damage; Speed = speed; Sense = sense; Food = food; Life = life; Density = density;
        }
    }

    /// <summary>Tuning for the other bugs on the map (see <see cref="WildKind"/>).</summary>
    [Serializable]
    public struct WildlifeParams
    {
        public WildStat Mite, Beetle, Spider, Caterpillar, Cricket, Woodlouse, Antlion, Assassin, Centipede;
        [Tooltip("Aphids a beetle eats per second when it reaches a farm.")]
        public float BeetleAphidAppetite;

        public static WildlifeParams Default => new WildlifeParams
        {
            //                         health damage speed sense food  life  density
            Mite =        new WildStat(4f,   0.3f, 3.5f,  2.5f,   5,  300f, 4f),
            Beetle =      new WildStat(260f, 6f,   2.2f,  1.6f, 120,  900f, 0.25f),
            Spider =      new WildStat(160f, 12f,  7f,   14f,    70, 1200f, 0.12f),
            Caterpillar = new WildStat(45f,  0f,   1.2f,  1.5f,  45,  700f, 0.35f),
            Cricket =     new WildStat(14f,  0.5f, 9f,    5f,    18,  500f, 0.6f),
            Woodlouse =   new WildStat(90f,  0.5f, 1.8f,  1.5f,  30, 1000f, 0.5f),
            Antlion =     new WildStat(120f, 9f,   0f,    3.5f,  50, 1800f, 1.2f), // per 10,000 of sand, not map
            Assassin =    new WildStat(70f,  6f,   3f,    6f,    35,  900f, 0.14f),
            Centipede =   new WildStat(420f, 18f,  9f,   12f,   160, 1500f, 0.04f),
            BeetleAphidAppetite = 4f,
        };

        public WildStat Get(int kind)
        {
            switch (kind)
            {
                case 0: return Mite;
                case 1: return Beetle;
                case 2: return Spider;
                case 3: return Caterpillar;
                case 4: return Cricket;
                case 5: return Woodlouse;
                case 6: return Antlion;
                case 7: return Assassin;
                default: return Centipede;
            }
        }

        public float Health(int kind) => Get(kind).Health;
        public float Damage(int kind) => Get(kind).Damage;
        public float Speed(int kind) => Get(kind).Speed;
        public float Sense(int kind) => Get(kind).Sense;
        public int Food(int kind) => Get(kind).Food;
        public float Life(int kind) => Get(kind).Life;
        public float Density(int kind) => Get(kind).Density;
    }

    /// <summary>Food production tuning: plants, aphid farms and fungus gardens.</summary>
    [Serializable]
    public struct FarmingParams
    {
        [Tooltip("Max leaves in a plant cell.")]
        public int LeafCap;
        [Tooltip("Chance per plant cell per second to regrow one leaf.")]
        public float LeafRegrow;
        public float AphidFarmCost;
        public float AphidCap;
        public float AphidGrowth;          // per second, logistic
        public float HoneydewPerAphid;     // food per aphid per second
        public float AphidLeafAppetite;    // leaves per aphid per second
        public int HoneydewCellCap;        // honeydew pools around the farm up to this per cell
        public float FarmMaxDistance;      // farms must be this close to your nest
        public float GardenLeavesPerLevel; // leaves per second a garden level can process
        public float FoodPerLeaf;
        public float LeafStorePerLevel;    // garden stops collecting when full

        public static FarmingParams Default => new FarmingParams
        {
            LeafCap = 30,
            LeafRegrow = 0.06f,
            AphidFarmCost = 40f,
            AphidCap = 120f,
            AphidGrowth = 0.06f,
            HoneydewPerAphid = 0.035f,
            AphidLeafAppetite = 0.01f,
            HoneydewCellCap = 25,
            FarmMaxDistance = 220f,
            GardenLeavesPerLevel = 1.2f,
            FoodPerLeaf = 1.4f,
            LeafStorePerLevel = 300f,
        };

        public static float GardenCost(int nextLevel) => 50f * nextLevel * (nextLevel + 1) * 0.5f + 30f;
        public const int MaxGarden = 6;
    }

    /// <summary>Choices from the start screen.</summary>
    [Serializable]
    public class GameSetup
    {
        public string ColonyName = "Your colony";
        public int Species;
        public int ColorIndex;
        public int Rivals = 3;
        public int MapSize = 768;
        public int Wildlife = 1;     // 0 none, 1 normal, 2 lots
        public bool Sandbox;         // food respawns, click to drop food, no win/lose
        public int Seed = 12345;
        public int MapStyle = 3;     // see AntSim.MapStyle: 0 meadow, 1 rocky, 2 lakes, 3 mixed
    }

    /// <summary>A herd of aphids on a plant, tended by one colony. Produces honeydew (food) around it.</summary>
    public class AphidFarm
    {
        public int Colony;
        public Vector2 Position;
        public float Aphids;
        public float Eaten; // aphids lost to beetles in the last second (HUD)
    }
}

namespace AntSim
{
    /// <summary>Swarming: raising a princess and founding new nests.</summary>
    [System.Serializable]
    public struct SwarmParams
    {
        public float Cost;            // food to raise a princess
        public float PrincessTime;    // seconds to raise her
        public int MinPopulation;     // colony needs this many ants to swarm
        [UnityEngine.Tooltip("Share of the parent nest's ants (by Role) that leave with the princess, in percent.")]
        public int EscortPercent;
        public int Males;
        public float MaleLife;
        public float MaleSpeed;
        public float PrincessSpeed;
        public float PrincessHealth;
        public float MinSpacing;      // from your own nests
        public float MaxDistance;     // from the nearest of your nests
        public float EnemyClearance;  // from enemy nests
        public float FlightTimeout;
        [UnityEngine.Tooltip("A nest with no queen and almost no ants is abandoned after this many seconds.")]
        public float AbandonTime;
        public int MaxNestsPerColony;

        public static SwarmParams Default => new SwarmParams
        {
            Cost = 250f,
            PrincessTime = 45f,
            MinPopulation = 150,
            EscortPercent = 20,
            Males = 24,
            MaleLife = 25f,
            MaleSpeed = 13f,
            PrincessSpeed = 7f,
            PrincessHealth = 150f,
            MinSpacing = 60f,
            MaxDistance = 320f,
            EnemyClearance = 80f,
            FlightTimeout = 200f,
            AbandonTime = 60f,
            MaxNestsPerColony = 8,
        };
    }
}
