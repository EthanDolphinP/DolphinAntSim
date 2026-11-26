using System.Collections.Generic;
using UnityEngine;

namespace AntSim
{
    /// <summary>
    /// In-game HUD, drawn with Unity UI (see <see cref="UiKit"/>). Scales with the screen, never overflows it:
    ///  - a slim top bar with your colony's vital signs and quick controls
    ///  - an alert strip (ants dying, raids, messages)
    ///  - a draggable, tabbed panel (Colonies / Orders / Nests / Farming / Diplomacy / Game / Help) whose body scrolls
    ///  - a win/lose dialog
    /// UI scale is automatic but can be nudged with the A- / A+ buttons (remembered between sessions).
    /// </summary>
    public class AntSimHud : MonoBehaviour
    {
        public AntWorld world;

        enum Tab { Colonies, Orders, Nests, Farming, Diplomacy, Game, Help, Defence }

        const float BarHeight = 50f;
        const float PanelWidth = 460f;

        static bool panelVisible = true;
        static AntSimHud instance;

        /// <summary>A text box has keyboard focus: game hotkeys are off.</summary>
        public static bool IsTyping { get; set; }

        /// <summary>The Esc menu is open: game hotkeys and camera keys are off.</summary>
        public static bool MenuOpen => instance != null && instance.menuOpen;

        public static bool IsMouseOverUi() => UiKit.PointerOverUi;

        UiKit ui;
        UiKit.Panel bar, alert, panel, dialog, shade, menu;
        enum MenuPage { Main, Settings, ConfirmMenu, ConfirmDesktop }
        bool menuOpen;
        MenuPage menuPage;
        bool pausedBeforeMenu;
        GameSettings.Styles settingsStyles;
        Tab tab = Tab.Orders;
        bool tabChanged;
        float fpsSmoothed = 60f;
        string saveName;
        string confirmDelete;
        List<SaveSlot> saves;
        float savesListedAt = -100f;

        UiStyle barLabel, barLabelSmall, barTitle, label, labelSmall, header, bigTitle, button, tabOn, tabOff, rowStyle, rowFocused, toggle, field,
            menuButton, menuButtonOn, statBox, statKey, statValue;

        void Awake()
        {
            instance = this;
            GameSettings.Load();
            MakeStyles();
            ui = UiKit.Create(transform, "HUD", 10);
            // Top bar across the screen (as Dolphin City Builder's): only its bottom edge shows a line.
            bar = ui.CreatePanel("Top bar", new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), UiTheme.PanelAlpha(0.97f),
                new RectOffset(18, 18, 5, 5), vertical: false, fitHeight: false, border: UiTheme.Line);
            bar.Body.Group.spacing = 6f;
            bar.Rect.anchorMin = new Vector2(0f, 1f);
            bar.Rect.anchorMax = new Vector2(1f, 1f);
            bar.Rect.sizeDelta = new Vector2(2f, BarHeight + 1f);
            bar.Rect.anchoredPosition = new Vector2(0f, 1f);
            alert = ui.CreatePanel("Alerts", new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                Color.Lerp(UiTheme.Panel, UiTheme.Bad, 0.16f), new RectOffset(12, 12, 6, 6), vertical: false,
                border: UiTheme.Bad, radius: UiTheme.TileRadius);
            panel = ui.CreatePanel("Panel", new Vector2(0f, 1f), new Vector2(0f, 1f), UiTheme.PanelAlpha(0.96f),
                new RectOffset(14, 14, 12, 14), draggable: true, border: UiTheme.Line, radius: UiTheme.PanelRadius);
            panel.Rect.anchoredPosition = new Vector2(12f, -(BarHeight + 40f));
            dialog = ui.CreatePanel("Dialog", new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), UiTheme.Panel,
                new RectOffset(18, 18, 16, 16), border: UiTheme.Accent, radius: UiTheme.PanelRadius);
            // Esc menu: a dark full-screen layer that swallows clicks, with the menu box on top.
            shade = ui.CreatePanel("Menu shade", Vector2.zero, Vector2.zero, new Color(UiTheme.Bg.r, UiTheme.Bg.g, UiTheme.Bg.b, 0.7f),
                new RectOffset(0, 0, 0, 0), fitHeight: false);
            shade.Rect.anchorMin = Vector2.zero;
            shade.Rect.anchorMax = Vector2.one;
            shade.Rect.offsetMin = shade.Rect.offsetMax = Vector2.zero;
            menu = ui.CreatePanel("Menu", new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), UiTheme.Panel,
                new RectOffset(20, 20, 16, 18), border: UiTheme.Line, radius: UiTheme.PanelRadius);
            alert.Visible = false;
            dialog.Visible = false;
            shade.Visible = false;
            menu.Visible = false;
        }

        /// <summary>Opens or closes the Esc menu (pausing the game while it's open, if the setting says so).</summary>
        public void SetMenu(bool open)
        {
            if (open == menuOpen || world == null) return;
            menuOpen = open;
            menuPage = MenuPage.Main;
            if (open)
            {
                pausedBeforeMenu = world.Paused;
                if (GameSettings.PauseInMenu) world.Paused = true;
            }
            else if (GameSettings.PauseInMenu) world.Paused = pausedBeforeMenu;
        }

        void OnDestroy()
        {
            if (instance == this) { instance = null; IsTyping = false; }
        }

        void Update()
        {
            float fps = 1f / Mathf.Max(Time.unscaledDeltaTime, 1e-4f);
            fpsSmoothed = Mathf.Lerp(fpsSmoothed, fps, 0.05f);
            IsTyping = UiKit.AnyInputFocused;
            if (world == null) return;
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                // Esc backs out one step: a text box, a menu sub-page, the menu, placing mode; otherwise opens the menu.
                var es = UnityEngine.EventSystems.EventSystem.current;
                if (IsTyping && es != null) es.SetSelectedGameObject(null);
                else if (menuOpen) { if (menuPage != MenuPage.Main) menuPage = MenuPage.Main; else SetMenu(false); }
                else if (world.PlacingFarm || world.PlacingNest || world.PlacingDefence >= 0 || world.PlacingMove >= 0)
                {
                    world.PlacingFarm = world.PlacingNest = false;
                    world.PlacingDefence = -1;
                    world.PlacingMove = -1;
                }
                else SetMenu(true);
            }
            if (!IsTyping && !menuOpen && Input.GetKeyDown(KeyCode.H)) panelVisible = !panelVisible;
        }

        float AutoScale()
        {
            // Designed for roughly 1100 x 900 "virtual" pixels; shrink or grow to match the real screen.
            float s = Mathf.Min(Screen.height / 900f, Screen.width / 1100f);
            return Mathf.Clamp(s + GameSettings.UiScaleOffset, 0.6f, 3f);
        }

        void LateUpdate()
        {
            if (world == null || world.Colonies == null) return;
            ui.Scale = AutoScale();
            Vector2 v = ui.VirtualSize;

            ui.Begin(bar); DrawTopBar(); ui.End();
            DrawAlerts(v.x);

            panel.Visible = panelVisible;
            if (panelVisible)
            {
                float w = Mathf.Min(PanelWidth, v.x - 20f);
                panel.Rect.sizeDelta = new Vector2(w, panel.Rect.sizeDelta.y);
                // Keep the panel on screen.
                var p = panel.Rect.anchoredPosition;
                p.x = Mathf.Clamp(p.x, 0f, Mathf.Max(0f, v.x - w));
                p.y = Mathf.Clamp(p.y, -(v.y - 80f), -(BarHeight + 4f));
                panel.Rect.anchoredPosition = p;
                ui.Begin(panel);
                DrawPanel(v.y + p.y);
                ui.End();
            }
            DrawGameOver(v);
            DrawMenu(v);
        }

        // ------------------------------------------------------------------ Esc menu

        void DrawMenu(Vector2 v)
        {
            shade.Visible = menuOpen;
            menu.Visible = menuOpen;
            if (!menuOpen) return;
            shade.Rect.SetAsLastSibling();
            menu.Rect.SetAsLastSibling();
            float w = Mathf.Min(menuPage == MenuPage.Settings ? 520f : 360f, v.x - 20f);
            menu.Rect.sizeDelta = new Vector2(w, menu.Rect.sizeDelta.y);
            ui.Begin(menu);
            switch (menuPage)
            {
                case MenuPage.Main:
                    ui.Label("Paused", bigTitle);
                    ui.Label(world.Sandbox ? "Sandbox game" : $"{Me.Name}, {FormatTime(world.SimTime)}", labelSmall);
                    ui.Space(6);
                    if (ui.Button("Resume  [Esc]", menuButtonOn)) SetMenu(false);
                    if (ui.Button("Quicksave  [F5]", menuButton))
                        world.Notify(world.SaveGame("Quicksave") ?? "Game saved (Quicksave).");
                    if (ui.Button("Save / load games...", menuButton)) { tab = Tab.Game; tabChanged = true; panelVisible = true; SetMenu(false); }
                    if (ui.Button("Settings", menuButton)) menuPage = MenuPage.Settings;
                    if (ui.Button("Help and controls", menuButton)) { tab = Tab.Help; tabChanged = true; panelVisible = true; SetMenu(false); }
                    ui.Space(8);
                    if (ui.Button("Quit to main menu", menuButton)) menuPage = MenuPage.ConfirmMenu;
                    if (ui.Button("Quit to desktop", menuButton)) menuPage = MenuPage.ConfirmDesktop;
                    if (!string.IsNullOrEmpty(world.Toast) && Time.unscaledTime - world.ToastTime < 5f) ui.Label(world.Toast, labelSmall);
                    break;
                case MenuPage.ConfirmMenu:
                case MenuPage.ConfirmDesktop:
                {
                    bool desktop = menuPage == MenuPage.ConfirmDesktop;
                    ui.Label(desktop ? "Quit to desktop?" : "Quit to the main menu?", header);
                    ui.Label(world.State == GameState.Playing
                        ? "Save first to carry on later; anything since your last save is lost otherwise."
                        : "This game is over.", labelSmall);
                    ui.Space(6);
                    if (world.State == GameState.Playing && ui.Button("Save and quit", menuButtonOn))
                    {
                        string err = world.SaveGame("Quicksave");
                        if (err != null) { world.Notify(err); menuPage = MenuPage.Main; }
                        else { ui.End(); Leave(desktop); return; }
                    }
                    if (ui.Button("Quit without saving", menuButton)) { ui.End(); Leave(desktop); return; }
                    if (ui.Button("Cancel", menuButton)) menuPage = MenuPage.Main;
                    break;
                }
                case MenuPage.Settings:
                    ui.Label("Settings", bigTitle);
                    ui.BeginScroll(Mathf.Max(200f, v.y - 140f));
                    bool back = GameSettings.Draw(ui, settingsStyles);
                    ui.EndScroll();
                    if (back) menuPage = MenuPage.Main;
                    break;
            }
            ui.End();
        }

        void Leave(bool desktop)
        {
            menuOpen = false;
            if (desktop) GameSettings.QuitToDesktop();
            else AntGameMenu.ReturnToMenu(world);
        }

        ColonyInfo Me => world.Colonies[world.CommandColony];

        // ------------------------------------------------------------------ top bar

        void DrawTopBar()
        {
            var me = Me;
            string hex = ColorUtility.ToHtmlStringRGB(me.Color);
            ui.Label($"<color=#{hex}>\u25CF</color> {me.Name}", barTitle);
            ui.Space(6);
            ui.Stat("Ants", $"{me.Population:N0}", statBox, statKey, statValue, 84);
            string foodCol = me.Starving ? UiTheme.BadHex : me.Food < 100f ? UiTheme.WarnHex : UiTheme.BrightHex;
            ui.Stat("Food", $"<color={foodCol}>{me.Food:N0}</color>", statBox, statKey, statValue, 84);
            if (!me.HasQueen) ui.Stat("Queen", $"<color={UiTheme.BadHex}>none</color>", statBox, statKey, statValue, 84);
            else if (me.NestCount > 1) ui.Stat("Nests", $"{me.NestCount} <size=11><color={UiTheme.DimHex}>{me.Queens} q</color></size>", statBox, statKey, statValue, 84);
            else ui.Stat("Queen", $"{me.QueenHealth:0} <size=11><color={UiTheme.DimHex}>hp</color></size>", statBox, statKey, statValue, 84);
            ui.Stat("Time", FormatTime(world.SimTime), statBox, statKey, statValue, 72);
            ui.Space(4);
            var notes = new System.Text.StringBuilder();
            if (me.SwarmState == 2) notes.Append($"<color={UiTheme.AccentHex}>Princess in flight</color>\n");
            if (world.SimTime < world.PeacefulStart)
                notes.Append($"<color={UiTheme.GoodHex}>Rivals peaceful for {FormatTime(world.PeacefulStart - world.SimTime)}</color>\n");
            if (GameSettings.ShowStats)
                notes.Append($"{world.TotalAnts:N0} ants total, {world.AntCount:N0} live\nFPS {fpsSmoothed:0}  tick {world.LastTickMs:0.0} ms");
            ui.Label(notes.ToString().TrimEnd('\n'), barLabelSmall);

            ui.FlexibleSpace();

            if (ui.Button(world.Paused ? "Play" : "Pause", button, 64)) world.Paused = !world.Paused;
            if (ui.Button("-", button, 28)) world.simSpeed = Mathf.Max(world.simSpeed * 0.5f, 0.125f);
            ui.Label($"{world.simSpeed:0.##}x", barLabel, 44);
            if (ui.Button("+", button, 28)) world.simSpeed = Mathf.Min(world.simSpeed * 2f, 16f);
            ui.Space(8);
            if (ui.Button(panelVisible ? "Hide panel" : "Show panel", button, 92)) panelVisible = !panelVisible;
            ui.Space(8);
            if (ui.Button("A-", button, 32)) SetScaleOffset(GameSettings.UiScaleOffset - 0.1f);
            if (ui.Button("A+", button, 32)) SetScaleOffset(GameSettings.UiScaleOffset + 0.1f);
            ui.Space(8);
            if (ui.Button("Menu  [Esc]", button, 96)) SetMenu(true);
        }

        static void SetScaleOffset(float v)
        {
            GameSettings.UiScaleOffset = GameSettings.ClampScaleOffset(v);
            GameSettings.Save();
        }

        // ------------------------------------------------------------------ alerts

        void DrawAlerts(float vw)
        {
            var me = Me;
            int c = world.CommandColony;
            bool dying = me.DistressAlerts >= 2;
            bool raided = false;
            string raider = "";
            Vector2 raidedAt = me.NestPosition;
            for (int o = 0; o < world.Colonies.Length; o++)
            {
                var other = world.Colonies[o];
                if (o == c || !other.RaidActive || other.AiRaidKind != 0) continue;
                int near = world.NearestNest(c, other.RaidTarget, false);
                if (near >= 0 && Vector2.Distance(other.RaidTarget, world.GetNest(near).Position) < 40f)
                { raided = true; raider = other.Name; raidedAt = world.GetNest(near).Position; }
            }
            bool toast = !string.IsNullOrEmpty(world.Toast) && Time.unscaledTime - world.ToastTime < 5f;
            bool placing = world.PlacingFarm || world.PlacingNest || world.PlacingDefence >= 0 || world.PlacingMove >= 0;

            alert.Visible = dying || raided || toast || placing;
            if (!alert.Visible) return;
            float w = Mathf.Min(720f, vw - 20f);
            alert.Rect.sizeDelta = new Vector2(w, alert.Rect.sizeDelta.y);
            alert.Rect.anchoredPosition = new Vector2(0f, -(BarHeight + 4f));

            ui.Begin(alert);
            if (world.PlacingMove >= 0)
            {
                var moving = world.GetNest(world.PlacingMove);
                ui.Label(world.SpeciesOf(c).TreeNester
                    ? $"Click a healthy tree within {AntWorld.MoveMaxDistance:0} of nest {moving?.Number} to move there ({AntWorld.MoveCost:0} food). Esc to cancel."
                    : $"Click the new site for nest {moving?.Number}, within {AntWorld.MoveMaxDistance:0} of it ({AntWorld.MoveCost:0} food). Esc to cancel.",
                    labelSmall);
            }
            else if (world.PlacingDefence >= 0)
            {
                int kind = world.PlacingDefence;
                ui.Label(kind == (int)DefenceKind.Fortified
                    ? $"Click one of your nests to fortify it ({world.DefenceCost(kind):0} food). Esc to cancel."
                    : $"Click near your nests to build a {AntWorld.DefenceNames[kind].ToLower()} ({world.DefenceCost(kind):0} food). " +
                      "Shift-click to build several. Esc to cancel.", labelSmall);
            }
            else if (world.PlacingNest && world.SpeciesOf(c).TreeNester)
                ui.Label($"Click a healthy tree for the new nest ({world.swarm.MinSpacing:0}-{world.swarm.MaxDistance:0} from your nests, " +
                         $"away from rivals). Costs {world.swarm.Cost:0} food. Esc to cancel.", labelSmall);
            else if (world.PlacingNest)
                ui.Label($"Click where the new nest should go ({world.swarm.MinSpacing:0}-{world.swarm.MaxDistance:0} from your nests, " +
                         $"away from rivals, not on rock or water). Costs {world.swarm.Cost:0} food. Esc to cancel.", labelSmall);
            else if (placing)
                ui.Label($"Click a green bush near your nest to place an aphid farm ({world.FarmCost(c):0} food). " +
                         "Shift-click to place several. Esc to cancel.", labelSmall);
            else if (raided)
            {
                ui.Label($"<color=#e07a6a><b>{raider} is raiding your nest!</b></color>", label);
                if (ui.Button("Go", button, 48)) world.LookAt(raidedAt);
            }
            else if (dying)
            {
                ui.Label($"<color=#e07a6a><b>Your ants are dying</b></color> ({me.DistressAlerts} recent deaths). " +
                         "Soldiers can smell it and are on their way.", labelSmall);
                if (ui.Button("Go", button, 48)) world.LookAt(me.DistressAt);
                if (ui.Button("Send all", button, 80)) world.OrderRaid(c, me.DistressAt);
            }
            else if (toast)
                ui.Label(world.Toast, labelSmall);
            ui.End();
        }

        // ------------------------------------------------------------------ panel

        void DrawPanel(float spaceBelowTop)
        {
            ui.BeginRow();
            TabButton(Tab.Colonies, "Colonies");
            TabButton(Tab.Orders, "Orders");
            TabButton(Tab.Nests, "Nests");
            TabButton(Tab.Farming, "Farming");
            ui.EndRow();
            ui.BeginRow();
            TabButton(Tab.Defence, "Defence");
            TabButton(Tab.Diplomacy, "Diplomacy");
            TabButton(Tab.Game, "Game");
            TabButton(Tab.Help, "Help");
            ui.EndRow();
            ui.Space(4);

            // The body scrolls if it would run off the bottom of the screen.
            ui.BeginScroll(Mathf.Max(80f, spaceBelowTop - 110f), tabChanged);
            tabChanged = false;
            switch (tab)
            {
                case Tab.Colonies: DrawColonies(); break;
                case Tab.Orders: DrawOrders(); break;
                case Tab.Nests: DrawNests(); break;
                case Tab.Farming: DrawFarming(); break;
                case Tab.Defence: DrawDefence(); break;
                case Tab.Diplomacy: DrawDiplomacy(); break;
                case Tab.Game: DrawGame(); break;
                case Tab.Help: DrawHelp(); break;
            }
            ui.EndScroll();
        }

        void TabButton(Tab t, string text)
        {
            if (ui.Button(text, tab == t ? tabOn : tabOff))
            {
                tab = t;
                tabChanged = true;
                confirmDelete = null;
            }
        }

        void DrawColonies()
        {
            ui.Label(world.Sandbox
                ? "Click a colony to select it and jump to it. Orders apply to the selected colony."
                : "Click a colony to jump to it.", labelSmall);
            for (int c = 0; c < world.Colonies.Length; c++)
            {
                var col = world.Colonies[c];
                string hex = ColorUtility.ToHtmlStringRGB(col.Color);

                string status;
                if (col.Extinct) status = "<color=#56605b>EXTINCT</color>";
                else if (col.HasQueen) status = col.NestCount > 1 ? $"{col.NestCount} nests, {col.Queens} queens" : $"Queen {col.QueenHealth:0} hp";
                else if (col.NewQueenProgress > 0f) status = $"<color=orange>NO QUEEN</color> - raising new {col.NewQueenProgress:P0}";
                else status = "<color=#e07a6a>NO QUEEN</color>";
                if (col.Starving) status += "  <color=#e07a6a>STARVING</color>";
                if (col.BroodStarving) status += "  <color=#f0a05a>larvae hungry</color>";
                if (col.RaidActive) status += col.AiRaidKind > 0 ? "  <color=orange>hunting</color>" : "  <color=orange>raiding</color>";
                if (col.SwarmState == 1) status += "  <color=#e6c35c>raising a princess</color>";
                if (col.SwarmState == 2) status += "  <color=#e6c35c>swarming</color>";
                if (col.Abstract) status += "  <color=#7fb8ff>asleep (off-screen)</color>";
                if (col.FrontLosses != null)
                {
                    float front = 0f;
                    foreach (float x in col.FrontLosses) front += x;
                    if (front > 0f) status += "  <color=#e07a6a>border battle</color>";
                }
                int wars = 0;
                for (int o = 0; o < world.Colonies.Length; o++)
                    if (o != c && !world.Colonies[o].Extinct && world.IsAtWar(c, o)) wars++;
                if (wars > 0 && !col.Extinct) status += $"  <color=#e07a6a>{wars} war{(wars > 1 ? "s" : "")}</color>";

                string you = col.IsPlayer ? "  <color=#ffffff>(you)</color>" : "";
                string text =
                    $"<b><color=#{hex}>{col.Name}</color></b>{you}   <color=#7d8a84>{world.SpeciesOf(c).Name}</color>\n" +
                    $"Workers {col.Workers:N0}   Soldiers {col.Soldiers:N0}   Food {col.Food:N0}\n" +
                    $"Brood {col.Eggs:N0} / {col.Larvae:N0} / {col.Pupae:N0}   Garden {col.GardenLevel}   Farms {col.AphidFarms}   Lost {col.CombatDeaths:N0}\n" +
                    status;

                if (ui.Button(text, c == world.FocusColony ? rowFocused : rowStyle))
                {
                    world.FocusColony = c;
                    world.LookAt(col.NestPosition);
                }
            }
            ui.Label("Brood = eggs / larvae / pupae.  Lost = ants killed in battle.", labelSmall);
            var bugs = new System.Text.StringBuilder("Prey: ");
            for (int k = 0; k < WildKinds.Count; k++)
            {
                if (WildKinds.IsPredator(k)) continue;
                bugs.Append($"{world.WildCount((WildKind)k)} {WildKinds.Plural((WildKind)k)}, ");
            }
            bugs.Length -= 2;
            bugs.Append(".  Predators: ");
            foreach (var k in WildKinds.Predators) bugs.Append($"{world.WildCount(k)} {WildKinds.Plural(k)}, ");
            bugs.Length -= 2;
            int living = 0, dead = 0;
            foreach (var t in world.Trees) if (t.Dead) dead++; else living++;
            ui.Label($"{bugs}.\nTrees: {living} living, {dead} dead.  Food on the ground: {world.MapFood:N0}", labelSmall);
            var cover = world.TerrainCover;
            ui.Label($"Map: {AntWorld.MapStyleNames[Mathf.Clamp(world.setup.MapStyle, 0, 3)]}, {cover.x:P0} rock, " +
                     $"{cover.y:P0} water, {cover.z:P0} sand.", labelSmall);
        }

        void DrawOrders()
        {
            int c = world.CommandColony;
            var f = Me;
            var sp = world.SpeciesOf(c);
            string hex = ColorUtility.ToHtmlStringRGB(f.Color);
            ui.Label($"<color=#{hex}>{f.Name}</color> <color=#7d8a84>({sp.Name})</color>", header);

            ui.Label($"Soldier ratio: {f.SoldierRatio:P0}", label);
            f.SoldierRatio = ui.Slider(f.SoldierRatio, 0f, 0.6f);

            DrawCastes(c, sp, f);

            ui.Label($"Scouts: {f.ScoutShare:P0} of workers (about {f.Workers * f.ScoutShare:N0})", label);
            f.ScoutShare = Mathf.Round(ui.Slider(f.ScoutShare, 0f, 0.3f) * 100f) / 100f;
            ui.Label("Scouts roam far and lay the food trails. Other workers follow trails closely but are a bit " +
                     "hopeless on their own: without scouts, food far from home is rarely found. " +
                     "More scouts find food faster but carry less.", labelSmall);
            ui.Label($"{f.Soldiers - f.Drones:N0} guards in {f.Squads} squads of about {world.sim.SquadSize}. Each squad guards " +
                     "its own side of the nest; squad 1 stays home by the queen.", labelSmall);
            ui.Label($"Drones: {f.DroneShare:P0} of soldiers ({f.Drones:N0} in {f.DroneSquads} squads)", label);
            f.DroneShare = Mathf.Round(ui.Slider(f.DroneShare, 0f, 0.5f) * 100f) / 100f;
            string guarding = f.GuardedTrails + f.GuardedFarms == 0
                ? "Nothing to guard yet: they wait near home."
                : $"Guarding {f.GuardedTrails} food trail{(f.GuardedTrails == 1 ? "" : "s")} and {f.GuardedFarms} farm{(f.GuardedFarms == 1 ? "" : "s")}.";
            ui.Label("Drones are escort soldiers. Small squads walk the busiest food trails out to where workers " +
                     "are harvesting, stand guard there and walk back, or keep watch around aphid farms. " + guarding, labelSmall);
            ui.Label($"Squads answering distress calls: {f.DistressResponse:P0}", label);
            f.DistressResponse = ui.Slider(f.DistressResponse, 0f, 1f);
            ui.Label("When ants die in battle, soldiers smell it from far away and whole squads march there. " +
                     "The rest stay home to guard the nest. Raids go in squad by squad, from several sides.", labelSmall);
            ui.Space(4);
            f.RaiseNewQueen = ui.Toggle(f.RaiseNewQueen, "Raise a new queen if she dies", toggle);

            ui.Space(8);
            ui.Label("Hunt & raid", header);
            if (f.RaidActive)
            {
                string what = f.AiRaidKind >= 1 ? $"Hunting a {WildKinds.Name((WildKind)(f.AiRaidKind - 1))}" : "Raiding";
                ui.Label($"<color=orange>{what} at ({f.RaidTarget.x:0}, {f.RaidTarget.y:0})</color>", label);
                ui.BeginRow();
                if (ui.Button("Go there", button)) world.LookAt(f.RaidTarget);
                if (ui.Button("Call off", button)) world.CancelRaid(c);
                ui.EndRow();
            }
            else
            {
                ui.BeginRow();
                if (ui.Button("Hunt prey  [B]", button))
                {
                    string err = world.HuntNearestOf(c, WildKinds.BigPrey, "No big prey around right now.", out var prey);
                    world.Notify(err ?? $"Soldiers sent to hunt the nearest {WildKinds.Name(prey)}.");
                }
                if (ui.Button("Hunt predator", button))
                {
                    string err = world.HuntNearestOf(c, WildKinds.Predators, "No predators around right now.", out var pred);
                    world.Notify(err ?? $"Soldiers sent after the nearest {WildKinds.Name(pred)}.");
                }
                ui.EndRow();
                ui.Label("Dead bugs and enemies drop food. Prey: caterpillars are easy, crickets hop away, woodlice roll " +
                         "up in their armour, beetles are a tough feast. Predators: antlions wait in sand pits, assassin " +
                         "bugs ambush, spiders hunt, and a centipede needs an army. " +
                         "Hover the map and press <b>R</b> to raid that spot (e.g. a rival nest).", labelSmall);
            }

            ui.Space(8);
            ui.Label("View", header);
            world.lod.Enabled = ui.Toggle(world.lod.Enabled, "Sleep off-screen rival colonies (faster)", toggle);
            ui.Label($"{world.SleepingColonies} of {world.Colonies.Length} colonies asleep.", labelSmall);
            ui.Label("Pheromone view", label);
            ui.BeginRow();
            if (ui.Button("Off", world.PheromoneView == 0 ? tabOn : tabOff)) world.PheromoneView = 0;
            if (ui.Button("All", world.PheromoneView == 1 ? tabOn : tabOff)) world.PheromoneView = 1;
            if (ui.Button("Selected", world.PheromoneView == 2 ? tabOn : tabOff)) world.PheromoneView = 2;
            ui.EndRow();

            if (world.Sandbox)
            {
                ui.Space(8);
                ui.Label("Sandbox", header);
                ui.Label($"Food on map target: {world.targetMapFood:N0}", label);
                world.targetMapFood = (int)ui.Slider(world.targetMapFood, 0f, 500000f);
                ui.BeginRow();
                if (ui.Button("+25k workers", button)) world.SpawnAnts(c, 25000, Caste.Worker, 40f);
                if (ui.Button("+5k soldiers", button)) world.SpawnAnts(c, 5000, Caste.Soldier, 30f);
                ui.EndRow();
            }
        }

        /// <summary>The species' castes: how many of each, and what the extra ones do.</summary>
        void DrawCastes(int c, SpeciesDef sp, ColonyInfo f)
        {
            if (sp.Castes == null || sp.Castes.Count == 0) return;
            bool asleep = f.Abstract;
            ui.Label("Castes", label);
            if (!asleep) ui.Label($"{world.MorphCount(c, 0):N0} ordinary workers and soldiers", labelSmall);
            for (int m = 1; m < MorphStats.MaxMorphs; m++)
            {
                var def = sp.Morph(m);
                if (def == null) continue;
                int n = asleep ? (int)((def.Base == Caste.Soldier ? f.Soldiers : f.Workers) * def.Share) : world.MorphCount(c, m);
                string what = def.Base == Caste.Soldier ? "soldiers" : "workers";
                string perk = def.Perk == MorphPerk.Gardener ? $"   garden +{Mathf.Min(100f, f.Gardeners * 0.5f):0}%"
                    : def.Perk == MorphPerk.Nurse ? $"   larva food -{Mathf.Min(35f, f.Nurses * 0.2f):0}%" : "";
                ui.Label($"<b>{def.Name}</b> ({def.Share:P0} of {what}): {(asleep ? "~" : "")}{n:N0}{perk}\n" +
                         $"<color=#7d8a84>{def.Description}</color>", labelSmall);
            }
        }

        void DrawNests()
        {
            int c = world.CommandColony;
            var f = Me;
            var sw = world.swarm;

            ui.Label("Your nests", header);
            for (int k = 0; k < world.NestSlots; k++)
            {
                var n = world.GetNest(k);
                if (n == null || n.Colony != c) continue;
                string state = n.MovingFrom >= 0 ? $"<color=#e6c35c>new site: the queen is on her way</color> ({n.MoveTime:0}s)"
                    : n.MovingTo >= 0 ? "<color=#e6c35c>moving out</color>"
                    : n.Founding ? "<color=#e6c35c>princess on her way</color>"
                    : n.HasQueen ? $"queen {n.QueenHealth:0} hp"
                    : f.Queens > 0 ? $"<color=#f0a05a>no queen</color> - abandoned in {Mathf.Max(0f, sw.AbandonTime - n.QueenlessTime):0}s"
                    : "<color=#e07a6a>no queen</color>";
                string main = k == f.PrimaryNest ? " (main)" : "";
                string ants = f.Abstract ? $"~{n.SleepShare:P0} of ants" : $"{n.Ants:N0} ants";
                string tree = "";
                if (world.SpeciesOf(c).TreeNester && !n.Founding && n.MovingTo < 0)
                {
                    float wood = world.NestTreeHealth(k);
                    tree = world.TreeAt(n.Position, 4f) == null ? "   <color=#e07a6a>tree fallen!</color>"
                        : wood <= 0f ? "   <color=#e07a6a>tree dead</color>"
                        : wood < 0.35f ? $"   <color=#f0a05a>tree {wood:P0}</color>" : $"   tree {wood:P0}";
                }
                string walls = world.IsFortified(k) ? "   fortified" : "";
                ui.BeginRow();
                ui.Label($"<b>Nest {n.Number}</b>{main}   {ants}   {state}{tree}{walls}", label);
                if (!n.Founding && n.MovingTo < 0)
                {
                    bool picking = world.PlacingMove == k;
                    if (ui.Button(picking ? "Cancel" : "Move", picking ? tabOn : button, 64))
                    {
                        string err = picking ? null : world.MoveBlocker(c, k);
                        if (err != null) world.Notify(err);
                        else
                        {
                            world.PlacingMove = picking ? -1 : k;
                            world.PlacingNest = world.PlacingFarm = false;
                            world.PlacingDefence = -1;
                        }
                    }
                }
                if (ui.Button("Go", button, 44)) world.LookAt(n.Position);
                ui.EndRow();
            }
            if (world.SpeciesOf(c).TreeNester)
            {
                ui.Label("Termites live inside trees and eat them: a nest finishes its tree in 35 to 100 minutes, faster " +
                         "the bigger the nest. When the tree is nearly eaten out the queen leads the nest to a healthy tree " +
                         $"within {AntWorld.MoveMaxDistance:0} (if there is one). A dead tree stands for 10 more minutes, then falls, " +
                         "and a queen still inside it then dies within minutes. " +
                         $"Wood feeds the colony <b>{f.WoodIncome:0.0} food/s</b>.", labelSmall);
                f.AutoMoveTree = ui.Toggle(f.AutoMoveTree, "Move to a new tree when ours is dying", toggle);
            }
            ui.Label("Food and brood are shared by all nests. Every nest with a queen lays eggs; " +
                     "new ants hatch in nests that have a queen.", labelSmall);

            ui.Label($"<b>Move</b> a nest ({AntWorld.MoveCost:0} food): the queen walks to the new site (up to " +
                     $"{AntWorld.MoveMaxDistance:0} away) with the nest's soldiers round her, and the workers go ahead. The old " +
                     "nest is given up when she arrives. She's out in the open on the way: if she's killed, her ants settle " +
                     "at the new site without her. Use it to get away from a rival, closer to food, or out of a dying tree.", labelSmall);
            ui.Space(8);
            ui.Label("Found a new nest", header);
            ui.Label($"The colony raises a princess ({sw.Cost:0} food, {sw.PrincessTime:0}s). She then flies to the site " +
                     $"with a cloud of winged males and {sw.EscortPercent}% of the nearest nest's ants as escort. " +
                     "If she gets there she becomes its queen. If she's killed on the way, the swarm fails.", labelSmall);
            if (f.SwarmState == 1)
            {
                ui.Label($"Raising a princess: <b>{f.SwarmProgress:P0}</b>", label);
                ui.BeginRow();
                if (ui.Button("Go to site", button)) world.LookAt(f.SwarmTarget);
                if (ui.Button("Call off (half refund)", button)) world.CancelSwarm(c);
                ui.EndRow();
            }
            else if (f.SwarmState == 2)
            {
                ui.Label($"<color=#e6c35c>The princess is flying to the new site</color> ({f.SwarmTimer:0}s).", label);
                if (ui.Button("Go to site", button)) world.LookAt(f.SwarmTarget);
            }
            else
            {
                string blocker = world.SwarmBlocker(c);
                ui.Label(blocker != null ? $"<color=#f0a05a>{blocker}</color>" : "", labelSmall);
                ui.BeginRow();
                if (ui.Button(world.PlacingNest ? "Cancel" : "Pick a site  [N]", world.PlacingNest ? tabOn : button))
                {
                    world.PlacingNest = !world.PlacingNest;
                    world.PlacingFarm = false;
                    world.PlacingDefence = -1;
                    world.PlacingMove = -1;
                }
                if (ui.Button("Let scouts choose", button))
                {
                    string err = world.SwarmBlocker(c);
                    if (err == null && world.FindNestSite(c, out var site)) err = world.StartSwarm(c, site);
                    else if (err == null) err = "Scouts found no good site nearby.";
                    world.Notify(err ?? "Scouts picked a site. Raising a princess.");
                }
                ui.EndRow();
            }

            ui.Space(8);
            ui.Label("Nesting rules", header);
            f.AutoSwarm = ui.Toggle(f.AutoSwarm, "Swarm automatically when crowded", toggle);
            ui.Label($"Swarm when there are more than <b>{f.SwarmAt:N0}</b> ants per nest", label);
            f.SwarmAt = Mathf.RoundToInt(ui.Slider(f.SwarmAt, 200f, 5000f) / 100f) * 100;
            ui.Label($"Never own more than <b>{f.MaxNests}</b> nests", label);
            f.MaxNests = Mathf.RoundToInt(ui.Slider(f.MaxNests, 1f, sw.MaxNestsPerColony));
            ui.Label($"Nests founded: {f.NestsFounded}   Swarms lost: {f.SwarmsFailed}", labelSmall);
            ui.Label("A nest whose queen dies is abandoned after a while; its ants move to the nearest nest with a queen.", labelSmall);
        }

        void DrawFarming()
        {
            int c = world.CommandColony;
            var f = Me;
            var sp = world.SpeciesOf(c);

            if (sp.NoFarming)
            {
                ui.Label("Food", header);
                ui.Label($"{sp.Name} don't keep aphids or grow fungus." +
                         (sp.LeafEater ? " Workers gather leaves (green bushes and fallen leaves under trees) and the colony eats them straight away." : ""),
                         labelSmall);
                if (sp.LeafEater)
                    ui.Label($"Leaves: <b>{f.LeafIncome:0.0} food/s</b>   ({world.farming.FoodPerLeaf * sp.GardenYield:0.0} food each)", label);
                if (sp.TreeNester) ui.Label($"Wood from your trees: <b>{f.WoodIncome:0.0} food/s</b>", label);
                return;
            }

            ui.Label("Fungus garden", header);
            ui.Label("Workers bring leaves from bushes; gardeners grow fungus on them, which feeds the colony.", labelSmall);
            if (f.GardenLevel == 0) ui.Label("You have no garden yet. Workers ignore leaves until you build one.", label);
            else
            {
                float store = world.farming.LeafStorePerLevel * f.GardenLevel;
                ui.Label($"Level {f.GardenLevel}   Leaves {f.Leaves:0} / {store:0}   Fungus food <b>{f.GardenIncome:0.0}/s</b>", label);
                float boost = 1f + Mathf.Min(1f, f.Gardeners * 0.005f);
                ui.Label($"Processes up to {world.farming.GardenLeavesPerLevel * f.GardenLevel * boost:0.0} leaves/s " +
                         $"at {world.farming.FoodPerLeaf * sp.GardenYield:0.0} food each" +
                         (f.Gardeners > 0 ? $" ({f.Gardeners:N0} minims tending it: +{(boost - 1f) * 100f:0}%)." : "."), labelSmall);
            }
            if (f.GardenLevel < FarmingParams.MaxGarden)
            {
                float cost = FarmingParams.GardenCost(f.GardenLevel + 1);
                if (ui.Button(f.GardenLevel == 0 ? $"Build garden ({cost:0} food)" : $"Upgrade to level {f.GardenLevel + 1} ({cost:0} food)", button))
                    world.Notify(world.UpgradeGarden(c) ?? "Fungus garden upgraded.");
            }

            ui.Space(10);
            ui.Label("Aphid farms", header);
            ui.Label("Place a herd of aphids on a bush. They eat the plant and drip honeydew (food) around " +
                     "the farm for your foragers. Beetles eat aphids; enemies steal honeydew. Defend them.", labelSmall);
            ui.Label($"Farms: {f.AphidFarms}   Honeydew <b>{f.FarmIncome:0.0}/s</b>", label);
            if (ui.Button(world.PlacingFarm ? "Cancel placing" : $"Place aphid farm ({world.FarmCost(c):0} food)  [F]", world.PlacingFarm ? tabOn : button))
            {
                world.PlacingFarm = !world.PlacingFarm; world.PlacingMove = -1;
                world.PlacingNest = false;
                world.PlacingDefence = -1;
            }
            foreach (var farm in world.Farms)
            {
                if (farm.Colony != c) continue;
                ui.BeginRow();
                string danger = farm.Eaten > 0f ? "  <color=#e07a6a>beetles!</color>" : "";
                ui.Label($"({farm.Position.x:0}, {farm.Position.y:0})  {farm.Aphids:0} aphids{danger}", labelSmall);
                if (ui.Button("Go", button, 44)) world.LookAt(farm.Position);
                ui.EndRow();
            }
        }

        void DrawDefence()
        {
            int c = world.CommandColony;
            var f = Me;
            ui.Label("Defences", header);
            ui.Label("Buildings that protect your nests and farms. Build them close to home (within " +
                     $"{world.farming.FarmMaxDistance:0} of a nest). They hurt or slow enemy ants and wild bugs alike.", labelSmall);
            for (int kind = 0; kind < AntWorld.DefenceNames.Length; kind++)
            {
                ui.Space(6);
                int have = world.DefenceCount(c, kind), max = world.DefenceLimitOf(kind);
                string count = kind == (int)DefenceKind.Fortified ? $"{have} of your nests" : $"{have} / {max}";
                ui.Label($"<b>{AntWorld.DefenceNames[kind]}</b>   <color=#7d8a84>{count}</color>", label);
                ui.Label(AntWorld.DefenceInfo[kind], labelSmall);
                bool on = world.PlacingDefence == kind;
                string verb = kind == (int)DefenceKind.Fortified ? "Fortify a nest" : "Build";
                if (ui.Button(on ? "Cancel" : $"{verb} ({world.DefenceCost(kind):0} food)", on ? tabOn : button))
                {
                    world.PlacingDefence = on ? -1 : kind;
                    world.PlacingFarm = world.PlacingNest = false; world.PlacingMove = -1;
                }
            }
            if (f.Captured > 0 || world.SpeciesOf(c).Captives > 0f)
            {
                ui.Space(8);
                ui.Label("Captives", header);
                ui.Label($"Workers captured from enemy colonies so far: <b>{f.Captured:N0}</b>. Each enemy ant your colony " +
                         "kills brings back a pupa that hatches as one of your workers.", labelSmall);
            }
        }

        void DrawDiplomacy()
        {
            int me = world.CommandColony;
            var f = Me;
            ui.Label($"Relations of <color=#{ColorUtility.ToHtmlStringRGB(f.Color)}>{f.Name}</color>", header);

            // A rival asking for a truce.
            if (me == AntWorld.Player && f.TruceOfferFrom >= 0)
            {
                var from = world.Colonies[f.TruceOfferFrom];
                ui.Label($"<color=#e6c35c><b>{from.Name} offers a truce</b></color> ({Mathf.Max(0f, 60f - (world.SimTime - f.TruceOfferTime)):0}s to answer)", label);
                ui.BeginRow();
                if (ui.Button("Accept", tabOn)) world.AnswerTruceOffer(true);
                if (ui.Button("Refuse", button)) world.AnswerTruceOffer(false);
                ui.EndRow();
                ui.Space(4);
            }

            for (int c = 0; c < world.Colonies.Length; c++)
            {
                if (c == me) continue;
                var other = world.Colonies[c];
                string name = $"<b><color=#{ColorUtility.ToHtmlStringRGB(other.Color)}>{other.Name}</color></b>";
                if (other.Extinct)
                {
                    ui.Label($"{name}   <color=#56605b>extinct</color>", label);
                    continue;
                }
                var stance = world.StanceOf(me, c);
                string st = stance == Stance.War ? "<color=#e07a6a>WAR</color>"
                    : stance == Stance.Truce ? $"<color=#e6c35c>truce</color> ({f.TruceLeft[c]:0}s left)"
                    : "<color=#7fc97f>peace</color>";
                float theirs = world.OpinionOf(c, me);
                string mood = $"{AntWorld.OpinionWord(theirs)} toward you ({theirs:+0;-0;0})";
                float contact = world.ContactBetween(me, c);
                string border = contact > 0.6f ? "<color=#f0a05a>territories overlap a lot</color>"
                    : contact > 0f ? "<color=#f0a05a>territories touch</color>" : "no shared border";
                string losses = "";
                if (f.LossesTo != null && other.LossesTo != null && (f.LossesTo[c] >= 1f || other.LossesTo[me] >= 1f))
                    losses = $"\nRecent losses: yours {f.LossesTo[c]:0}, theirs {other.LossesTo[me]:0}";
                if (f.FrontLosses != null && other.FrontLosses != null && (f.FrontLosses[c] > 0f || other.FrontLosses[me] > 0f))
                    losses += "  <color=#e07a6a>(fighting at the border)</color>";
                ui.Label($"{name}   {st}   <color=#7d8a84>{world.SpeciesOf(c).Name}, {other.Soldiers:N0} soldiers</color>\n" +
                         $"They are {mood}. {border}.{losses}", labelSmall);
                ui.BeginRow();
                if (stance == Stance.War)
                {
                    if (ui.Button("Offer truce", button)) world.Notify(world.OfferTruce(me, c));
                }
                else if (ui.Button(stance == Stance.Truce ? "Break truce: war!" : "Declare war", button))
                    world.Notify(world.DeclareWar(me, c) ?? $"You declared war on {other.Name}.");
                if (ui.Button($"Gift {world.diplomacy.GiftFood:0} food", button)) world.Notify(world.SendGift(me, c));
                ui.EndRow();
                ui.Space(2);
            }

            // Wars between the others.
            string wars = "";
            for (int a = 0; a < world.Colonies.Length; a++)
            for (int b = a + 1; b < world.Colonies.Length; b++)
            {
                if (a == me || b == me || world.Colonies[a].Extinct || world.Colonies[b].Extinct) continue;
                var s2 = world.StanceOf(a, b);
                if (s2 == Stance.Peace) continue;
                bool front = world.Colonies[a].FrontLosses != null && (world.Colonies[a].FrontLosses[b] > 0f || world.Colonies[b].FrontLosses[a] > 0f);
                wars += $"{world.Colonies[a].Name} and {world.Colonies[b].Name}: {(s2 == Stance.War ? "<color=#e07a6a>war</color>" : "<color=#e6c35c>truce</color>")}" +
                        (front ? " (border battle)" : "") + "\n";
            }
            ui.Space(6);
            ui.Label("Between the others", header);
            ui.Label(wars.Length > 0 ? wars.TrimEnd() : "All at peace.", labelSmall);

            ui.Space(6);
            ui.Label("Recent events", header);
            if (world.DiplomacyLog.Count == 0) ui.Label("Nothing yet.", labelSmall);
            foreach (var e in world.DiplomacyLog) ui.Label(e, labelSmall);

            ui.Space(6);
            ui.Label("Colonies start at peace. Sharing a border (competing for food), losing ants to someone and being raided " +
                     "all sour relations; time apart, a common enemy and gifts improve them. Rivals declare war when they are " +
                     "hostile enough (not in the opening minutes) and ask for a truce when a war goes badly. A truce lasts " +
                     $"{world.diplomacy.TruceLength / 60f:0} minutes, then becomes peace unless tempers are still high. " +
                     "Breaking a truce makes every colony trust you less. Bugs are always hostile.", labelSmall);
        }

        void DrawGame()
        {
            ui.Label("Save game", header);
            if (saveName == null) saveName = $"{world.Colonies[AntWorld.Player].Name} {System.DateTime.Now:MMM d HH-mm}";
            saveName = ui.TextField(saveName, 40, field);
            ui.BeginRow();
            if (ui.Button("Save", button))
            {
                world.Notify(world.SaveGame(saveName) ?? $"Saved \"{saveName}\".");
                savesListedAt = -100f;
            }
            if (ui.Button("Quicksave  [F5]", button))
            {
                world.Notify(world.SaveGame("Quicksave") ?? "Game saved (Quicksave).");
                savesListedAt = -100f;
            }
            ui.EndRow();
            ui.Label(world.autosaveInterval > 0f
                ? $"The game also autosaves every {world.autosaveInterval / 60f:0} minutes (Esc, Settings). F9 loads the quicksave."
                : "Autosave is off (Esc, Settings). F9 loads the quicksave.", labelSmall);

            ui.Space(8);
            ui.Label("Load", header);
            if (saves == null || Time.unscaledTime - savesListedAt > 5f) { saves = AntWorld.ListSaves(); savesListedAt = Time.unscaledTime; }
            if (saves.Count == 0) ui.Label("No saves yet.", labelSmall);
            foreach (var s in saves)
            {
                ui.Label($"<b>{s.Name}</b>   <color=#7d8a84>{s.Written:MMM d, HH:mm}</color>\n<color=#7d8a84>{s.Summary}</color>", labelSmall);
                ui.BeginRow();
                if (ui.Button("Load", button)) { AntGameMenu.LoadSave(world, s.Path); return; }
                bool sure = confirmDelete == s.Path;
                if (ui.Button(sure ? "Really delete?" : "Delete", sure ? tabOn : button))
                {
                    if (sure) { AntWorld.DeleteSave(s.Path); confirmDelete = null; savesListedAt = -100f; }
                    else confirmDelete = s.Path;
                }
                ui.EndRow();
            }

            ui.Space(10);
            if (ui.Button("Quit to menu", button)) AntGameMenu.ReturnToMenu(world);
        }

        void DrawHelp()
        {
            ui.Label(
                "<b>Goal</b>\n" +
                (world.Sandbox ? "Sandbox: no goal, just play.\n\n"
                    : "Keep your queen alive and outlast every rival colony.\n\n") +
                "<b>Food</b>\n" +
                "Starting food runs out. Get more by:\n" +
                " - hunting: dead bugs and enemy ants become food\n" +
                " - aphid farms on bushes (Farming tab, F)\n" +
                " - a fungus garden fed with leaves (Farming tab, G)\n\n" +
                "<b>Camera</b>\n" +
                "WASD / arrows, right or middle drag: pan.  Mouse wheel: zoom\n\n" +
                "<b>Keys</b>\n" +
                "Space: pause      - / = : slower / faster\n" +
                "R: raid the spot under the mouse      X: call off\n" +
                "B: hunt the nearest big prey      F: place aphid farm      G: upgrade garden\n" +
                "N: pick a site for a new nest (Nests tab)\n" +
                "F5: quicksave      F9: load the quicksave (Game tab for more)\n" +
                "Tab: select next colony      P: pheromone view      H: hide panel\n" +
                "Esc: menu (settings, save, quit to menu or desktop)\n" +
                (world.Sandbox ? "Left click: drop food.  T / Y: +25k workers / +5k soldiers\n" : "") + "\n" +
                "<b>Reading the map</b>\n" +
                "Green: bushes.  Brown: fallen leaves.  Gold: food and honeydew.  Coloured haze: trails.  Red haze: fighting.\n" +
                "Trees: a trunk in dappled shade. They drop leaves now and then, and all of them when they die. A dead tree " +
                "is a grey snag that falls after a while; saplings grow to replace it.\n" +
                "Grey: rock.  Blue: water.  Tan: sand (slow going). Princesses and males fly over rock and water.\n" +
                "Ringed patches: aphid farms.  Pale spots in a nest: fungus garden.\n" +
                "Pale yellow, slim ants: scouts.  Pale blue soldiers: drones (escorts).\n" +
                "Bigger or smaller ants of your colour: castes (majors, minims...). See the Orders tab.\n" +
                "Big white-rimmed ant: queen.  Grey nest rim: no queen.\n" +
                "Gold winged ant: princess.  Small dark winged ants: males (swarm).  Dashed ring: a nest being founded or moved into.\n" +
                "Amber blobs: resin traps.  Earth mound with a ring: stinger mound.  Stone rim: fortified nest.\n" +
                "Prey: red dots are mites, green shells beetles, green stripy worms caterpillars, brown hoppers crickets, " +
                "grey banded ovals woodlice.\n" +
                "Predators: black with legs: spiders.  Sand pits: antlions.  Dark with orange edges: assassin bugs.  " +
                "Long and many-legged: centipedes (run!).\n\n" +
                "Too small or too big? Use A- / A+ in the top bar.", label);
        }

        // ------------------------------------------------------------------ win / lose

        void DrawGameOver(Vector2 v)
        {
            dialog.Visible = world.State != GameState.Playing;
            if (!dialog.Visible) return;
            bool won = world.State == GameState.Won;
            var me = world.Colonies[AntWorld.Player];
            dialog.Rect.sizeDelta = new Vector2(Mathf.Min(460f, v.x - 20f), dialog.Rect.sizeDelta.y);
            ui.Begin(dialog);
            ui.Label(won ? "Victory!" : "Your colony has fallen", bigTitle);
            ui.Label(won
                ? $"{me.Name} outlasted every rival in {FormatTime(world.SimTime)}."
                : $"{me.Name} survived {FormatTime(world.SimTime)}.", label);
            ui.Label($"Ants raised: {me.TotalBorn:N0}   Lost in battle: {me.CombatDeaths:N0}   " +
                     $"Food gathered: {me.TotalDelivered:N0}", labelSmall);
            ui.Space(10);
            if (ui.Button("New game", button)) AntGameMenu.ReturnToMenu(world);
            ui.End();
        }

        // ------------------------------------------------------------------ styles

        void MakeStyles()
        {
            label = UiTheme.Label(14);
            labelSmall = UiTheme.Small(12);
            header = UiTheme.Section(12);
            bigTitle = UiTheme.Title(24);
            barTitle = UiTheme.Title(17);
            barTitle.Wrap = false;
            barLabel = UiTheme.Label(14);
            barLabel.Wrap = false;
            barLabelSmall = UiTheme.Small(11);
            barLabelSmall.Wrap = false;
            toggle = UiTheme.Label(14);
            field = UiTheme.Field(15);
            button = UiTheme.Button(13, 30f);
            button.Padding = new RectOffset(6, 6, 3, 3);
            tabOff = UiTheme.Tab(button);
            tabOn = UiTheme.On(button);
            rowStyle = UiTheme.Card(14);
            rowStyle.Padding = new RectOffset(9, 9, 6, 6);
            rowFocused = UiTheme.CardOn(rowStyle);
            menuButton = UiTheme.Button(15, 38f);
            menuButtonOn = UiTheme.On(menuButton);
            statBox = UiTheme.StatBox();
            statKey = UiTheme.StatKey();
            statValue = UiTheme.StatValue();
            settingsStyles = new GameSettings.Styles
            {
                Header = header, Label = label, Small = labelSmall, Button = button, ButtonOn = tabOn, Toggle = toggle,
            };
        }

        static string FormatTime(float seconds)
        {
            int s = (int)seconds;
            return $"{s / 60}:{s % 60:00}";
        }
    }
}
