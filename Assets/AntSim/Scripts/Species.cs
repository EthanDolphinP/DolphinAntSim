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

    /// <summary>Tuning for the other bugs on the map.</summary>
    [Serializable]
    public struct WildlifeParams
    {
        public float MiteHealth, BeetleHealth, SpiderHealth;
        public float MiteDamage, BeetleDamage, SpiderDamage;
        public float MiteSpeed, BeetleSpeed, SpiderSpeed;
        public float MiteSense, BeetleSense, SpiderSense;
        public int MiteFood, BeetleFood, SpiderFood;
        public float MiteLife, BeetleLife, SpiderLife;
        [Tooltip("Animals per 10,000 square units at 'Normal' density.")]
        public float MiteDensity, BeetleDensity, SpiderDensity;
        [Tooltip("Aphids a beetle eats per second when it reaches a farm.")]
        public float BeetleAphidAppetite;

        public static WildlifeParams Default => new WildlifeParams
        {
            MiteHealth = 4f, BeetleHealth = 260f, SpiderHealth = 160f,
            MiteDamage = 0.3f, BeetleDamage = 6f, SpiderDamage = 12f,
            MiteSpeed = 3.5f, BeetleSpeed = 2.2f, SpiderSpeed = 7f,
            MiteSense = 2.5f, BeetleSense = 1.6f, SpiderSense = 14f,
            MiteFood = 5, BeetleFood = 120, SpiderFood = 70,
            MiteLife = 300f, BeetleLife = 900f, SpiderLife = 1200f,
            MiteDensity = 4f, BeetleDensity = 0.25f, SpiderDensity = 0.15f,
            BeetleAphidAppetite = 4f,
        };

        public float Health(int kind) => kind == 0 ? MiteHealth : kind == 1 ? BeetleHealth : SpiderHealth;
        public float Damage(int kind) => kind == 0 ? MiteDamage : kind == 1 ? BeetleDamage : SpiderDamage;
        public float Speed(int kind) => kind == 0 ? MiteSpeed : kind == 1 ? BeetleSpeed : SpiderSpeed;
        public float Sense(int kind) => kind == 0 ? MiteSense : kind == 1 ? BeetleSense : SpiderSense;
        public int Food(int kind) => kind == 0 ? MiteFood : kind == 1 ? BeetleFood : SpiderFood;
        public float Life(int kind) => kind == 0 ? MiteLife : kind == 1 ? BeetleLife : SpiderLife;
        public float Density(int kind) => kind == 0 ? MiteDensity : kind == 1 ? BeetleDensity : SpiderDensity;
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
