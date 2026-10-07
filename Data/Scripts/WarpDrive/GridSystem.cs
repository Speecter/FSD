using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;

namespace WarpDriveMod
{
    public class GridSystem : IEquatable<GridSystem>
    {
        public bool isStatic => staticCount > 0;
        public bool valid => IsValid();
        private long lastValidTick;
        public Dictionary<string, BlockCounter> blockCounters { get; private set; } = new Dictionary<string, BlockCounter>();
        public int id { get; private set; }
        public MyCubeGrid mainGrid => gridGroup.Min as MyCubeGrid;

        private int staticCount;
        public Dictionary<MyCubeGrid, HashSet<HeatSink>> heatSinks = new Dictionary<MyCubeGrid, HashSet<HeatSink>>();
        public readonly SortedSet<IMyCubeGrid> gridGroup = new SortedSet<IMyCubeGrid>(new GridByCount());
        private readonly HashSet<IMyCubeGrid> gridGroupOld = new HashSet<IMyCubeGrid>();
        private bool _valid = true;

        public event Action<GridSystem> OnSystemInvalidated;

        public GridSystem(MyCubeGrid firstGrid)
        {
            if (firstGrid == null)
                throw new NullReferenceException("Attempt to create a grid using a null grid.");

            id = WarpDriveSession.instance.rand.Next(int.MinValue, int.MaxValue);
            if (firstGrid.MarkedForClose)
                return;

            MyAPIGateway.GridGroups.GetGroup(firstGrid, GridLinkTypeEnum.Logical, gridGroup);
            gridGroupOld.UnionWith(gridGroup);

            foreach (IMyCubeGrid grid in gridGroupOld)
                Add((MyCubeGrid)grid);
        }

        public bool Contains(MyCubeGrid grid)
        {
            return gridGroup.Contains(grid);
        }

        private void Add(MyCubeGrid grid)
        {
            if (grid == null)
                throw new NullReferenceException("Attempt to add a null grid.");

            if (grid.IsStatic)
                staticCount++;

            grid.OnBlockAdded += Grid_OnBlockAdded;
            grid.OnBlockRemoved += Grid_OnBlockRemoved;
            grid.OnStaticChanged += OnIsStaticChanged;
            grid.OnClose += Grid_OnClose;
            grid.OnGridSplit += Grid_OnGridSplit;

            foreach (MyCubeBlock s in grid.GetFatBlocks())
            {
                Grid_OnBlockAdded(s.SlimBlock);
            }
        }

        public void AddCounter(string key, BlockCounter counter)
        {
            foreach (MyCubeGrid grid in gridGroup)
            {
                foreach (MyCubeBlock block in grid.GetFatBlocks())
                {
                    counter.TryAddCount(block);
                }
            }
            blockCounters[key] = counter;
        }

        private void Grid_OnBlockRemoved(IMySlimBlock obj)
        {
            MyCubeGrid grid = (MyCubeGrid)obj.CubeGrid;
            IMyCubeBlock fat = obj.FatBlock;
            if (grid == null)
                return;

            HashSet<HeatSink> gridSinks;
            if (heatSinks.TryGetValue(grid, out gridSinks))
            {
                foreach (HeatSink queuedSink in gridSinks)
                    WarpDriveSession.instance?.QueueExposureCheck(queuedSink);
            }

            if (fat == null)
                return;

            foreach (BlockCounter counter in blockCounters.Values)
            {
                counter.TryRemoveCount(fat);
            }

            if (gridSinks != null && fat.GameLogic?.GetAs<HeatSink>() != null)
                gridSinks.RemoveWhere(tracked => tracked == null || tracked.block == null || tracked.block == fat);

            Resort(grid);
        }

        private void Grid_OnBlockAdded(IMySlimBlock obj)
        {
            MyCubeGrid grid = (MyCubeGrid)obj.CubeGrid;
            IMyCubeBlock fat = obj.FatBlock;
            if (grid == null)
                return;

            HashSet<HeatSink> gridSinks;
            if (heatSinks.TryGetValue(grid, out gridSinks))
            {
                foreach (HeatSink queuedSink in gridSinks)
                    WarpDriveSession.instance?.QueueExposureCheck(queuedSink);
            }

            if (fat == null)
                return;

            foreach (BlockCounter counter in blockCounters.Values)
            {
                counter.TryAddCount(fat);
            }

                HeatSink heatSink = fat.GameLogic?.GetAs<HeatSink>();
                if (heatSink != null)
                    AddSink(grid, heatSink);

            Resort(grid);
        }

        public void Resort(MyCubeGrid grid)
        {
            if (gridGroup.Remove(grid))
                gridGroup.Add(grid);
        }

        private void Grid_OnClose(IMyEntity obj)
        {
            Invalidate();
        }

        private void Grid_OnGridSplit(MyCubeGrid arg1, MyCubeGrid arg2)
        {
            Invalidate();
        }

         public bool IsValid(bool forceCheck = false)
        {
            if (!_valid || (!forceCheck && lastValidTick == WarpDriveSession.instance.runtime))
                return _valid;

            if (gridGroup.Count == 0 || mainGrid == null)
            {
                Invalidate();
                return false;
            }

            MyCubeGrid main = mainGrid;
            gridGroup.Clear();
            MyAPIGateway.GridGroups.GetGroup(main, GridLinkTypeEnum.Logical, gridGroup);

            if (gridGroupOld.SetEquals(gridGroup))
            {
                lastValidTick = WarpDriveSession.instance.runtime;
                return true;
            }

            Invalidate();
            return false;
        }

        public void Invalidate()
        {
            if (!_valid)
                return;

            _valid = false;
            OnSystemInvalidated?.Invoke(this);
            OnSystemInvalidated = null;
            foreach (BlockCounter counter in blockCounters.Values)
            {
                counter.Dispose();
            }

            foreach (MyCubeGrid grid in gridGroupOld)
            {
                grid.OnBlockAdded -= Grid_OnBlockAdded;
                grid.OnBlockRemoved -= Grid_OnBlockRemoved;
                grid.OnStaticChanged -= OnIsStaticChanged;
                grid.OnClose -= Grid_OnClose;
                grid.OnGridSplit -= Grid_OnGridSplit;
            }
        }

        private void OnIsStaticChanged(MyCubeGrid arg1, bool arg2)
        {
            staticCount += arg1.IsStatic ? 1 : -1;
        }

        public void AddSink(MyCubeGrid grid, HeatSink heatSink)
        {
            HashSet<HeatSink> gridSinks;
            if (!heatSinks.TryGetValue(grid, out gridSinks))
            {
                gridSinks = new HashSet<HeatSink> { heatSink };
                heatSinks[grid] = gridSinks;
            }
            else
                gridSinks.Add(heatSink);
        }

        public int CountActiveSinks(out int activeSmallCount, bool apply = false)
        {
            int activeCount = 0;
            activeSmallCount = 0;

            foreach (HashSet<HeatSink> gridSinks in heatSinks.Values)
            {
                foreach (HeatSink heatSink in gridSinks)
                {
                    if (heatSink?.block == null || heatSink.block.MarkedForClose)
                        continue;

                    if (apply && HeatSink.heatEnabled && heatSink.exposureQueued)
                        heatSink.ExposureCheck();

                    bool active = HeatSink.heatEnabled && heatSink.block.IsFunctional && heatSink.block.Enabled && heatSink.isExposed;

                    if (apply)
                    heatSink.SetActive(active);

                    if (!active)
                        continue;

                        activeCount++;

                    if (heatSink.isSmall)
                        activeSmallCount++;
                }
            }

            return activeCount;
        }

        public MatrixD FindWorldMatrix(IMyShipController cockpit)
        {
            if (gridGroup.Count == 0 || mainGrid == null)
                return Matrix.Zero;

            if (cockpit != null)
            {
                MatrixD result = cockpit.WorldMatrix;
                result.Translation = mainGrid.WorldMatrix.Translation;
                return result;
            }
            return mainGrid.WorldMatrix;
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as GridSystem);
        }

        public bool Equals(GridSystem other)
        {
            return other != null && id == other.id;
        }

        public override int GetHashCode()
        {
            return 2108858624 + id.GetHashCode();
        }

        public class BlockCounter
        {
            public int count { get; private set; }
            public event Action<IMyCubeBlock> OnBlockAdded;
            public event Action<IMyCubeBlock> OnBlockRemoved;
            private readonly Func<IMyCubeBlock, bool> method;

            public BlockCounter(Func<IMyCubeBlock, bool> blockFilter)
            {
                method = blockFilter;
            }

            public void TryAddCount(IMyCubeBlock block)
            {
                if (method.Invoke(block))
                {
                    count++;
                    OnBlockAdded?.Invoke(block);
                }
            }
            public void TryRemoveCount(IMyCubeBlock block)
            {
                if (method.Invoke(block))
                {
                    count--;
                    OnBlockRemoved?.Invoke(block);
                }
            }

            public void Dispose()
            {
                OnBlockAdded = null;
                OnBlockRemoved = null;
            }
        }

        private class GridByCount : IComparer<IMyCubeGrid>
        {
            public int Compare(IMyCubeGrid x, IMyCubeGrid y)
            {
                int result1 = ((MyCubeGrid)y).BlocksCount.CompareTo(((MyCubeGrid)x).BlocksCount);
                if (result1 == 0)
                    return x.EntityId.CompareTo(y.EntityId);
                return result1;
            }
        }
    }
}
