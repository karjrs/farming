using System.Linq;

namespace FarmingRevamp;

// Heightmap (16×16 voxel columns, heights in voxels) of a soil block being tilled into three N–S furrows.
public class TillingGrid
{
    public const int Size = 16;
    // Loosened soil sits 1 voxel lower, like vanilla farmland; furrows are 1 voxel deep, heaps rise up to 3 voxels
    public const int Surface = 15, FurrowHeight = 14, MaxHeight = 18;
    // Furrows leave a 2-voxel margin at the north and south ends
    public const int FurrowStartZ = 2, FurrowEndZ = 14;

    // Three furrows (rows), 2 voxels wide
    public static readonly int[][] Furrows = { new[] { 2, 3 }, new[] { 7, 8 }, new[] { 12, 13 } };

    // The heap columns along both sides of each furrow (index = 2 × furrow + side, low x side first), that collapse
    // back into it when covering
    public static readonly int[][] Ridges = Furrows.SelectMany(f => new[] { new[] { f[0] - 1 }, new[] { f[^1] + 1 } }).ToArray();

    public readonly byte[] Heights = Enumerable.Repeat((byte)Surface, Size * Size).ToArray();

    public int this[int x, int z]
    {
        get => Heights[x * Size + z];
        set => Heights[x * Size + z] = (byte)value;
    }

    public static bool IsFurrow(int x, int z) => z >= FurrowStartZ && z < FurrowEndZ && Furrows.Any(f => f.Contains(x));

    public bool IsDug(int row) => Furrows[row].All(x => Enumerable.Range(FurrowStartZ, FurrowEndZ - FurrowStartZ).All(z => this[x, z] <= FurrowHeight));

    public bool IsDone => Enumerable.Range(0, Furrows.Length).All(IsDug);

    // Digs columns `cols` (a furrow anywhere across the block) one voxel deep at z and heaps the soil evenly on both
    // sides (an odd voxel goes to the lower side). Heaps land on whatever is next to them, half filling a neighbouring
    // furrow; soil pushed past the block edge is lost. Returns false if it was already dug there.
    public bool DigStep(int[] cols, int z)
    {
        int removed = 0;
        foreach (int c in cols)
        {
            if (this[c, z] > FurrowHeight) { this[c, z]--; removed++; }
        }
        if (removed == 0) return false;

        int left = cols[0] - 1, right = cols[^1] + 1;
        int toLeft = removed / 2 + (removed % 2 == 1 && Height(left, z) <= Height(right, z) ? 1 : 0);
        Heap(left, -1, z, toLeft);
        Heap(right, 1, z, removed - toLeft);
        return true;
    }

    // Spreads the soil of columns `cols` at z evenly over them, the remainder onto the highest ones (raking).
    // Returns false if they were level already.
    public bool Level(int[] cols, int z)
    {
        int total = cols.Sum(x => this[x, z]);
        int[] byHeight = cols.OrderByDescending(x => this[x, z]).ToArray();
        bool changed = false;
        for (int i = 0; i < byHeight.Length; i++)
        {
            int h = total / cols.Length + (i < total % cols.Length ? 1 : 0);
            changed |= this[byHeight[i], z] != h;
            this[byHeight[i], z] = h;
        }
        return changed;
    }

    public bool IsFilled(int row) => Furrows[row].All(x => Enumerable.Range(FurrowStartZ, FurrowEndZ - FurrowStartZ).All(z => this[x, z] >= Surface));

    public bool IsAllFilled => Enumerable.Range(0, Furrows.Length).All(IsFilled);

    // The grid of a freshly tilled field, matching the furrowed farmland shape.
    public static TillingGrid Tilled()
    {
        var grid = new TillingGrid();
        for (int row = 0; row < Furrows.Length; row++)
        {
            for (int z = FurrowStartZ; z < FurrowEndZ; z++) grid.DigStep(Furrows[row], z);
        }
        return grid;
    }

    // Collapses `ridge` at z into the furrow column next to it, filling that column up to the surface with the ridge's
    // heaped soil; whatever the ridge lacks comes from beyond the block. Returns false if it was already filled there.
    public bool CollapseStep(int ridge, int z)
    {
        int rx = Ridges[ridge][0], fx = ridge % 2 == 0 ? rx + 1 : rx - 1;
        if (this[fx, z] >= Surface) return false;
        for (; this[fx, z] < Surface; this[fx, z]++)
        {
            if (this[rx, z] > Surface) this[rx, z]--;
        }
        return true;
    }

    public bool CanCollapse(int ridge, int z) => this[ridge % 2 == 0 ? Ridges[ridge][0] + 1 : Ridges[ridge][0] - 1, z] < Surface;

    void Heap(int x, int dir, int z, int amount)
    {
        for (; amount > 0 && x >= 0 && x < Size; x += dir)
        {
            if (this[x, z] >= MaxHeight) continue;
            this[x, z]++;
            amount--;
        }
    }

    // Height of column x, or max beyond the block
    int Height(int x, int z) => x >= 0 && x < Size ? this[x, z] : int.MaxValue;

}
