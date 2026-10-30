using System;
using System.Collections.Generic;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;
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
        bool BeginHyperdiction(long hyperdictorBlockId, int seed, Vector3D dropPos);
        void AbortHyperdiction();
        void HyperdictionDrop();
    }

    public enum HyperdictionMessageKind
    {
        Start = 0,
        Abort = 1,
        Drop = 2,
        InterceptStart = 3,   // no longer sent (the Hyperdictor ship does not chase any more); kept so the numbering stays stable
        InterceptCancel = 4
    }

    /// <summary>
    /// Owns every Hyperdictor in the world. The server watches ships in hyperspace transit: one whose path crosses an engaged,
    /// hostile Hyperdictor's field is caught on the spot, pulled through the deterministic 9s sequence and dropped next to the
    /// (stationary) Hyperdictor ship. The Hyperdictor ship itself never jumps. Everyone simulates the same 9s timeline.
    /// </summary>
    public class HyperdictionSession
    {
        public static HyperdictionSession Instance { get; private set; }

        private const int DIAG_INTERVAL_TICKS = 600;   // while a Hyperdictor is engaged, the log says every 10 s why it is (not) catching

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
            public readonly List<VictimEntry> Entries = new List<VictimEntry>();
            public bool AnyDropped;
        }

        private readonly List<HyperdictorBlock> hyperdictors = new List<HyperdictorBlock>();
        private readonly Dictionary<long, HyperdictorBlock> hyperdictorsById = new Dictionary<long, HyperdictorBlock>();
        private readonly List<CatchEvent> events = new List<CatchEvent>();
        private readonly List<IHyperdictionVictim> victimBuffer = new List<IHyperdictionVictim>();
        private readonly List<HyperdictorBlock> readyBuffer = new List<HyperdictorBlock>();

        // Where every ship in transit was last tick, so a fast ship that crosses a field between two ticks is still caught
        private Dictionary<long, Vector3D> lastVictimPos = new Dictionary<long, Vector3D>();
        private Dictionary<long, Vector3D> nextVictimPos = new Dictionary<long, Vector3D>();

        private int tick;
        private int lastDiagTick = -DIAG_INTERVAL_TICKS;

        private bool IsAuthority { get { return NetAuthority.IsAuthority; } }

        public HyperdictionSession()
        {
            Instance = this;
        }

        public void Close()
        {
            hyperdictors.Clear();
            hyperdictorsById.Clear();
            events.Clear();
            lastVictimPos.Clear();
            nextVictimPos.Clear();
            HyperdictionRules.Close();
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

        /// <summary>The Hyperdictor mounted on the given ship, if any (read by the HUD on every machine).</summary>
        public HyperdictorBlock GetHyperdictorOnGrid(MyCubeGrid grid)
        {
            if (grid == null)
                return null;

            for (int i = 0; i < hyperdictors.Count; i++)
            {
                if (hyperdictors[i].MainGrid == grid)
                    return hyperdictors[i];
            }
            return null;
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

            IHyperdictionVictim victim = FindVictim(msg.victimGridEntityId);
            if (victim == null)
                return;

            switch ((HyperdictionMessageKind)msg.kind)
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

        private void Broadcast(HyperdictionMessageKind kind, long gridId, long hyperdictorBlockId, int seed, Vector3D position)
        {
            if (!MyAPIGateway.Multiplayer.IsServer)
                return;

            var msg = new HyperdictionSequenceMessage
            {
                kind = (int)kind,
                victimGridEntityId = gridId,
                hyperdictorBlockEntityId = hyperdictorBlockId,
                randomSeed = seed,
                dropPosition = position
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

            CheckFields();
        }

        /// <summary>Ships in the tunnel right now that a field could still take.</summary>
        private void CollectVictims()
        {
            victimBuffer.Clear();

            var session = WarpDriveSession.instance;
            if (session == null)
                return;

            foreach (var hs in session.GetHyperspaceSystems())
            {
                if (hs != null && hs.CanBeHyperdicted)
                    victimBuffer.Add(hs);
            }

            foreach (var cap in session.GetCapitalFSDSystems())
            {
                if (cap != null && cap.CanBeHyperdicted)
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

        private static Vector3D GridCenter(MyCubeGrid grid)
        {
            return Vector3D.Transform((Vector3D)grid.PositionComp.LocalAABB.Center, grid.WorldMatrix);
        }

        private bool IsHeld(IHyperdictionVictim victim)
        {
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
        public static bool IsFriendly(HyperdictorBlock hd, MyCubeGrid victimGrid)
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

        /// <summary>A hostile (not own, not faction) Hyperdictor with its field up has this ship inside its radius.</summary>
        public bool IsInsideEnemyField(MyCubeGrid grid)
        {
            if (grid == null)
                return false;

            Vector3D pos = grid.PositionComp.GetPosition();
            for (int i = 0; i < hyperdictors.Count; i++)
            {
                HyperdictorBlock hd = hyperdictors[i];
                MyCubeGrid hdGrid = hd.MainGrid;
                if (!hd.IsEngaged || hd.Block == null || hd.Block.MarkedForClose || !hd.Block.IsFunctional || hdGrid == null)
                    continue;
                if (hdGrid == grid || MyAPIGateway.GridGroups.HasConnection(hdGrid, grid, GridLinkTypeEnum.Physical))
                    continue;
                if (Vector3D.DistanceSquared(hdGrid.PositionComp.GetPosition(), pos) > hd.FieldRadiusMeters * hd.FieldRadiusMeters)
                    continue;
                if (IsFriendly(hd, grid))
                    continue;
                return true;
            }
            return false;
        }

        /// <summary>A ship this Hyperdictor caught is still being held.</summary>
        public bool IsCatching(HyperdictorBlock hd)
        {
            for (int i = 0; i < events.Count; i++)
            {
                if (events[i].Hyperdictor == hd)
                    return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ catching

        private static string GridName(MyCubeGrid grid)
        {
            return grid != null ? "\"" + grid.DisplayName + "\"" : "?";
        }

        private static void Diag(string text)
        {
            MyLog.Default.WriteLine("[Hyperdiction] diag: " + text);
        }

        /// <summary>
        /// Every tick: a ship in the tunnel whose path (last tick's position to this tick's) enters an engaged, hostile field is caught.
        /// Only distances are computed here; the expensive drop search runs once, at the catch.
        /// </summary>
        private void CheckFields()
        {
            bool diag = tick - lastDiagTick >= DIAG_INTERVAL_TICKS;
            bool diagged = false;

            readyBuffer.Clear();
            for (int h = 0; h < hyperdictors.Count; h++)
            {
                HyperdictorBlock hd = hyperdictors[h];
                if (hd.CanCatch && hd.MainGrid != null)
                    readyBuffer.Add(hd);
                else if (diag && hd.IsEngaged)
                {
                    Diag("Hyperdictor on " + GridName(hd.MainGrid) + " is engaged but cannot catch: " + hd.CatchBlocker);
                    diagged = true;
                }
            }

            if (readyBuffer.Count == 0)
            {
                lastVictimPos.Clear();
                if (diagged)
                    lastDiagTick = tick;
                return;
            }

            CollectVictims();
            nextVictimPos.Clear();

            for (int i = 0; i < victimBuffer.Count; i++)
            {
                IHyperdictionVictim victim = victimBuffer[i];
                MyCubeGrid grid = victim.HyperdictionGrid;
                if (grid == null || grid.MarkedForClose)
                    continue;

                Vector3D cur = GridCenter(grid);
                Vector3D prev;
                if (!lastVictimPos.TryGetValue(grid.EntityId, out prev))
                    prev = cur;
                nextVictimPos[grid.EntityId] = cur;

                if (IsHeld(victim))
                    continue;

                for (int h = 0; h < readyBuffer.Count; h++)
                {
                    HyperdictorBlock hd = readyBuffer[h];
                    if (hd.IsBusy || GroupContains(victim.GridSystem, hd.MainGrid))
                        continue;

                    Vector3D hdPos = GridCenter(hd.MainGrid);
                    Vector3D entry;
                    if (!HyperdictionSequence.SegmentSphereEntry(prev, cur, hdPos, hd.FieldRadiusMeters, out entry))
                        continue;

                    if (IsFriendly(hd, grid))
                    {
                        if (diag)
                        {
                            Diag("Hyperdictor on " + GridName(hd.MainGrid) + " lets " + GridName(grid) + " pass: friendly (own ship or same faction)");
                            diagged = true;
                        }
                        continue;
                    }

                    if (TryCatch(hd, victim))
                        break;
                }
            }

            // Ships that left the tunnel drop out of the map here
            var swap = lastVictimPos;
            lastVictimPos = nextVictimPos;
            nextVictimPos = swap;

            if (diag)
            {
                diagged = true;
                Diag(readyBuffer.Count + " Hyperdictor(s) ready, " + victimBuffer.Count + " ship(s) in hyperspace transit");
            }

            if (diagged)
                lastDiagTick = tick;
        }

        /// <summary>Catches the ship (and the rest of its fleet in the tunnel) and drops it next to the Hyperdictor ship.</summary>
        private bool TryCatch(HyperdictorBlock hd, IHyperdictionVictim victim)
        {
            MyCubeGrid victimGrid = victim.HyperdictionGrid;
            MyCubeGrid hdGrid = hd.MainGrid;
            Vector3D hdPos = GridCenter(hdGrid);
            Vector3D victimPos = GridCenter(victimGrid);

            var ignore = new HashSet<IMyCubeGrid>();
            foreach (var g in victim.GridSystem.gridGroup) ignore.Add(g);
            var hdGroup = new List<IMyCubeGrid>();
            MyAPIGateway.GridGroups.GetGroup(hdGrid, GridLinkTypeEnum.Physical, hdGroup);
            foreach (var g in hdGroup) ignore.Add(g);

            int seed = WarpDriveSession.instance.rand.Next(1, 100000);
            Vector3D drop = HyperdictionSequence.ComputeDropNear(hdPos, victimPos - hdPos, seed,
                victimGrid.PositionComp.WorldVolume.Radius, hdGrid.PositionComp.WorldVolume.Radius, ignore);

            if (!StartCatch(hd, victim, drop, seed))
            {
                Diag("Hyperdictor on " + GridName(hdGrid) + " could not hold " + GridName(victimGrid) + " (it was no longer catchable)");
                return false;
            }

            Diag("Hyperdictor on " + GridName(hdGrid) + " caught " + GridName(victimGrid) + " " + (Vector3D.Distance(hdPos, victimPos) / 1000.0).ToString("0.0")
                + " km out; dropping it " + (Vector3D.Distance(hdPos, drop) / 1000.0).ToString("0.0") + " km from the Hyperdictor");
            return true;
        }

        // ------------------------------------------------------------------ catch & monitoring

        /// <summary>False when no victim could be held.</summary>
        private bool StartCatch(HyperdictorBlock hd, IHyperdictionVictim primary, Vector3D dropPos, int baseSeed)
        {
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

            var ev = new CatchEvent { Hyperdictor = hd };

            for (int i = 0; i < victims.Count; i++)
            {
                IHyperdictionVictim v = victims[i];
                Vector3D drop = dropPos;
                if (i > 0)
                {
                    // Keep the formation: preserve the member's offset relative to the primary, capped at 1 km
                    Vector3D offset = GridCenter(v.HyperdictionGrid) - primaryCenter;
                    double len = offset.Length();
                    if (len > 1000.0)
                        offset *= 1000.0 / len;
                    drop = dropPos + offset;
                }

                int seed = baseSeed + i * 7919;
                if (!v.BeginHyperdiction(hd.Block.EntityId, seed, drop))
                    continue;

                ev.Entries.Add(new VictimEntry { Victim = v, Sequence = v.Hyperdiction, Seed = seed, DropPos = drop });
                Broadcast(HyperdictionMessageKind.Start, v.HyperdictionGrid.EntityId, hd.Block.EntityId, seed, drop);
            }

            if (ev.Entries.Count == 0)
                return false;

            hd.IsBusy = true;
            events.Add(ev);
            return true;
        }

        private void MonitorEvents()
        {
            for (int e = events.Count - 1; e >= 0; e--)
            {
                CatchEvent ev = events[e];
                HyperdictorBlock hd = ev.Hyperdictor;
                bool hdOk = hd != null && hd.Block != null && !hd.Block.MarkedForClose && hd.Block.IsFunctional && hd.IsEngaged;

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
                    if (entry.Victim != victim)
                        continue;

                    // Already dropped, waiting for MonitorEvents to clear the entry: nothing more to do
                    if (entry.Dropped)
                        return;

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
