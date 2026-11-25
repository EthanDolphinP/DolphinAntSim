# Dolphin Ant Simulator

A colony simulator built for very large ant counts. Unity 2022.3, URP 2D, Burst, Jobs and Collections.

## Download and play (Windows)

1. Download `DolphinAntSimulator-vX.Y-Windows.zip` from the [Releases](https://github.com/EthanDolphinP/DolphinAntSim/releases) page.
2. Unzip it anywhere and run **Dolphin Ant Simulator.exe**. Nothing to install. Windows SmartScreen may warn about an unknown publisher the first time: choose *More info ▸ Run anyway*.
3. **Alt+Enter** switches between full screen and a window. Saves go to `%USERPROFILE%\AppData\LocalLow\Dolphin\Dolphin Ant Simulator\Saves`.

Needs 64-bit Windows 10 or 11 and a DirectX 11 graphics card.

## Building from source

Open this folder in Unity **2022.3.49f1** (Unity Hub ▸ Add ▸ this folder). To make a release build, use the menu **AntSim ▸ Build Windows Release**, or from a terminal:

```
"C:\Program Files\Unity\Hub\Editor\2022.3.49f1\Editor\Unity.exe" -batchmode -quit -projectPath . -executeMethod AntSim.EditorTools.AntSimBuild.BuildWindows -logFile build.log
```

The game lands in `Builds/Windows`. Zip that folder (leave out the `*_BurstDebugInformation_DoNotShip` folder) and attach the zip to a GitHub release.

### Making a release

With [GitHub CLI](https://cli.github.com/) signed in once (`gh auth login`), close the project in Unity and run:

```
.\release.ps1 1.2 "What's new in this version"
```

It sets the version, builds the .exe, zips it, commits and tags `v1.2`, pushes, and publishes the GitHub release with the zip attached. Leave out the notes to have GitHub list the commits instead.

**Update notice:** the start screen asks GitHub for the latest release once per run. If it's newer than the running version, it says so and offers a **Download** button that opens the release page. Nothing is downloaded or replaced automatically, and the check can be turned off in Settings.

### Discord announcements

`release.ps1` can post each release to a Discord channel (for example `#ant-updates`) and ping a role (for example `@Antvester`). One-time setup:

1. In Discord, open the channel's settings: **Integrations > Webhooks > New Webhook**, name it, and **Copy Webhook URL**.
2. Turn on **User Settings > Advanced > Developer Mode**, then in **Server Settings > Roles** right-click the role and **Copy Role ID**.
3. Make `announce.local.json` next to `release.ps1` (it's in `.gitignore`; the webhook link lets anyone post, so keep it out of git):

   ```json
   { "webhook": "https://discord.com/api/webhooks/...", "roleId": "123456789012345678" }
   ```

4. Check it with `.\announce.ps1 -Test` (posts a test line, no ping).

After that every `release.ps1` run announces the release with the notes and a download link. Use `-NoDiscord` to skip it, or `.\announce.ps1 1.2` (`-NoPing` for a quiet post) to announce an existing release by hand.

## Running it in the editor

Open any scene (e.g. `Assets/Scenes/SampleScene`) and press **Play**. The **start screen** appears: name your colony, pick a species and colour, the number of rivals, map size, terrain, wildlife and mode, then press **Start**. Saved games are listed at the top: **Continue** picks up the latest one.

- **Survival:** a little food at the start, then you have to earn it. Rivals stay peaceful for the first 10 minutes. Win by outlasting every rival colony; lose if your colony dies out.
- **Sandbox:** food keeps appearing, left click drops food, you can command any colony, and there's no win or lose.

To tune values in the Inspector, add an empty GameObject with the `AntWorld` component to the scene yourself. It then skips the menu and starts a sandbox game from the Inspector fields.

### Species

| Species | Strength | Weakness |
|---|---|---|
| Harvester ants | +25% food per foraging trip | none (balanced) |
| Leafcutter ants | Start with a fungus garden, double food per leaf | Weaker bite |
| Army ants | Fast, +40% bite, lay faster, more soldiers | Short lives, poor farmers |
| Dairy ants | Aphid farms cost half and give double honeydew | Slightly fragile |
| Fire ants | +30% bite for every ant | Cost 15% more to raise |

Each species is drawn with its own body shape. Rival colonies get a random species.

**Castes.** Besides plain workers and soldiers, each species has up to three extra castes. They are bigger or smaller ants of the colony's colour, with their own speed, health, bite and load:

| Species | Castes |
|---|---|
| Harvester | **Major** (12% of workers): carries two crumbs a trip, bites hard, slower |
| Leafcutter | **Minim** (20% of workers): stays home and tends the fungus garden (up to double garden speed) · **Major** (35% of soldiers): huge defenders |
| Army | **Submajor** (30% of soldiers) · **Major** (10% of soldiers): sabre-jawed giants · **Porter** (15% of workers): carries two crumbs |
| Dairy | **Nurse** (10% of workers): stays home and makes larvae up to 35% cheaper to feed |
| Fire | **Minim** (30% of workers): quick foragers · **Major** (25% of soldiers) |

The *Orders* tab shows how many of each caste you have and what they do; the start screen lists each species' castes.

**Species are data.** Each species is a `SpeciesDef` ScriptableObject in `Assets/AntSim/Resources/Species/`, loaded at start-up (sorted by `Order`). Edit one in the Inspector, or duplicate one to add a species (*Create ▸ AntSim ▸ Species*). Castes are a list on the asset (name, description, base caste, share, size, speed, health, bite, load, stays home, perk). If the folder is empty the built-in defaults are used, and *AntSim ▸ Write Default Species Assets* writes them out again.

### Food

After the starting caches run out, food comes from:

- **Hunting.** Every dead mite, beetle, spider or enemy ant becomes food on the ground. Beetles are a feast but take a crowd to bring down. *Orders ▸ Hunt beetle / Hunt spider* (or **B**) sends your soldiers after the nearest one. Hunts follow the prey and end when it's dead.
- **Aphid farms.** Place a herd on a green bush near your nest (*Farming* tab or **F**, then click). Aphids eat the plant, breed and drip **honeydew** (food) around the farm for your foragers. Beetles eat aphids, and any colony's ants can steal the honeydew, so farms are worth defending.
- **Fungus garden.** Build and upgrade it in the nest (*Farming* tab or **G**). Workers then also cut **leaves** from bushes (you'll see green bits in their jaws), and the garden turns stored leaves into food at a rate set by its level. Bushes regrow slowly.

### Wildlife

Bugs live in the same simulation as the ants (one extra "colony" slot that is hostile to everyone):

- **Mites:** small red prey that graze on bushes and flee from ants.
- **Beetles:** big green armoured grazers. They strip bushes, eat aphids and fight back hard.
- **Spiders:** black hunters with a long sense range. They hunt ants. Workers away from home flee them and raise the alarm.

Populations are topped up over time (off by choosing *Wildlife: None*). A beetle or spider in a colony's core wakes a sleeping colony.

### Nests and swarming

A colony can own several nests (up to 8). Food and brood are shared by all of them; every ant belongs to one nest and forages from it.

- **Founding a nest:** in the *Nests* tab, click **Pick a site** (or press **N**) and click the map, or **Let scouts choose** (they prefer plants and food nearby and keep away from enemies). The site must be 60–320 units from your nests and at least 80 from any rival nest.
- **The swarm:** the colony raises a **princess** (250 food, 45 s). She then flies straight to the site at high speed, ignoring fights, with a cloud of winged **males** and an escort of about 20% of the nearest nest's ants, who walk there and start foraging from the new home. A dashed ring marks the site. If she arrives she becomes the new nest's **queen**. If she's killed on the way, or doesn't arrive within 200 s, the swarm fails and the escort walks home.
- **More queens, more eggs:** each extra queen adds half the first queen's laying rate. New ants hatch in the nests that have a queen.
- **Losing a queen:** a nest whose queen dies is abandoned after 60 s if another nest still has a queen; its ants move there. If no nest has a queen, the colony tries to raise a replacement in the nest with the most ants.
- **Nesting rules:** turn on *Swarm automatically when crowded* and set how many ants per nest trigger a swarm and the most nests to own. Rivals always swarm (from 900 ants per nest, up to 4 nests), so they spread across the map.
- Sleeping colonies remember what share of their ants lives in each nest, forage around every nest, and wake each nest's ants at home. A sleeping colony founds nests instantly (nobody is watching the flight).

### Ant AI

- **Scouts and workers:** a small share of workers (5% by default, *Orders ▸ Scouts*) are **scouts**. They run 30% faster, see food 2.5 times further away, range twice as far, prefer fresh ground and lay the food trails. Ordinary workers follow trails closely (sharper turns, little wobble). Off the trails they're a bit hopeless: they amble about near home (within about 48 units), only notice food right in front of them, and go home when their trail runs out. Scouts are drawn pale yellow and slim.
- **Rival trails:** searching workers keep out of the busy food trails of colonies they're at war with. They're strongly put off when wandering, only slightly while on their own trail. Scouts barely care.
- **Dead-end detection:** a searching ant that sits at the top of a food trail with nothing uphill and no food in sight decides the source is gone. It erases the stale trail end, so others stop coming, and ignores trails for a few seconds while it wanders off. This stops the old loop where ants circled where food used to be; the scouts find the next source.
- **Distress calls ("death smell"):** every ant killed in battle marks a coarse per-colony distress map. Every 0.5 s the strongest spots become hotspots. Soldiers smell them from up to 320 units away, march there and then follow the alarm pheromone into the fight. *Orders ▸ Squads answering distress calls* sets what share of soldier squads responds; the rest guard the nest. Your top bar warns you when your ants are dying, with **Go** and **Send all** buttons.
- **Squads:** soldiers are organised in squads of about 20 (`SimParams.SquadSize`). Each squad holds its own sector around its nest and moves between posts in it as a group, so the nest is ringed by small patrols instead of one crowd. Squad 1 is the home guard and stays by the queen. Stragglers catch up with their squad, and members ahead of the pack wait for the rest. Raids go in squad by squad from several sides, and whole squads answer distress calls, spread over the different calls. Squads break up to fight (they follow the alarm pheromone), then regroup.
- **Drones (escorts):** a rare kind of soldier (10% of soldiers by default, *Orders ▸ Drones*), drawn pale blue. They form small squads of about 10 (`SimParams.DroneSquadSize`). Every 2 s each nest gets a list of up to 6 posts: the colony's aphid farms nearest that nest (up to 3), then the food sources its workers are busiest harvesting, scored as food on the ground times the strength of the colony's food trail there, at most 260 units away (`SimParams.DroneRange`). Each drone squad takes a post. On a food source the squad walks out along the workers' trail, stands guard at the food for a while, walks back to the near end of the trail and repeats. On a farm it circles the farm a post at a time. Drones go for any fight they smell nearby, but don't join raids or answer far-off distress calls. With nothing to guard, they wait near home. Posts keep their slot from one update to the next, so squads don't swap jobs.
- **Smooth motion:** ants ease toward their wanted speed (`SimParams.Acceleration`) instead of jumping between walking, waiting and charging. Squads blend smoothly between catching up, marching and holding a post. Ants are drawn where they'd be between the 30 Hz sim ticks, so movement looks fluid at any frame rate. The camera's keyboard pan eases in and out and its zoom glides (`AntCameraController.smoothing`).
- **Rival AI:** rivals build gardens and farms as they grow, hunt beetles and spiders near home, raise more soldiers when attacked, and raid weaker neighbours (you included) once they have an army.

**Performance tip:** in the editor, turn off **Jobs ▸ Burst ▸ Safety Checks** when you stress test. Safety checks are useful while you develop but make jobs several times slower.

### Terrain and maps

Every game gets a new map. Pick a style on the start screen:

- **Meadow:** open grass with a few boulders.
- **Rocky:** broken rock ridges (about 15% of the map); ants have to find the gaps.
- **Lakes:** ponds (about 20%) and a river crossing the map, with shallow sandy fords.
- **Mixed** (default): some rock, some ponds, sometimes a river.

**Rock** (grey) and **water** (blue) block ants and bugs. Ants feel ahead for them and follow the edge round (keeping to one side until they're clear); wandering ants just turn back. Their sensors treat obstacles as dead ends, so trails bend around them.

**Guard posts and raids:** soldiers' patrol posts are always on open ground the squad can walk to from the nest (not in a pond or across an inlet), and soldiers on the wrong side of water head home round it first. Raids and hunts get their own route map to the target, so squads walk round lakes to it. Soldiers going somewhere keep following an edge until the straight way to their goal is clear, and don't try to walk to squad-mates across water.

**Way home:** every nest has a coarse "way home" map (walking distance on 4×4 cells, going round rock and water), built when the nest appears or moves. An ant heading home walks straight at the nest while that works; when the straight line would cross a river or a ridge, it follows the map to the nearest ford or gap instead. **Sand** (tan, mostly on shores) slows ants to 60%. Princesses and winged males fly over everything.

The generator clears a border and a clearing around every starting nest, then flood-fills from the first nest: if a nest can't be reached, it digs a sandy path; walkable pockets nobody can reach become rock, so no food or bugs get stranded. Food, bushes and wildlife never spawn on rock or water, and you can't found a nest there. A 768 map generates in about 25 ms.

### Diplomacy

Every colony starts **at peace** with every other. Wars now come from how colonies get on, and they can end.

- **Opinion** (−100 to +100) is how one colony feels about another. It drops when they share a border (twice as fast when food is short), when they kill each other's ants, and a lot when one raids the other. A common enemy improves it, and quiet time apart drifts it back toward neutral (quickly in a lull during a war).
- **Stances:** *peace*, *war* or *truce*. A rival declares war once its opinion of a neighbour falls below −60, after the peaceful start, if it has at least half their soldiers (or really hates them). Colonies that aren't at war don't fight; their ants walk past each other.
- **Truces** last 3 minutes. Afterwards they become a lasting peace if both sides' opinions are above −40, or the war resumes. A rival offers a truce once it's losing, worn out by losses, has cooled off, or the war has dragged on for 8 minutes. Another rival accepts on the same grounds.
- **The Diplomacy tab** shows each colony's stance, its opinion of you, how much border you share, your recent losses to each other and any border fighting. You can **declare war**, **offer a truce**, **send a gift** (200 food, +15 opinion) and **accept or refuse** truces rivals offer you (they expire after 60 s). Breaking a truce costs you 50 opinion. It also lists the relations between the other colonies and the recent events. Wars and truce offers that concern you pop up as toasts.

### Sleeping-colony wars

When at least one of two colonies at war is asleep (off-screen), their battles are fought as numbers rather than ants:

- **Border wars:** colonies whose territories overlap commit a share of their army (15–40%, growing with the overlap) to the front. Each second, each side kills a number of the other's ants in proportion to its fighting strength (soldiers fully, workers a little, the species' attack) divided by the enemy species' health. Soldiers die first, never more than 85% of the colony in one go. The sleeping side's dead become food on the front.
- **Raids:** a sleeping rival that raids attacks statistically for up to 90 s, with 60% of its army against 80% of the defender's. If the defence breaks (fewer than 40 soldiers and under a quarter of the attacker's), or the siege ends with the attacker still more than three times stronger, both colonies wake up and the real ants storm the nearest nest.
- When an awake colony takes losses this way, the ants killed are real ones near the front.

### Saving and loading

- **F5** quicksaves, **F9** loads the quicksave. The *Game* tab saves under any name, lists your saves (load or delete), and quits to the menu. The game also **autosaves every 5 minutes**.
- Saves go to `Application.persistentDataPath/Saves/*.antsave`. Each is one gzip file: a header (format version, ant size, a summary for the save list, the game setup), the colony, nest and farm records stored field by field **by name** (so adding a field later doesn't break old saves), then the raw simulation arrays: every ant, pheromones (as 16-bit floats), food, leaves, terrain, plants, sleeping colonies' age bins and trails.
- A medium game with 4 colonies saves in about 120 ms to about 0.6 MB and loads in about 60–100 ms.

### Controls

| Input | Action |
|---|---|
| WASD / arrows, right or middle drag | Pan |
| Mouse wheel | Zoom at cursor |
| Left click | Place aphid farm (after F) · Pick nest site (after N) · Sandbox: drop food |
| Space | Pause |
| `-` / `=` | Halve / double sim speed |
| Tab | Select next colony |
| P | Pheromone view: all / focused colony / off |
| R | Raid: your soldiers march on the mouse position |
| X | Call off raid / hunt |
| B | Hunt the nearest beetle |
| F | Place an aphid farm (click a bush; Shift-click for several; Esc cancels) |
| G | Build / upgrade the fungus garden |
| N | Pick a site for a new nest (click the map; Esc cancels) |
| T / Y | Sandbox only: +25,000 workers / +5,000 soldiers |
| F5 / F9 | Quicksave / load the quicksave |
| H | Hide / show the side panel |
| Esc | Menu: resume, quicksave, save/load, settings, help, quit to main menu or desktop (also backs out of placing and sub-pages) |

**Esc menu and settings.** Esc (or **Menu** in the top bar) pauses the game and opens the menu. Quitting to the main menu or the desktop asks first and offers *Save and quit*. **Settings** (also on the start screen, next to *Start* and *Quit*) are remembered between sessions:

- **Display:** full screen or window, VSync, frame rate cap (when VSync is off).
- **Camera:** scroll and zoom speed, scrolling at the screen edge.
- **Interface:** interface size (same as A- / A+), FPS and ant counts in the top bar, pausing while the menu is open.
- **Saving:** autosave off or every 2, 5 or 10 minutes.

The HUD and start screen are real Unity UI (uGUI) built in code by `UiKit`, a small immediate-mode layer over pooled Canvas elements (no prefabs needed). The HUD scales with the window. The **top bar** shows your colony (ants, food, queen), time, pause/speed, panel toggle and **A- / A+** to resize the UI (remembered between sessions). An **alert strip** under it warns you about raids and dying ants. The **side panel** is draggable and has tabs: *Colonies* (click one to jump to it), *Orders*, *Nests*, *Farming*, *Diplomacy*, *Game* (save, load, quit) and *Help*. It scrolls if it doesn't fit.

## Architecture

```
AntWorld (MonoBehaviour, fixed 30 Hz tick)
 ├─ AssignBucketsJob  Burst, parallel. Spatial hash: one bucket grid per colony
 ├─ BuildHashJob      Burst. Counting sort of ants by bucket
 ├─ CombatJob         Burst, parallel. Nearest hostile within sense range, bite if in range (atomic damage)
 ├─ AntStepJob        Burst, parallel. Apply damage, fight or forage/patrol/raid, move, age, die
 ├─ EvaporateJob      Burst, parallel. Pheromone decay on every cell
 ├─ CompactJob        Burst. Removes dead ants and counts castes per colony
 ├─ UpdateColony      Main thread. Colony "brain": food, upkeep, queen, brood, caste rules
 ├─ EvaluateLod       Every 0.5 s: put unseen, peaceful colonies to sleep, wake them on demand
 ├─ DistressJob       Every 0.5 s: fade the distress map, pick hotspots for soldiers
 ├─ UpdateEcology     Every 1 s: plants regrow, aphid farms, wildlife spawns, rival AI, win/lose
 └─ Render            One instanced draw call for all ants plus one texture for the ground
```

- **Ants** are plain structs (`Ant`) in a `NativeList`. There are no GameObjects per ant.
- **Pheromones** are per-colony grids with three channels: *to home* (laid by ants searching for food), *to food* (laid by ants carrying food home) and *alarm* (laid by ants that are fighting). Ants only sample three cells ahead of them, so there is no pathfinding.
- **Food and leaves** are grids of ints. Pickups are atomic, so nothing is ever duplicated.
- **Wildlife** shares the ant list under colony slot `WildColony` (= colony count), with the bug kind in the `Caste` field. That way combat, the spatial hash and rendering all work for bugs at no extra cost.
- **Growth has no cap.** Egg laying scales with population and is limited only by stored food (minus a reserve). Food on the map is the real constraint.
- **Queen and brood:**
  - The queen is a real ant: large, white-rimmed, drawn on top, and she stays in the nest. She has 600 HP and fights back. She doesn't age or starve, so only combat can kill her.
  - Only a living queen lays eggs. Brood is stored as numbers, not ants: cohorts go from **egg (10 s) → larva (25 s, must be fed) → pupa (15 s) → adult**. Unfed larvae die.
  - **If the queen dies,** laying stops and the nest rim turns grey. With the *Raise a new queen* rule on (and at least 40 ants and 150 food), the colony raises a replacement over 60 s. Otherwise it dies out once its brood and adults are gone.
- **Colony logic works on totals, never individual ants.** This is where player rules live (soldier ratio for now).
- **Combat:** the hash is split per colony, so an ant only scans buckets of colonies it is at war with. Friendly crowds cost nothing. Neighbour checks are capped per ant, so huge melees stay bounded.
  - Fighting ants lay **alarm pheromone** (fast-decaying, shown red). Soldiers within their leash follow it, so battles recruit reinforcements on their own.
  - Dead ants become **food on the ground**, so winning a war feeds the colony.
  - **Diplomacy** is a hostility matrix (`SetWar`) driven by `AntWorld.Diplomacy.cs`: opinions, stances and truces updated once a second, plus the statistical wars of sleeping colonies. Everyone starts at peace. The combat job counts damage per colony pair, which feeds the opinions.
  - **Raids** are one-off orders (`OrderRaid`). Soldiers march to the target, answer alarms there and roam around it. Upkeep still applies, so a huge army can starve its own colony.

### LOD: sleeping colonies

The default map is 1024×1024 with 6 colonies. Only colonies you can see (or that are under attack) are simulated ant by ant. The others **sleep**, and the top bar shows *total* vs *live* ants.

- **Falling asleep:** a colony sleeps when its nest area is off-screen, nothing is fighting near its nest, it isn't raiding or being raided, and it has been observed awake for 2 minutes (so its forage rate is known). Its ants are removed and stored as **counts in 10-second age bins**, so they still die of old age on schedule. Its pheromone trails are frozen, not evaporated.
- **While asleep:** births, brood, queen and upkeep run exactly as normal. Foraging is statistical. The colony eats map food from inside its territory at the rate it measured while awake, and it ranges further if food runs short. Cost is constant, whatever the size: a 3-million-ant sleeping colony doesn't change the tick time.
- **Waking up:** when you pan to it, ≥10 enemies reach its core, a raid targets it, or it fights near its nest. Ants respawn **on its own preserved trails** with their real ages, so the colony picks up where it left off. A woken colony stays awake for at least 60 s to avoid flicker.
- Toggle it in *Rules ▸ LOD*. Tuning values are in `AntWorld ▸ Lod` in the Inspector.
- **Known limits:** sleeping colonies don't fight each other or the wildlife (wars and predators only matter where ants are live; a beetle or spider in a colony's core wakes it). Sleeping colonies grow a bit differently from fully simulated ones. In 10-minute tests the LOD total came out at 55–99% of the full-sim total, depending on the map.

Files: `Assets/AntSim/Scripts/` (C#), `Assets/AntSim/Resources/` (shaders). The main parts:

- `AntWorld.cs`: tick loop, economy and LOD.
- `AntWorld.Ecology.cs`: plants, farms, wildlife, distress, rival AI, win/lose.
- `AntGameMenu.cs`: the start screen. `AntSimHud.cs`: the in-game HUD. `UiKit.cs`: the UI toolkit both use.
- `SpeciesDef.cs` / `SpeciesLibrary.cs`: species and caste assets and their loader.
- `Species.cs`: game setup, wildlife, farming and swarm tuning.
- `AntWorld.Terrain.cs`, `TerrainJobs.cs`: terrain and map generation.
- `AntWorld.Save.cs`: save files.
- `AntWorld.Nests.cs`: nest slots, swarming (princess, flight, founding, failure), abandonment, nest sites.

## Measured so far (headless, editor, safety checks on)

- 4 colonies grow from 1,600 to about 10,000 ants in 8 sim-minutes, limited by food.
- About 200,000 ants run at about 4 ms per tick without combat and about 9 ms with combat.
- In 5 sim-minutes, colonies fight naturally over shared food (200–750 battle deaths each).
- A 20,000-soldier raid wiped out the target colony's army in 90 s. It then starved the raider, because army upkeep is real.
- **LOD:** a 3,000,000-ant sleeping colony costs nothing: 3.3 ms/tick vs 3.65 ms baseline. Waking 400,000 ants onto trails takes about 36 ms, and 400k live ants then tick at about 14 ms. Panning to a sleeping colony, raiding it, and letting it fall asleep again all work.
- **Survival game (4 rivals, 768 map, AI playing every colony incl. yours):** colonies build fungus gardens (up to 13 food/s) and 5–7 aphid farms each (up to 27 food/s of honeydew), hunt beetles, and reach 1,100–2,100 ants by minute 12. War raids begin after the 10-minute peaceful start. Tick stays around 2.5 ms because off-screen rivals sleep.
- **Distress calls:** 3 spiders hunting foragers 120 units from the nest. With 60% of soldiers answering, about 150 soldiers arrive within 20 s and kill all three (21 workers lost). With 0%, the spiders survive and keep killing.
- **Dead ends:** after a food source runs out, about 30 ants mill around the empty spot, falling to a handful within 90 s. Without the dead-end logic it was 60–120 for over a minute (the numbers are noisy run to run).
- **Performance with wildlife:** 200,000 ants plus about 1,000 bugs at 8.8 ms per tick (the same as before wildlife).
- **Swarming:** a manual swarm launches 24 winged males and 237 escorts; the princess flies about 140 units in about 18 s and is crowned. Killing the new queen gets that nest abandoned 60 s later, with every ant moved home. Killing a princess in flight fails the swarm cleanly: the site is freed, males vanish and all 185 escorts walk home. In a 15-minute AI game every colony founds a second nest by minute 10. Once war starts, nests lose queens and get abandoned or regrow. A sleeping rival expands to 4 nests and wakes with its ants split by nest (46/34/7/13%).
- **Performance with nests:** 200,000 ants at 9.4 ms per tick. Drawing nests and checking for intruders now cost the same however many nests there are.
- **Regicide test:** a 3,000-soldier raid killed Azure's queen. Azure raised a new one within about 60 s and recovered to about 1,300 ants. Amber, with the rule off, lost its queen, finished hatching its brood and dwindled to 55 workers.
- **Drones:** with 300 soldiers at 20% drones (79 drones in 8 squads), food 90 units out and an aphid farm on a bush: drones spend half their time at the farm and 13% at the food source, and at any moment most are along the trail or at the farm. Of 290 guards, none were at the farm. Drones stay 3–5 units from their squad centre. With 0% drones, no soldier guards the trail or farm on purpose. In a 12-minute AI game every colony fields 2–6 drone squads guarding 3–11 food trails and 3–6 farms.
- **Cost of drones and smooth motion:** the AI game ticks the same with drones and eased speed as without either (2.4–3.5 ms). 200,000 ants run at 12.7–12.9 ms per tick, against 10.7–12.0 ms last phase; instant acceleration gives 11.8 ms. So about 1 ms comes from ants moving a little differently, and the rest is within run-to-run noise.
- **Castes:** about 20% of new ants hatch as a species caste, and the share holds in every test game. Caste counts survive sleep, waking and save/load.
- **Terrain:** across all four map styles, 0 of about 2,300 ants ever stood inside rock or water after 90 s, and every colony kept foraging. Cover: Meadow 3% rock; Rocky 16% rock; Lakes 19% water, 10% sand; Mixed 7% rock, 9% water.
- **Save/load round trip:** a 4-colony game saved after 4 minutes (3 rivals asleep) reloads with every count identical (ants, castes, pheromone total, terrain, nests, farms, food, brood), then plays on normally for 2 more minutes.
- **Performance with terrain and castes:** 200,000 ants at 11.0–12.0 ms per tick on Meadow, Mixed and Rocky maps, so obstacles and castes cost nothing measurable.
- **Trapped ants:** 18 Lakes, Mixed and Rocky maps, counting ants that spend 40 s pressed against rock or water without getting anywhere: 122 with the old code, 31 now (mostly soldiers sent to posts across water before). 150,000 ants tick 0.3–0.7 ms slower for it (about 11 ms).
- **Way home:** 200 food-carrying ants released on the far side of a river from their nest, on 6 maps. Before, 131–188 were still stranded after 2 minutes, crowding the shore. With the way-home map, 0–8 are left after 90–120 s. 200,000 ants still tick at 11.7–12.6 ms.
- **Sleeping border war:** two sleeping colonies at war with overlapping territories lose about 40 ants each per 30 s, and their opinions of each other fall to about −60 within 2 minutes. A 1,770-soldier statistical raid wore a 260-soldier defence down to about 130 in 60 s, then both colonies woke and the attacker stormed the nest.
- **Diplomacy in AI games (25 minutes, 4 colonies, 512 and 768 maps):** wars break out between neighbours from minute 9–13 on. Truces follow within a few minutes; some lapse back into war and some become a lasting peace. Distant colonies on a 768 map mostly stay at peace. Relations survive save/load unchanged. Ticks stay at 1.4–2.1 ms.

## Roadmap

1. ~~Stress test and rendering~~ ✅
2. ~~Foraging with pheromones, food economy, growth~~ ✅
3. ~~Combat: spatial hash, alarm recruitment, raids, war/peace~~ ✅
4. ~~Brood and queen: egg/larva/pupa pipeline, queen unit, killing the queen, raising a replacement, collapse~~ ✅
5. ~~LOD simulation: unseen colonies sleep as age-binned numbers and forage statistically, and wake onto their trails when seen or attacked~~ ✅
6. ~~Sleeping-colony wars: statistical battles between sleeping colonies whose territories overlap~~ ✅
7. ~~Gameplay: start screen, species, earned food (hunting, aphids, fungus), wildlife, distress calls, rival AI, win/lose~~ ✅
8. ~~New nests from swarming: princesses, winged males, escorts, multiple queens, nesting rules, abandonment~~ ✅
9. **More rules and commands:** forage radius, migrate whole nests, defend a nest, per-nest orders
10. ~~Species as data: ScriptableObject species, more castes per species~~ ✅
11. ~~Diplomacy: relations between colonies driven by contact, war and truces~~ ✅
12. ~~Terrain/obstacles, map generation, save/load, real UI~~ ✅
