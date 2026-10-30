using System;
using System.Collections.Generic;
using RichHudFramework.UI;
using RichHudFramework.UI.Rendering;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRageMath;

namespace WarpDriveMod
{
    /// <summary>
    /// The fleet jump lobby. Passive while flying (a compact summary); pressing Fleet Jump makes it interactive with the mouse cursor:
    /// the leader picks the destination (GPS or blind with a range), removes ships, engages or disbands; a wing can leave.
    /// Up to 8 ships are listed at once; the mouse wheel or the UP / DN buttons page through bigger fleets.
    /// </summary>
    public class FleetLobbyMenu : HudElementBase
    {
        public const float PanelWidth = 390f;
        private const int MaxRows = 8;
        private const float RowHeight = 22f;
        private const float ButtonHeight = 24f;
        private const float Pad = 10f;
        private const int NameChars = 18;
        private const float RangeStep = 0.05f;

        private static readonly Color ButtonColor = new Color(18, 42, 60, 220);
        private static readonly Color ButtonHover = new Color(40, 90, 125, 240);
        private static readonly Color DangerButton = new Color(70, 20, 20, 220);
        private static readonly Color DangerHover = new Color(120, 35, 35, 240);

        private readonly TexturedBox back;
        private readonly TexturedBox accent;
        private readonly Label header;
        private readonly Label destCaption;
        private readonly Label destLabel;
        private readonly Label rangeLabel;
        private readonly Label shipsCaption;
        private readonly Label footer;
        private readonly LabelBoxButton closeButton, prevButton, nextButton, rangeDown, rangeUp, leftButton, engageButton, scrollUp, scrollDown;
        private readonly Label[] roles = new Label[MaxRows];
        private readonly Label[] names = new Label[MaxRows];
        private readonly Label[] states = new Label[MaxRows];
        private readonly LabelBoxButton[] removeButtons = new LabelBoxButton[MaxRows];
        private readonly long[] rowGridIds = new long[MaxRows];

        private readonly List<JumpTarget> targets = new List<JumpTarget>();

        // A ship's readiness walks its blocks (cockpit, drive, power, mass): refresh one ship per slow tick, show the cached rest
        private struct CachedStatus
        {
            public string Text;
            public Color Color;
        }
        private readonly Dictionary<long, CachedStatus> statusCache = new Dictionary<long, CachedStatus>();
        private int statusCursor;
        private FleetJumpLobby lobby;
        private long localGridId;
        private bool isLeader;
        private int targetIndex;
        private int firstRow;           // first listed ship, for fleets bigger than MaxRows
        private int shownFirstRow = -1;
        private int slowCounter;
        private bool dirty = true;

        /// <summary>True while the mouse cursor is up and the buttons work.</summary>
        public bool Interactive { get; private set; }

        public FleetLobbyMenu(HudParentBase parent) : base(parent)
        {
            back = new TexturedBox(this) { Color = HudPalette.Panel };
            accent = new TexturedBox(this) { Color = HudPalette.Primary };

            header = MakeLabel(PanelWidth - 2f * Pad - 30f);
            destCaption = MakeLabel(PanelWidth - 2f * Pad);
            destLabel = MakeLabel(PanelWidth - 2f * Pad - 70f);
            rangeLabel = MakeLabel(PanelWidth - 2f * Pad - 70f);
            shipsCaption = MakeLabel(PanelWidth - 2f * Pad);
            footer = MakeLabel(PanelWidth - 2f * Pad);

            closeButton = MakeButton("X", 26f, false, Close);
            prevButton = MakeButton("<", 28f, false, () => StepTarget(-1));
            nextButton = MakeButton(">", 28f, false, () => StepTarget(1));
            rangeDown = MakeButton("-", 28f, false, () => StepRange(-RangeStep));
            rangeUp = MakeButton("+", 28f, false, () => StepRange(RangeStep));
            leftButton = MakeButton("DISBAND", 130f, true, LeftButtonPressed);
            engageButton = MakeButton("ENGAGE", 150f, false, EngagePressed);
            scrollUp = MakeButton("UP", 40f, false, () => Scroll(MaxRows));
            scrollDown = MakeButton("DN", 40f, false, () => Scroll(-MaxRows));
            scrollUp.Size = scrollDown.Size = new Vector2(40f, RowHeight - 2f);

            for (int i = 0; i < MaxRows; i++)
            {
                int row = i;
                roles[i] = MakeLabel(40f);
                names[i] = MakeLabel(160f);
                states[i] = MakeLabel(84f);
                removeButtons[i] = MakeButton("REMOVE", 70f, true, () => RemovePressed(row));
            }

            Visible = false;
        }

        private Label MakeLabel(float width)
        {
            return new Label(this)
            {
                AutoResize = false,
                Size = new Vector2(width, RowHeight),
                BuilderMode = TextBuilderModes.Unlined,
                VertCenterText = true
            };
        }

        private LabelBoxButton MakeButton(string text, float width, bool danger, Action onClick)
        {
            var button = new LabelBoxButton(this)
            {
                AutoResize = false,
                Size = new Vector2(width, ButtonHeight),
                Color = danger ? DangerButton : ButtonColor,
                HighlightColor = danger ? DangerHover : ButtonHover,
                VertCenterText = true,
                BuilderMode = TextBuilderModes.Unlined,
                Visible = false
            };
            button.TextBoard.SetText(text, HudPalette.Text(danger ? HudPalette.Danger : HudPalette.Primary, 0.78f, TextAlignment.Center));
            button.MouseInput.LeftClicked += (sender, args) => onClick();
            return button;
        }

        // ------------------------------------------------------------------ open / close

        public void Open(FleetJumpLobby fleet, long gridId)
        {
            lobby = fleet;
            localGridId = gridId;
            statusCache.Clear();
            firstRow = 0;
            isLeader = fleet != null && fleet.LeaderGridId == gridId;
            Interactive = fleet != null && !fleet.IsExecuting;
            dirty = true;

            if (isLeader)
                RebuildTargets();
        }

        public void Close()
        {
            Interactive = false;
            dirty = true;
        }

        private void RebuildTargets()
        {
            var leaderHs = FleetJumpSystem.FindHyperspace(lobby.LeaderGridId);
            var leaderGrid = MyEntities.GetEntityById(lobby.LeaderGridId) as MyCubeGrid;
            if (leaderHs == null || leaderGrid == null)
            {
                targets.Clear();
                return;
            }

            float power = leaderHs.GetDrivePowerMW();
            JumpTargets.Build(targets, leaderGrid.PositionComp.GetPosition(), HyperspaceSystem.MIN_JUMP_DISTANCE,
                leaderHs.GetMaxJumpDistance(power), leaderHs.GetCurrentManualDistance(power));
            targetIndex = JumpTargets.IndexOf(targets, lobby.Mode == HyperspaceSystem.JumpMode.GpsWaypoint ? lobby.TargetGpsCoords : null);
        }

        // ------------------------------------------------------------------ actions

        private long LocalPlayerId { get { return MyAPIGateway.Session?.Player?.IdentityId ?? 0L; } }

        /// <summary>Moves the ship list: positive goes up towards the leader (mouse wheel up).</summary>
        public void Scroll(int rows)
        {
            if (rows == 0)
                return;

            firstRow -= rows;   // clamped to the fleet size in Update
            dirty = true;
        }

        private void StepTarget(int step)
        {
            if (!isLeader || lobby == null || targets.Count == 0)
                return;

            targetIndex = (targetIndex + step + targets.Count) % targets.Count;
            JumpTarget t = targets[targetIndex];
            if (t.Blind)
                FleetJumpSystem.SetFleetTarget(lobby, HyperspaceSystem.JumpMode.ManualDistance, null, string.Empty, lobby.JumpDistanceRatio, LocalPlayerId);
            else
                FleetJumpSystem.SetFleetTarget(lobby, HyperspaceSystem.JumpMode.GpsWaypoint, t.Coords, t.Name, lobby.JumpDistanceRatio, LocalPlayerId);
            dirty = true;
        }

        private void StepRange(float delta)
        {
            if (!isLeader || lobby == null || lobby.Mode != HyperspaceSystem.JumpMode.ManualDistance)
                return;

            float ratio = MathHelper.Clamp(lobby.JumpDistanceRatio + delta, 0f, 1f);
            FleetJumpSystem.SetFleetTarget(lobby, HyperspaceSystem.JumpMode.ManualDistance, null, string.Empty, ratio, LocalPlayerId);
            dirty = true;
        }

        private void RemovePressed(int row)
        {
            if (!isLeader || lobby == null || rowGridIds[row] == 0L || rowGridIds[row] == lobby.LeaderGridId)
                return;

            FleetJumpSystem.CancelFleetJump(rowGridIds[row], LocalPlayerId);
            dirty = true;
        }

        private void LeftButtonPressed()
        {
            if (lobby == null)
                return;

            // Leader disbands the whole fleet, a wing just leaves it (the drive's spool-down message reports it)
            FleetJumpSystem.CancelFleetJump(isLeader ? lobby.LeaderGridId : localGridId, LocalPlayerId);
            Close();
        }

        private void EngagePressed()
        {
            if (isLeader && lobby != null && FleetJumpSystem.TryEngageFleetJump(lobby))
                Close();
        }

        // ------------------------------------------------------------------ per frame

        /// <param name="left">Left edge of the panel.</param>
        /// <param name="centerY">Vertical centre of the panel.</param>
        public void Update(FsdHudModel m, bool slow, float left, float centerY)
        {
            if (m.Lobby == null || m.Lobby.IsExecuting)
            {
                Visible = false;
                Interactive = false;
                lobby = null;
                return;
            }

            if (m.Lobby != lobby || m.IsFleetLeader != isLeader)
            {
                lobby = m.Lobby;
                isLeader = m.IsFleetLeader;
                dirty = true;
            }
            Visible = true;

            int total = lobby.Members.Count;
            int rowCount = Math.Min(MaxRows, total);
            firstRow = MathHelper.Clamp(firstRow, 0, Math.Max(0, total - MaxRows));
            if (firstRow != shownFirstRow)
            {
                shownFirstRow = firstRow;
                dirty = true;
            }
            bool paged = total > MaxRows;
            bool blind = lobby.Mode != HyperspaceSystem.JumpMode.GpsWaypoint;
            bool leaderControls = Interactive && isLeader;

            // ---- layout (top to bottom)
            float height = Pad + 24f + 18f + RowHeight + (blind ? RowHeight + 4f : 0f) + 22f + rowCount * RowHeight + 8f + RowHeight + (Interactive ? ButtonHeight + 8f : 0f) + Pad;
            Size = new Vector2(PanelWidth, height);
            back.Size = Size;
            accent.Size = new Vector2(PanelWidth, 2f);
            accent.Offset = new Vector2(0f, height * 0.5f - 1f);
            Offset = new Vector2(left + PanelWidth * 0.5f, centerY);

            float inner = PanelWidth * 0.5f - Pad;
            float y = height * 0.5f - Pad;

            header.Offset = new Vector2(-15f, y - 12f);
            closeButton.Offset = new Vector2(inner - 13f, y - 12f);
            closeButton.Visible = Interactive;
            y -= 24f;

            destCaption.Offset = new Vector2(0f, y - 9f);
            y -= 18f;

            destLabel.Offset = new Vector2(0f, y - RowHeight * 0.5f);
            prevButton.Offset = new Vector2(-inner + 14f, y - RowHeight * 0.5f);
            nextButton.Offset = new Vector2(inner - 14f, y - RowHeight * 0.5f);
            prevButton.Visible = nextButton.Visible = leaderControls;
            y -= RowHeight;

            rangeLabel.Visible = blind;
            rangeDown.Visible = rangeUp.Visible = blind && leaderControls;
            if (blind)
            {
                y -= 4f;
                rangeLabel.Offset = new Vector2(0f, y - RowHeight * 0.5f);
                rangeDown.Offset = new Vector2(-inner + 14f, y - RowHeight * 0.5f);
                rangeUp.Offset = new Vector2(inner - 14f, y - RowHeight * 0.5f);
                y -= RowHeight;
            }

            shipsCaption.Offset = new Vector2(0f, y - 11f);
            scrollUp.Visible = scrollDown.Visible = Interactive && paged;
            scrollUp.Offset = new Vector2(inner - 66f, y - 11f);
            scrollDown.Offset = new Vector2(inner - 20f, y - 11f);
            y -= 22f;

            for (int i = 0; i < MaxRows; i++)
            {
                float ry = y - RowHeight * (i + 0.5f);
                roles[i].Offset = new Vector2(-inner + 20f, ry);
                names[i].Offset = new Vector2(-inner + 44f + 80f, ry);
                states[i].Offset = new Vector2(inner - (leaderControls ? 76f : 0f) - 42f, ry);
                removeButtons[i].Offset = new Vector2(inner - 35f, ry);
            }
            y -= rowCount * RowHeight + 8f;

            footer.Offset = new Vector2(0f, y - RowHeight * 0.5f);
            y -= RowHeight;

            leftButton.Visible = Interactive;
            engageButton.Visible = leaderControls;
            if (Interactive)
            {
                y -= 8f;
                float by = y - ButtonHeight * 0.5f;
                if (isLeader)
                {
                    leftButton.Offset = new Vector2(-inner + 65f, by);
                    engageButton.Offset = new Vector2(inner - 75f, by);
                }
                else
                {
                    leftButton.Offset = new Vector2(0f, by);
                }
            }

            // ---- one ship's readiness per slow tick (round robin), texts about three times a second
            if (slow && lobby.Members.Count > 0)
            {
                statusCursor = (statusCursor + 1) % lobby.Members.Count;
                RefreshStatus(lobby.Members[statusCursor]);
            }

            if (!dirty && (!slow || ++slowCounter % 3 != 0))
                return;

            dirty = false;
            RefreshContent(rowCount, blind, leaderControls);
        }

        private void RefreshContent(int rowCount, bool blind, bool leaderControls)
        {
            int total = lobby.Members.Count;
            header.TextBoard.SetText("FLEET JUMP  -  " + total + (total == 1 ? " SHIP" : " SHIPS"), HudPalette.Text(HudPalette.Primary, 0.88f));
            destCaption.TextBoard.SetText("DESTINATION", HudPalette.Text(HudPalette.Dim, 0.66f));

            string dest = blind ? "BLIND JUMP  " + HudFormat.Distance(lobby.LeaderJumpDistance)
                : (string.IsNullOrEmpty(lobby.TargetGpsName) ? "GPS TARGET" : lobby.TargetGpsName) + "  " + HudFormat.Distance(lobby.LeaderJumpDistance);
            destLabel.TextBoard.SetText(dest, HudPalette.Text(Color.White, 0.82f, TextAlignment.Center));

            if (blind)
                rangeLabel.TextBoard.SetText("RANGE  " + HudFormat.Percent(lobby.JumpDistanceRatio) + "  OF MAX", HudPalette.Number(HudPalette.Primary, 0.76f, TextAlignment.Center));

            string caption = total > MaxRows ? "SHIPS  " + (firstRow + 1) + "-" + (firstRow + rowCount) + " OF " + total : "SHIPS";
            shipsCaption.TextBoard.SetText(caption, HudPalette.Text(HudPalette.Dim, 0.66f));

            // Readiness counts every ship, listed or not
            int notReady = 0;
            for (int m = 0; m < total; m++)
            {
                if (Status(lobby.Members[m]).Color != HudPalette.Ready)
                    notReady++;
            }

            for (int i = 0; i < MaxRows; i++)
            {
                bool used = i < rowCount;
                roles[i].Visible = names[i].Visible = states[i].Visible = used;
                if (!used)
                {
                    removeButtons[i].Visible = false;
                    rowGridIds[i] = 0L;
                    continue;
                }

                int index = firstRow + i;
                FleetMemberInfo member = lobby.Members[index];
                bool memberIsLeader = member.GridEntityId == lobby.LeaderGridId;
                rowGridIds[i] = member.GridEntityId;
                removeButtons[i].Visible = leaderControls && !memberIsLeader;

                CachedStatus cached = Status(member);
                string status = cached.Text;
                Color color = cached.Color;

                string name = string.IsNullOrEmpty(member.GridName) ? "UNNAMED" : member.GridName;
                if (name.Length > NameChars)
                    name = name.Substring(0, NameChars - 1) + ".";

                roles[i].TextBoard.SetText(memberIsLeader ? "LEAD" : "W" + index, HudPalette.Text(memberIsLeader ? HudPalette.Primary : HudPalette.Dim, 0.7f));
                names[i].TextBoard.SetText(name, HudPalette.Text(member.GridEntityId == localGridId ? HudPalette.Primary : Color.White, 0.76f));
                states[i].TextBoard.SetText(status, HudPalette.Number(color, 0.74f, TextAlignment.Right));
            }

            string footerText;
            Color footerColor;
            if (!Interactive)
            {
                footerText = "PRESS FLEET JUMP TO OPEN THE LOBBY";
                footerColor = HudPalette.Dim;
            }
            else if (!isLeader)
            {
                footerText = "WAITING FOR THE LEADER TO ENGAGE";
                footerColor = HudPalette.Dim;
            }
            else if (notReady == 0)
            {
                footerText = "ALL SHIPS READY";
                footerColor = HudPalette.Ready;
            }
            else
            {
                footerText = notReady + (notReady == 1 ? " SHIP NOT READY" : " SHIPS NOT READY");
                footerColor = HudPalette.Warning;
            }
            footer.TextBoard.SetText(footerText, HudPalette.Text(footerColor, 0.74f, TextAlignment.Center));

            leftButton.TextBoard.SetText(isLeader ? "DISBAND" : "LEAVE FLEET", HudPalette.Text(HudPalette.Danger, 0.78f, TextAlignment.Center));
            engageButton.TextBoard.SetText("ENGAGE", HudPalette.Text(notReady == 0 ? HudPalette.Ready : HudPalette.Dim, 0.82f, TextAlignment.Center));
        }

        /// <summary>The ship's last known readiness; worked out now if it has none yet.</summary>
        private CachedStatus Status(FleetMemberInfo member)
        {
            CachedStatus cached;
            return statusCache.TryGetValue(member.GridEntityId, out cached) ? cached : RefreshStatus(member);
        }

        private CachedStatus RefreshStatus(FleetMemberInfo member)
        {
            string text;
            Color color;
            ShipStatus(member, out text, out color);
            var cached = new CachedStatus { Text = text, Color = color };
            statusCache[member.GridEntityId] = cached;
            return cached;
        }

        private void ShipStatus(FleetMemberInfo member, out string status, out Color color)
        {
            HyperspaceSystem hs = FleetJumpSystem.FindHyperspace(member.GridEntityId);
            double offset = FleetJumpSystem.GetMemberAlignmentOffset(member.GridEntityId, lobby);

            if (hs != null && hs.State == HyperspaceSystem.HyperState.Cooldown)
            {
                status = "COOLING";
                color = HudPalette.Warning;
            }
            else if (hs != null && hs.GetMaxJumpDistance(hs.GetDrivePowerMW()) + 1.0 < lobby.LeaderJumpDistance)
            {
                status = "NO RANGE";
                color = HudPalette.Danger;
            }
            else if (offset > 900.0)
            {
                status = "--";
                color = HudPalette.Dim;
            }
            else if (offset <= HyperspaceSystem.ALIGNMENT_TOLERANCE_DEG)
            {
                status = "READY";
                color = HudPalette.Ready;
            }
            else
            {
                status = HudFormat.Degrees((float)offset);
                color = HudPalette.Warning;
            }
        }
    }
}
