using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace FarmingRevamp;

// Soil being dug over with a shovel, and the dug soil it becomes; selection boxes (per segment) come from its block entity.
public class BlockSoilDigging : Block
{
    public override Cuboidf[] GetSelectionBoxes(IBlockAccessor blockAccessor, BlockPos pos) =>
        (blockAccessor.GetBlockEntity(pos) as BlockEntitySoilDigging)?.SelectionBoxes is { Length: > 0 } boxes ? boxes : base.GetSelectionBoxes(blockAccessor, pos);

    public override bool DoPartialSelection(IWorldAccessor world, BlockPos pos) => true;
}
