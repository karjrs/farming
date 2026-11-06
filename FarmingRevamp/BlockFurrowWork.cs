using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace FarmingRevamp;

// Field block worked with a hoe, shovel, seeds or bare hand (tilling, sowing); selection boxes come from its block entity.
public class BlockFurrowWork : Block
{
    // Bare hand: each click works the voxel under the cursor, where the field allows that
    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        if (!byPlayer.InventoryManager.ActiveHotbarSlot.Empty || world.BlockAccessor.GetBlockEntity(blockSel.Position) is not BlockEntityFurrowWork be || be.WorkWidth(null) == 0)
        {
            return base.OnBlockInteractStart(world, byPlayer, blockSel);
        }
        if (world.Side == EnumAppSide.Client && be.PaintVoxel(blockSel) is var (row, z)) be.SendPaint(row, z);
        return true;
    }

    public override Cuboidf[] GetSelectionBoxes(IBlockAccessor blockAccessor, BlockPos pos)
    {
        return (blockAccessor.GetBlockEntity(pos) as BlockEntityFurrowWork)?.SelectionBoxes ?? base.GetSelectionBoxes(blockAccessor, pos);
    }

    public override bool DoPartialSelection(IWorldAccessor world, BlockPos pos) => true;
}
