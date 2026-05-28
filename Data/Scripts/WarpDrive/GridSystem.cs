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
        /// <summary>
        /// True if at least 1 of the grids in the system is static.
        /// </summary>
        public bool IsStatic => staticCount > 0;
        public bool Valid => IsValid();
        public long InvalidOn { get; private set; }
        public Dictionary<string, BlockCounter> BlockCounters { get; private set; } = new Dictionary<string, BlockCounter>();
        public IReadOnlyCollection<MyCubeGrid> Grids => grids;
        public int Id { get; private set; }
        public MyCubeGrid MainGrid
        {
            get
            {
                foreach (var grid in grids)
                {
                    if (grid == null)
                        continue;

                    return grid;
                }

                return null;
            }
        }

        private int staticCount;
        public Dictionary<MyCubeGrid, HashSet<IMyShipController>> cockpits = new Dictionary<MyCubeGrid, HashSet<IMyShipController>>();
        private readonly SortedSet<MyCubeGrid> grids = new SortedSet<MyCubeGrid>(new GridByCount());
        private bool _valid = true;

        /// <summary>
        /// Called when a grid no longer belongs to this grid system.
        /// </summary>
        public event Action<GridSystem> OnSystemInvalidated;

        public GridSystem(MyCubeGrid firstGrid)
        {
            if (firstGrid == null)
                throw new NullReferenceException("Attempt to create a grid using a null grid.");

            Id = WarpDriveSession.Instance.Rand.Next(int.MinValue, int.MaxValue);
            if (firstGrid.MarkedForClose)
                return;

            List<IMyCubeGrid> connectedGrids = new List<IMyCubeGrid>();
            MyAPIGateway.GridGroups.GetGroup(firstGrid, GridLinkTypeEnum.Logical, connectedGrids);

            foreach (IMyCubeGrid grid in connectedGrids)
            {
                if (!Add((MyCubeGrid)grid))
                    throw new ArgumentException($"Invalid add state with {firstGrid.EntityId} and {grid.EntityId}");
            }
        }

        public bool Contains(MyCubeGrid grid)
        {
            return grids.Contains(grid);
        }

        private bool Add(MyCubeGrid grid)
        {
            if (grid == null)
                throw new NullReferenceException("Attempt to add a null grid.");

            if (!grids.Add(grid))
                throw new ArgumentException("Grid already exists.");

            if (grid.IsStatic)
                staticCount++;

            grid.OnBlockAdded += Grid_OnBlockAdded;
            grid.OnBlockRemoved += Grid_OnBlockRemoved;
            grid.OnStaticChanged += Grid_OnIsStaticChanged;
            grid.OnClose += Grid_OnClose;
            grid.OnGridSplit += Grid_OnGridSplit;

            foreach (MyCubeBlock s in grid.GetFatBlocks())
            {
                Grid_OnBlockAdded(s.SlimBlock);
            }

            return true;
        }

        public void AddCounter(string key, BlockCounter counter)
        {
            foreach (MyCubeGrid grid in grids)
            {
                foreach (MyCubeBlock block in grid.GetFatBlocks())
                {
                    counter.TryAddCount(block);
                }
            }
            BlockCounters[key] = counter;
        }

        private void Grid_OnBlockRemoved(IMySlimBlock obj)
        {
            MyCubeGrid grid = (MyCubeGrid)obj.CubeGrid;
            IMyCubeBlock fat = obj.FatBlock;
            if (fat == null || grid == null)
                return;

            foreach (BlockCounter counter in BlockCounters.Values)
            {
                counter.TryRemoveCount(fat);
            }

            if (IsShipController(fat))
            {
                HashSet<IMyShipController> gridCockpits;
                if (cockpits.TryGetValue(grid, out gridCockpits))
                {
                    gridCockpits.Remove((IMyShipController)fat);
                    cockpits[grid] = gridCockpits;
                }
            }

            Resort(grid);
        }

        private void Grid_OnBlockAdded(IMySlimBlock obj)
        {
            MyCubeGrid grid = (MyCubeGrid)obj.CubeGrid;
            IMyCubeBlock fat = obj.FatBlock;
            if (fat == null || grid == null)
                return;

            foreach (BlockCounter counter in BlockCounters.Values)
            {
                counter.TryAddCount(fat);
            }

            if (IsShipController(fat))
            {
                HashSet<IMyShipController> gridCockpits;
                if (!cockpits.TryGetValue(grid, out gridCockpits))
                {
                    gridCockpits = new HashSet<IMyShipController>
                    {
                        (IMyShipController)fat
                    };
                    cockpits[grid] = gridCockpits;
                }
                else
                    gridCockpits.Add((IMyShipController)fat);
            }
            Resort(grid);
        }

        public void Resort(MyCubeGrid grid)
        {
            if (grids.Remove(grid))
                grids.Add(grid);
        }

        private void Grid_OnClose(IMyEntity obj)
        {
            Invalidate();
        }

        private void Grid_OnGridSplit(MyCubeGrid arg1, MyCubeGrid arg2)
        {
            Invalidate();
        }

        public bool IsValid()
        {
            if (!_valid || InvalidOn == WarpDriveSession.Instance.Runtime)
                return _valid;

            // Update the state of the Valid bool
            if (grids.Count > 0 && grids.Count == 1)
            {
                InvalidOn = WarpDriveSession.Instance.Runtime;
                return true;
            }
            else
            {
                if (grids.Count > 1 && MainGrid != null)
                {
                    var realCountList = new List<IMyCubeGrid>();
                    MyAPIGateway.GridGroups.GetGroup(MainGrid, GridLinkTypeEnum.Logical, realCountList);
                    if (grids.Count == realCountList.Count)
                    {
                        InvalidOn = WarpDriveSession.Instance.Runtime;
                        return true;
                    }
                    else
                    {
                        Invalidate();
                        return false;
                    }
                }

                Invalidate();
                return false;
            }
        }

        public void Invalidate()
        {
            _valid = false;
            OnSystemInvalidated?.Invoke(this);
            OnSystemInvalidated = null;
            foreach (BlockCounter counter in BlockCounters.Values)
            {
                counter.Dispose();
            }

            foreach (MyCubeGrid grid in grids)
            {
                grid.OnBlockAdded -= Grid_OnBlockAdded;
                grid.OnBlockRemoved -= Grid_OnBlockRemoved;
                grid.OnStaticChanged -= Grid_OnIsStaticChanged;
                grid.OnClose -= Grid_OnClose;
                grid.OnGridSplit -= Grid_OnGridSplit;
            }
        }

        private void Grid_OnIsStaticChanged(MyCubeGrid arg1, bool arg2)
        {
            if (arg1.IsStatic)
                staticCount++;
            else
                staticCount--;
        }

        #region WorldMatrix

        public bool IsShipController(IMyCubeBlock block)
        {
            if (block == null || !(block is IMyTerminalBlock) || block is IMyCryoChamber)
                return false;

            return (block as IMyShipController)?.CanControlShip == true;
        }

        private bool IsLiveShipController(IMyCubeBlock block)
        {
            if (block == null || !(block is IMyTerminalBlock) || block is IMyCryoChamber)
                return false;

            if ((block as IMyShipController)?.CanControlShip == true)
            {
                if ((block as IMyShipController).IsUnderControl)
                    return true;
            }

            return false;
        }

        private IMyShipController FindMainCockpit()
        {
            if (grids.Count == 0)
                return null;

            // Loop through all grids starting at largest until an in use one is found
            foreach (MyCubeGrid grid in grids)
            {
                // Use the main cockpit if it exists
                IMyTerminalBlock block = grid.MainCockpit;
                if (block != null && IsLiveShipController(block))
                    return (IMyShipController)block;

                HashSet<IMyShipController> controlledgridCockpits = new HashSet<IMyShipController>();
                if (cockpits.TryGetValue(grid, out controlledgridCockpits))
                {
                    foreach (IMyShipController cockpit in controlledgridCockpits)
                    {
                        if (cockpit.IsUnderControl)
                            return cockpit;
                    }
                }
            }

            // No in use cockpit was found.
            if (MainGrid == null)
                return null;

            HashSet<IMyShipController> gridCockpits;
            if (cockpits.TryGetValue(MainGrid, out gridCockpits))
                return gridCockpits.FirstElement();

            return null;
        }

        public MatrixD FindWorldMatrix()
        {
            if (grids.Count == 0 || MainGrid == null)
                return Matrix.Zero;

            IMyShipController cockpit = FindMainCockpit();
            if (cockpit != null)
            {
                MatrixD result = cockpit.WorldMatrix;
                result.Translation = MainGrid.WorldMatrix.Translation;
                return result;
            }
            return MainGrid.WorldMatrix;
        }

        public MatrixD FindBlockWorldMatrix(IMyCubeGrid Grid)
        {
            var MyGrid = Grid as MyCubeGrid;
            IMyTerminalBlock block = MyGrid.MainCockpit;

            // Use the main cockpit if it exists
            if (block != null && block is IMyTerminalBlock && !(block is IMyCryoChamber))
            {
                if ((block as IMyShipController)?.CanControlShip == true)
                {
                    if ((block as IMyShipController).IsUnderControl)
                    {
                        IMyShipController maincockpit = (IMyShipController)block;
                        MatrixD result = maincockpit.WorldMatrix;
                        result.Translation = Grid.WorldMatrix.Translation;
                        return result;
                    }
                }
            }

            HashSet<IMyShipController> controlledgridCockpits;
            if (cockpits.TryGetValue(MyGrid, out controlledgridCockpits))
            {
                foreach (IMyShipController cockpit in controlledgridCockpits)
                {
                    if (cockpit.IsUnderControl)
                    {
                        MatrixD result = cockpit.WorldMatrix;
                        result.Translation = Grid.WorldMatrix.Translation;
                        return result;
                    }
                }
            }

            // No in use cockpit was found.
            HashSet<IMyShipController> gridCockpits;
            if (cockpits.TryGetValue(MyGrid, out gridCockpits))
            {
                MatrixD result = gridCockpits.FirstElement().WorldMatrix;
                result.Translation = Grid.WorldMatrix.Translation;
                return result;
            }

            return MyGrid.WorldMatrix;
        }

        #endregion
        public override bool Equals(object obj)
        {
            return Equals(obj as GridSystem);
        }

        public bool Equals(GridSystem other)
        {
            return other != null && Id == other.Id;
        }

        public override int GetHashCode()
        {
            return 2108858624 + Id.GetHashCode();
        }

        public class BlockCounter
        {
            public int Count { get; private set; }
            public event Action<IMyCubeBlock> OnBlockAdded;
            public event Action<IMyCubeBlock> OnBlockRemoved;
            private readonly Func<IMyCubeBlock, bool> method;

            public BlockCounter(Func<IMyCubeBlock, bool> method)
            {
                this.method = method;
            }

            public void TryAddCount(IMyCubeBlock block)
            {
                if (method.Invoke(block))
                {
                    Count++;
                    OnBlockAdded?.Invoke(block);
                }
            }
            public void TryRemoveCount(IMyCubeBlock block)
            {
                if (method.Invoke(block))
                {
                    Count--;
                    OnBlockRemoved?.Invoke(block);
                }
            }

            public void Dispose()
            {
                OnBlockAdded = null;
                OnBlockRemoved = null;
            }
        }

        private class GridByCount : IComparer<MyCubeGrid>
        {
            public int Compare(MyCubeGrid x, MyCubeGrid y)
            {
                int result1 = y.BlocksCount.CompareTo(x.BlocksCount);
                if (result1 == 0)
                    return x.EntityId.CompareTo(y.EntityId);
                return result1;
            }
        }
    }
}
