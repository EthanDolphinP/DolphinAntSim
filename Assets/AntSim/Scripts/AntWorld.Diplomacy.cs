using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace AntSim
{
    public enum Stance { War, Peace, Truce }

    /// <summary>Tuning for relations between colonies.</summary>
    [Serializable]
    public struct DiplomacyParams
    {
        [Tooltip("Opinion lost per second when territories overlap completely (competition for food).")]
        public float ContactPenalty;
        [Tooltip("Opinion lost per own ant killed by the other colony.")]
        public float LossPenalty;
        [Tooltip("Opinion lost per second while the other colony raids one of our nests.")]
        public float RaidPenalty;
        [Tooltip("Opinion regained per second (toward neutral) when out of contact and not fighting.")]
        public float Recovery;
        [Tooltip("Opinion gained per second per shared enemy.")]
        public float CommonEnemyBonus;
        [Tooltip("An AI declares war when its opinion falls below this.")]
        public float WarThreshold;
        [Tooltip("An AI accepts a truce when its opinion is above this (or it is losing).")]
        public float TruceThreshold;
        [Tooltip("When a truce ends it becomes peace if both opinions are above this; otherwise war resumes.")]
        public float PeaceThreshold;
        public float TruceLength;
        [Tooltip("Wars last at least this long before anyone asks for a truce.")]
        public float MinWarTime;
        public float GiftFood;
        public float GiftOpinion;
        [Tooltip("Opinion lost by the betrayed colony when someone breaks a truce (others lose a third of it).")]
        public float BetrayalPenalty;
        [Tooltip("Sleeping-colony battles: ants killed per second per engaged fighter.")]
        public float BattleKillRate;

        public static DiplomacyParams Default => new DiplomacyParams
        {
            ContactPenalty = 0.15f,
            LossPenalty = 0.25f,
            RaidPenalty = 2f,
            Recovery = 0.05f,
            CommonEnemyBonus = 0.04f,
            WarThreshold = -60f,
            TruceThreshold = -45f,
            PeaceThreshold = -40f,
            TruceLength = 180f,
            MinWarTime = 120f,
            GiftFood = 200f,
            GiftOpinion = 15f,
            BetrayalPenalty = 50f,
            BattleKillRate = 0.012f,
        };
    }

    /// <summary>
    /// Relations between colonies, and wars between colonies that are asleep.
    ///
    /// Every colony has an opinion (-100..100) of every other. Overlapping territories (competition for food),
    /// ants lost to them and raids on our nests make it worse; time apart, shared enemies and gifts make it better.
    /// Rival AIs declare war when their opinion is low enough (never in the peaceful opening), ask for a truce when
    /// a war goes badly or has cooled off, and a truce turns into peace when it ends unless tempers are still high.
    ///
    /// Sleeping colonies have no live ants to fight with, so when two colonies at war have overlapping territories
    /// and at least one is asleep, the border battle is resolved by numbers once a second: each side commits a share
    /// of its army depending on how much the territories overlap, kills in proportion to its engaged strength, and the
    /// dead become food on the front. An awake side really loses ants near the front (which raises a distress call).
    /// Sleeping AIs raid the same way; if the defender is all but beaten, both wake up for the real assault on the nest.
    /// </summary>
    public partial class AntWorld
    {
        public DiplomacyParams diplomacy = DiplomacyParams.Default;

        NativeArray<int> pairDamage; // [attacker * MaxColonies + victim], damage x100 dealt by live ants since last update
        readonly List<string> diplomacyLog = new List<string>();
        /// <summary>Recent diplomatic events, newest first.</summary>
        public IReadOnlyList<string> DiplomacyLog => diplomacyLog;

        void AllocateDiplomacy()
        {
            pairDamage = new NativeArray<int>(MaxColonies * MaxColonies, Allocator.Persistent);
            killRef = new NativeReference<int>(Allocator.Persistent);
        }

        void DisposeDiplomacy()
        {
            if (pairDamage.IsCreated) pairDamage.Dispose();
            if (killRef.IsCreated) killRef.Dispose();
        }

        /// <summary>New games: colonies start at peace and neutral; wars come from contact.</summary>
        void StartDiplomacy()
        {
            for (int a = 0; a < colonyCount; a++)
            for (int b = 0; b < colonyCount; b++)
                if (a != b) hostile[a * MaxColonies + b] = 0;
            EnsureDiplomacy();
        }

        /// <summary>Makes sure every colony has its relation arrays (old saves don't).</summary>
        void EnsureDiplomacy()
        {
            for (int c = 0; c < colonyCount; c++)
            {
                var col = Colonies[c];
                if (col.Opinion != null && col.Opinion.Length == colonyCount) continue;
                col.Opinion = new float[colonyCount];
                col.TruceLeft = new float[colonyCount];
                col.LossesTo = new float[colonyCount];
                col.WarSince = new float[colonyCount];
                for (int o = 0; o < colonyCount; o++)
                    if (o != c && IsAtWar(c, o)) { col.Opinion[o] = -60f; col.WarSince[o] = SimTime; }
            }
            for (int c = 0; c < colonyCount; c++)
            {
                var col = Colonies[c];
                if (col.Contact == null || col.Contact.Length != colonyCount) col.Contact = new float[colonyCount];
                if (col.FrontLosses == null || col.FrontLosses.Length != colonyCount) col.FrontLosses = new float[colonyCount];
                if (col.OfferCooldown == null || col.OfferCooldown.Length != colonyCount) col.OfferCooldown = new float[colonyCount];
            }
        }

        public Stance StanceOf(int a, int b)
        {
            if (a == b) return Stance.Peace;
            if (IsAtWar(a, b)) return Stance.War;
            var col = Colonies[a];
            return col.TruceLeft != null && col.TruceLeft[b] > 0f ? Stance.Truce : Stance.Peace;
        }

        /// <summary>How colony a feels about colony b (-100..100).</summary>
        public float OpinionOf(int a, int b) => Colonies[a].Opinion != null ? Colonies[a].Opinion[b] : 0f;

        /// <summary>0..1: how much the two colonies' territories overlap (last diplomacy update).</summary>
        public float ContactBetween(int a, int b) => Colonies[a].Contact != null ? Colonies[a].Contact[b] : 0f;

        public static string OpinionWord(float v) =>
            v <= -60f ? "hostile" : v <= -25f ? "wary" : v < 25f ? "neutral" : v < 60f ? "friendly" : "allied";

        void Log(string message, bool toast)
        {
            diplomacyLog.Insert(0, $"{FormatClock(SimTime)}  {message}");
            if (diplomacyLog.Count > 12) diplomacyLog.RemoveAt(diplomacyLog.Count - 1);
            if (toast) Notify(message);
        }

        static string FormatClock(float t) => $"{(int)t / 60}:{(int)t % 60:00}";

        void ChangeOpinion(int of, int about, float delta)
        {
            var op = Colonies[of].Opinion;
            op[about] = math.clamp(op[about] + delta, -100f, 100f);
        }

        // ------------------------------------------------------------------ changing stance

        void BeginWar(int a, int b)
        {
            SetWar(a, b, true);
            Colonies[a].TruceLeft[b] = Colonies[b].TruceLeft[a] = 0f;
            Colonies[a].WarSince[b] = Colonies[b].WarSince[a] = SimTime;
        }

        void BeginTruce(int a, int b)
        {
            SetWar(a, b, false);
            Colonies[a].TruceLeft[b] = Colonies[b].TruceLeft[a] = diplomacy.TruceLength;
            ChangeOpinion(a, b, 15f);
            ChangeOpinion(b, a, 15f);
            // Call off raids on each other.
            foreach (var (x, y) in new[] { (a, b), (b, a) })
            {
                var cx = Colonies[x];
                if (cx.RaidActive && cx.AiRaidKind == 0)
                {
                    int near = NearestNest(y, cx.RaidTarget, false);
                    if (near >= 0 && math.distance((float2)cx.RaidTarget, nests[near].Position) < 60f) CancelRaid(x);
                }
                if (cx.StatRaidTarget == y) cx.StatRaidTarget = -1;
            }
        }

        /// <summary>Player (or sandbox-commanded colony) declares war. Breaking a truce angers everyone.</summary>
        public string DeclareWar(int me, int other)
        {
            if (IsAtWar(me, other)) return "Already at war.";
            if (Colonies[other].Extinct) return "That colony is gone.";
            bool betrayal = StanceOf(me, other) == Stance.Truce;
            BeginWar(me, other);
            ChangeOpinion(other, me, -30f);
            if (betrayal)
            {
                ChangeOpinion(other, me, -diplomacy.BetrayalPenalty);
                for (int o = 0; o < colonyCount; o++)
                    if (o != me && o != other) ChangeOpinion(o, me, -diplomacy.BetrayalPenalty / 3f);
            }
            Log($"{Colonies[me].Name} declared war on {Colonies[other].Name}{(betrayal ? ", breaking the truce" : "")}.", false);
            return betrayal ? $"You broke the truce with {Colonies[other].Name}. Every colony trusts you less." : null;
        }

        /// <summary>Asks another colony for a truce. Returns a message for the player.</summary>
        public string OfferTruce(int me, int other)
        {
            if (!IsAtWar(me, other)) return "You're not at war with them.";
            var them = Colonies[other];
            if (them.OfferCooldown[me] > 0f) return $"{them.Name} won't hear of it again so soon.";
            if (WouldAcceptTruce(other, me))
            {
                BeginTruce(me, other);
                Log($"{Colonies[me].Name} and {them.Name} agreed a truce.", false);
                return $"{them.Name} accepted a truce ({diplomacy.TruceLength / 60f:0} minutes).";
            }
            them.OfferCooldown[me] = 60f;
            return $"{them.Name} refused. They are {OpinionWord(them.Opinion[me])} toward you ({them.Opinion[me]:0}).";
        }

        /// <summary>Sends food as a gift: it improves their opinion of you.</summary>
        public string SendGift(int me, int other)
        {
            var col = Colonies[me];
            if (col.Food < diplomacy.GiftFood) return $"A gift costs {diplomacy.GiftFood:0} food.";
            col.Food -= diplomacy.GiftFood;
            Colonies[other].Food += diplomacy.GiftFood;
            ChangeOpinion(other, me, diplomacy.GiftOpinion);
            return $"{Colonies[other].Name} accepted your gift. They are now {OpinionWord(Colonies[other].Opinion[me])} ({Colonies[other].Opinion[me]:0}).";
        }

        /// <summary>The player answers a truce offer from a rival.</summary>
        public void AnswerTruceOffer(bool accept)
        {
            var me = Colonies[Player];
            int from = me.TruceOfferFrom;
            me.TruceOfferFrom = -1;
            if (from < 0 || !IsAtWar(Player, from)) return;
            if (accept)
            {
                BeginTruce(Player, from);
                Log($"You agreed a truce with {Colonies[from].Name}.", true);
            }
            else
            {
                ChangeOpinion(from, Player, -10f);
                Colonies[from].OfferCooldown[Player] = 120f;
                Log($"You refused {Colonies[from].Name}'s truce.", false);
            }
        }

        bool WouldAcceptTruce(int c, int other)
        {
            var col = Colonies[c];
            var them = Colonies[other];
            if (SimTime - col.WarSince[other] < diplomacy.MinWarTime * 0.5f && col.Opinion[other] < -80f) return false;
            bool losing = col.Soldiers < them.Soldiers * 0.6f;
            // Long wars get tiresome, unless the enemy is nearly finished.
            bool tired = SimTime - col.WarSince[other] > 480f && them.Soldiers > col.Soldiers * 0.25f;
            return col.Opinion[other] > diplomacy.TruceThreshold || losing || tired || Weariness(c, other) > 0.08f;
        }

        /// <summary>Share of the colony lost to this enemy recently.</summary>
        float Weariness(int c, int other)
        {
            var col = Colonies[c];
            return col.LossesTo[other] / math.max(60f, col.Population + col.LossesTo[other]);
        }

        // ------------------------------------------------------------------ once a second

        void UpdateDiplomacy(float dt)
        {
            EnsureDiplomacy();
            var d = diplomacy;
            float decay = math.pow(0.5f, dt / 120f); // remembered losses fade with a 2-minute half-life

            // Territory overlap for every pair.
            for (int a = 0; a < colonyCount; a++)
            for (int b = a + 1; b < colonyCount; b++)
            {
                float ov = Colonies[a].Extinct || Colonies[b].Extinct ? 0f : Overlap(a, b, out _, out _);
                Colonies[a].Contact[b] = Colonies[b].Contact[a] = ov;
            }

            // Losses inflicted by live ants since the last update (damage / typical health = ants).
            float antHp = (combat.WorkerHealth * 3f + combat.SoldierHealth) * 0.25f;
            TakeCaptives(antHp);
            for (int c = 0; c < colonyCount; c++)
            {
                var col = Colonies[c];
                for (int o = 0; o < colonyCount; o++)
                {
                    if (o == c) continue;
                    float lost = pairDamage[o * MaxColonies + c] * 0.01f / antHp + col.FrontLosses[o] * dt;
                    col.LossesTo[o] = col.LossesTo[o] * decay + lost;
                    col.OfferCooldown[o] = math.max(0f, col.OfferCooldown[o] - dt);

                    var stance = StanceOf(c, o);
                    float change = -d.LossPenalty * math.min(lost, 40f);
                    if (stance != Stance.War) change -= d.ContactPenalty * col.Contact[o] * (col.Starving ? 2f : 1f);
                    if (RaidingNestOf(o, c)) change -= d.RaidPenalty;
                    int shared = 0;
                    for (int x = 0; x < colonyCount; x++)
                        if (x != c && x != o && !Colonies[x].Extinct && IsAtWar(c, x) && IsAtWar(o, x)) shared++;
                    change += shared * d.CommonEnemyBonus;
                    // Nobody dying: tempers cool toward neutral. At peace that needs distance as well (a shared border
                    // keeps grating); in a war any lull in the fighting helps, and quite quickly.
                    bool quiet = lost < 0.05f && !RaidingNestOf(o, c);
                    if (quiet && (stance == Stance.War || col.Contact[o] < 0.05f))
                    {
                        float rate = stance == Stance.War ? d.Recovery * 5f : d.Recovery;
                        col.Opinion[o] += math.clamp(0f - col.Opinion[o], -rate * dt, rate * dt);
                    }
                    col.Opinion[o] = math.clamp(col.Opinion[o] + change * dt, -100f, 100f);
                }
            }
            // Clear afterwards: each colony's row is read again for every victim above.
            for (int c = 0; c < colonyCount; c++)
                for (int x = 0; x < MaxColonies; x++) pairDamage[c * MaxColonies + x] = 0;

            // Truces run out.
            for (int a = 0; a < colonyCount; a++)
            for (int b = a + 1; b < colonyCount; b++)
            {
                var ca = Colonies[a];
                if (ca.TruceLeft[b] <= 0f) continue;
                ca.TruceLeft[b] = Colonies[b].TruceLeft[a] = math.max(0f, ca.TruceLeft[b] - dt);
                if (ca.TruceLeft[b] > 0f) continue;
                bool peace = ca.Opinion[b] > d.PeaceThreshold && Colonies[b].Opinion[a] > d.PeaceThreshold;
                if (peace) Log($"The truce between {ca.Name} and {Colonies[b].Name} has become a lasting peace.", a == Player || b == Player);
                else
                {
                    BeginWar(a, b);
                    Log($"The truce between {ca.Name} and {Colonies[b].Name} has ended: war again.", a == Player || b == Player);
                }
            }

            // Truce offers to the player expire after a minute.
            var me = Colonies[Player];
            if (me.TruceOfferFrom >= 0 && SimTime - me.TruceOfferTime > 60f)
            {
                Colonies[me.TruceOfferFrom].OfferCooldown[Player] = 120f;
                me.TruceOfferFrom = -1;
            }

            // AI decisions.
            for (int c = 0; c < colonyCount; c++)
            {
                var col = Colonies[c];
                if (c == Player || col.Extinct) continue;
                for (int o = 0; o < colonyCount; o++)
                {
                    if (o == c || Colonies[o].Extinct) continue;
                    var other = Colonies[o];
                    if (other.Workers + other.Soldiers == 0 && !other.Abstract) continue; // nobody left to fight
                    var stance = StanceOf(c, o);
                    if (stance == Stance.Peace && SimTime > PeacefulStart && col.Opinion[o] < d.WarThreshold
                        && (col.Soldiers >= other.Soldiers * 0.5f || col.Opinion[o] < -90f))
                    {
                        BeginWar(c, o);
                        ChangeOpinion(o, c, -20f);
                        Log($"{col.Name} declared war on {(o == Player ? "you" : other.Name)}!", o == Player);
                    }
                    else if (stance == Stance.War && SimTime - col.WarSince[o] > d.MinWarTime && col.OfferCooldown[o] <= 0f)
                    {
                        bool losing = col.Soldiers < other.Soldiers * 0.35f && col.LossesTo[o] > 20f;
                        bool cooled = col.Opinion[o] > -35f;
                        bool tired = SimTime - col.WarSince[o] > 480f;
                        if (!(losing || cooled || tired || Weariness(c, o) > 0.12f)) continue;
                        col.OfferCooldown[o] = 90f;
                        if (o == Player)
                        {
                            if (me.TruceOfferFrom < 0)
                            {
                                me.TruceOfferFrom = c;
                                me.TruceOfferTime = SimTime;
                                Log($"{col.Name} offers you a truce (Diplomacy tab).", true);
                            }
                        }
                        else if (WouldAcceptTruce(o, c))
                        {
                            BeginTruce(c, o);
                            Log($"{col.Name} and {other.Name} agreed a truce.", false);
                        }
                    }
                }
            }
        }

        bool RaidingNestOf(int raider, int victim)
        {
            var r = Colonies[raider];
            if (r.StatRaidTarget == victim) return true;
            if (!r.RaidActive || r.AiRaidKind != 0) return false;
            int near = NearestNest(victim, r.RaidTarget, false);
            return near >= 0 && math.distance((float2)r.RaidTarget, nests[near].Position) < 60f;
        }

        /// <summary>
        /// 0..1: how far two colonies' territories overlap, over their closest pair of nests, plus the middle of the
        /// overlap (the front) and the nests involved.
        /// </summary>
        float Overlap(int a, int b, out float2 front, out int2 nestPair)
        {
            float ra = Colonies[a].TerritoryRadius, rb = Colonies[b].TerritoryRadius;
            float best = 0f;
            front = default;
            nestPair = new int2(-1);
            for (int i = 0; i < nestHigh; i++)
            {
                var ni = nestInfo[i];
                if (ni == null || ni.Colony != a || ni.Founding) continue;
                for (int j = 0; j < nestHigh; j++)
                {
                    var nj = nestInfo[j];
                    if (nj == null || nj.Colony != b || nj.Founding) continue;
                    float2 pa = nests[i].Position, pb = nests[j].Position;
                    float dist = math.distance(pa, pb);
                    float o = ra + rb - dist;
                    if (o <= 0f) continue;
                    float frac = math.saturate(o / (0.5f * math.min(ra, rb)));
                    if (frac <= best) continue;
                    best = frac;
                    float2 dir = dist > 1e-3f ? (pb - pa) / dist : new float2(1f, 0f);
                    front = pa + dir * math.clamp(ra - o * 0.5f, 0f, dist);
                    nestPair = new int2(i, j);
                }
            }
            return best;
        }

        // ------------------------------------------------------------------ sleeping wars

        /// <summary>Border battles (and raids) between colonies at war where at least one of them is asleep.</summary>
        void UpdateSleepWars(float dt)
        {
            EnsureDiplomacy();
            for (int c = 0; c < colonyCount; c++)
                for (int o = 0; o < colonyCount; o++)
                    Colonies[c].FrontLosses[o] = 0f;

            for (int a = 0; a < colonyCount; a++)
            for (int b = a + 1; b < colonyCount; b++)
            {
                var ca = Colonies[a];
                var cb = Colonies[b];
                if (ca.Extinct || cb.Extinct || !IsAtWar(a, b) || (!ca.Abstract && !cb.Abstract)) continue;

                float frac = Overlap(a, b, out float2 front, out int2 pair);
                bool aRaids = ca.StatRaidTarget == b, bRaids = cb.StatRaidTarget == a;
                if (frac <= 0f && !aRaids && !bRaids) continue;
                if (aRaids || bRaids)
                {
                    // The battle is at the defender's nest closest to the attacker.
                    int att = aRaids ? a : b, def = aRaids ? b : a;
                    int dn = NearestNest(def, NestPos(att), false);
                    if (dn >= 0) front = nests[dn].Position;
                    frac = 1f;
                }

                // Committed forces: a share of the army that grows with the overlap; raids commit far more.
                float fa = aRaids ? 0.6f : bRaids ? 0.8f : 0.15f + 0.25f * frac;
                float fb = bRaids ? 0.6f : aRaids ? 0.8f : 0.15f + 0.25f * frac;
                float powerA = Strength(a) * fa * frac, powerB = Strength(b) * fb * frac;
                float killsOnB = diplomacy.BattleKillRate * powerA / SpeciesOf(b).Health * dt;
                float killsOnA = diplomacy.BattleKillRate * powerB / SpeciesOf(a).Health * dt;
                int lostA = TakeLosses(a, killsOnA, front, frac);
                int lostB = TakeLosses(b, killsOnB, front, frac);
                ca.FrontLosses[b] = lostA / dt;
                cb.FrontLosses[a] = lostB / dt;
                // Dead from the sleeping side(s) become food on the front (the awake side's bodies drop where they fall).
                if (ca.Abstract) DropBodies(lostA, front);
                if (cb.Abstract) DropBodies(lostB, front);

                // A beaten defender: wake both sides and let the real ants storm the nest.
                for (int k = 0; k < 2; k++)
                {
                    int att = k == 0 ? a : b, def = k == 0 ? b : a;
                    var cAtt = Colonies[att];
                    var cDef = Colonies[def];
                    if (cAtt.StatRaidTarget != def) continue;
                    // Storm once the defence is broken, or when the siege ends with the attacker still far stronger.
                    bool broken = cDef.Soldiers < cAtt.Soldiers * 0.25f && cDef.Soldiers < 40;
                    bool siegeOver = cAtt.StatRaidLeft <= dt && cDef.Soldiers < cAtt.Soldiers * 0.3f;
                    if (broken || siegeOver)
                    {
                        cAtt.StatRaidTarget = -1;
                        int dn = NearestNest(def, NestPos(att), false);
                        Wake(att);
                        Wake(def);
                        if (dn >= 0) OrderRaid(att, nests[dn].Position);
                        Log($"{cAtt.Name} is storming a nest of {cDef.Name}.", false);
                    }
                }
            }

            // Statistical raids run out.
            for (int c = 0; c < colonyCount; c++)
            {
                var col = Colonies[c];
                if (col.StatRaidTarget < 0) continue;
                col.StatRaidLeft -= dt;
                if (col.StatRaidLeft <= 0f || !IsAtWar(c, col.StatRaidTarget) || Colonies[col.StatRaidTarget].Extinct)
                    col.StatRaidTarget = -1;
            }
        }

        /// <summary>Fighting strength: soldiers count fully, workers a little.</summary>
        float Strength(int c)
        {
            var col = Colonies[c];
            var sp = SpeciesOf(c);
            float soldiers = col.Abstract ? col.AbstractSoldiers : col.Soldiers;
            float workers = col.Abstract ? col.AbstractWorkers : col.Workers;
            return (soldiers + workers * 0.2f) * sp.Damage;
        }

        /// <summary>Removes about `kills` ants (soldiers first). Returns how many died.</summary>
        int TakeLosses(int c, float kills, float2 front, float frac)
        {
            var col = Colonies[c];
            // Fractions carry over between seconds so small battles still kill someone eventually.
            col.BattleCarry += kills;
            int n = (int)col.BattleCarry;
            if (n <= 0) return 0;
            col.BattleCarry -= n;
            float soldiers = col.Abstract ? col.AbstractSoldiers : col.Soldiers;
            float workers = col.Abstract ? col.AbstractWorkers : col.Workers;
            float soldierShare = math.min(0.85f, soldiers / math.max(1f, soldiers + workers * 0.3f));
            int ks = math.min((int)math.round(n * soldierShare), (int)soldiers);
            int kw = math.min(n - ks, (int)workers);
            if (col.Abstract)
            {
                ScaleBins(col.SleepSoldierAges, ks, ref col.AbstractSoldiers);
                ScaleBins(col.SleepWorkerAges, kw, ref col.AbstractWorkers);
                col.CombatDeaths += ks + kw;
                return ks + kw;
            }
            // Awake: real ants near the front fall (and their deaths raise the alarm at home).
            killRef.Value = 0;
            new KillNearJob
            {
                Ants = ants.AsArray(), Colony = c, Center = front, Radius = math.max(25f, col.TerritoryRadius * (1.2f - frac * 0.5f)),
                Soldiers = ks, Workers = kw, Seed = rng.NextUInt(1, uint.MaxValue), Killed = killRef,
            }.Run();
            return killRef.Value;
        }

        static void ScaleBins(float[] bins, int kills, ref float total)
        {
            if (kills <= 0 || total <= 0f) return;
            float keep = math.max(0f, 1f - kills / total);
            for (int i = 0; i < bins.Length; i++) bins[i] *= keep;
            total *= keep;
        }

        void DropBodies(int bodies, float2 front)
        {
            for (int i = 0; i < bodies; i++)
            {
                float2 p = front + rng.NextFloat2Direction() * rng.NextFloat(0f, 12f);
                if (!grid.Contains(p) || IsBlocked(p)) continue;
                food[grid.CellIndex(p)] += combat.SoldierCorpseFood;
            }
        }

        /// <summary>A sleeping AI attacks a sleeping enemy by numbers, or wakes up to attack an awake one for real.</summary>
        void SleepingRaid(int c, int target)
        {
            var col = Colonies[c];
            if (Colonies[target].Abstract)
            {
                col.StatRaidTarget = target;
                col.StatRaidLeft = 90f;
            }
            else
            {
                Wake(c);
                int tn = NearestNest(target, NestPos(c), false);
                OrderRaid(c, tn >= 0 ? nests[tn].Position : NestPos(target));
                if (target == Player) Notify($"{col.Name} is raiding your nest!");
            }
        }

        NativeReference<int> killRef;
    }

    /// <summary>Kills up to N soldiers and M workers of one colony inside a circle (they die next step, with corpses).</summary>
    [BurstCompile]
    public struct KillQueensJob : IJob
    {
        public NativeArray<Ant> Ants;
        public int Colony;

        public void Execute()
        {
            for (int i = 0; i < Ants.Length; i++)
            {
                var a = Ants[i];
                if (a.Colony != Colony || a.Caste != (byte)Caste.Queen || a.Dead != 0) continue;
                a.Health = 0f;
                Ants[i] = a;
            }
        }
    }

    [BurstCompile]
    public struct KillNearJob : IJob
    {
        public NativeArray<Ant> Ants;
        public int Colony, Soldiers, Workers;
        public float2 Center;
        public float Radius;
        public uint Seed;
        public NativeReference<int> Killed;

        public void Execute()
        {
            int inS = 0, inW = 0;
            float r2 = Radius * Radius;
            for (int i = 0; i < Ants.Length; i++)
            {
                var a = Ants[i];
                if (a.Colony != Colony || a.Dead != 0 || a.Health <= 0f || math.distancesq(a.Position, Center) > r2) continue;
                if (a.Caste == (byte)Caste.Soldier) inS++;
                else if (a.Caste == (byte)Caste.Worker) inW++;
            }
            float pS = inS > 0 ? math.min(1f, Soldiers / (float)inS) : 0f;
            float pW = inW > 0 ? math.min(1f, Workers / (float)inW) : 0f;
            var rng = new Unity.Mathematics.Random(Seed);
            int leftS = Soldiers, leftW = Workers, killed = 0;
            for (int i = 0; i < Ants.Length && (leftS > 0 || leftW > 0); i++)
            {
                var a = Ants[i];
                if (a.Colony != Colony || a.Dead != 0 || a.Health <= 0f || math.distancesq(a.Position, Center) > r2) continue;
                bool soldier = a.Caste == (byte)Caste.Soldier;
                if (soldier ? (leftS <= 0 || rng.NextFloat() >= pS) : a.Caste != (byte)Caste.Worker || leftW <= 0 || rng.NextFloat() >= pW)
                    continue;
                a.Health = 0f; // dies in the next step: drops its body as food and raises a distress call
                Ants[i] = a;
                if (soldier) leftS--; else leftW--;
                killed++;
            }
            Killed.Value = killed;
        }
    }
}
