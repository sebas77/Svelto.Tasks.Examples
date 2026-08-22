using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Main-thread maze search that traverses the sampled grid directly. It is the synchronous baseline
/// for comparing the same direct-grid branch traversal used by LabyrinthFastSearch.
/// </summary>
public sealed class LabyrinthSingleThreadSearch : LabyrinthSearchBase
{
    protected override string DisplayName => "SingleThreadSearch";

    bool[] _passable;
    int[] _predecessors;
    int _startNode;
    int _goalNode;

    const int Unvisited = -2;
    const int NoParent = -1;

    void Start()
    {
        if (!TryInitializeTexture() || !TryBuildPassableGrid(out _passable, out _) || !TryPickDeterministicStartGoal(_passable, out _startNode, out _goalNode))
        {
            Debug.LogError("Unable to initialize a searchable labyrinth.");
            enabled = false;
            return;
        }

        _predecessors = new int[_passable.Length];
        for (int i = 0; i < _predecessors.Length; i++)
            _predecessors[i] = Unvisited;

        _predecessors[_startNode] = NoParent;

        long timerStart = Stopwatch.GetTimestamp();
        MarkSolving();
        bool hasSolution = Search();
        long searchEnd = Stopwatch.GetTimestamp();

        Finish(hasSolution, searchEnd - timerStart);
    }

    protected override void OnDestroy()
    {
        DisposeRendering();
        base.OnDestroy();
    }

    bool Search()
    {
        var frontier = new Queue<int>();
        frontier.Enqueue(_startNode);

        while (frontier.Count > 0)
        {
            int node = frontier.Dequeue();
            if (ExploreBranch(node, frontier))
                return true;
        }

        return false;
    }

    bool ExploreBranch(int node, Queue<int> frontier)
    {
        while (true)
        {
            if (node == _goalNode)
                return true;

            int y = node / gridWidth;
            int x = node - y * gridWidth;
            int claimedCount = 0;
            int first = -1;
            int second = -1;
            int third = -1;
            int fourth = -1;

            if (TryAddClaim(node, x + 1, y, ref claimedCount, ref first, ref second, ref third, ref fourth)
             || TryAddClaim(node, x - 1, y, ref claimedCount, ref first, ref second, ref third, ref fourth)
             || TryAddClaim(node, x, y + 1, ref claimedCount, ref first, ref second, ref third, ref fourth)
             || TryAddClaim(node, x, y - 1, ref claimedCount, ref first, ref second, ref third, ref fourth))
                return true;

            if (claimedCount == 0)
                return false;

            if (claimedCount == 1)
            {
                node = first;
                continue;
            }

            frontier.Enqueue(second);
            if (claimedCount > 2)
                frontier.Enqueue(third);
            if (claimedCount > 3)
                frontier.Enqueue(fourth);

            node = first;
        }
    }

    bool TryAddClaim(int parent, int x, int y, ref int claimedCount, ref int first, ref int second, ref int third, ref int fourth)
    {
        int node = TryClaimNeighbour(parent, x, y);
        if (node < 0)
            return false;

        if (node == _goalNode)
            return true;

        switch (claimedCount++)
        {
            case 0: first = node; break;
            case 1: second = node; break;
            case 2: third = node; break;
            default: fourth = node; break;
        }

        return false;
    }

    int TryClaimNeighbour(int parent, int x, int y)
    {
        if (x < 0 || y < 0 || x >= gridWidth || y >= gridHeight)
            return -1;

        int node = y * gridWidth + x;
        if (_passable[node] == false || _predecessors[node] != Unvisited)
            return -1;

        _predecessors[node] = parent;
        return node;
    }

    void Finish(bool hasSolution, long elapsedTicks)
    {
        long elapsedNs = (long)(elapsedTicks * (1_000_000_000.0 / Stopwatch.Frequency));
        var winningPath = hasSolution ? BuildWinningPath(_predecessors, _goalNode, Unvisited) : null;

        for (int i = 0; i < _predecessors.Length; i++)
        {
            if (_predecessors[i] != Unvisited)
                PaintNode(i, exploredColor);
        }

        if (winningPath != null)
        {
            for (int i = 0; i < winningPath.Count; i++)
                PaintNode(winningPath[i], winningColor);

            Debug.Log($"LabyrinthSingleThreadSearch solved in {winningPath.Count - 1} steps in {elapsedNs} ns ({elapsedNs / 1_000_000.0:F3} ms).");
        }
        else
        {
            Debug.Log($"LabyrinthSingleThreadSearch found no path to the goal in {elapsedNs} ns ({elapsedNs / 1_000_000.0:F3} ms).");
        }

        RecordCompletion(hasSolution, elapsedNs / 1_000_000.0);
        ApplyPaint();
    }

    void PaintNode(int node, Color color)
    {
        int y = node / gridWidth;
        PaintGridCell(node - y * gridWidth, y, color);
    }
}
