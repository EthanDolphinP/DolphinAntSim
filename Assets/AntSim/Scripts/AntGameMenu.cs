using System.Collections.Generic;
using UnityEngine;

namespace AntSim
{
    /// <summary>
    /// Start screen (Unity UI via <see cref="UiKit"/>): continue or load a saved game, or set up a new one —
    /// name your colony, pick a species and colour, and choose the map.
    /// Creates the <see cref="AntWorld"/> when you press Start.
    /// </summary>
    public class AntGameMenu : MonoBehaviour
    {
        const string PrefKey = "AntSim.LastSetup";

        static readonly int[] MapSizes = { 512, 768, 1024 };
        static readonly string[] MapNames = { "Small", "Medium", "Large" };
        static readonly string[] WildNames = { "None", "Normal", "Lots" };
        static readonly string[] MapStyleInfo =
        {
            "Open grass with a few boulders.",
            "Broken rock ridges: ants must find the gaps, and nests are easier to defend.",
            "Ponds and a river with shallow fords. Water blocks ants; shores are sandy and slow.",
            "A bit of everything: rocks, ponds and sometimes a river.",
        };

        GameSetup setup = new GameSetup();
        string message;

        UiKit ui;
        UiKit.Panel background, page;
        List<SaveSlot> saves;
        bool showAllSaves;
        string confirmDelete;
        UiStyle title, header, label, small, error, button, buttonOn, card, cardOn, field;

        /// <summary>Tears down the current game and shows the start screen again.</summary>
        public static void ReturnToMenu(AntWorld world)
        {
            if (world != null) Destroy(world.gameObject);
            new GameObject("AntGameMenu").AddComponent<AntGameMenu>();
        }

        /// <summary>Replaces the current game (if any) with a saved one. Falls back to the start screen on failure.</summary>
        public static void LoadSave(AntWorld current, string path)
        {
            if (current != null) Destroy(current.gameObject);
            var w = AntWorld.LoadGame(path, out string error);
            if (w == null)
            {
                var menu = new GameObject("AntGameMenu").AddComponent<AntGameMenu>();
                menu.message = error;
            }
        }

        void Awake()
        {
            string json = PlayerPrefs.GetString(PrefKey, "");
            if (!string.IsNullOrEmpty(json))
            {
                try { JsonUtility.FromJsonOverwrite(json, setup); } catch { setup = new GameSetup(); }
            }
            setup.Species = Mathf.Clamp(setup.Species, 0, SpeciesDef.All.Length - 1);
            setup.MapStyle = Mathf.Clamp(setup.MapStyle, 0, AntWorld.MapStyleNames.Length - 1);
            var cam = Camera.main;
            if (cam == null)
            {
                cam = new GameObject("Main Camera").AddComponent<Camera>();
                cam.tag = "MainCamera";
                cam.orthographic = true;
            }
            cam.backgroundColor = new Color(0.04f, 0.04f, 0.05f);
            cam.clearFlags = CameraClearFlags.SolidColor;

            MakeStyles();
            ui = UiKit.Create(transform, "Menu", 20);
            background = ui.CreatePanel("Background", Vector2.zero, Vector2.zero, new Color(0.05f, 0.05f, 0.07f, 1f),
                new RectOffset(0, 0, 0, 0), fitHeight: false);
            background.Rect.anchorMin = Vector2.zero;
            background.Rect.anchorMax = Vector2.one;
            background.Rect.offsetMin = background.Rect.offsetMax = Vector2.zero;
            page = ui.CreatePanel("Page", new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), Color.clear,
                new RectOffset(0, 0, 0, 0));
            saves = AntWorld.ListSaves();
        }

        void StartGame()
        {
            setup.Seed = Random.Range(1, int.MaxValue);
            PlayerPrefs.SetString(PrefKey, JsonUtility.ToJson(setup));
            AntWorld.Create(setup);
            Destroy(gameObject);
        }

        void Load(string path)
        {
            Destroy(gameObject);
            LoadSave(null, path);
        }

        void LateUpdate()
        {
            ui.Scale = Mathf.Clamp(Mathf.Min(Screen.height / 900f, Screen.width / 1100f), 0.6f, 3f);
            Vector2 v = ui.VirtualSize;
            float w = Mathf.Min(760f, v.x - 20f);
            page.Rect.sizeDelta = new Vector2(w, page.Rect.sizeDelta.y);
            page.Rect.anchoredPosition = new Vector2(0f, -10f);

            ui.Begin(page);
            ui.BeginScroll(v.y - 20f);
            DrawPage();
            ui.EndScroll();
            ui.End();
        }

        void DrawPage()
        {
            ui.BeginRow();
            ui.Label("Dolphin Ant Simulator", title);
            if (ui.Button("Start", buttonOn, 120)) { StartGame(); return; }
            ui.EndRow();
            ui.Label("Raise a colony from a single queen. Find food, farm aphids and fungus, hunt bugs, " +
                     "and outlast the rival colonies.", small);
            if (!string.IsNullOrEmpty(message)) ui.Label(message, error);

            if (saves.Count > 0)
            {
                ui.Space(8);
                ui.Label("Saved games", header);
                int shown = showAllSaves ? saves.Count : Mathf.Min(3, saves.Count);
                for (int i = 0; i < shown; i++)
                {
                    var s = saves[i];
                    ui.BeginRow();
                    ui.Label($"<b>{s.Name}</b>   <color=#9fb0c8>{s.Written:MMM d, HH:mm}</color>\n<color=#9fb0c8>{s.Summary}</color>", small);
                    if (ui.Button(i == 0 ? "Continue" : "Load", i == 0 ? buttonOn : button, 100)) { Load(s.Path); return; }
                    bool sure = confirmDelete == s.Path;
                    if (ui.Button(sure ? "Sure?" : "Delete", button, 70))
                    {
                        if (sure) { AntWorld.DeleteSave(s.Path); saves = AntWorld.ListSaves(); confirmDelete = null; }
                        else confirmDelete = s.Path;
                    }
                    ui.EndRow();
                }
                if (saves.Count > 3 && ui.Button(showAllSaves ? "Show fewer" : $"Show all {saves.Count} saves", button))
                    showAllSaves = !showAllSaves;
            }

            ui.Space(10);
            ui.Label("New game", title);
            ui.Label("Colony name", header);
            setup.ColonyName = ui.TextField(setup.ColonyName ?? "", 24, field);

            ui.Space(4);
            ui.Label("Species", header);
            var all = SpeciesDef.All;
            for (int i = 0; i < all.Length; i++)
            {
                var sp = all[i];
                if (ui.Button($"<b>{sp.Name}</b>\n{sp.Description}\n<color=#9fb0c8>{StatLine(sp)}</color>{CasteLine(sp)}",
                        setup.Species == i ? cardOn : card))
                    setup.Species = i;
            }

            ui.Space(4);
            ui.Label("Colour", header);
            ui.BeginRow();
            for (int i = 0; i < AntWorld.Palette.Length; i++)
                if (ui.Swatch(AntWorld.Palette[i], setup.ColorIndex == i, 44f)) setup.ColorIndex = i;
            ui.FlexibleSpace();
            ui.EndRow();

            ui.Space(4);
            ui.Label($"Rival colonies: {setup.Rivals}", header);
            setup.Rivals = Mathf.RoundToInt(ui.Slider(setup.Rivals, 1, 6));

            ui.Space(4);
            ui.Label("Map size", header);
            ui.BeginRow();
            for (int i = 0; i < MapSizes.Length; i++)
                if (ui.Button($"{MapNames[i]} ({MapSizes[i]})", setup.MapSize == MapSizes[i] ? buttonOn : button))
                    setup.MapSize = MapSizes[i];
            ui.EndRow();

            ui.Space(4);
            ui.Label("Terrain", header);
            ui.BeginRow();
            for (int i = 0; i < AntWorld.MapStyleNames.Length; i++)
                if (ui.Button(AntWorld.MapStyleNames[i], setup.MapStyle == i ? buttonOn : button)) setup.MapStyle = i;
            ui.EndRow();
            ui.Label(MapStyleInfo[Mathf.Clamp(setup.MapStyle, 0, MapStyleInfo.Length - 1)] +
                     " A new map is made every game; nests always start in a clearing and can reach each other.", small);

            ui.Space(4);
            ui.Label("Wildlife (mites, beetles, spiders)", header);
            ui.BeginRow();
            for (int i = 0; i < WildNames.Length; i++)
                if (ui.Button(WildNames[i], setup.Wildlife == i ? buttonOn : button)) setup.Wildlife = i;
            ui.EndRow();

            ui.Space(4);
            ui.Label("Mode", header);
            ui.BeginRow();
            if (ui.Button("Survival", !setup.Sandbox ? buttonOn : button)) setup.Sandbox = false;
            if (ui.Button("Sandbox", setup.Sandbox ? buttonOn : button)) setup.Sandbox = true;
            ui.EndRow();
            ui.Label(setup.Sandbox
                ? "Food keeps appearing, you can drop food with the mouse and command any colony. No win or lose."
                : "Only a little food at the start. Hunt, farm aphids and grow fungus to survive. " +
                  "Win by outlasting every rival colony.", small);

            ui.Space(12);
            if (ui.Button("Start", buttonOn)) { StartGame(); return; }
            ui.Space(10);
        }

        static string StatLine(SpeciesDef sp)
        {
            string s = "";
            void Add(string n, float v) { if (Mathf.Abs(v - 1f) > 0.01f) s += $"{n} {(v > 1f ? "+" : "")}{(v - 1f) * 100f:0}%   "; }
            Add("Speed", sp.Speed);
            Add("Health", sp.Health);
            Add("Bite", sp.Damage);
            Add("Lifespan", sp.Lifespan);
            Add("Cost", sp.AntCost);
            Add("Laying", sp.LayRate);
            Add("Forage", sp.ForageYield);
            Add("Fungus", sp.GardenYield);
            Add("Honeydew", sp.Honeydew);
            Add("Farm price", sp.FarmCost);
            return s.Length == 0 ? "No modifiers" : s.TrimEnd();
        }

        static string CasteLine(SpeciesDef sp)
        {
            if (sp.Castes == null || sp.Castes.Count == 0) return "";
            var parts = new List<string>();
            for (int m = 1; m <= MorphDef.MaxExtra; m++)
            {
                var d = sp.Morph(m);
                if (d == null) continue;
                parts.Add($"{d.Name} ({d.Share:P0} of {(d.Base == Caste.Soldier ? "soldiers" : "workers")})");
            }
            return $"\n<color=#d8c48a>Castes: {string.Join(", ", parts)}</color>";
        }

        void MakeStyles()
        {
            var text = new Color(0.92f, 0.92f, 0.94f);
            label = new UiStyle { FontSize = 15, Text = text };
            small = new UiStyle { FontSize = 13, Text = new Color(0.68f, 0.70f, 0.75f) };
            error = new UiStyle { FontSize = 14, Text = new Color(1f, 0.5f, 0.45f) };
            title = new UiStyle { FontSize = 30, FontStyle = FontStyle.Bold, Text = text };
            header = new UiStyle { FontSize = 16, FontStyle = FontStyle.Bold, Text = text, Height = 28f, Align = TextAnchor.LowerLeft };
            field = new UiStyle { FontSize = 16, Text = text };
            button = new UiStyle
            {
                FontSize = 15, Text = text, Align = TextAnchor.MiddleCenter, Wrap = false, Height = 34f,
                Background = new Color(0.20f, 0.20f, 0.25f), Hover = new Color(0.22f, 0.32f, 0.50f), Pressed = new Color(0.30f, 0.45f, 0.70f),
            };
            buttonOn = button.Copy();
            buttonOn.FontStyle = FontStyle.Bold;
            buttonOn.Background = buttonOn.Hover = new Color(0.30f, 0.45f, 0.70f);
            card = new UiStyle
            {
                FontSize = 14, Text = text, Align = TextAnchor.UpperLeft, Wrap = true,
                Background = new Color(0.12f, 0.12f, 0.15f), Hover = new Color(0.20f, 0.20f, 0.25f), Pressed = new Color(0.22f, 0.32f, 0.50f),
                Padding = new RectOffset(10, 10, 8, 8),
            };
            cardOn = card.Copy();
            cardOn.Background = cardOn.Hover = new Color(0.22f, 0.32f, 0.50f);
        }
    }
}
