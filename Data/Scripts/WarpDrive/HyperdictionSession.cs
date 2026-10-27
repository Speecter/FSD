using System;
using System.Collections.Generic;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace WarpDriveMod
{
    /// <summary>A ship in hyperspace transit that a Hyperdictor can pull out (standard FSD or Capital FSD).</summary>
    public interface IHyperdictionVictim
    {
        MyCubeGrid HyperdictionGrid { get; }
        GridSystem GridSystem { get; }
        HyperdictionSequence Hyperdiction { get; }
        bool CanBeHyperdicted { get; }
        bool IsTrackable { get; }
        bool TryGetTransitPlan(out HyperdictionTransitPlan plan);
        bool BeginHyperdiction(long hyperdictorBlockId, int seed, Vector3D dropPos);
        void AbortHyperdiction();
        void HyperdictionDrop();
    }

    public enum HyperdictionMessageKind
    {
        Start = 0,
        Abort = 1,
        Drop = 2,
        InterceptStart = 3,   // victimGridEntityId = the Hyperdictor ship's grid; dropPosition = where it arrives
        InterceptCancel = 4
    }

    /// <summary>Snapshot of a ship's current transit leg: enough to predict where it will be at any later time.</summary>
    public class HyperdictionTransitPlan
    {
        public Vector3D Origin;
        public Vector3D Direction;
        public double Distance;
        public double ElapsedSeconds;
        public double TotalSeconds;
        public double AccelSeconds;
        public double DecelSeconds;
        public bool InTransit;                 // false while the ship is still charging/counting down
        public double TimeUntilEntrySeconds;   // real seconds until the transit clock starts (0 once in transit)

        public double DistanceAt(double seconds)
        {
            float progress;
            double speed;
            HyperspaceSystem.CalculateTransitProgress(seconds, TotalSeconds, Distance, out progress, out speed, AccelSeconds, DecelSeconds);
            return progress * Distance;
        }

        public double SpeedAt(double seconds)
        {
            float progress;
            double speed;
            HyperspaceSystem.CalculateTransitProgress(seconds, TotalSeconds, Distance, out progress, out speed, AccelSeconds, DecelSeconds);
            return speed;
        }
    }

    /// <summary>
    /// Owns every Hyperdictor in the world. The server watches ships that are jumping, plans an interception (where the victim
    /// will be caught, where it will be thrown, where the Hyperdictor ship follows), starts the ship's own FSD charge early enough,
    /// catches the victim at the planned point and triggers the drop. Everyone simulates the deterministic 9s timeline.
    /// </summary>
    public class HyperdictionSession
    {
        public static HyperdictionSession Instance { get; private set; }

        public const double INTERCEPT_LEAD_SECONDS = 15.0;   // 10s FSD spool-up + 5s countdown
        public const double CHASE_JOURNEY_SECONDS = 11.0;    // the FSD's shortest journey: lands ~2s after the victim drops
        private const int PLAN_INTERVAL_TICKS = 30;
        private const int PLAN_RETRY_TICKS = 600;
        private const double ARRIVAL_MARGIN_SECONDS = 1.5;   // the victim must still be in the tunnel this long after the catch
        private const double TURN_BUDGET_SECONDS = 11.0;     // alignment time that fits inside the 15s lead

        private class VictimEntry
        {
            public IHyperdictionVictim Victim;
            public HyperdictionSequence Sequence;
            public int Seed;
            public Vector3D DropPos;
            public bool Dropped;
        }

        private class CatchEvent
        {
            public HyperdictorBlock Hyperdictor;
            public HyperspaceSystem Interceptor;
            public readonly List<VictimEntry> Entries = new List<VictimEntry>();
            public bool AnyDropped;
            public int WaitTicks;
        }

        /// <summary>One planned interception, from the first prediction until the victim is caught (or the plan collapses).</summary>
        private class InterceptPlan
        {
            public HyperdictorBlock Hyperdictor;
            public HyperspaceSystem Interceptor;
            public IHyperdictionVictim Victim;
            public long VictimGridId;
            public Vector3D Origin;      // the victim's transit line
            public Vector3D Direction;
            public double CatchDistance; // distance along the line where the victim gets caught
            public Vector3D CatchPos;
            public Vector3D DropPos;
            public Vector3D FollowPos;
            public int Seed;
            public int StartTick;        // when the Hyperdictor ship begins charging
            public bool Started;
        }

        private readonly List<HyperdictorBlock> hyperdictors = new List<HyperdictorBlock>();
        private readonly Dictionary<long, HyperdictorBlock> hyperdictorsById = new Dictionary<long, HyperdictorBlock>();
        private readonly List<CatchEvent> events = new List<CatchEvent>();
        private readonly List<InterceptPlan> plans = new List<InterceptPlan>();
        private readonly Dictionary<long, int> retryAfterTick = new Dictionary<long, int>();
        private readonly List<IHyperdictionVictim> victimBuffer = new List<IHyperdictionVictim>();
        private int tick;

        private bool IsAuthority { get { return MyAPIGateway.Multiplayer.IsServer || !MyAPIGateway.Multiplayer.MultiplayerActive; } }

        public HyperdictionSession()
        {
            Instance = this;
        }

        public void Close()
        {
            hyperdictors.Clear();
            hyperdictorsById.Clear();
            events.Clear();
            plans.Clear();
            retryAfterTick.Clear();
            if (Instance == this)
                Instance = null;
        }

        // ------------------------------------------------------------------ registry

        public void RegisterHyperdictor(HyperdictorBlock hd)
        {
            if (hd == null || hd.Block == null || hyperdictors.Contains(hd))
                return;

            hyperdictors.Add(hd);
            hyperdictorsById[hd.Block.EntityId] = hd;
        }

        public void UnregisterHyperdictor(HyperdictorBlock hd)
        {
            if (hd == null)
                return;

            hyperdictors.Remove(hd);
            if (hd.Block != null)
                hyperdictorsById.Remove(hd.Block.EntityId);
        }

        // ------------------------------------------------------------------ network handlers

        public void HandleToggleMessage(HyperdictorToggleMessage msg, ulong sender)
        {
            if (msg == null || !MyAPIGateway.Multiplayer.IsServer)
                return;

            HyperdictorBlock hd;
            if (!hyperdictorsById.TryGetValue(msg.entityId, out hd) || hd.Block == null)
                return;

            // Never trust the identity in the packet; resolve it from the sender's steam id
            long identity = MyAPIGateway.Players.TryGetIdentityId(sender);
            if (identity == 0L || !hd.Block.HasPlayerAccess(identity))
                return;

            hd.RequestToggleEngagement(identity);
        }

        public void HandleSyncMessage(HyperdictorSyncMessage msg)
        {
            if (msg == null || MyAPIGateway.Multiplayer.IsServer)
                return;

            HyperdictorBlock hd;
            if (hyperdictorsById.TryGetValue(msg.entityId, out hd))
                hd.ApplyServerSync(msg);
        }

        public void HandleClientSequenceMessage(HyperdictionSequenceMessage msg)
        {
            if (msg == null || MyAPIGateway.Multiplayer.IsServer)
                return;

            var kind = (HyperdictionMessageKind)msg.kind;
            if (kind == HyperdictionMessageKind.InterceptStart || kind == HyperdictionMessageKind.InterceptCancel)
            {
                HyperspaceSystem chaser = WarpDriveSession.instance?.GetGridHyperspace(msg.victimGridEntityId);
                if (chaser == null)
                    return;

                if (kind == HyperdictionMessageKind.InterceptStart)
                    chaser.StartIntercept(msg.dropPosition, msg.journeySeconds);
                else
                    chaser.CancelIntercept();
                return;
            }

            IHyperdictionVictim victim = FindVictim(msg.victimGridEntityId);
            if (victim == null)
                return;

            switch (kind)
            {
                case HyperdictionMessageKind.Start:
                    if (victim.Hyperdiction == null)
                        victim.BeginHyperdiction(msg.hyperdictorBlockEntityId, msg.randomSeed, msg.dropPosition);
                    break;
                case HyperdictionMessageKind.Abort:
                    if (victim.Hyperdiction != null)
                        victim.AbortHyperdiction();
                    break;
                case HyperdictionMessageKind.Drop:
                    if (victim.Hyperdiction != null)
                        victim.HyperdictionDrop();
                    break;
            }
        }

        private void Broadcast(HyperdictionMessageKind kind, long gridId, long hyperdictorBlockId, int seed, Vector3D position, float journeySeconds = 0f)
        {
            if (!MyAPIGateway.Multiplayer.IsServer)
                return;

            var msg = new HyperdictionSequenceMessage
            {
                kind = (int)kind,
                victimGridEntityId = gridId,
                hyperdictorBlockEntityId = hyperdictorBlockId,
                randomSeed = seed,
                dropPosition = position,
                journeySeconds = journeySeconds
            };

            byte[] data = MyAPIGateway.Utilities.SerializeToBinary(msg);
            MyAPIGateway.Multiplayer.SendMessageToOthers(WarpDriveSession.hyperdictionSequencePacket, data);
        }

        private static IHyperdictionVictim FindVictim(long gridId)
        {
            var session = WarpDriveSession.instance;
            if (session == null || gridId == 0L)
                return null;

            IHyperdictionVictim victim = session.GetGridHyperspace(gridId);
            if (victim == null)
                victim = session.GetGridCapitalFSD(gridId);
            return victim;
        }

        // ------------------------------------------------------------------ simulation

        public void UpdateSimulation()
        {
            if (!IsAuthority)
                return;

            tick++;

            if (events.Count > 0)
                MonitorEvents();

            if (plans.Count > 0)
                UpdatePlans();

            if (tick % PLAN_INTERVAL_TICKS == 0)
                PlanNewIntercepts();
        }

        private void CollectVictims()
        {
            victimBuffer.Clear();

            var session = WarpDriveSession.instance;
            if (session == null)
                return;

            foreach (var hs in session.GetHyperspaceSystems())
            {
                if (hs != null && hs.IsTrackable)
                    victimBuffer.Add(hs);
            }

            foreach (var cap in session.GetCapitalFSDSystems())
            {
                if (cap != null && cap.IsTrackable)
                    victimBuffer.Add(cap);
            }
        }

        private static bool GroupContains(GridSystem gridSystem, MyCubeGrid grid)
        {
            if (gridSystem == null || grid == null)
                return false;

            foreach (var g in gridSystem.gridGroup)
            {
                if (g == grid)
                    return true;
            }
            return false;
        }

        /// <summary>The chase is still on: charging toward the jump, in the tunnel, or already landed at the follow point.
        /// Only a chase that aborted before entering hyperspace (or emergency-dropped) lets the victim go.</summary>
        private static bool ChaserHolding(HyperspaceSystem chaser)
        {
            return chaser.IsIntercepting || chaser.InterceptCommitted;
        }

        private static Vector3D GridCenter(MyCubeGrid grid)
        {
            return Vector3D.Transform((Vector3D)grid.PositionComp.LocalAABB.Center, grid.WorldMatrix);
        }

        private bool IsPlanned(IHyperdictionVictim victim)
        {
            for (int i = 0; i < plans.Count; i++)
            {
                if (plans[i].Victim == victim)
                    return true;
            }
            for (int i = 0; i < events.Count; i++)
            {
                foreach (var entry in events[i].Entries)
                {
                    if (entry.Victim == victim)
                        return true;
                }
            }
            return false;
        }

        /// <summary>Own ships and faction mates are never targeted.</summary>
        private static bool IsFriendly(HyperdictorBlock hd, MyCubeGrid victimGrid)
        {
            var owners = victimGrid.BigOwners;
            if (owners == null || hd.Block == null)
                return false;

            for (int i = 0; i < owners.Count; i++)
            {
                var relation = hd.Block.GetUserRelationToOwner(owners[i]);
                if (relation == MyRelationsBetweenPlayerAndBlock.Owner || relation == MyRelationsBetweenPlayerAndBlock.FactionShare)
                    return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ planning

        private void PlanNewIntercepts()
        {
            for (int h = 0; h < hyperdictors.Count; h++)
            {
                HyperdictorBlock hd = hyperdictors[h];
                if (!hd.CanCatch)
                    continue;

                CollectVictims();

                InterceptPlan best = null;
                for (int i = 0; i < victimBuffer.Count; i++)
                {
                    IHyperdictionVictim victim = victimBuffer[i];
                    MyCubeGrid grid = victim.HyperdictionGrid;
                    if (grid == null || grid.MarkedForClose || IsPlanned(victim) || GroupContains(victim.GridSystem, hd.MainGrid))
                        continue;

                    int retryAt;
                    if (retryAfterTick.TryGetValue(grid.EntityId, out retryAt) && tick < retryAt)
                        continue;

                    if (IsFriendly(hd, grid))
                        continue;

                    InterceptPlan plan = BuildPlan(hd, victim);
                    if (plan == null)
                    {
                        retryAfterTick[grid.EntityId] = tick + PLAN_RETRY_TICKS;
                        continue;
                    }

                    if (best == null || plan.StartTick < best.StartTick)
                        best = plan;
                }

                if (best != null)
                {
                    hd.IsBusy = true;
                    plans.Add(best);
                }
            }
        }

        /// <summary>
        /// Where along the victim's line the catch happens (never before the Hyperdictor ship can be ready, never after the victim
        /// would have arrived) and how many seconds from now the victim gets there.
        /// </summary>
        private static bool ComputeCatchDistance(HyperdictionTransitPlan tp, Vector3D hdPos, double fieldRadius, double leadSeconds, out double catchDistance, out double secondsUntilCatch)
        {
            catchDistance = 0.0;
            secondsUntilCatch = 0.0;

            Vector3D rel = hdPos - tp.Origin;
            double along = Vector3D.Dot(rel, tp.Direction);
            double perp2 = Math.Max(0.0, rel.LengthSquared() - along * along);
            double radius = fieldRadius;

            // Natural catch: where the line enters the field (up to 25km, set by the capacitor charge), or its closest approach if it misses
            double natural = perp2 <= radius * radius ? along - Math.Sqrt(radius * radius - perp2) : along;

            double clock = tp.InTransit ? tp.ElapsedSeconds : 0.0;
            double untilEntry = tp.InTransit ? 0.0 : tp.TimeUntilEntrySeconds;
            double latestClock = tp.TotalSeconds - ARRIVAL_MARGIN_SECONDS;
            if (latestClock <= clock)
                return false;

            // Earliest point the chase can be ready for
            double readyClock = clock + Math.Max(0.0, leadSeconds - untilEntry);
            if (readyClock > latestClock)
                return false;

            double minDistance = tp.DistanceAt(readyClock);
            double maxDistance = tp.DistanceAt(latestClock);
            double nowDistance = tp.DistanceAt(clock);

            catchDistance = Math.Max(Math.Max(natural, minDistance), nowDistance);
            if (catchDistance > maxDistance)
                return false;

            double lo = clock, hi = latestClock;
            for (int i = 0; i < 40; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (tp.DistanceAt(mid) < catchDistance)
                    lo = mid;
                else
                    hi = mid;
            }

            secondsUntilCatch = untilEntry + (hi - clock);
            return true;
        }

        private InterceptPlan BuildPlan(HyperdictorBlock hd, IHyperdictionVictim victim)
        {
            HyperspaceSystem chaser = hd.Interceptor;
            HyperdictionTransitPlan tp;
            if (chaser == null || !victim.TryGetTransitPlan(out tp))
                return null;

            MyCubeGrid victimGrid = victim.HyperdictionGrid;
            Vector3D hdPos = hd.MainGrid.PositionComp.GetPosition();
            double maxRange = chaser.MaxInterceptRange;
            double victimRadius = victimGrid.PositionComp.WorldVolume.Radius;
            double hdRadius = hd.MainGrid.PositionComp.WorldVolume.Radius;
            float allowGravity = Settings.instance != null ? Settings.instance.AllowInGravityMax : 0.2f;

            var ignore = new HashSet<IMyCubeGrid>();
            foreach (var g in victim.GridSystem.gridGroup) ignore.Add(g);
            var hdGroup = new List<IMyCubeGrid>();
            MyAPIGateway.GridGroups.GetGroup(hd.MainGrid, GridLinkTypeEnum.Physical, hdGroup);
            foreach (var g in hdGroup) ignore.Add(g);

            int baseSeed = WarpDriveSession.instance.rand.Next(1, 100000);
            double lead = INTERCEPT_LEAD_SECONDS;

            for (int attempt = 0; attempt < 4; attempt++)
            {
                double catchDistance, secondsUntilCatch;
                if (!ComputeCatchDistance(tp, hdPos, hd.FieldRadiusMeters, lead, out catchDistance, out secondsUntilCatch))
                    return null;

                Vector3D catchPos = tp.Origin + tp.Direction * catchDistance;
                double catchClock = (tp.InTransit ? tp.ElapsedSeconds : 0.0) + Math.Max(0.0, secondsUntilCatch - tp.TimeUntilEntrySeconds);
                int seed = baseSeed + attempt * 104729;

                Vector3D drop = HyperdictionSequence.ComputeSpreadDrop(catchPos, tp.Direction, tp.SpeedAt(catchClock), seed, victimRadius, ignore);
                Vector3D follow = HyperdictionSequence.ComputeFollowPosition(catchPos, drop, HyperdictionSequence.FOLLOW_STANDOFF_METERS + victimRadius + hdRadius);

                double chaseDistance = Vector3D.Distance(hdPos, follow);
                if (chaseDistance < 1000.0 || chaseDistance > maxRange)
                    continue;

                string blockedBy;
                if (HyperspaceSystem.IsTrajectoryBlockedByPlanet(hdPos, follow, out blockedBy))
                    continue;

                float g;
                Vector3D gravity = MyAPIGateway.Physics.CalculateNaturalGravityAt(follow, out g);
                if (gravity.Length() / WarpSystem.EARTH_GRAVITY > allowGravity)
                    continue;

                // A ship that turns slowly needs to start earlier than the standard 15s
                double alignSeconds = chaser.EstimateAlignSeconds(follow - hdPos);
                double neededLead = INTERCEPT_LEAD_SECONDS + Math.Max(0.0, alignSeconds - TURN_BUDGET_SECONDS);
                if (neededLead > lead + 0.01)
                {
                    lead = neededLead;
                    continue;
                }

                return new InterceptPlan
                {
                    Hyperdictor = hd,
                    Interceptor = chaser,
                    Victim = victim,
                    VictimGridId = victimGrid.EntityId,
                    Origin = tp.Origin,
                    Direction = tp.Direction,
                    CatchDistance = catchDistance,
                    CatchPos = catchPos,
                    DropPos = drop,
                    FollowPos = follow,
                    Seed = seed,
                    StartTick = tick + (int)Math.Max(0.0, (secondsUntilCatch - lead) * 60.0)
                };
            }

            return null;
        }

        private void CancelPlan(InterceptPlan plan)
        {
            if (plan.Started && plan.Interceptor != null && plan.Interceptor.IsIntercepting)
            {
                plan.Interceptor.CancelIntercept();
                if (plan.Interceptor.GridSystem?.mainGrid != null)
                    Broadcast(HyperdictionMessageKind.InterceptCancel, plan.Interceptor.GridSystem.mainGrid.EntityId, 0L, 0, Vector3D.Zero);
            }

            if (plan.Hyperdictor != null)
                plan.Hyperdictor.IsBusy = false;
            plans.Remove(plan);
        }

        private bool StartChase(InterceptPlan plan)
        {
            if (plan.Started)
                return true;

            if (!plan.Interceptor.StartIntercept(plan.FollowPos, (float)CHASE_JOURNEY_SECONDS))
                return false;

            plan.Started = true;
            MyCubeGrid chaserGrid = plan.Interceptor.GridSystem.mainGrid;
            Broadcast(HyperdictionMessageKind.InterceptStart, chaserGrid.EntityId, plan.Hyperdictor.Block.EntityId, plan.Seed, plan.FollowPos, (float)CHASE_JOURNEY_SECONDS);
            return true;
        }

        private void UpdatePlans()
        {
            for (int p = plans.Count - 1; p >= 0; p--)
            {
                InterceptPlan plan = plans[p];
                HyperdictorBlock hd = plan.Hyperdictor;
                MyCubeGrid victimGrid = plan.Victim.HyperdictionGrid;

                bool hdOk = hd != null && hd.Block != null && !hd.Block.MarkedForClose && hd.Block.IsFunctional && hd.IsEngaged;
                bool victimOk = victimGrid != null && !victimGrid.MarkedForClose && plan.Victim.IsTrackable;
                // A chaser that already jumped (in the tunnel or waiting at the follow point after landing early) still holds the plan
                bool chaserOk = !plan.Started || ChaserHolding(plan.Interceptor);

                if (!hdOk || !victimOk || !chaserOk)
                {
                    CancelPlan(plan);
                    continue;
                }

                if (!plan.Started)
                {
                    // Keep the schedule honest while the victim is still charging (it may hold for alignment)
                    if (tick >= plan.StartTick && !StartChase(plan))
                    {
                        CancelPlan(plan);
                        continue;
                    }
                    if (!plan.Started && tick % PLAN_INTERVAL_TICKS == 0)
                        RescheduleStart(plan);
                }

                // Caught when the victim reaches the planned point on its line
                if (plan.Victim.CanBeHyperdicted)
                {
                    double along = Vector3D.Dot(GridCenter(victimGrid) - plan.Origin, plan.Direction);
                    if (along >= plan.CatchDistance)
                    {
                        if (!StartChase(plan))
                        {
                            CancelPlan(plan);
                            continue;
                        }

                        plans.RemoveAt(p);
                        StartCatch(plan);
                    }
                }
            }
        }

        private void RescheduleStart(InterceptPlan plan)
        {
            HyperdictionTransitPlan tp;
            if (!plan.Victim.TryGetTransitPlan(out tp))
                return;

            double clock = tp.InTransit ? tp.ElapsedSeconds : 0.0;
            double untilEntry = tp.InTransit ? 0.0 : tp.TimeUntilEntrySeconds;

            double lo = clock, hi = tp.TotalSeconds;
            for (int i = 0; i < 40; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (tp.DistanceAt(mid) < plan.CatchDistance)
                    lo = mid;
                else
                    hi = mid;
            }

            double secondsUntilCatch = untilEntry + (hi - clock);
            double alignSeconds = plan.Interceptor.EstimateAlignSeconds(plan.FollowPos - plan.Hyperdictor.MainGrid.PositionComp.GetPosition());
            double lead = INTERCEPT_LEAD_SECONDS + Math.Max(0.0, alignSeconds - TURN_BUDGET_SECONDS);
            plan.StartTick = tick + (int)Math.Max(0.0, (secondsUntilCatch - lead) * 60.0);
        }

        // ------------------------------------------------------------------ catch & monitoring

        private void StartCatch(InterceptPlan plan)
        {
            HyperdictorBlock hd = plan.Hyperdictor;
            IHyperdictionVictim primary = plan.Victim;
            MyCubeGrid primaryGrid = primary.HyperdictionGrid;
            Vector3D primaryCenter = GridCenter(primaryGrid);

            // Fleet-wide catch: every other ship of the same fleet that is currently in the tunnel gets pulled out too
            var victims = new List<IHyperdictionVictim> { primary };
            var lobby = FleetJumpSystem.GetLobbyForGrid(primaryGrid.EntityId);
            if (lobby != null && primary is HyperspaceSystem)
            {
                foreach (long memberId in lobby.InTransitGridIds)
                {
                    if (memberId == primaryGrid.EntityId)
                        continue;

                    var member = WarpDriveSession.instance.GetGridHyperspace(memberId);
                    if (member != null && member.CanBeHyperdicted && !GroupContains(member.GridSystem, hd.MainGrid))
                        victims.Add(member);
                }
            }

            var ev = new CatchEvent { Hyperdictor = hd, Interceptor = plan.Interceptor };

            for (int i = 0; i < victims.Count; i++)
            {
                IHyperdictionVictim v = victims[i];
                Vector3D drop = plan.DropPos;
                if (i > 0)
                {
                    // Keep the formation: preserve the member's offset relative to the primary, capped at 1 km
                    Vector3D offset = GridCenter(v.HyperdictionGrid) - primaryCenter;
                    double len = offset.Length();
                    if (len > 1000.0)
                        offset *= 1000.0 / len;
                    drop = plan.DropPos + offset;
                }

                int seed = plan.Seed + i * 7919;
                if (!v.BeginHyperdiction(hd.Block.EntityId, seed, drop))
                    continue;

                ev.Entries.Add(new VictimEntry { Victim = v, Sequence = v.Hyperdiction, Seed = seed, DropPos = drop });
                Broadcast(HyperdictionMessageKind.Start, v.HyperdictionGrid.EntityId, hd.Block.EntityId, seed, drop);
            }

            if (ev.Entries.Count == 0)
            {
                hd.IsBusy = false;
                return;
            }

            hd.IsBusy = true;
            events.Add(ev);
        }

        private void MonitorEvents()
        {
            for (int e = events.Count - 1; e >= 0; e--)
            {
                CatchEvent ev = events[e];
                HyperdictorBlock hd = ev.Hyperdictor;
                bool hdOk = hd != null && hd.Block != null && !hd.Block.MarkedForClose && hd.Block.IsFunctional && hd.IsEngaged
                    && (ev.Interceptor == null || ChaserHolding(ev.Interceptor));

                for (int i = ev.Entries.Count - 1; i >= 0; i--)
                {
                    VictimEntry entry = ev.Entries[i];
                    IHyperdictionVictim v = entry.Victim;
                    MyCubeGrid grid = v.HyperdictionGrid;

                    // Dropped, destroyed, or left the sequence by other means (emergency drop, jump aborted, recovery finished)
                    if (entry.Dropped || grid == null || grid.MarkedForClose || v.Hyperdiction != entry.Sequence)
                    {
                        ev.Entries.RemoveAt(i);
                        continue;
                    }

                    if (!hdOk && !entry.Sequence.IsRecovering)
                    {
                        v.AbortHyperdiction();
                        Broadcast(HyperdictionMessageKind.Abort, grid.EntityId, hd != null && hd.Block != null ? hd.Block.EntityId : 0L, entry.Seed, entry.DropPos);
                    }
                }

                if (ev.Entries.Count == 0)
                {
                    // The Hyperdictor ship is still on its way in behind the victim: the event (and the cooldown) ends when it lands
                    if (ev.Interceptor != null && ev.Interceptor.IsIntercepting && ev.WaitTicks++ < 60 * 40)
                        continue;

                    FinishEvent(ev);
                    events.RemoveAt(e);
                }
            }
        }

        private void FinishEvent(CatchEvent ev)
        {
            HyperdictorBlock hd = ev.Hyperdictor;
            if (hd == null)
                return;

            hd.IsBusy = false;
            if (ev.AnyDropped && hd.Block != null && !hd.Block.MarkedForClose)
                hd.OnVictimDropped(0L);
        }

        /// <summary>Called on the server by a victim whose sequence reached the final tick. Drops it and tells everyone else.</summary>
        public void OnSequenceComplete(IHyperdictionVictim victim)
        {
            if (!IsAuthority || victim == null || victim.HyperdictionGrid == null)
                return;

            long gridId = victim.HyperdictionGrid.EntityId;
            foreach (var ev in events)
            {
                foreach (var entry in ev.Entries)
                {
                    if (entry.Victim != victim || entry.Dropped)
                        continue;

                    entry.Dropped = true;
                    ev.AnyDropped = true;
                    victim.HyperdictionDrop();
                    Broadcast(HyperdictionMessageKind.Drop, gridId, ev.Hyperdictor.Block.EntityId, entry.Seed, entry.DropPos);

                    // Dropped entries are done; MonitorEvents removes them next tick since Hyperdiction is now null
                    return;
                }
            }

            // No tracked event (e.g. the event was cleaned up): still make sure the victim leaves the tunnel
            victim.HyperdictionDrop();
        }
    }
}
