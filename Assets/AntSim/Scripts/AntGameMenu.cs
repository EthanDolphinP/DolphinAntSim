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
        UiKit.Panel background, top, page;
        List<SaveSlot> saves;
        bool showAllSaves;
        string confirmDelete;
        UiStyle title, pageTitle, header, label, small, error, button, buttonOn, card, cardOn, field, toggle, box, notice;
        enum Page { Main, Settings, ConfirmQuit }
        Page pageShown;
        GameSettings.Styles settingsStyles;

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
            cam.backgroundColor = UiTheme.Bg;
            cam.clearFlags = CameraClearFlags.SolidColor;

            GameSettings.Load();
            UpdateCheck.Begin();
            string updateProblem = GameUpdater.Cleanup();
            if (updateProblem != null) message = updateProblem;
            MakeStyles();
            ui = UiKit.Create(transform, "Menu", 20);
            background = ui.CreatePanel("Background", Vector2.zero, Vector2.zero, UiTheme.Bg,
                new RectOffset(0, 0, 0, 0), fitHeight: false);
            background.Rect.anchorMin = Vector2.zero;
            background.Rect.anchorMax = Vector2.one;
            background.Rect.offsetMin = background.Rect.offsetMax = Vector2.zero;
            top = ui.CreatePanel("Top bar", new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), UiTheme.Panel,
                new RectOffset(24, 24, 8, 8), vertical: false, fitHeight: false, border: UiTheme.Line);
            top.Rect.anchorMin = new Vector2(0f, 1f);
            top.Rect.anchorMax = new Vector2(1f, 1f);
            top.Rect.sizeDelta = new Vector2(2f, TopHeight + 1f);
            top.Rect.anchoredPosition = new Vector2(0f, 1f);
            page = ui.CreatePanel("Page", new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), Color.clear,
                new RectOffset(0, 0, 0, 0));
            saves = AntWorld.ListSaves();
        }

        const float TopHeight = 54f;

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
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                var es = UnityEngine.EventSystems.EventSystem.current;
                if (UiKit.AnyInputFocused && es != null) es.SetSelectedGameObject(null);
                else pageShown = pageShown == Page.Main ? Page.ConfirmQuit : Page.Main;
            }
            ui.Scale = Mathf.Clamp(Mathf.Min(Screen.height / 900f, Screen.width / 1100f) + GameSettings.UiScaleOffset, 0.6f, 3f);
            Vector2 v = ui.VirtualSize;
            float w = Mathf.Min(820f, v.x - 24f);
            page.Rect.sizeDelta = new Vector2(w, page.Rect.sizeDelta.y);
            page.Rect.anchoredPosition = new Vector2(0f, -(TopHeight + 14f));

            ui.Begin(top);
            ui.Label("Dolphin Ant Simulator", title);
            ui.Label($"v{Application.version}", small);
            ui.FlexibleSpace();
            if (ui.Button("New game", pageShown == Page.Main ? buttonOn : button, 120)) pageShown = Page.Main;
            if (ui.Button("Settings", pageShown == Page.Settings ? buttonOn : button, 110)) pageShown = Page.Settings;
            if (ui.Button("Quit", pageShown == Page.ConfirmQuit ? buttonOn : button, 80)) pageShown = Page.ConfirmQuit;
            ui.End();

            ui.Begin(page);
            ui.BeginScroll(v.y - TopHeight - 24f);
            if (pageShown == Page.Settings)
            {
                ui.BeginBox(box);
                ui.Label("Settings", pageTitle);
                if (GameSettings.Draw(ui, settingsStyles)) pageShown = Page.Main;
                ui.EndBox();
            }
            else if (pageShown == Page.ConfirmQuit)
            {
                ui.BeginBox(box);
                ui.Label("Quit to desktop?", pageTitle);
                ui.BeginRow();
                if (ui.Button("Quit", buttonOn, 160)) GameSettings.QuitToDesktop();
                if (ui.Button("Cancel", button, 160)) pageShown = Page.Main;
                ui.FlexibleSpace();
                ui.EndRow();
                ui.EndBox();
            }
            else DrawPage();
            ui.Space(10);
            ui.EndScroll();
            ui.End();
        }

        void DrawPage()
        {
            if (UpdateCheck.NewerVersion != null)
            {
                ui.BeginBox(notice);
                ui.BeginRow();
                ui.Label($"<b><color={UiTheme.AccentHex}>A new version is out: {UpdateCheck.NewerVersion}</color></b>  " +
                         $"<color={UiTheme.DimHex}>(you have {Application.version})</color>", label);
                ui.FlexibleSpace();
                var st = GameUpdater.Current;
                bool canUpdate = GameUpdater.Supported && UpdateCheck.ZipUrl != null;
                if (st == GameUpdater.State.Ready)
                {
                    if (ui.Button("Restart now", buttonOn, 140)) GameUpdater.RestartToUpdate();
                }
                else if (st != GameUpdater.State.Working && canUpdate)
                {
                    if (ui.Button(st == GameUpdater.State.Failed ? "Try again" : "Update", buttonOn, 120)) GameUpdater.Begin();
                }
                if (st != GameUpdater.State.Ready && ui.Button("Release page", button, 140)) Application.OpenURL(UpdateCheck.DownloadUrl);
                ui.EndRow();
                if (st != GameUpdater.State.Idle)
                {
                    var statusStyle = st == GameUpdater.State.Failed ? error : small;
                    ui.Label(GameUpdater.Status, statusStyle);
                }
                else if (canUpdate)
                    ui.Label("Update downloads only the files that changed, then restarts the game. Saves and settings are kept.", small);
                ui.EndBox();
                ui.Space(6);
            }
            ui.Label("Raise a colony from a single queen. Find food, farm aphids and fungus, hunt bugs, " +
                     "and outlast the rival colonies.", small);
            if (!string.IsNullOrEmpty(message)) ui.Label(message, error);
            ui.Space(6);

            if (saves.Count > 0)
            {
                ui.BeginBox(box);
                ui.Label("Saved games", header);
                int shown = showAllSaves ? saves.Count : Mathf.Min(3, saves.Count);
                for (int i = 0; i < shown; i++)
                {
                    var s = saves[i];
                    ui.BeginRow();
                    ui.Label($"<b>{s.Name}</b>   <color=#7d8a84>{s.Written:MMM d, HH:mm}</color>\n<color=#7d8a84>{s.Summary}</color>", small);
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
                ui.EndBox();
                ui.Space(10);
            }

            ui.BeginBox(box);
            ui.Label("New game", pageTitle);
            ui.Label("Colony name", header);
            setup.ColonyName = ui.TextField(setup.ColonyName ?? "", 24, field);

            ui.Space(4);
            ui.Label("Species", header);
            var all = SpeciesDef.All;
            for (int i = 0; i < all.Length; i++)
            {
                var sp = all[i];
                if (ui.Button($"<b>{sp.Name}</b>   {DifficultyTag(sp)}\n{sp.Description}\n<color=#7d8a84>{StatLine(sp)}</color>{CasteLine(sp)}",
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
            if (ui.Button("Start", buttonOn)) { ui.EndBox(); StartGame(); return; }
            ui.EndBox();
        }

        static string DifficultyTag(SpeciesDef sp)
        {
            string hex = sp.Difficulty <= 0 ? UiTheme.GoodHex : sp.Difficulty == 1 ? UiTheme.DimHex
                : sp.Difficulty == 2 ? UiTheme.WarnHex : UiTheme.BadHex;
            return $"<size=12><color={hex}>{sp.DifficultyName}</color></size>";
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
            return $"\n<color=#e6c35c>Castes: {string.Join(", ", parts)}</color>";
        }

        void MakeStyles()
        {
            label = UiTheme.Label(15);
            small = UiTheme.Small(13);
            error = UiTheme.Label(14);
            error.Text = UiTheme.Bad;
            title = UiTheme.Title(21);
            title.Wrap = false;
            pageTitle = UiTheme.Title(19);
            header = UiTheme.Section(12);
            header.Height = 28f;
            field = UiTheme.Field(16);
            button = UiTheme.Button(15, 36f);
            buttonOn = UiTheme.On(button);
            card = UiTheme.Card(14);
            cardOn = UiTheme.CardOn(card);
            toggle = UiTheme.Label(15);
            box = UiTheme.Box();
            notice = UiTheme.Box();
            notice.Background = Color.Lerp(UiTheme.Panel, UiTheme.Highlight, 0.7f);
            notice.Border = UiTheme.Accent;
            settingsStyles = new GameSettings.Styles
            {
                Header = header, Label = label, Small = small, Button = button, ButtonOn = buttonOn, Toggle = toggle,
            };
        }
    }
}
