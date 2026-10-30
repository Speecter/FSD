using System.Text;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Game.ModAPI;
using VRage.Utils;

namespace WarpDriveMod
{
    public static class HyperdictorControls
    {
        private static bool controlsInitialized = false;

        public static void RegisterControls()
        {
            if (controlsInitialized || MyAPIGateway.Utilities.IsDedicated)
                return;

            controlsInitialized = true;

            var separator = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSeparator, IMyUpgradeModule>("HyperdictorSeparator");
            separator.Enabled = IsHyperdictor;
            separator.Visible = IsHyperdictor;
            MyAPIGateway.TerminalControls.AddControl<IMyUpgradeModule>(separator);

            var engageBtn = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyUpgradeModule>("HyperdictorEngageBtn");
            engageBtn.Title = MyStringId.GetOrCompute("Engage / Disengage Interdiction Field");
            engageBtn.Tooltip = MyStringId.GetOrCompute("Engages the 500 km hyperdiction field. The ship must be completely stationary and the capacitor fully charged.");
            engageBtn.Enabled = IsHyperdictor;
            engageBtn.Visible = IsHyperdictor;
            engageBtn.SupportsMultipleBlocks = false;
            engageBtn.Action = OnEngagePressed;
            MyAPIGateway.TerminalControls.AddControl<IMyUpgradeModule>(engageBtn);

            var engageAction = MyAPIGateway.TerminalControls.CreateAction<IMyUpgradeModule>("HyperdictorEngageAction");
            engageAction.Name = new StringBuilder("Hyperdictor Engage / Disengage");
            engageAction.Icon = "Textures\\GUI\\Icons\\Actions\\Toggle.dds";
            engageAction.Enabled = IsHyperdictor;
            engageAction.Action = OnEngagePressed;
            engageAction.Writer = ActionWriter;
            MyAPIGateway.TerminalControls.AddAction<IMyUpgradeModule>(engageAction);

            var statusProp = MyAPIGateway.TerminalControls.CreateProperty<string, IMyUpgradeModule>("HyperdictorStatus");
            statusProp.Enabled = IsHyperdictor;
            statusProp.Visible = IsHyperdictor;
            statusProp.Getter = (b) => { var hd = GetHyperdictor(b); return hd != null ? hd.State.ToString() : "None"; };
            MyAPIGateway.TerminalControls.AddControl<IMyUpgradeModule>(statusProp);
        }

        private static HyperdictorBlock GetHyperdictor(IMyTerminalBlock block)
        {
            return block?.GameLogic?.GetAs<HyperdictorBlock>();
        }

        private static bool IsHyperdictor(IMyTerminalBlock block)
        {
            return GetHyperdictor(block) != null;
        }

        private static void ActionWriter(IMyTerminalBlock block, StringBuilder sb)
        {
            var hd = GetHyperdictor(block);
            if (hd == null)
                return;

            switch (hd.State)
            {
                case HyperdictorState.Engaged:
                    sb.Append("Engaged");
                    break;
                case HyperdictorState.Charged:
                    sb.Append("Ready");
                    break;
                case HyperdictorState.Charging:
                    sb.Append("Chg ").Append((int)(hd.StoredChargeRatio * 100)).Append('%');
                    break;
                case HyperdictorState.CoolingDown:
                    int secs = hd.CooldownTicksRemaining / 60;
                    sb.Append("Cool ").Append(secs / 60).Append(':').Append((secs % 60).ToString("D2"));
                    break;
                case HyperdictorState.Disrupted:
                    sb.Append("Disrupted");
                    break;
                default:
                    sb.Append("Off");
                    break;
            }
        }

        private static void OnEngagePressed(IMyTerminalBlock block)
        {
            var hd = GetHyperdictor(block);
            if (hd == null)
                return;

            long playerId = MyAPIGateway.Session?.Player?.IdentityId ?? 0L;

            if (MyAPIGateway.Multiplayer.IsServer)
            {
                hd.RequestToggleEngagement(playerId);
                return;
            }

            var msg = new HyperdictorToggleMessage
            {
                entityId = block.EntityId,
                sendingPlayerID = playerId
            };

            byte[] data = MyAPIGateway.Utilities.SerializeToBinary(msg);
            MyAPIGateway.Multiplayer.SendMessageToServer(WarpDriveSession.hyperdictorTogglePacket, data);
        }
    }
}
