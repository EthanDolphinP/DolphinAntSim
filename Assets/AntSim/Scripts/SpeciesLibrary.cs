using System.Collections.Generic;
using UnityEngine;

namespace AntSim
{
    /// <summary>
    /// Finds the species: every <see cref="SpeciesDef"/> asset in Resources/Species, sorted by Order.
    /// If there are none (e.g. the assets were deleted), the built-in defaults below are used instead.
    /// </summary>
    public static class SpeciesLibrary
    {
        public const string ResourceFolder = "Species";
        static SpeciesDef[] all;

        public static SpeciesDef[] All
        {
            get
            {
                if (all == null || all.Length == 0 || all[0] == null) Load();
                return all;
            }
        }

        /// <summary>Forget the cached list (after editing assets).</summary>
        public static void Reload() => all = null;

        /// <summary>Index of the species with this display name, or -1.</summary>
        public static int IndexOf(string name)
        {
            var list = All;
            for (int i = 0; i < list.Length; i++) if (list[i].Name == name) return i;
            return -1;
        }

        static void Load()
        {
            var found = new List<SpeciesDef>(Resources.LoadAll<SpeciesDef>(ResourceFolder));
            // Built-in species that have no asset (e.g. ones added in an update) are used as they are.
            foreach (var d in CreateDefaults())
                if (!found.Exists(f => f.Name == d.Name)) found.Add(d);
            found.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : string.CompareOrdinal(a.Name, b.Name));
            all = found.ToArray();
        }

        /// <summary>The built-in species (also what the editor menu writes out as assets).</summary>
        public static List<SpeciesDef> CreateDefaults()
        {
            var list = new List<SpeciesDef>();
            SpeciesDef Make(string asset, int order)
            {
                var s = ScriptableObject.CreateInstance<SpeciesDef>();
                s.name = asset;
                s.Order = order;
                list.Add(s);
                return s;
            }

            var harvester = Make("Harvester", 0);
            harvester.Name = "Harvester ants";
            harvester.Description = "Balanced seed gatherers. Foragers bring home 25% more food per trip, and big-headed majors carry double.";
            harvester.Shape = 1; harvester.ForageYield = 1.25f; harvester.SoldierRatio = 0.1f; harvester.Difficulty = 0;
            harvester.Castes.Add(new MorphDef
            {
                Name = "Major", Description = "Big-headed seed crushers: carry two crumbs a trip and bite hard, but slower.",
                Base = Caste.Worker, Share = 0.12f, Size = 1.35f, Speed = 0.85f, Health = 1.6f, Damage = 1.4f, Carry = 2,
            });

            var leaf = Make("Leafcutter", 1);
            leaf.Name = "Leafcutter ants";
            leaf.Description = "Fungus farmers. Start with a fungus garden and get double food per leaf. Tiny minims tend the garden; huge majors defend it.";
            leaf.Shape = 2; leaf.GardenYield = 2f; leaf.StartGarden = 1; leaf.Damage = 0.8f; leaf.Health = 1.1f; leaf.SoldierRatio = 0.08f;
            leaf.Difficulty = 1;
            leaf.Castes.Add(new MorphDef
            {
                Name = "Minim", Description = "Tiny gardeners that never leave the nest: each one speeds up the fungus garden.",
                Base = Caste.Worker, Share = 0.2f, Size = 0.6f, Speed = 0.8f, Health = 0.5f, Damage = 0.5f,
                StaysHome = true, Perk = MorphPerk.Gardener,
            });
            leaf.Castes.Add(new MorphDef
            {
                Name = "Major", Description = "Huge soldiers with scissor jaws: very tough, slow.",
                Base = Caste.Soldier, Share = 0.35f, Size = 1.6f, Speed = 0.75f, Health = 2f, Damage = 1.8f,
            });

            var army = Make("Army", 2);
            army.Name = "Army ants";
            army.Description = "Fast, vicious raiders that live off the hunt. Short lives, poor farmers. Submajors and sabre-jawed majors lead the raids.";
            army.Shape = 1; army.Speed = 1.2f; army.Damage = 1.4f; army.Sense = 1.2f; army.Lifespan = 0.8f; army.LayRate = 1.2f;
            army.GardenYield = 0.5f; army.Honeydew = 0.5f; army.SoldierRatio = 0.25f; army.Difficulty = 1;
            army.Castes.Add(new MorphDef
            {
                Name = "Submajor", Description = "Bigger, harder-biting soldiers that keep up with the column.",
                Base = Caste.Soldier, Share = 0.3f, Size = 1.3f, Speed = 1.05f, Health = 1.4f, Damage = 1.4f,
            });
            army.Castes.Add(new MorphDef
            {
                Name = "Major", Description = "Sabre-jawed giants: few, slow, devastating.",
                Base = Caste.Soldier, Share = 0.1f, Size = 1.7f, Speed = 0.8f, Health = 2.2f, Damage = 2f,
            });
            army.Castes.Add(new MorphDef
            {
                Name = "Porter", Description = "Long-legged workers that haul two crumbs at a time.",
                Base = Caste.Worker, Share = 0.15f, Size = 1.15f, Speed = 1.05f, Health = 1.1f, Carry = 2,
            });

            var dairy = Make("Dairy", 3);
            dairy.Name = "Dairy ants";
            dairy.Description = "Aphid herders. Aphid farms cost half and give double honeydew. Slightly fragile. Nurses at home make brood cheaper.";
            dairy.Shape = 3; dairy.Honeydew = 2f; dairy.FarmCost = 0.5f; dairy.Health = 0.9f; dairy.SoldierRatio = 0.08f;
            dairy.Difficulty = 0;
            dairy.Castes.Add(new MorphDef
            {
                Name = "Nurse", Description = "Stay in the nest feeding the larvae: brood needs less food.",
                Base = Caste.Worker, Share = 0.1f, Size = 0.85f, Speed = 0.8f, Health = 0.8f, Damage = 0.6f,
                StaysHome = true, Perk = MorphPerk.Nurse,
            });

            var fire = Make("Fire", 4);
            fire.Name = "Fire ants";
            fire.Description = "Painful stings: every ant hits 30% harder. Costlier to raise. Quick minims forage; big majors guard.";
            fire.Shape = 0; fire.Damage = 1.3f; fire.Health = 0.95f; fire.AntCost = 1.15f; fire.SoldierRatio = 0.15f;
            fire.Difficulty = 1;
            fire.Castes.Add(new MorphDef
            {
                Name = "Minim", Description = "Small, quick foragers. Fragile.",
                Base = Caste.Worker, Share = 0.3f, Size = 0.75f, Speed = 1.15f, Health = 0.7f, Damage = 0.8f,
            });
            fire.Castes.Add(new MorphDef
            {
                Name = "Major", Description = "Large soldiers with a big sting.",
                Base = Caste.Soldier, Share = 0.25f, Size = 1.45f, Speed = 0.9f, Health = 1.5f, Damage = 1.6f,
            });

            var termite = Make("Termites", 5);
            termite.Name = "Termites";
            termite.Description = "Not ants at all. They can only nest inside trees and live on wood and leaves: each nest eats " +
                                  "its tree, which slowly dies. When it falls they must already have moved to another tree. " +
                                  "Soft-bodied and slow, but they breed fast.";
            termite.Shape = 4; termite.Difficulty = 2;
            termite.TreeNester = true; termite.LeafEater = true; termite.NoFarming = true;
            termite.Speed = 0.75f; termite.Health = 0.8f; termite.Damage = 0.7f; termite.Sense = 0.8f;
            termite.Lifespan = 1.4f; termite.AntCost = 0.75f; termite.LayRate = 1.3f; termite.ForageYield = 0.8f;
            termite.GardenYield = 0.3f; termite.SoldierRatio = 0.14f;
            termite.Castes.Add(new MorphDef
            {
                Name = "Nasute", Description = "Soldiers with a nozzle on the head that squirts glue: quick and sticky.",
                Base = Caste.Soldier, Share = 0.5f, Size = 1.0f, Speed = 1.1f, Health = 0.9f, Damage = 1.2f,
            });
            termite.Castes.Add(new MorphDef
            {
                Name = "Mandible soldier", Description = "Huge-jawed guards that plug the nest. Very slow.",
                Base = Caste.Soldier, Share = 0.3f, Size = 1.55f, Speed = 0.6f, Health = 2.2f, Damage = 2f,
            });

            var amazon = Make("Amazon", 6);
            amazon.Name = "Amazon ants";
            amazon.Description = "Slave-makers. Their sickle jaws are made for war, not work: workers bring home little food. " +
                                 "Every enemy ant they kill brings back a captured pupa that hatches as their worker, " +
                                 "so they must keep fighting to grow.";
            amazon.Shape = 2; amazon.Difficulty = 2;
            amazon.ForageYield = 0.4f; amazon.GardenYield = 0.5f; amazon.Honeydew = 0.6f;
            amazon.Damage = 1.6f; amazon.Health = 1.2f; amazon.Speed = 1.05f; amazon.LayRate = 0.7f;
            amazon.SoldierRatio = 0.35f; amazon.Captives = 0.6f;
            amazon.Castes.Add(new MorphDef
            {
                Name = "Sickle-jaw", Description = "Raiders with long, curved jaws that pierce armour.",
                Base = Caste.Soldier, Share = 0.4f, Size = 1.3f, Speed = 1.05f, Health = 1.3f, Damage = 1.7f,
            });

            var bullet = Make("Bullet", 7);
            bullet.Name = "Bullet ants";
            bullet.Description = "Giants with the most painful sting there is. A queen lays only a few eggs and each ant costs " +
                                 "a lot, so every death hurts. Few, slow-breeding, nearly unbeatable one-on-one.";
            bullet.Shape = 1; bullet.Difficulty = 3;
            bullet.Health = 3.2f; bullet.Damage = 2.6f; bullet.Speed = 0.9f; bullet.Sense = 1.2f;
            bullet.AntCost = 3.5f; bullet.LayRate = 0.35f; bullet.Lifespan = 1.8f; bullet.ForageYield = 1.8f;
            bullet.SoldierRatio = 0.15f;
            return list;
        }
    }
}
