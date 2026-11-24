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

        enum Tab { Colonies, Orders, Nests, Farming, Diplomacy, Game, Help }

        const string ScaleOffsetPref = "AntSim.UiScaleOffset";
        const float BarHeight = 34f;
        const float PanelWidth = 460f;

        static bool panelVisible = true;
        static AntSimHud instance;

        /// <summary>A text box has keyboard focus: game hotkeys are off.</summary>
        public static bool IsTyping { get; set; }

        public static bool IsMouseOverUi() => UiKit.PointerOverUi;

        UiKit ui;
        UiKit.Panel bar, alert, panel, dialog;
        Tab tab = Tab.Orders;
        bool tabChanged;
        float scaleOffset;
        float fpsSmoothed = 60f;
        string saveName;
        string confirmDelete;
        List<SaveSlot> saves;
        float savesListedAt = -100f;

        UiStyle barLabel, barLabelSmall, label, labelSmall, header, bigTitle, button, tabOn, tabOff, rowStyle, rowFocused, toggle, field;

        void Awake()
        {
            instance = this;
            scaleOffset = PlayerPrefs.GetFloat(ScaleOffsetPref, 0f);
            MakeStyles();
            ui = UiKit.Create(transform, "HUD", 10);
            bar = ui.CreatePanel("Top bar", new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Color(0.06f, 0.06f, 0.08f, 0.95f),
                new RectOffset(10, 10, 4, 4), vertical: false, fitHeight: false);
            bar.Rect.anchorMin = new Vector2(0f, 1f);
            bar.Rect.anchorMax = new Vector2(1f, 1f);
            bar.Rect.sizeDelta = new Vector2(0f, BarHeight);
            bar.Rect.anchoredPosition = Vector2.zero;
            alert = ui.CreatePanel("Alerts", new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Color(0.18f, 0.08f, 0.07f, 0.92f),
                new RectOffset(10, 10, 3, 3), vertical: false);
            panel = ui.CreatePanel("Panel", new Vector2(0f, 1f), new Vector2(0f, 1f), new Color(0.09f, 0.09f, 0.11f, 0.95f),
                new RectOffset(10, 10, 10, 10), draggable: true);
            panel.Rect.anchoredPosition = new Vector2(10f, -(BarHeight + 40f));
            dialog = ui.CreatePanel("Dialog", new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Color(0.09f, 0.09f, 0.11f, 0.97f),
                new RectOffset(16, 16, 14, 14));
            alert.Visible = false;
            dialog.Visible = false;
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
            if (!IsTyping && Input.GetKeyDown(KeyCode.H)) panelVisible = !panelVisible;
        }

        float AutoScale()
        {
            // Designed for roughly 1100 x 900 "virtual" pixels; shrink or grow to match the real screen.
            float s = Mathf.Min(Screen.height / 900f, Screen.width / 1100f);
            return Mathf.Clamp(s + scaleOffset, 0.6f, 3f);
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
        }

        ColonyInfo Me => world.Colonies[world.CommandColony];

        // ------------------------------------------------------------------ top bar

        void DrawTopBar()
        {
            var me = Me;
            string hex = ColorUtility.ToHtmlStringRGB(me.Color);
            ui.Label($"<b><color=#{hex}>{me.Name}</color></b>", barLabel);
            ui.Label($"<b>{me.Population:N0}</b> ants", barLabel);
            string foodCol = me.Starving ? "#ff5555" : me.Food < 100f ? "#ffbb55" : "#ffd27a";
            ui.Label($"<color={foodCol}>Food <b>{me.Food:N0}</b></color>", barLabel);
            ui.Label(!me.HasQueen ? "<color=#ff5555>NO QUEEN</color>"
                : me.NestCount > 1 ? $"{me.NestCount} nests, {me.Queens} queens" : $"Queen {me.QueenHealth:0} hp", barLabelSmall);
            ui.Label(me.SwarmState == 2 ? "<color=#ffd27a>Princess in flight</color>" : "", barLabelSmall);
            ui.Label($"Time {FormatTime(world.SimTime)}", barLabelSmall);
            ui.Label(world.SimTime < world.PeacefulStart
                ? $"<color=#8fdc8f>Rivals peaceful for {FormatTime(world.PeacefulStart - world.SimTime)}</color>" : "", barLabelSmall);
            ui.Label($"{world.TotalAnts:N0} ants total, {world.AntCount:N0} live", barLabelSmall);
            ui.Label($"FPS {fpsSmoothed:0}  tick {world.LastTickMs:0.0} ms", barLabelSmall);

            ui.FlexibleSpace();

            if (ui.Button(world.Paused ? "Play" : "Pause", button, 64)) world.Paused = !world.Paused;
            if (ui.Button("-", button, 28)) world.simSpeed = Mathf.Max(world.simSpeed * 0.5f, 0.125f);
            ui.Label($"{world.simSpeed:0.##}x", barLabel, 44);
            if (ui.Button("+", button, 28)) world.simSpeed = Mathf.Min(world.simSpeed * 2f, 16f);
            ui.Space(8);
            if (ui.Button(panelVisible ? "Hide panel" : "Show panel", button, 92)) panelVisible = !panelVisible;
            ui.Space(8);
            if (ui.Button("A-", button, 32)) SetScaleOffset(scaleOffset - 0.1f);
            if (ui.Button("A+", button, 32)) SetScaleOffset(scaleOffset + 0.1f);
        }

        void SetScaleOffset(float v)
        {
            scaleOffset = Mathf.Clamp(v, -1f, 2f);
            PlayerPrefs.SetFloat(ScaleOffsetPref, scaleOffset);
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
            bool placing = world.PlacingFarm || world.PlacingNest;

            alert.Visible = dying || raided || toast || placing;
            if (!alert.Visible) return;
            float w = Mathf.Min(720f, vw - 20f);
            alert.Rect.sizeDelta = new Vector2(w, alert.Rect.sizeDelta.y);
            alert.Rect.anchoredPosition = new Vector2(0f, -(BarHeight + 4f));

            ui.Begin(alert);
            if (world.PlacingNest)
                ui.Label($"Click where the new nest should go ({world.swarm.MinSpacing:0}-{world.swarm.MaxDistance:0} from your nests, " +
                         $"away from rivals, not on rock or water). Costs {world.swarm.Cost:0} food. Esc to cancel.", labelSmall);
            else if (placing)
                ui.Label($"Click a green bush near your nest to place an aphid farm ({world.FarmCost(c):0} food). " +
                         "Shift-click to place several. Esc to cancel.", labelSmall);
            else if (raided)
            {
                ui.Label($"<color=#ff7766><b>{raider} is raiding your nest!</b></color>", label);
                if (ui.Button("Go", button, 48)) world.LookAt(raidedAt);
            }
            else if (dying)
            {
                ui.Label($"<color=#ff7766><b>Your ants are dying</b></color> ({me.DistressAlerts} recent deaths). " +
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
                if (col.Extinct) status = "<color=#888888>EXTINCT</color>";
                else if (col.HasQueen) status = col.NestCount > 1 ? $"{col.NestCount} nests, {col.Queens} queens" : $"Queen {col.QueenHealth:0} hp";
                else if (col.NewQueenProgress > 0f) status = $"<color=orange>NO QUEEN</color> - raising new {col.NewQueenProgress:P0}";
                else status = "<color=#ff5555>NO QUEEN</color>";
                if (col.Starving) status += "  <color=#ff5555>STARVING</color>";
                if (col.BroodStarving) status += "  <color=#ff9955>larvae hungry</color>";
                if (col.RaidActive) status += col.AiRaidKind > 0 ? "  <color=orange>hunting</color>" : "  <color=orange>raiding</color>";
                if (col.SwarmState == 1) status += "  <color=#ffd27a>raising a princess</color>";
                if (col.SwarmState == 2) status += "  <color=#ffd27a>swarming</color>";
                if (col.Abstract) status += "  <color=#7fb3ff>asleep (off-screen)</color>";
                if (col.FrontLosses != null)
                {
                    float front = 0f;
                    foreach (float x in col.FrontLosses) front += x;
                    if (front > 0f) status += "  <color=#ff7766>border battle</color>";
                }
                int wars = 0;
                for (int o = 0; o < world.Colonies.Length; o++)
                    if (o != c && !world.Colonies[o].Extinct && world.IsAtWar(c, o)) wars++;
                if (wars > 0 && !col.Extinct) status += $"  <color=#ff5555>{wars} war{(wars > 1 ? "s" : "")}</color>";

                string you = col.IsPlayer ? "  <color=#ffffff>(you)</color>" : "";
                string text =
                    $"<b><color=#{hex}>{col.Name}</color></b>{you}   <color=#9fb0c8>{world.SpeciesOf(c).Name}</color>\n" +
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
            ui.Label($"Wildlife: {world.WildCount(WildKind.Mite)} mites, {world.WildCount(WildKind.Beetle)} beetles, " +
                     $"{world.WildCount(WildKind.Spider)} spiders.  Food on the ground: {world.MapFood:N0}", labelSmall);
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
            ui.Label($"<color=#{hex}>{f.Name}</color> <color=#9fb0c8>({sp.Name})</color>", header);

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
                string what = f.AiRaidKind == 1 + (int)WildKind.Beetle ? "Hunting a beetle"
                    : f.AiRaidKind == 1 + (int)WildKind.Spider ? "Hunting a spider" : "Raiding";
                ui.Label($"<color=orange>{what} at ({f.RaidTarget.x:0}, {f.RaidTarget.y:0})</color>", label);
                ui.BeginRow();
                if (ui.Button("Go there", button)) world.LookAt(f.RaidTarget);
                if (ui.Button("Call off", button)) world.CancelRaid(c);
                ui.EndRow();
            }
            else
            {
                ui.BeginRow();
                if (ui.Button("Hunt beetle", button))
                    world.Notify(world.HuntNearest(c, WildKind.Beetle) ?? "Soldiers sent to hunt the nearest beetle.");
                if (ui.Button("Hunt spider", button))
                    world.Notify(world.HuntNearest(c, WildKind.Spider) ?? "Soldiers sent after the nearest spider.");
                ui.EndRow();
                ui.Label("Dead bugs and enemies drop food. Beetles are a feast but tough. " +
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
                         $"<color=#9fb0c8>{def.Description}</color>", labelSmall);
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
                string state = n.Founding ? "<color=#ffd27a>princess on her way</color>"
                    : n.HasQueen ? $"queen {n.QueenHealth:0} hp"
                    : f.Queens > 0 ? $"<color=#ff9955>no queen</color> - abandoned in {Mathf.Max(0f, sw.AbandonTime - n.QueenlessTime):0}s"
                    : "<color=#ff5555>no queen</color>";
                string main = k == f.PrimaryNest ? " (main)" : "";
                string ants = f.Abstract ? $"~{n.SleepShare:P0} of ants" : $"{n.Ants:N0} ants";
                ui.BeginRow();
                ui.Label($"<b>Nest {n.Number}</b>{main}   {ants}   {state}", label);
                if (ui.Button("Go", button, 44)) world.LookAt(n.Position);
                ui.EndRow();
            }
            ui.Label("Food and brood are shared by all nests. Every nest with a queen lays eggs; " +
                     "new ants hatch in nests that have a queen.", labelSmall);

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
                ui.Label($"<color=#ffd27a>The princess is flying to the new site</color> ({f.SwarmTimer:0}s).", label);
                if (ui.Button("Go to site", button)) world.LookAt(f.SwarmTarget);
            }
            else
            {
                string blocker = world.SwarmBlocker(c);
                ui.Label(blocker != null ? $"<color=#ff9955>{blocker}</color>" : "", labelSmall);
                ui.BeginRow();
                if (ui.Button(world.PlacingNest ? "Cancel" : "Pick a site  [N]", world.PlacingNest ? tabOn : button))
                {
                    world.PlacingNest = !world.PlacingNest;
                    world.PlacingFarm = false;
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
                world.PlacingFarm = !world.PlacingFarm;
            foreach (var farm in world.Farms)
            {
                if (farm.Colony != c) continue;
                ui.BeginRow();
                string danger = farm.Eaten > 0f ? "  <color=#ff7766>beetles!</color>" : "";
                ui.Label($"({farm.Position.x:0}, {farm.Position.y:0})  {farm.Aphids:0} aphids{danger}", labelSmall);
                if (ui.Button("Go", button, 44)) world.LookAt(farm.Position);
                ui.EndRow();
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
                ui.Label($"<color=#ffd27a><b>{from.Name} offers a truce</b></color> ({Mathf.Max(0f, 60f - (world.SimTime - f.TruceOfferTime)):0}s to answer)", label);
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
                    ui.Label($"{name}   <color=#888888>extinct</color>", label);
                    continue;
                }
                var stance = world.StanceOf(me, c);
                string st = stance == Stance.War ? "<color=#ff5555>WAR</color>"
                    : stance == Stance.Truce ? $"<color=#ffd27a>truce</color> ({f.TruceLeft[c]:0}s left)"
                    : "<color=#66ff66>peace</color>";
                float theirs = world.OpinionOf(c, me);
                string mood = $"{AntWorld.OpinionWord(theirs)} toward you ({theirs:+0;-0;0})";
                float contact = world.ContactBetween(me, c);
                string border = contact > 0.6f ? "<color=#ff9955>territories overlap a lot</color>"
                    : contact > 0f ? "<color=#ffbb55>territories touch</color>" : "no shared border";
                string losses = "";
                if (f.LossesTo != null && other.LossesTo != null && (f.LossesTo[c] >= 1f || other.LossesTo[me] >= 1f))
                    losses = $"\nRecent losses: yours {f.LossesTo[c]:0}, theirs {other.LossesTo[me]:0}";
                if (f.FrontLosses != null && other.FrontLosses != null && (f.FrontLosses[c] > 0f || other.FrontLosses[me] > 0f))
                    losses += "  <color=#ff7766>(fighting at the border)</color>";
                ui.Label($"{name}   {st}   <color=#9fb0c8>{world.SpeciesOf(c).Name}, {other.Soldiers:N0} soldiers</color>\n" +
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
                wars += $"{world.Colonies[a].Name} and {world.Colonies[b].Name}: {(s2 == Stance.War ? "<color=#ff5555>war</color>" : "<color=#ffd27a>truce</color>")}" +
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
                ? $"The game also autosaves every {world.autosaveInterval / 60f:0} minutes. F9 loads the quicksave."
                : "Autosave is off. F9 loads the quicksave.", labelSmall);

            ui.Space(8);
            ui.Label("Load", header);
            if (saves == null || Time.unscaledTime - savesListedAt > 5f) { saves = AntWorld.ListSaves(); savesListedAt = Time.unscaledTime; }
            if (saves.Count == 0) ui.Label("No saves yet.", labelSmall);
            foreach (var s in saves)
            {
                ui.Label($"<b>{s.Name}</b>   <color=#9fb0c8>{s.Written:MMM d, HH:mm}</color>\n<color=#9fb0c8>{s.Summary}</color>", labelSmall);
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
                " - hunting: dead mites, beetles, spiders and enemy ants become food\n" +
                " - aphid farms on bushes (Farming tab, F)\n" +
                " - a fungus garden fed with leaves (Farming tab, G)\n\n" +
                "<b>Camera</b>\n" +
                "WASD / arrows, right or middle drag: pan.  Mouse wheel: zoom\n\n" +
                "<b>Keys</b>\n" +
                "Space: pause      - / = : slower / faster\n" +
                "R: raid the spot under the mouse      X: call off\n" +
                "B: hunt the nearest beetle      F: place aphid farm      G: upgrade garden\n" +
                "N: pick a site for a new nest (Nests tab)\n" +
                "F5: quicksave      F9: load the quicksave (Game tab for more)\n" +
                "Tab: select next colony      P: pheromone view      H: hide panel\n" +
                (world.Sandbox ? "Left click: drop food.  T / Y: +25k workers / +5k soldiers\n" : "") + "\n" +
                "<b>Reading the map</b>\n" +
                "Green: bushes.  Gold: food and honeydew.  Coloured haze: trails.  Red haze: fighting.\n" +
                "Grey: rock.  Blue: water.  Tan: sand (slow going). Princesses and males fly over rock and water.\n" +
                "Ringed patches: aphid farms.  Pale spots in a nest: fungus garden.\n" +
                "Pale yellow, slim ants: scouts.  Pale blue soldiers: drones (escorts).\n" +
                "Bigger or smaller ants of your colour: castes (majors, minims...). See the Orders tab.\n" +
                "Big white-rimmed ant: queen.  Grey nest rim: no queen.\n" +
                "Gold winged ant: princess.  Small dark winged ants: males (swarm).  Dashed ring: a nest being founded.\n" +
                "Red dots: mites.  Green shells: beetles.  Black with legs: spiders.\n\n" +
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
            var text = new Color(0.92f, 0.92f, 0.94f);
            var dim = new Color(0.65f, 0.66f, 0.70f);
            label = new UiStyle { FontSize = 14, Text = text };
            labelSmall = new UiStyle { FontSize = 12, Text = dim };
            header = new UiStyle { FontSize = 15, FontStyle = FontStyle.Bold, Text = text, Height = 24f, Align = TextAnchor.LowerLeft };
            bigTitle = new UiStyle { FontSize = 26, FontStyle = FontStyle.Bold, Text = text };
            barLabel = new UiStyle { FontSize = 14, Text = text, Wrap = false };
            barLabelSmall = new UiStyle { FontSize = 12, Text = dim, Wrap = false };
            toggle = new UiStyle { FontSize = 14, Text = text };
            field = new UiStyle { FontSize = 15, Text = text };
            button = new UiStyle
            {
                FontSize = 13, Text = text, Align = TextAnchor.MiddleCenter, Wrap = false, Height = 26f,
                Background = new Color(0.22f, 0.22f, 0.27f), Hover = new Color(0.32f, 0.32f, 0.38f), Pressed = new Color(0.30f, 0.42f, 0.62f),
            };
            tabOff = button.Copy();
            tabOff.Background = new Color(0.18f, 0.18f, 0.22f);
            tabOn = button.Copy();
            tabOn.FontStyle = FontStyle.Bold;
            tabOn.Background = tabOn.Hover = new Color(0.30f, 0.42f, 0.62f);
            rowStyle = new UiStyle
            {
                FontSize = 14, Text = text, Align = TextAnchor.UpperLeft, Wrap = true,
                Background = new Color(1f, 1f, 1f, 0.03f), Hover = new Color(1f, 1f, 1f, 0.09f), Pressed = new Color(0.30f, 0.42f, 0.62f, 0.6f),
                Padding = new RectOffset(8, 8, 6, 6),
            };
            rowFocused = rowStyle.Copy();
            rowFocused.Background = rowFocused.Hover = new Color(0.30f, 0.42f, 0.62f, 0.45f);
            rowFocused.Text = Color.white;
        }

        static string FormatTime(float seconds)
        {
            int s = (int)seconds;
            return $"{s / 60}:{s % 60:00}";
        }
    }
}
