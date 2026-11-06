using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace FarmingRevamp;

// Farmland whose furrows are covered while a crop grows on it and open up again once it is gone (harvested, eaten,
// broken), ready to be sown again.
public class BlockFurrowedFarmland : BlockFarmland
{
    public override void OnNeighbourBlockChange(IWorldAccessor world, BlockPos pos, BlockPos neibpos)
    {
        base.OnNeighbourBlockChange(world, pos, neibpos);
        // Opens once the space above is empty, so a dead crop still keeps it covered
        if (world.Side != EnumAppSide.Server || Variant["cover"] != "covered" || world.BlockAccessor.GetBlock(pos.UpCopy()).Id != 0) return;

        Block? open = world.GetBlock(CodeWithVariant("cover", "open"));
        // Exchange keeps the farmland block entity (nutrients, moisture)
        if (open != null) world.BlockAccessor.ExchangeBlock(open.BlockId, pos);
    }
}
