using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using Svelto.Tasks;
using Svelto.Tasks.ExtraLean;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Open pool maze solver. Each branch of the search is scheduled as its own root task across an
/// ExtraLean MultiThreadRunnerPool. Shared visited state is an int[] of predecessors so that a node
/// is globally claimed at most once and the winning path can be reconstructed. Nothing is painted
/// while searching: when the search ends the whole explored area is painted once, then the winning
/// path is painted over it.
/// This is not the most efficent way to solve a maze, but it is a good demonstration of how to use Svelto.Tasks
/// to implement a parallel algorithm through short lived tasks.
/// </summary>
public sealed class LabyrinthPoolSearch : LabyrinthSearchBase
{
    [Header("Pool")]
    [SerializeField, Tooltip("-1 = default (ProcessorCount - 2)")]
    int threadCount = -1;

    // Display name for the stats window (set by each subclass per the base class mechanism).
    protected override string DisplayName => "PoolSearch";

    Graph _graph;
    bool[] _passableGrid;
    int[] _gridToNode;
    int[] _nodeToGridX;
    int[] _nodeToGridY;

    int _startNode;
    int _goalNode;

    MultiThreadRunnerPool _pool;
    IteratorBlockPool<SearchBranchData> _searchBranchPool;
    int[] _predecessors;

    const int Unvisited = -2;
    const int NoParent = -1;

    int _solved;
    int _activeBranches;
    bool _handled;
    long _timerStart;
    long _searchEnd;

    void Start()
    {
        if (!TryInitializeTexture() || !BuildGraphFromTexture())
        {
            enabled = false;
            return;
        }

        if (!TryPickDeterministicStartGoal(_passableGrid, out int startGridNode, out int goalGridNode))
        {
            Debug.LogError("Unable to determine deterministic start/goal in labyrinth.");
            enabled = false;
            return;
        }

        _startNode = _gridToNode[startGridNode];
        _goalNode = _gridToNode[goalGridNode];

        _predecessors = new int[_graph.nodeCount];
        for (int i = 0; i < _predecessors.Length; i++)
            _predecessors[i] = Unvisited;

        _predecessors[_startNode] = NoParent;

        int workers = threadCount > 0 ? threadCount : Math.Max(1, Environment.ProcessorCount - 2);
        _pool = new MultiThreadRunnerPool("LabyrinthPoolSearch", workers);
        _searchBranchPool = new IteratorBlockPool<SearchBranchData>(SearchBranch, "LabyrinthSearchBranch");

        _timerStart = Stopwatch.GetTimestamp();
        MarkSolving(); // stats window: searching has started
        ScheduleBranch(_startNode);
    }

    void Update()
    {
        if (_handled)
            return;

        if (Volatile.Read(ref _solved) != 0)
        {
            if (Volatile.Read(ref _activeBranches) != 0)
                return;

            _handled = true;
            Finish(hasSolution: true);
            return;
        }

        // natural exhaustion without a solution
        if (Volatile.Read(ref _solved) == 0 && Volatile.Read(ref _activeBranches) == 0)
        {
            _handled = true;
            FinishAndReportNoPath();
        }
    }

    protected override void OnDestroy()
    {
        Interlocked.Exchange(ref _solved, 1);

        var spinWait = new SpinWait();
        while (Volatile.Read(ref _activeBranches) != 0)
            spinWait.SpinOnce();

        _pool?.Dispose();
        _searchBranchPool?.Dispose();
        DisposeRendering();
        base.OnDestroy();
    }

    IEnumerator SearchBranch(SearchBranchData data)
    {
        while (true)
        {
            int node = data.node;
            int[] neighborOffsets = _graph.neighborOffsets;
            int[] neighbors = _graph.neighbors;
            try
            {
                while (Volatile.Read(ref _solved) == 0)
                {
                    if (node == _goalNode)
                    {
                        StopSearch();
                        break;
                    }

                    int claimedFirst = -1;
                    int claimedCount = 0;

                    int firstNeighbor = neighborOffsets[node];
                    int endNeighbor = neighborOffsets[node + 1];
                    for (int i = firstNeighbor; i < endNeighbor; i++)
                    {
                        int to = neighbors[i];
                        if (Interlocked.CompareExchange(ref _predecessors[to], node, Unvisited) == Unvisited)
                        {
                            if (claimedCount == 0)
                                claimedFirst = to;
                            claimedCount++;
                        }
                    }

                    if (claimedCount == 0)
                        break; // dead end, nothing left to explore

                    if (claimedCount == 1)
                    {
                        node = claimedFirst;
                        continue;
                    }

                    // Keep one child local; only alternatives need a pool handoff.
                    for (int i = firstNeighbor; i < endNeighbor; i++)
                    {
                        int to = neighbors[i];
                        if (to != claimedFirst && _predecessors[to] == node)
                        {
                            if (Volatile.Read(ref _solved) == 0)
                                ScheduleBranch(to);
                        }
                    }

                    node = claimedFirst;
                }
            }
            finally
            {
                if (Interlocked.Decrement(ref _activeBranches) == 0)
                    Volatile.Write(ref _searchEnd, Stopwatch.GetTimestamp());
            }

            yield return TaskContract.Break.It;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void ScheduleBranch(int node)
    {
        Interlocked.Increment(ref _activeBranches);
        var (data, branch) = _searchBranchPool.Get();
        data.node = node;
        branch.RunOn(_pool);
    }

    void Finish(bool hasSolution)
    {
        long searchEnd = Volatile.Read(ref _searchEnd);
        long elapsedTicks = searchEnd - _timerStart;
        long elapsedNs = (long)(elapsedTicks * (1_000_000_000.0 / Stopwatch.Frequency));
        
        _pool.Dispose();
       
        var winningPath = hasSolution ? BuildWinningPath(_predecessors, _goalNode, Unvisited) : null;
        // paint every explored node once
        for (int i = 0; i < _predecessors.Length; i++)
        {
            if (_predecessors[i] != Unvisited)
                PaintNode(i, exploredColor);
        }

        // paint the winning path over it
        if (winningPath != null)
        {
            for (int i = 0; i < winningPath.Count; i++)
                PaintNode(winningPath[i], winningColor);

            Debug.Log($"LabyrinthPoolSearch solved in {winningPath.Count - 1} steps in {elapsedNs} ns ({elapsedNs / 1_000_000.0:F3} ms).");
        }
        else
        {
            Debug.Log($"LabyrinthPoolSearch found no path to the goal in {elapsedNs} ns ({elapsedNs / 1_000_000.0:F3} ms).");
        }

        RecordCompletion(hasSolution, elapsedNs / 1_000_000.0); // stats window: store result once
        ApplyPaint();
    }

    bool BuildGraphFromTexture()
    {
        if (!TryBuildPassableGrid(out _passableGrid, out _))
            return false;

        _gridToNode = new int[_passableGrid.Length];
        for (int i = 0; i < _gridToNode.Length; i++)
            _gridToNode[i] = -1;

        var nodeXs = new List<int>(_passableGrid.Length / 2);
        var nodeYs = new List<int>(_passableGrid.Length / 2);

        for (int gy = 0; gy < gridHeight; gy++)
        {
            for (int gx = 0; gx < gridWidth; gx++)
            {
                int gi = gy * gridWidth + gx;
                if (!_passableGrid[gi])
                    continue;

                int ni = nodeXs.Count;
                _gridToNode[gi] = ni;
                nodeXs.Add(gx);
                nodeYs.Add(gy);
            }
        }

        if (nodeXs.Count < 2)
            return false;

        _nodeToGridX = nodeXs.ToArray();
        _nodeToGridY = nodeYs.ToArray();

        int nodeCount = nodeXs.Count;
        int[] neighborOffsets = new int[nodeCount + 1];
        for (int n = 0; n < nodeCount; n++)
        {
            int gx = _nodeToGridX[n];
            int gy = _nodeToGridY[n];
            neighborOffsets[n + 1] = neighborOffsets[n] + CountNeighbours(gx, gy, _gridToNode);
        }

        int[] neighbors = new int[neighborOffsets[nodeCount]];
        for (int n = 0; n < nodeCount; n++)
        {
            int gx = _nodeToGridX[n];
            int gy = _nodeToGridY[n];
            int nextNeighbor = neighborOffsets[n];
            TryAddNeighbour(gx + 1, gy, _gridToNode, neighbors, ref nextNeighbor);
            TryAddNeighbour(gx - 1, gy, _gridToNode, neighbors, ref nextNeighbor);
            TryAddNeighbour(gx, gy + 1, _gridToNode, neighbors, ref nextNeighbor);
            TryAddNeighbour(gx, gy - 1, _gridToNode, neighbors, ref nextNeighbor);
        }

        _graph = new Graph(neighborOffsets, neighbors);
        return true;
    }

    int CountNeighbours(int gx, int gy, int[] gridToNode)
    {
        int count = 0;
        if (gx + 1 < gridWidth && gridToNode[gy * gridWidth + gx + 1] >= 0)
            count++;
        if (gx > 0 && gridToNode[gy * gridWidth + gx - 1] >= 0)
            count++;
        if (gy + 1 < gridHeight && gridToNode[(gy + 1) * gridWidth + gx] >= 0)
            count++;
        if (gy > 0 && gridToNode[(gy - 1) * gridWidth + gx] >= 0)
            count++;

        return count;
    }

    void TryAddNeighbour(int gx, int gy, int[] gridToNode, int[] neighbors, ref int nextNeighbor)
    {
        if (gx < 0 || gy < 0 || gx >= gridWidth || gy >= gridHeight)
            return;

        int node = gridToNode[gy * gridWidth + gx];
        if (node >= 0)
            neighbors[nextNeighbor++] = node;
    }

    void PaintNode(int node, Color color)
    {
        PaintGridCell(_nodeToGridX[node], _nodeToGridY[node], color);
    }

    sealed class SearchBranchData
    {
        public int node;
    }
    
    void StopSearch()
    {
        Volatile.Write(ref _solved, 1);
    }

    void FinishAndReportNoPath()
    {
        Finish(hasSolution: false);
    }
}
