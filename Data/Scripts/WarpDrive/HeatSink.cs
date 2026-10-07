using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;

namespace WarpDriveMod
{
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_UpgradeModule), false, "HeatSinkLarge", "HeatSinkSmall", "HeatSinkVentLarge", "HeatSinkVentSmall")]
    public class HeatSink : MyGameLogicComponent
    {
        private static readonly Color minUseColor = new Color(255, 20, 0);
        private static readonly Color maxUseColor = new Color(255, 197, 20);

        private static readonly string[] ventPlates = { "HeatVentPlate1", "HeatVentPlate2", "HeatVentPlate3", "HeatVentPlate4", "HeatVentPlate5" };

        private static readonly string[] standardEmissives = { "Emissive0", "Emissive1", "Emissive2", "Emissive3" };

        public static bool heatEnabled = true;

        public IMyFunctionalBlock block { get; private set; }

        public bool isActive { get; private set; }
        public bool isExposed { get; private set; }
        public bool exposureQueued;

        private MyResourceSinkComponent sink;

        private bool isVent;
        public bool isSmall { get; private set; }
        private Dictionary<string, MatrixD> subpartRestPosi;

        private float smoothedHeatRatio;

        private float sinkPowerUse => 0.5f * (isSmall ? 0.25f : 1f);
        private WarpSystem system => WarpDriveSession.instance?.GetGridWarpSystem(block?.CubeGrid as MyCubeGrid);

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            base.Init(objectBuilder);

            try
            {
            block = (IMyFunctionalBlock)Entity;

                isSmall = new[] { "HeatSinkSmall", "HeatSinkVentSmall" }.Contains(block.BlockDefinition.SubtypeId);

                sink = new MyResourceSinkComponent();
                sink.Init(MyStringHash.GetOrCompute("Utility"), sinkPowerUse, GetRequiredPower, (MyCubeBlock)Entity);
                Entity.Components.Add(sink);
                sink.Update();

                isVent = new[] { "HeatSinkVentLarge", "HeatSinkVentSmall" }.Contains(block.BlockDefinition.SubtypeId);
                if (isVent)
                    subpartRestPosi = new Dictionary<string, MatrixD>();

                if (!MyAPIGateway.Utilities.IsDedicated)
                    block.AppendingCustomInfo += Block_AppendingCustomInfo;

                NeedsUpdate = MyEntityUpdateEnum.EACH_10TH_FRAME | MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }
            catch (Exception)
            {
            }
        }

        private float GetRequiredPower()
        {
            if (block == null || !block.IsFunctional || !block.Enabled)
                return 0f;

            if (!heatEnabled)
                return 0.0001f;

            return IsOperating() ? sinkPowerUse : 0.002f * (isSmall ? 0.5f : 1f);
        }

        private bool IsOperating()
        {
            if (!heatEnabled || !isActive)
                return false;

            WarpSystem warpSystem = system;

            return warpSystem != null && (warpSystem.warpState != WarpSystem.State.idle || warpSystem.driveHeat > 0);
        }

        private static readonly Vector3I[] cellNeighbors =
        {
            new Vector3I(1, 0, 0), new Vector3I(-1, 0, 0),
            new Vector3I(0, 1, 0), new Vector3I(0, -1, 0),
            new Vector3I(0, 0, 1), new Vector3I(0, 0, -1),
        };

        private static readonly HashSet<Vector3I> visitedCells = new HashSet<Vector3I>();
        private static readonly Queue<Vector3I> cellQueue = new Queue<Vector3I>();

        private static bool IsOutsideBounds(IMyCubeGrid cubeGrid, Vector3I cell)
        {
            Vector3I min = cubeGrid.Min;
            Vector3I max = cubeGrid.Max;
            return cell.X < min.X || cell.Y < min.Y || cell.Z < min.Z
                || cell.X > max.X || cell.Y > max.Y || cell.Z > max.Z;
        }

        public void ExposureCheck()
        {
            isExposed = false;
            visitedCells.Clear();
            cellQueue.Clear();

            try
            {
                IMyCubeGrid cubeGrid = block?.CubeGrid;
                if (cubeGrid == null)
                    return;

                Vector3I faceStep = Base6Directions.GetIntVector(block.Orientation.TransformDirection(isVent ? Base6Directions.Direction.Up : Base6Directions.Direction.Forward));
                Vector3I frontCell = block.Position + faceStep;

                if (cubeGrid.CubeExists(frontCell))
                    return;

                Vector3I cell = frontCell;
                while (!IsOutsideBounds(cubeGrid, cell) && !cubeGrid.CubeExists(cell))
                    cell += faceStep;

                if (IsOutsideBounds(cubeGrid, cell))
                {
                    isExposed = true;
                    return;
                }

                visitedCells.Add(frontCell);
                cellQueue.Enqueue(frontCell);

                while (cellQueue.Count > 0)
                {
                    Vector3I current = cellQueue.Dequeue();

                    for (int i = 0; i < cellNeighbors.Length; i++)
                    {
                        Vector3I neighbor = current + cellNeighbors[i];

                        if (visitedCells.Contains(neighbor))
                            continue;

                        if (IsOutsideBounds(cubeGrid, neighbor))
                        {
                            isExposed = true;
                            return;
                        }

                        if (cubeGrid.CubeExists(neighbor))
                            continue;

                        visitedCells.Add(neighbor);

                        if (visitedCells.Count > 128)
                        {
                            isExposed = true;
                            return;
                        }

                        cellQueue.Enqueue(neighbor);
                    }
                }
            }
            catch (Exception)
            {
                isExposed = false;
            }
            finally
            {
                visitedCells.Clear();
                cellQueue.Clear();
            }
        }

        private void Block_AppendingCustomInfo(IMyTerminalBlock arg1, StringBuilder info)
        {
            if (arg1 == null || block == null)
                return;

            info?.AppendLine("Status: " + (!heatEnabled ? "Ready" : !isExposed ? "Not exposed enough" : IsOperating() ? "Active" : "Ready"));

            info?.AppendLine("Max Required Power: " + (heatEnabled ? sinkPowerUse * 1000f : 0.1f).ToString("N") + " kW");

            info?.AppendLine("Required Power: " + (GetRequiredPower() * 1000f).ToString("N") + " kW");
        }

        public override void UpdateOnceBeforeFrame()
        {
            system?.grid?.AddSink(block.CubeGrid as MyCubeGrid, this);
            WarpDriveSession.instance?.QueueExposureCheck(this);
        }

        public override void UpdateBeforeSimulation10()
        {
            sink?.Update();

            if (!MyAPIGateway.Utilities.IsDedicated)
            {
                UpdateVisuals();
                block?.RefreshCustomInfo();
            }
        }

        private void UpdateVisuals()
        {
            if (block == null)
                return;

            WarpSystem warpSystem = system;
            float dissipationRate = warpSystem?.heatDissipationRate ?? 0f;

            smoothedHeatRatio = MathHelper.Lerp(smoothedHeatRatio,
                (warpSystem?.driveHeat ?? 0) > 0 ? 1f
                : dissipationRate > 0.0001f ? MathHelper.Clamp((warpSystem?.heatGenerationRate ?? 0f) / dissipationRate, 0f, 1f)
                : 0f,
                0.05f);

            if (isVent)
            {
                try
                {
                    ((MyCubeBlock)Entity).SetEmissiveParts("Emissive", Color.Lerp(minUseColor, maxUseColor, smoothedHeatRatio), MathHelper.Lerp(0.1f, 3f, smoothedHeatRatio));
                }
                catch (Exception)
                {
                }

                try
                {
                    for (int i = 0; i < ventPlates.Length; i++)
                    {
                        string plate = ventPlates[i];

                        MyEntitySubpart subpart;
                        if (!((MyEntity)Entity).TryGetSubpart(plate, out subpart))
                            continue;

                        MatrixD restPose;
                        if (!subpartRestPosi.TryGetValue(plate, out restPose))
                        {
                            restPose = subpart.PositionComp.LocalMatrixRef;
                            subpartRestPosi[plate] = restPose;
                        }

                        subpart.PositionComp.LocalMatrix = MatrixD.CreateFromAxisAngle(Vector3D.UnitZ, MathHelper.ToRadians((i == 4 ? -1f : 1f) * (-45f + 90f * smoothedHeatRatio))) * restPose;
                    }
                }
                catch (Exception)
                {
                }

                return;
            }

            try
            {
                MyCubeBlock cubeBlock = (MyCubeBlock)Entity;
                int lightCount = standardEmissives.Length;

                if (!block.IsWorking)
                {
                    for (int i = 0; i < lightCount; i++)
                        cubeBlock.SetEmissiveParts(standardEmissives[i], new Color(255, 0, 0), 1f);
                    return;
                }

                if (smoothedHeatRatio <= 0.01f) //starts going idle after falling below 1%
                {
                    for (int i = 0; i < lightCount; i++)
                        cubeBlock.SetEmissiveParts(standardEmissives[i], Color.Black, 0f);

                    cubeBlock.SetEmissiveParts(standardEmissives[0], minUseColor, 1f);
                    return;
                }

                for (int i = 0; i < lightCount; i++)
                {
                    float segmentFill = MathHelper.Clamp(smoothedHeatRatio * lightCount - i, 0f, 1f);

                    if (segmentFill <= 0f)
                        cubeBlock.SetEmissiveParts(standardEmissives[i], Color.Black, 0f);
                    else
                        cubeBlock.SetEmissiveParts(standardEmissives[i], Color.Lerp(minUseColor, maxUseColor, smoothedHeatRatio),
                            Math.Max(MathHelper.Lerp(0.1f, 3f, smoothedHeatRatio) * segmentFill, i == 0 ? 1f : 0f));
                }
            }
            catch (Exception)
            {
            }
        }

        public void SetActive(bool active)
        {
            isActive = active;
        }

        public override void Close()
        {
            isActive = false;

            if (!MyAPIGateway.Utilities.IsDedicated && block != null)
                block.AppendingCustomInfo -= Block_AppendingCustomInfo;
        }

        public override bool Equals(object obj)
        {
            var other = obj as HeatSink;
            return other != null && block == other.block;
        }

        public override int GetHashCode()
        {
            return block?.GetHashCode() ?? 0;
        }
    }
}
