using System;
using System.Collections.Generic;
using UnityEngine;

namespace AntSim
{
    /// <summary>
    /// A playable ant species, as an asset. Everything is a multiplier on the base tuning.
    /// Species live in Resources/Species; make new ones with Create ▸ AntSim ▸ Species (or duplicate one).
    /// </summary>
    [CreateAssetMenu(menuName = "AntSim/Species", fileName = "NewSpecies")]
    public class SpeciesDef : ScriptableObject
    {
        [Tooltip("Sort order on the start screen.")]
        public int Order;
        public string Name = "New species";
        [TextArea(2, 4)] public string Description = "";
        [Tooltip("Body drawing style in the shader: 0 ellipse, 1 segmented, 2 slender, 3 chunky.")]
        [Range(0, 3)] public int Shape;
        public float Speed = 1f;
        public float Health = 1f;
        public float Damage = 1f;
        public float Sense = 1f;
        public float Lifespan = 1f;
        public float AntCost = 1f;
        public float LayRate = 1f;
        [Tooltip("Food per delivered crumb.")]
        public float ForageYield = 1f;
        [Tooltip("Fungus food per leaf.")]
        public float GardenYield = 1f;
        [Tooltip("Aphid farm output.")]
        public float Honeydew = 1f;
        [Tooltip("Aphid farm price.")]
        public float FarmCost = 1f;
        [Range(0f, 0.6f)] public float SoldierRatio = 0.1f;
        [Tooltip("Fungus garden level at the start.")]
        public int StartGarden;
        [Tooltip("Extra castes: variants of workers or soldiers (at most 3).")]
        public List<MorphDef> Castes = new List<MorphDef>();

        public SpeciesStats Stats => new SpeciesStats
        {
            SpeedMul = Speed, DamageMul = Damage, SenseMul = Sense, HealthMul = Health,
        };

        /// <summary>All species: the assets in Resources/Species, or built-in defaults if there are none.</summary>
        public static SpeciesDef[] All => SpeciesLibrary.All;

        /// <summary>Morph slot (1..3) of an extra caste, in list order; 0 is the plain worker or soldier.</summary>
        public MorphDef Morph(int slot) => slot >= 1 && slot <= Castes.Count && slot <= MorphDef.MaxExtra ? Castes[slot - 1] : null;

        /// <summary>The name of a morph slot, given its base caste.</summary>
        public string MorphName(int slot, Caste baseCaste)
        {
            var m = Morph(slot);
            return m != null ? m.Name : baseCaste == Caste.Soldier ? "Soldier" : "Worker";
        }
    }

    /// <summary>What an extra caste does beyond its numbers.</summary>
    public enum MorphPerk : byte
    {
        None = 0,
        [Tooltip("Stays in the nest and tends the fungus garden: more leaves processed per second.")]
        Gardener = 1,
        [Tooltip("Stays in the nest and tends the brood: larvae need less food.")]
        Nurse = 2,
    }

    /// <summary>An extra caste: a variant of the worker or soldier caste with its own size and numbers.</summary>
    [Serializable]
    public class MorphDef
    {
        public const int MaxExtra = 3; // morph slots 1..3 per species (0 = plain worker/soldier)

        public string Name = "Major";
        [TextArea(1, 3)] public string Description = "";
        [Tooltip("Worker or Soldier: which caste this is a variant of (it behaves like one).")]
        public Caste Base = Caste.Worker;
        [Tooltip("Share of that caste that hatch as this variant.")]
        [Range(0f, 1f)] public float Share = 0.1f;
        [Tooltip("Drawn size, relative to the base caste.")]
        public float Size = 1f;
        public float Speed = 1f;
        public float Health = 1f;
        public float Damage = 1f;
        [Tooltip("Food crumbs carried per trip.")]
        [Range(1, 4)] public int Carry = 1;
        [Tooltip("Never leaves the nest (gardeners, nurses).")]
        public bool StaysHome;
        public MorphPerk Perk;

        public MorphStats Stats => new MorphStats
        {
            SpeedMul = Speed, HealthMul = Health, DamageMul = Damage, Size = Size,
            Carry = (byte)Mathf.Clamp(Carry, 1, 4), StaysHome = (byte)(StaysHome ? 1 : 0),
        };
    }
}
