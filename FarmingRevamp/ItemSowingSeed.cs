using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

namespace FarmingRevamp;

// Seeds: right click on empty farmland uses one seed and starts sowing (BlockEntitySowingFarmland); right clicks on
// it then place the seed in its spots. Bell peppers are bushes and keep the vanilla planting.
public class ItemSowingSeed : ItemPlantableSeed
{
    public override void OnHeldInteractStart(ItemSlot itemslot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handHandling)
    {
        IBlockAccessor ba = byEntity.World.BlockAccessor;
        BlockEntity? be = blockSel == null ? null : ba.GetBlockEntity(blockSel.Position);

        if (be is BlockEntitySowingFarmland sowing)
        {
            handHandling = EnumHandHandling.PreventDefault;
            if (byEntity.World.Side == EnumAppSide.Client && sowing.CanSeed(blockSel!.SelectionBoxIndex)) sowing.SendPlaceSeed(blockSel.SelectionBoxIndex);
            return;
        }

        string type = Variant["type"];
        Block? crop = byEntity.World.GetBlock(CodeWithPath("crop-" + type + "-1"));
        if (be is not BlockEntityFarmland || type == "bellpepper" || crop == null || ba.GetBlock(blockSel!.Position.UpCopy()).Id != 0)
        {
            base.OnHeldInteractStart(itemslot, byEntity, blockSel!, entitySel, firstEvent, ref handHandling);
            return;
        }

        handHandling = EnumHandHandling.PreventDefault;
        IPlayer? byPlayer = (byEntity as EntityPlayer)?.Player;
        if (byEntity.World.Side != EnumAppSide.Server || byPlayer == null || !byEntity.World.Claims.TryAccess(byPlayer, blockSel.Position, EnumBlockAccessFlags.Use)) return;

        Block farmland = ba.GetBlock(blockSel.Position);
        // Vanilla farmland has no side variant; its furrows run north–south
        Block? sowingBlock = byEntity.World.GetBlock(new AssetLocation("farmingrevamp",
            $"sowingfarmland-{farmland.Variant["state"]}-{farmland.Variant["fertility"]}-{farmland.Variant["side"] ?? "ns"}"));
        if (sowingBlock == null) return;

        var farmlandData = new TreeAttribute();
        be.ToTreeAttributes(farmlandData);
        ba.SetBlock(sowingBlock.BlockId, blockSel.Position);
        (ba.GetBlockEntity(blockSel.Position) as BlockEntitySowingFarmland)?.Setup(Code, farmlandData);
        byEntity.World.PlaySoundAt(new AssetLocation("sounds/block/plant"), blockSel.Position, 0.4375);

        if (byPlayer.WorldData.CurrentGameMode == EnumGameMode.Creative) return;
        itemslot.TakeOut(1);
        itemslot.MarkDirty();
    }
}
