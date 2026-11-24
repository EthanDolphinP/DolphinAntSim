using System.IO;
using AntSim;
using UnityEditor;
using UnityEngine;

/// <summary>Editor helper: writes the built-in species out as assets you can edit.</summary>
public static class AntSimSpeciesAssets
{
    const string Folder = "Assets/AntSim/Resources/" + SpeciesLibrary.ResourceFolder;

    [MenuItem("AntSim/Write Default Species Assets")]
    public static void WriteDefaults()
    {
        Directory.CreateDirectory(Folder);
        int made = 0;
        foreach (var sp in SpeciesLibrary.CreateDefaults())
        {
            string path = $"{Folder}/{sp.name}.asset";
            if (File.Exists(path)) { Object.DestroyImmediate(sp); continue; }
            AssetDatabase.CreateAsset(sp, path);
            made++;
        }
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        SpeciesLibrary.Reload();
        Debug.Log($"SPECIES wrote {made} species assets to {Folder}");
    }
}
