public sealed class Graph
{
    public readonly int[] neighborOffsets;
    public readonly int[] neighbors;

    public Graph(int[] neighborOffsets, int[] neighbors)
    {
        this.neighborOffsets = neighborOffsets;
        this.neighbors = neighbors;
    }

    public int nodeCount => neighborOffsets.Length - 1;
}
