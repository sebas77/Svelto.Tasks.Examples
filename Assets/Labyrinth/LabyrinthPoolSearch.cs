using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Svelto.Tasks.ExtraLean;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Open pool maze solver. Each branch of the search is scheduled as its own root task across an
/// ExtraLean MultiThreadRunnerPool. Shared visited state is an int[] of predecessors so that a node
/// is globally claimed at most once and the winning path can be reconstructed. Nothing is painted
/// while searching: when the search ends the whole explored area is painted once, then the winning
/// path is painted over it.
/// </summary>
public sealed class LabyrinthPoolSearch : MonoBehaviour
{
    [Header("References")]
    [SerializeField] string labyrinthResourcePath = "Labyrinths/Labyrinth_1024";
    [SerializeField] Renderer labyrinthRenderer;

    [Header("Pool")]
    [SerializeField, Tooltip("-1 = default (ProcessorCount - 2)")]
    int threadCount = -1;

    [Header("Grid")]
    [SerializeField, Range(2, 16)] int sampleStride = 4;
    [SerializeField, Range(0f, 1f)] float wallThreshold = 0.5f;

    [Header("Painting")]
    [SerializeField, Range(1, 8)] int paintRadiusPixels = 2;
    [SerializeField] Color exploredColor = new Color32(60, 120, 255, 255);
    [SerializeField] Color winningColor = new Color32(255, 60, 60, 255);

    Texture2D _workingTexture;
    Material _originalMaterial;
    Material _runtimeMaterial;
    Color32[] _workingPixels;
    int _pixelWidth;
    int _pixelHeight;

    Graph _graph;
    int _gridWidth;
    int _gridHeight;
    int[] _nodeToGridX;
    int[] _nodeToGridY;
    List<int> _availableNodes;

    int _startNode;
    int _goalNode;

    MultiThreadRunnerPool _pool;
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

        if (!PickDeterministicStartGoal(out _startNode, out _goalNode))
        {
            Debug.LogError("Unable to determine deterministic start/goal in labyrinth.");
            enabled = false;
            return;
        }

        _predecessors = new int[_graph.adjacency.Length];
        for (int i = 0; i < _predecessors.Length; i++)
            _predecessors[i] = Unvisited;

        _predecessors[_startNode] = NoParent;

        int workers = threadCount > 0 ? threadCount : Math.Max(1, Environment.ProcessorCount - 2);
        _pool = new MultiThreadRunnerPool("LabyrinthPoolSearch", workers);

        _timerStart = Stopwatch.GetTimestamp();
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

    void OnDestroy()
    {
        Interlocked.Exchange(ref _solved, 1);
        _pool?.Stop();

        var spinWait = new SpinWait();
        while (Volatile.Read(ref _activeBranches) != 0)
            spinWait.SpinOnce();

        _pool?.Dispose();

        if (labyrinthRenderer != null && labyrinthRenderer.sharedMaterial == _runtimeMaterial)
            labyrinthRenderer.sharedMaterial = _originalMaterial;
        if (_runtimeMaterial != null)
            Destroy(_runtimeMaterial);
        if (_workingTexture != null)
            Destroy(_workingTexture);
    }

    IEnumerator SearchBranch(int node)
    {
        try
        {
            while (Volatile.Read(ref _solved) == 0)
            {
                if (node == _goalNode)
                {
                    StopSearch();
                    yield break;
                }

                int claimedFirst = -1;
                int claimedCount = 0;

                GraphEdge[] edges = _graph.adjacency[node];
                for (int i = 0; i < edges.Length; i++)
                {
                    int to = edges[i].to;
                    if (Interlocked.CompareExchange(ref _predecessors[to], node, Unvisited) == Unvisited)
                    {
                        if (claimedCount == 0)
                            claimedFirst = to;
                        claimedCount++;
                    }
                }

                if (claimedCount == 0)
                    yield break; // dead end, nothing left to explore

                if (claimedCount == 1)
                {
                    node = claimedFirst; // keep walking through the single claimed neighbour
                    yield return null;
                    continue;
                }

                // true fork: schedule one child for every claimed neighbour, parent has no responsibility left
                for (int i = 0; i < edges.Length; i++)
                {
                    int to = edges[i].to;
                    if (_predecessors[to] == node)
                        ScheduleBranch(to);
                }

                yield break;
            }
        }
        finally
        {
            if (Interlocked.Decrement(ref _activeBranches) == 0)
                Volatile.Write(ref _searchEnd, Stopwatch.GetTimestamp());
        }
    }

    void ScheduleBranch(int node)
    {
        if (Volatile.Read(ref _solved) != 0)
            return;

        Interlocked.Increment(ref _activeBranches);
        try
        {
            SearchBranch(node).RunOn(_pool);
        }
        catch
        {
            if (Interlocked.Decrement(ref _activeBranches) == 0)
                Volatile.Write(ref _searchEnd, Stopwatch.GetTimestamp());
            throw;
        }
    }

    void StopSearch()
    {
        _pool.Stop();
        Volatile.Write(ref _solved, 1);
    }

    void Finish(bool hasSolution)
    {
        long disposeStart = Stopwatch.GetTimestamp();
        _pool.Dispose();
        long disposeEnd = Stopwatch.GetTimestamp();

        long searchEnd = Volatile.Read(ref _searchEnd);
        if (searchEnd == 0)
            searchEnd = disposeStart;

        long elapsedTicks = searchEnd - _timerStart + disposeEnd - disposeStart;
        long elapsedNs = (long)(elapsedTicks * (1_000_000_000.0 / Stopwatch.Frequency));

        var winningPath = hasSolution ? ReconstructWinningPath() : null;

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

        ApplyPaint();
    }

    void FinishAndReportNoPath()
    {
        Finish(hasSolution: false);
    }

    List<int> ReconstructWinningPath()
    {
        var path = new List<int>();
        int cur = _goalNode;

        while (cur >= 0 && cur != Unvisited)
        {
            path.Add(cur);
            if (path.Count > _predecessors.Length)
                break;
            cur = _predecessors[cur];
        }

        path.Reverse();
        return path;
    }

    // ============================================================
    //  graph / texture helpers (copied from Labyrinth.cs, unmodified)
    // ============================================================

    bool TryInitializeTexture()
    {
        if (labyrinthRenderer == null)
        {
            GameObject go = GameObject.Find("LabyrinthDisplay");
            if (go != null)
                labyrinthRenderer = go.GetComponent<Renderer>();
        }

        Texture2D sourceTexture = null;
        if (labyrinthRenderer != null && labyrinthRenderer.sharedMaterial != null)
            sourceTexture = labyrinthRenderer.sharedMaterial.mainTexture as Texture2D;

        if (sourceTexture == null)
            sourceTexture = Resources.Load<Texture2D>(labyrinthResourcePath);

        if (sourceTexture == null)
            return false;

        Texture2D readable = ExtractReadableTexture(sourceTexture);
        _pixelWidth = readable.width;
        _pixelHeight = readable.height;

        _workingTexture = new Texture2D(_pixelWidth, _pixelHeight, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Point
        };

        _workingPixels = readable.GetPixels32();
        Destroy(readable);
        _workingTexture.SetPixels32(_workingPixels);
        _workingTexture.Apply(false, false);

        if (labyrinthRenderer != null && labyrinthRenderer.sharedMaterial != null)
        {
            _originalMaterial = labyrinthRenderer.sharedMaterial;
            _runtimeMaterial = new Material(_originalMaterial);
            _runtimeMaterial.mainTexture = _workingTexture;
            labyrinthRenderer.sharedMaterial = _runtimeMaterial;
        }

        return true;
    }

    Texture2D ExtractReadableTexture(Texture2D source)
    {
        RenderTexture rt = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32);
        Graphics.Blit(source, rt);

        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = rt;

        Texture2D readable = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
        readable.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
        readable.Apply(false, false);

        RenderTexture.active = previous;
        RenderTexture.ReleaseTemporary(rt);
        return readable;
    }

    bool BuildGraphFromTexture()
    {
        _gridWidth = Mathf.Max(2, _pixelWidth / Mathf.Max(1, sampleStride));
        _gridHeight = Mathf.Max(2, _pixelHeight / Mathf.Max(1, sampleStride));

        bool[] passableGrid = new bool[_gridWidth * _gridHeight];
        for (int gy = 0; gy < _gridHeight; gy++)
        {
            for (int gx = 0; gx < _gridWidth; gx++)
            {
                int sx = Mathf.Clamp(gx * sampleStride + sampleStride / 2, 0, _pixelWidth - 1);
                int sy = Mathf.Clamp(gy * sampleStride + sampleStride / 2, 0, _pixelHeight - 1);

                Color32 c = _workingPixels[sy * _pixelWidth + sx];
                float luma = (0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b) / 255f;
                passableGrid[gy * _gridWidth + gx] = luma >= wallThreshold;
            }
        }

        int[] gridToNode = new int[passableGrid.Length];
        for (int i = 0; i < gridToNode.Length; i++)
            gridToNode[i] = -1;

        var nodeXs = new List<int>(passableGrid.Length / 2);
        var nodeYs = new List<int>(passableGrid.Length / 2);
        _availableNodes = new List<int>(passableGrid.Length / 2);

        for (int gy = 0; gy < _gridHeight; gy++)
        {
            for (int gx = 0; gx < _gridWidth; gx++)
            {
                int gi = gy * _gridWidth + gx;
                if (!passableGrid[gi])
                    continue;

                int ni = nodeXs.Count;
                gridToNode[gi] = ni;
                nodeXs.Add(gx);
                nodeYs.Add(gy);
                _availableNodes.Add(ni);
            }
        }

        if (_availableNodes.Count < 2)
            return false;

        _nodeToGridX = nodeXs.ToArray();
        _nodeToGridY = nodeYs.ToArray();

        GraphEdge[][] adjacency = new GraphEdge[_availableNodes.Count][];
        for (int n = 0; n < adjacency.Length; n++)
        {
            int gx = _nodeToGridX[n];
            int gy = _nodeToGridY[n];

            var edges = new List<GraphEdge>(4);
            TryAddEdge(gx + 1, gy, gridToNode, edges);
            TryAddEdge(gx - 1, gy, gridToNode, edges);
            TryAddEdge(gx, gy + 1, gridToNode, edges);
            TryAddEdge(gx, gy - 1, gridToNode, edges);
            adjacency[n] = edges.ToArray();
        }

        _graph = new Graph(adjacency);
        return true;
    }

    void TryAddEdge(int gx, int gy, int[] gridToNode, List<GraphEdge> edges)
    {
        if (gx < 0 || gy < 0 || gx >= _gridWidth || gy >= _gridHeight)
            return;

        int node = gridToNode[gy * _gridWidth + gx];
        if (node >= 0)
            edges.Add(new GraphEdge(node, 1f));
    }

    bool PickDeterministicStartGoal(out int startNode, out int goalNode)
    {
        startNode = FindFirstPassableNearBorder(left: true);
        goalNode = FindFirstPassableNearBorder(left: false);

        if (startNode >= 0 && goalNode >= 0 && startNode != goalNode)
            return true;

        if (_availableNodes.Count < 2)
            return false;

        startNode = _availableNodes[0];
        goalNode = startNode;
        int bestDist = -1;

        for (int i = 1; i < _availableNodes.Count; i++)
        {
            int node = _availableNodes[i];
            int dist = Manhattan(startNode, node);
            if (dist > bestDist)
            {
                bestDist = dist;
                goalNode = node;
            }
        }

        return goalNode != startNode;
    }

    int FindFirstPassableNearBorder(bool left)
    {
        int startX = left ? 0 : _gridWidth - 1;
        int dir = left ? 1 : -1;

        for (int offset = 0; offset < _gridWidth; offset++)
        {
            int gx = startX + offset * dir;
            if (gx < 0 || gx >= _gridWidth)
                break;

            for (int gy = 0; gy < _gridHeight; gy++)
            {
                int found = FindNodeByGrid(gx, gy);
                if (found >= 0)
                    return found;
            }
        }

        return -1;
    }

    int FindNodeByGrid(int gx, int gy)
    {
        for (int i = 0; i < _nodeToGridX.Length; i++)
        {
            if (_nodeToGridX[i] == gx && _nodeToGridY[i] == gy)
                return i;
        }

        return -1;
    }

    int Manhattan(int a, int b)
    {
        return Mathf.Abs(_nodeToGridX[a] - _nodeToGridX[b]) + Mathf.Abs(_nodeToGridY[a] - _nodeToGridY[b]);
    }

    void PaintNode(int node, Color color)
    {
        int px = Mathf.Clamp(_nodeToGridX[node] * sampleStride + sampleStride / 2, 0, _pixelWidth - 1);
        int py = Mathf.Clamp(_nodeToGridY[node] * sampleStride + sampleStride / 2, 0, _pixelHeight - 1);

        Color32 c = color;
        for (int dy = -paintRadiusPixels; dy <= paintRadiusPixels; dy++)
        {
            int y = py + dy;
            if (y < 0 || y >= _pixelHeight)
                continue;

            for (int dx = -paintRadiusPixels; dx <= paintRadiusPixels; dx++)
            {
                int x = px + dx;
                if (x < 0 || x >= _pixelWidth)
                    continue;

                _workingPixels[y * _pixelWidth + x] = c;
            }
        }
    }

    void ApplyPaint()
    {
        _workingTexture.SetPixels32(_workingPixels);
        _workingTexture.Apply(false, false);
    }
}
