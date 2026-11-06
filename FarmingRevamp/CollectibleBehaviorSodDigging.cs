using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace FarmingRevamp;

// Shovel: right click on soil (with or without grass) or forest floor starts digging it over (BlockEntitySoilDigging);
// holding right click on a segment there digs it, `width` segments square at once.
public class CollectibleBehaviorSodDigging : CollectibleBehavior
{
    public int Width { get; private set; } = 1;

    public CollectibleBehaviorSodDigging(CollectibleObject collObj) : base(collObj) { }

    public override void Initialize(JsonObject properties)
    {
        base.Initialize(properties);
        Width = properties["width"].AsInt(1);
    }

    public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handHandling, ref EnumHandling handling)
    {
        if (blockSel == null) return;
        IWorldAccessor world = byEntity.World;

        if (world.BlockAccessor.GetBlockEntity(blockSel.Position) is BlockEntitySoilDigging be)
        {
            if (be.IsDone) return;
            handHandling = EnumHandHandling.PreventDefault;
            handling = EnumHandling.PreventSubsequent;
            if (world.Side != EnumAppSide.Client) return;
            int[] segments = be.SegmentsAround(blockSel, Width);
            if (segments.Length > 0) BlockEntitySoilDigging.StartHold(be, segments);
            return;
        }

        Block block = world.BlockAccessor.GetBlock(blockSel.Position);
        string? fertility = Fertility(block);
        IPlayer? byPlayer = (byEntity as EntityPlayer)?.Player;
        if (fertility == null || byPlayer == null || !world.Claims.TryAccess(byPlayer, blockSel.Position, EnumBlockAccessFlags.BuildOrBreak)) return;

        // Plants on top (tall grass, flowers) are cut; anything more solid is in the way
        BlockPos up = blockSel.Position.UpCopy();
        Block above = world.BlockAccessor.GetBlock(up);
        if (above.Id != 0 && above.Replaceable < 6000) return;

        handHandling = EnumHandHandling.PreventDefault;
        handling = EnumHandling.PreventSubsequent;
        if (world.Side != EnumAppSide.Server) return;

        Block? digging = world.GetBlock(new AssetLocation("farmingrevamp", "diggingsoil-" + fertility));
        if (digging == null) return;
        if (above.Id != 0) world.BlockAccessor.BreakBlock(up, byPlayer);
        world.BlockAccessor.SetBlock(digging.BlockId, blockSel.Position);
        (world.BlockAccessor.GetBlockEntity(blockSel.Position) as BlockEntitySoilDigging)?.Begin(block);
        world.PlaySoundAt(new AssetLocation("sounds/block/dirt1"), blockSel.Position, 0);
    }

    public override bool OnHeldInteractStep(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, ref EnumHandling handling)
    {
        if (blockSel == null || byEntity.World.BlockAccessor.GetBlockEntity(blockSel.Position) is not BlockEntitySoilDigging be) return false;
        handling = EnumHandling.PreventSubsequent;
        // The server digs on its own clock and stops on the client's stop packet
        return byEntity.World.Side != EnumAppSide.Client || be.IsHeld;
    }

    public override void OnHeldInteractStop(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, ref EnumHandling handling)
    {
        if (byEntity.World.Side == EnumAppSide.Client) BlockEntitySoilDigging.StopHold();
    }

    public override bool OnHeldInteractCancel(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, EnumItemUseCancelReason cancelReason, ref EnumHandling handled)
    {
        if (byEntity.World.Side == EnumAppSide.Client) BlockEntitySoilDigging.StopHold();
        return base.OnHeldInteractCancel(secondsUsed, slot, byEntity, blockSel, entitySel, cancelReason, ref handled);
    }

    // Fertility of the dug soil, or null if the block cannot be dug over: vanilla soil (the tillable block) and forest floor
    internal static string? Fertility(Block block)
    {
        if (block.Code == null || block.Code.Domain != "game") return null;
        if (block.FirstCodePart() == "forestfloor") return "verylow";
        return block.FirstCodePart() == "soil" ? block.Variant["fertility"] : null;
    }
}
