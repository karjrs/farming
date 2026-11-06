using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace FarmingRevamp;

// Soil being tilled with a hoe: a furrow (2 voxels wide) can be dug anywhere across the block, by holding right click
// (voxel by voxel from its far end towards the player: the hoe is pulled in; on a furrow to dig it snaps to it) or in
// paint mode each voxel swept over.
// The three furrows to dig are outlined in orange, everything else that could be dug in grey. A rake (any seed covering
// tool) levels the soil in its band instead. Once the three furrows are dug it becomes open (furrowed) farmland.
public class BlockEntityTillingSoil : BlockEntityFurrowWork
{
    // Line i is the furrow over columns i and i + 1
    static readonly int[][] FreeFurrows = Enumerable.Range(0, TillingGrid.Size - 1).Select(x => new[] { x, x + 1 }).ToArray();

    protected override Block? ColumnBlock => Api.World.GetBlock(new AssetLocation("soil-" + Block.Variant["fertility"] + "-none"));

    // One box per column across, so the hit position tells where to dig
    protected override Cuboidf[] BuildSelectionBoxes() => Enumerable.Range(0, TillingGrid.Size).Select(x => Box(x, x + 1, 0, TillingGrid.Size)).ToArray();

    protected override int[][] Lines => FreeFurrows;

    // The furrow centred on the voxel edge nearest to x
    protected override int LineAt(double x) => Math.Clamp((int)Math.Round(x) - 1, 0, FreeFurrows.Length - 1);

    // Holding on a furrow to dig snaps to it
    protected override int HoldLineAt(double x) =>
        TillingGrid.Furrows.FirstOrDefault(f => f.Contains((int)x)) is int[] furrow ? furrow[0] : LineAt(x);

    protected override IEnumerable<int[]> OutlineBoxes() => DiggableTops(true);

    protected override IEnumerable<int[]> OptionalOutlineBoxes() => DiggableTops(false);

    // The top voxel of each column that can still be dug, in the furrows to dig or elsewhere
    IEnumerable<int[]> DiggableTops(bool furrows) =>
        from x in Enumerable.Range(0, TillingGrid.Size) from z in Rows
        where Grid[x, z] > TillingGrid.FurrowHeight && TillingGrid.IsFurrow(x, z) == furrows
        select new[] { x, Grid[x, z] - 1, z, x + 1, Grid[x, z], z + 1 };

    protected override bool CanWork(int line, int z) => FreeFurrows[line].Any(x => Grid[x, z] > TillingGrid.FurrowHeight);

    protected override bool HoeStep(int line, int z) => Grid.DigStep(FreeFurrows[line], z);

    protected override int[] HoldStep(CollectibleObject tool, int[] lines, int z)
    {
        if (tool.GetBehavior<CollectibleBehaviorSeedCovering>() == null) return base.HoldStep(tool, lines, z);
        return Grid.Level(lines.SelectMany(line => FreeFurrows[line]).Distinct().ToArray(), z) ? lines : Array.Empty<int>();
    }

    protected override bool IsComplete => Grid.IsDone;

    protected override void OnComplete(IPlayer player)
    {
        Block? farmland = Api.World.GetBlock(new AssetLocation("farmingrevamp", $"farmland-dry-{Block.Variant["fertility"]}-{Block.Variant["side"]}-open"));
        Block? soil = Api.World.GetBlock(new AssetLocation("soil-" + Block.Variant["fertility"] + "-none"));
        if (farmland == null || soil == null) return;

        Api.World.BlockAccessor.SetBlock(farmland.BlockId, Pos);
        (Api.World.BlockAccessor.GetBlockEntity(Pos) as BlockEntityFarmland)?.OnCreatedFromSoil(soil);
    }
}
