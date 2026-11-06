using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace FarmingRevamp;

// Tool that covers sown seeds: holding right click on a sown field collapses its ridges in a band `width` voxels across
// centred on the cursor (shovel 4: a furrow and both its ridges, rake 9: two furrows); on soil being tilled it levels
// the band instead.
public class CollectibleBehaviorSeedCovering : CollectibleBehavior
{
    public int Width { get; private set; } = 1;

    public CollectibleBehaviorSeedCovering(CollectibleObject collObj) : base(collObj) { }

    public override void Initialize(JsonObject properties)
    {
        base.Initialize(properties);
        Width = properties["width"].AsInt(1);
    }

    public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handHandling, ref EnumHandling handling)
    {
        if (blockSel == null || byEntity.World.BlockAccessor.GetBlockEntity(blockSel.Position) is not BlockEntityFurrowWork field) return;

        handHandling = EnumHandHandling.PreventDefault;
        handling = EnumHandling.PreventSubsequent;
        if (byEntity.World.Side != EnumAppSide.Client) return;
        int[] lines = field.LinesAround(blockSel, Width);
        if (lines.Length > 0) BlockEntityFurrowWork.StartHold(field, lines);
    }

    public override bool OnHeldInteractStep(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, ref EnumHandling handling)
    {
        if (blockSel == null || byEntity.World.BlockAccessor.GetBlockEntity(blockSel.Position) is not BlockEntityFurrowWork field) return false;

        handling = EnumHandling.PreventSubsequent;
        // The server works on its own clock and stops on the client's stop packet
        return byEntity.World.Side != EnumAppSide.Client || field.IsHeld;
    }

    public override void OnHeldInteractStop(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, ref EnumHandling handling)
    {
        if (byEntity.World.Side == EnumAppSide.Client) BlockEntityFurrowWork.StopHold();
    }

    public override bool OnHeldInteractCancel(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, EnumItemUseCancelReason cancelReason, ref EnumHandling handled)
    {
        if (byEntity.World.Side == EnumAppSide.Client) BlockEntityFurrowWork.StopHold();
        return base.OnHeldInteractCancel(secondsUsed, slot, byEntity, blockSel, entitySel, cancelReason, ref handled);
    }
}
