using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Fixed-worker maze search that traverses the sampled grid directly. Unlike LabyrinthPoolSearch,
/// it does not create a task for each fork or allocate an adjacency array for each grid node.
/// </summary>
public sealed class LabyrinthFastSearch : LabyrinthSearchBase
{
    [Header("Workers")]
    [SerializeField, Tooltip("-1 = default (ProcessorCount - 2)")]
    int threadCount = -1;

    // Display name for the stats window (set by each subclass per the base class mechanism).
    protected override string DisplayName => "FastSearch";

    bool[] _passable;
    int[] _predecessors;
    int _startNode;
    int _goalNode;

    readonly ConcurrentQueue<int> _frontier = new ConcurrentQueue<int>();
    Thread[] _workers;
    int _pendingBranches;
    int _finishedWorkers;
    int _solved;
    int _cancelled;
    bool _handled;
    long _timerStart;
    long _searchEnd;

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
        _frontier.Enqueue(_startNode);
        _pendingBranches = 1;

        int workerCount = threadCount > 0 ? threadCount : Math.Max(1, Environment.ProcessorCount - 2);
        _workers = new Thread[workerCount];
        for (int i = 0; i < _workers.Length; i++)
        {
            _workers[i] = new Thread(SearchWorker)
            {
                IsBackground = true,
                Name = "LabyrinthFastSearch #" + i
            };
        }
        
        _timerStart = Stopwatch.GetTimestamp();
        MarkSolving(); // stats window: searching has started
        
        for (int i = 0; i < _workers.Length; i++)
        {
            _workers[i].Start();
        }
    }

    void Update()
    {
        if (_handled || _workers == null || Volatile.Read(ref _searchEnd) == 0)
            return;

        _handled = true;
        Finish(Volatile.Read(ref _solved) != 0);
    }

    protected override void OnDestroy()
    {
        Interlocked.Exchange(ref _cancelled, 1);
        if (_workers != null)
        {
            for (int i = 0; i < _workers.Length; i++)
                _workers[i].Join();
        }

        DisposeRendering();
        base.OnDestroy();
    }

    void SearchWorker()
    {
        var spinWait = new SpinWait();
        try
        {
            while (Volatile.Read(ref _solved) == 0 && Volatile.Read(ref _cancelled) == 0)
            {
                if (_frontier.TryDequeue(out int node))
                {
                    spinWait.Reset();
                    try
                    {
                        if (ExploreBranch(node))
                            Interlocked.Exchange(ref _solved, 1);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _pendingBranches);
                    }

                    continue;
                }

                if (Volatile.Read(ref _pendingBranches) == 0)
                    break;

                spinWait.SpinOnce();
            }
        }
        finally
        {
            if (Interlocked.Increment(ref _finishedWorkers) == _workers.Length)
                Volatile.Write(ref _searchEnd, Stopwatch.GetTimestamp());
        }
    }

    bool ExploreBranch(int node)
    {
        while (Volatile.Read(ref _solved) == 0 && Volatile.Read(ref _cancelled) == 0)
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

            QueueBranch(second);
            if (claimedCount > 2)
                QueueBranch(third);
            if (claimedCount > 3)
                QueueBranch(fourth);

            node = first;
        }

        return false;
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
        if (_passable[node] == false || Interlocked.CompareExchange(ref _predecessors[node], parent, Unvisited) != Unvisited)
            return -1;

        return node;
    }

    void QueueBranch(int node)
    {
        Interlocked.Increment(ref _pendingBranches);
        _frontier.Enqueue(node);
    }

    void Finish(bool hasSolution)
    {
        long elapsedTicks = Volatile.Read(ref _searchEnd) - _timerStart;
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

            Debug.Log($"LabyrinthFastSearch solved in {winningPath.Count - 1} steps in {elapsedNs} ns ({elapsedNs / 1_000_000.0:F3} ms).");
        }
        else
        {
            Debug.Log($"LabyrinthFastSearch found no path to the goal in {elapsedNs} ns ({elapsedNs / 1_000_000.0:F3} ms).");
        }

        RecordCompletion(hasSolution, elapsedNs / 1_000_000.0); // stats window: store result once
        ApplyPaint();
    }

    void PaintNode(int node, Color color)
    {
        int y = node / gridWidth;
        PaintGridCell(node - y * gridWidth, y, color);
    }

}
