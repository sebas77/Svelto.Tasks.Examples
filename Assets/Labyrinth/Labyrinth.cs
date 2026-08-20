using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Random = System.Random;

// ============================================
// STATE INTERFACE
// ============================================
public interface IState
{
    bool IsTerminal { get; }
    double GetReward(int player);
    void GetActions(List<IState> actions);
    int CurrentPlayer { get; }

    // For equality checks in Select
    bool Equals(IState other);
    int GetHashCode();
}

// ============================================
// GRAPH PATHFINDING EXAMPLE (SINGLE-PLAYER)
// ============================================
public readonly struct GraphEdge
{
    public readonly int to;
    public readonly float cost;

    public GraphEdge(int to, float cost)
    {
        this.to = to;
        this.cost = cost;
    }
}

public readonly struct Float2
{
    public readonly float x;
    public readonly float y;

    public Float2(float x, float y)
    {
        this.x = x;
        this.y = y;
    }

    public static float Distance(Float2 a, Float2 b)
    {
        float dx = a.x - b.x;
        float dy = a.y - b.y;
        return (float)Math.Sqrt(dx * dx + dy * dy);
    }
}

public sealed class Graph
{
    public readonly GraphEdge[][] adjacency;
    public readonly Float2[] positions;

    public Graph(GraphEdge[][] adjacency, Float2[] positions = null)
    {
        this.adjacency = adjacency;
        this.positions = positions;
    }

    public bool HasPositions => positions != null;
}

public sealed class GraphPathState : IState, IEquatable<GraphPathState>
{
    readonly Graph graph;
    readonly int current;
    readonly int goal;
    readonly int previous;
    readonly int stepsLeft;
    readonly float costSoFar;

    // Single-player: keep player constant so selection/backprop don't flip signs.
    public int CurrentPlayer => 1;

    public bool IsTerminal => current == goal || stepsLeft == 0;

    public GraphPathState(Graph graph, int start, int goal, int maxSteps)
    {
        this.graph = graph;
        this.current = start;
        this.goal = goal;
        previous = -1;
        stepsLeft = maxSteps;
        costSoFar = 0;
    }

    GraphPathState(Graph graph, int current, int goal, int previous, int stepsLeft, float costSoFar)
    {
        this.graph = graph;
        this.current = current;
        this.goal = goal;
        this.previous = previous;
        this.stepsLeft = stepsLeft;
        this.costSoFar = costSoFar;
    }

    float HeuristicToGoal()
    {
        if (graph.HasPositions == false) return 0;
        return Float2.Distance(graph.positions[current], graph.positions[goal]);
    }

    public double GetReward(int player)
    {
        // Reward is always from the (single) player's perspective.
        // Goal reached => (0,1]. Not reached => in [-1,0], guided by heuristic when available.
        if (current == goal)
            return 1.0 / (1.0 + costSoFar);

        float h = HeuristicToGoal();
        if (h <= 0) return 0;
        return -(h / (1.0 + h));
    }

    public void GetActions(List<IState> actions)
    {
        actions.Clear();

        if (IsTerminal) return;

        var edges = graph.adjacency[current];
        for (int i = 0; i < edges.Length; i++)
        {
            int to = edges[i].to;

            // Simple cycle reduction: avoid immediate backtracking.
            if (to == previous) continue;

            actions.Add(new GraphPathState(graph, to, goal, current, stepsLeft - 1, costSoFar + edges[i].cost));
        }
    }

    public bool Equals(IState other) => other is GraphPathState s && Equals(s);

    public bool Equals(GraphPathState other)
    {
        if (other == null) return false;
        return graph == other.graph
            && current == other.current
            && goal == other.goal
            && previous == other.previous
            && stepsLeft == other.stepsLeft
            && costSoFar.Equals(other.costSoFar);
    }

    public override bool Equals(object obj) => obj is GraphPathState s && Equals(s);

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + (graph != null ? graph.GetHashCode() : 0);
            hash = hash * 31 + current;
            hash = hash * 31 + goal;
            hash = hash * 31 + previous;
            hash = hash * 31 + stepsLeft;
            hash = hash * 31 + costSoFar.GetHashCode();
            return hash;
        }
    }

    public override string ToString() => $"{current} -> {goal} (cost {costSoFar}, stepsLeft {stepsLeft})";
}

// ============================================
// LOCK-FREE NODE (NO LINQ, NO ConcurrentBag)
// ============================================
public class Node
{
    public IState State { get; }
    public Node Parent { get; }

    // Lock-free Treiber stack of children.
    // NOTE: child._nextSibling is only written before publishing child via CAS.
    Node _childrenHead;
    Node _nextSibling;

    // Atomic counters
    long _visits = 0;

    // Fixed-point accumulation to allow Interlocked.Add.
    // Increase scale to reduce bias for non-binary rewards (e.g., pathfinding).
    const long ValueScale = 1_000_000;
    long _valueScaled = 0;

    // Pre-computed total actions for expansion tracking
    readonly int _totalActions;

    // Next action index to expand (lock-free expansion tracking)
    int _nextActionIndex = 0;

    // Cached actions list (computed once per node)
    readonly IState[] _actions;

    public long Visits => Interlocked.Read(ref _visits);
    public double Value => Interlocked.Read(ref _valueScaled) / (double)ValueScale;

    // True if all children have been created
    public bool AllChildrenCreated => Volatile.Read(ref _nextActionIndex) >= _totalActions;

    public Node(IState state, Node parent = null)
    {
        State = state;
        Parent = parent;

        if (state.IsTerminal)
        {
            _actions = Array.Empty<IState>();
            _totalActions = 0;
        }
        else
        {
            var actions = new List<IState>(8);
            state.GetActions(actions);
            _actions = actions.Count == 0 ? Array.Empty<IState>() : actions.ToArray();
            _totalActions = _actions.Length;
        }
    }

    // Lock-free visit and value updates
    public void IncrementVisits() => Interlocked.Increment(ref _visits);

    public void AddValue(double reward)
    {
        long scaled = (long)(reward * ValueScale);
        Interlocked.Add(ref _valueScaled, scaled);
    }

    void AddChild(Node child)
    {
        Node head;
        do
        {
            head = _childrenHead;
            child._nextSibling = head;
        } while (Interlocked.CompareExchange(ref _childrenHead, child, head) != head);
    }

    /// <summary>
    /// Lock-free expansion using atomic index increment.
    /// Each thread gets a unique action to expand. No locks, no HashSet.
    /// </summary>
    public Node Expand()
    {
        if (State.IsTerminal || _totalActions == 0)
            return null;

        // Atomically get next index and increment
        int index = Interlocked.Increment(ref _nextActionIndex) - 1;

        // Bounds check
        if ((uint)index >= (uint)_totalActions)
            return null;

        // Create child for this specific action
        var child = new Node(_actions[index], this);
        AddChild(child);

        return child;
    }

    /// <summary>
    /// Fully expanded == all actions have been assigned an expansion index.
    /// (Don't require each child to be visited; that stalls selection under contention.)
    /// </summary>
    public bool IsFullyExpanded()
    {
        return AllChildrenCreated;
    }

    /// <summary>
    /// UCT Selection - traverses tree using UCT formula
    /// </summary>
    public Node Select(double c = 1.414)
    {
        Node current = this;

        while (!current.State.IsTerminal && current.IsFullyExpanded())
        {
            current = current.GetBestChild(c);
            if (current == null) break;
        }

        return current;
    }

    Node GetBestChild(double c)
    {
        Node head = Volatile.Read(ref _childrenHead);
        if (head == null) return null;

        Node best = null;
        double bestScore = double.MinValue;

        long parentVisits = Visits;
        if (parentVisits < 1) parentVisits = 1;

        int parentPlayer = State.CurrentPlayer;
        double logParent = Math.Log(parentVisits);

        for (Node child = head; child != null; child = child._nextSibling)
        {
            long childVisits = child.Visits;

            // Child value is stored from the child's CurrentPlayer perspective.
            // Convert to this (parent) node perspective before comparing.
            double mean;
            if (childVisits == 0)
            {
                mean = 0;
            }
            else
            {
                mean = child.Value / childVisits;
                if (child.State.CurrentPlayer != parentPlayer)
                    mean = -mean;
            }

            double exploration = childVisits == 0
                ? double.MaxValue
                : c * Math.Sqrt(2.0 * logParent / childVisits);

            double score = mean + exploration;

            if (score > bestScore)
            {
                bestScore = score;
                best = child;
            }
        }

        return best;
    }

    public Node GetMostVisitedChild()
    {
        Node head = Volatile.Read(ref _childrenHead);
        if (head == null) return null;

        Node best = null;
        long bestVisits = long.MinValue;

        for (Node child = head; child != null; child = child._nextSibling)
        {
            long v = child.Visits;
            if (v > bestVisits)
            {
                bestVisits = v;
                best = child;
            }
        }

        return best;
    }

    /// <summary>
    /// Random rollout simulation
    /// </summary>
    public double Rollout(Random rng, List<IState> actionsBuffer)
    {
        var state = State;
        int depth = 0;
        int maxDepth = 100;

        while (!state.IsTerminal && depth < maxDepth)
        {
            state.GetActions(actionsBuffer);
            if (actionsBuffer.Count == 0) break;

            state = actionsBuffer[rng.Next(actionsBuffer.Count)];
            depth++;
        }

        return state.GetReward(State.CurrentPlayer);
    }

    /// <summary>
    /// Backpropagate reward up the tree
    /// </summary>
    public void Backpropagate(double reward)
    {
        Node current = this;
        int currentPlayer = State.CurrentPlayer;

        while (current != null)
        {
            current.IncrementVisits();

            // Adjust reward for current player perspective
            double adjustedReward = current.State.CurrentPlayer == currentPlayer
                ? reward
                : -reward;

            current.AddValue(adjustedReward);
            current = current.Parent;
        }
    }

    public List<IState> ExtractMostVisitedPath(int maxDepth)
    {
        var path = new List<IState>(maxDepth + 1);

        Node current = this;
        int depth = 0;
        while (current != null && depth <= maxDepth)
        {
            path.Add(current.State);
            if (current.State.IsTerminal) break;

            current = current.GetMostVisitedChild();
            if (current == null) break;

            depth++;
        }

        return path;
    }
}

// ============================================
// PARALLEL MCTS (LOCK-FREE)
// ============================================
public class ParallelMCTS
{
    public int NumIterations { get; set; } = 10000;
    public int ParallelTasks { get; set; } = Environment.ProcessorCount;
    public double UctConstant { get; set; } = 1.414;

    struct WorkerLocal
    {
        public Random rng;
        public List<IState> rolloutActions;
    }

    public List<IState> SearchPath(IState rootState, int maxDepth)
    {
        var root = new Node(rootState);
        var options = new ParallelOptions { MaxDegreeOfParallelism = ParallelTasks };

        Parallel.For<WorkerLocal>(
            0,
            NumIterations,
            options,
            () =>
            {
                int seed = unchecked(Environment.TickCount * 486187739 + Thread.CurrentThread.ManagedThreadId);
                return new WorkerLocal
                {
                    rng = new Random(seed),
                    rolloutActions = new List<IState>(64)
                };
            },
            (i, loopState, local) =>
            {
                Node selected = root.Select(UctConstant);
                Node expanded = selected?.Expand();
                if (expanded == null)
                    expanded = selected;

                if (expanded == null)
                    return local;

                double reward = expanded.Rollout(local.rng, local.rolloutActions);
                expanded.Backpropagate(reward);

                return local;
            },
            local => { }
        );

        return root.ExtractMostVisitedPath(maxDepth);
    }
}

public sealed class MazePathState : IState, IEquatable<MazePathState>
{
    readonly Graph _graph;
    readonly int _currentNode;
    readonly int _goalNode;
    readonly int _previousNode;
    readonly int _stepsLeft;
    readonly int _width;
    readonly int _height;

    public int NodeIndex => _currentNode;
    public int GoalNode => _goalNode;
    public int CurrentPlayer => 1;
    public bool IsTerminal => _currentNode == _goalNode || _stepsLeft <= 0;

    public MazePathState(Graph graph, int width, int height, int startNode, int goalNode, int maxSteps)
    {
        _graph = graph;
        _width = width;
        _height = height;
        _currentNode = startNode;
        _goalNode = goalNode;
        _previousNode = -1;
        _stepsLeft = maxSteps;
    }

    MazePathState(Graph graph, int width, int height, int currentNode, int goalNode, int previousNode, int stepsLeft)
    {
        _graph = graph;
        _width = width;
        _height = height;
        _currentNode = currentNode;
        _goalNode = goalNode;
        _previousNode = previousNode;
        _stepsLeft = stepsLeft;
    }

    public double GetReward(int player)
    {
        if (_currentNode == _goalNode)
            return 1.0;

        int cx = _currentNode % _width;
        int cy = _currentNode / _width;
        int gx = _goalNode % _width;
        int gy = _goalNode / _width;

        int manhattan = Mathf.Abs(cx - gx) + Mathf.Abs(cy - gy);
        int maxDist = _width + _height;
        return -Mathf.Clamp01((float)manhattan / Mathf.Max(1, maxDist));
    }

    public void GetActions(List<IState> actions)
    {
        actions.Clear();
        if (IsTerminal)
            return;

        GraphEdge[] edges = _graph.adjacency[_currentNode];
        for (int i = 0; i < edges.Length; i++)
        {
            int next = edges[i].to;
            if (next == _previousNode)
                continue;

            actions.Add(new MazePathState(_graph, _width, _height, next, _goalNode, _currentNode, _stepsLeft - 1));
        }
    }

    public bool Equals(IState other) => other is MazePathState state && Equals(state);

    public bool Equals(MazePathState other)
    {
        if (other == null)
            return false;

        return ReferenceEquals(_graph, other._graph)
               && _currentNode == other._currentNode
               && _goalNode == other._goalNode
               && _previousNode == other._previousNode
               && _stepsLeft == other._stepsLeft;
    }

    public override bool Equals(object obj) => obj is MazePathState state && Equals(state);

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + (_graph != null ? _graph.GetHashCode() : 0);
            hash = hash * 31 + _currentNode;
            hash = hash * 31 + _goalNode;
            hash = hash * 31 + _previousNode;
            hash = hash * 31 + _stepsLeft;
            return hash;
        }
    }
}

public sealed class Labyrinth : MonoBehaviour
{
    [Header("References")]
    [SerializeField] string labyrinthResourcePath = "Labyrinths/Labyrinth_1024";
    [SerializeField] Renderer labyrinthRenderer;

    [Header("Fork Search")]
    [SerializeField, Range(1, 128)] int maxParallelAgents = 16;
    [SerializeField, Range(1, 512)] int maxStepsPerAgent = 220;

    [Header("MCTS (kept as-is)")]
    [SerializeField, Range(10, 3000)] int iterationsPerSearchChunk = 64;
    [SerializeField, Range(1, 32)] int parallelTasks = 12;
    [SerializeField, Range(1, 20)] int searchChunksPerMove = 1;
    [SerializeField, Range(0.0f, 0.2f)] float artificialChunkDelaySeconds = 0.0f;

    [Header("Grid")]
    [SerializeField, Range(2, 16)] int sampleStride = 4;
    [SerializeField, Range(0f, 1f)] float wallThreshold = 0.5f;

    [Header("Painting")]
    [SerializeField, Range(1, 8)] int paintRadiusPixels = 2;

    [Header("Fast Mode Display")]
    [SerializeField, Tooltip("When ON, the maze is painted while the search advances. When OFF, the search runs as fast as possible and the solved maze is painted once at the end.")]
    bool debugPaintSearch = false;
    [SerializeField] Color exploredColor = new Color32(60, 120, 255, 255);
    [SerializeField] Color winningColor = new Color32(255, 60, 60, 255);

    Texture2D _workingTexture;
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
    int _agentIdCounter;

    int[] _predecessors;

    const int Unvisited = -2;
    const int NoParent = -1;

    readonly List<AgentRuntime> _agents = new List<AgentRuntime>(128);
    readonly HashSet<int> _visitedNodes = new HashSet<int>();

    sealed class AgentRuntime
    {
        public int id;
        public int currentNode;
        public int previousNode;
        public Color32 color;
        public GameObject marker;
        public ParallelMCTS mcts;
    }

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
        _visitedNodes.Add(_startNode);

        if (debugPaintSearch)
        {
            PaintNode(_startNode, new Color32(80, 255, 80, 255));
            PaintNode(_goalNode, new Color32(255, 255, 255, 255));
            ApplyPaint();
        }

        SpawnAgent(_startNode, -1, new Color32(255, 80, 80, 255));
        StartCoroutine(RunForkSearch());
    }

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
        _workingTexture.SetPixels32(_workingPixels);
        _workingTexture.Apply(false, false);

        if (labyrinthRenderer != null)
        {
            Material runtimeMaterial = new Material(labyrinthRenderer.sharedMaterial);
            runtimeMaterial.mainTexture = _workingTexture;
            labyrinthRenderer.material = runtimeMaterial;
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

    System.Collections.IEnumerator RunForkSearch()
    {
        long timerStart = Stopwatch.GetTimestamp();
        int maxTicks = _graph.adjacency.Length;

        for (int tick = 0; tick < maxTicks && _agents.Count > 0; tick++)
        {
            bool solved = AdvanceForkSearchTick();
            if (solved)
            {
                FinishSearch(true, ElapsedNanoseconds(timerStart));
                yield break;
            }

            if (debugPaintSearch)
            {
                ApplyPaint();
                if (artificialChunkDelaySeconds > 0f)
                    yield return new WaitForSeconds(artificialChunkDelaySeconds);
                else
                    yield return null;
            }
        }

        FinishSearch(false, ElapsedNanoseconds(timerStart));
    }

    bool AdvanceForkSearchTick()
    {
        var spawns = new List<(int node, int prev, Color32 color)>();

        for (int i = _agents.Count - 1; i >= 0; i--)
        {
            AgentRuntime agent = _agents[i];
            if (agent.currentNode == _goalNode)
                return true;

            List<int> options = GetNextOptions(agent.currentNode, agent.previousNode);
            if (options.Count == 0)
            {
                RemoveAgentAt(i);
                continue;
            }

            int chosen = options[0];
            if (options.Count > 1)
            {
                int chunks = debugPaintSearch ? searchChunksPerMove : 1;
                agent.mcts.NumIterations = debugPaintSearch
                    ? iterationsPerSearchChunk
                    : iterationsPerSearchChunk * searchChunksPerMove;

                for (int chunk = 0; chunk < chunks; chunk++)
                {
                    var root = new MazePathState(_graph, _gridWidth, _gridHeight, agent.currentNode, _goalNode, maxStepsPerAgent);
                    List<IState> path = agent.mcts.SearchPath(root, maxStepsPerAgent);

                    if (path.Count > 1 && path[1] is MazePathState best && options.Contains(best.NodeIndex))
                        chosen = best.NodeIndex;

                    if (debugPaintSearch)
                        PaintNode(chosen, Blend(agent.color, 0.35f));
                }
            }

            int previous = agent.currentNode;
            if (!_visitedNodes.Add(chosen) && chosen != _goalNode)
            {
                RemoveAgentAt(i);
                continue;
            }

            if (_predecessors[chosen] == Unvisited)
                _predecessors[chosen] = previous;

            agent.previousNode = previous;
            agent.currentNode = chosen;

            if (debugPaintSearch)
            {
                PaintNode(chosen, agent.color);
                MoveMarker(agent.marker.transform, chosen);
            }

            for (int o = 0; o < options.Count; o++)
            {
                int alt = options[o];
                if (alt == chosen)
                    continue;
                if (_agents.Count + spawns.Count >= maxParallelAgents)
                    break;
                if (!_visitedNodes.Add(alt))
                    continue;

                _predecessors[alt] = previous;
                spawns.Add((alt, previous, ForkColor(agent.color)));
            }
        }

        for (int i = 0; i < spawns.Count; i++)
            SpawnAgent(spawns[i].node, spawns[i].prev, spawns[i].color);

        for (int i = 0; i < _agents.Count; i++)
        {
            if (_agents[i].currentNode == _goalNode)
                return true;
        }

        return false;
    }

    void FinishSearch(bool solved, long elapsedNs)
    {
        if (debugPaintSearch)
        {
            if (solved)
                PaintNode(_goalNode, Color.white);
            ApplyPaint();
            Debug.Log(solved
                ? $"Labyrinth (debug paint) solved in {elapsedNs} ns."
                : $"Labyrinth (debug paint) found no path in {elapsedNs} ns.");
            return;
        }

        // fast mode: paint every explored node once, then the winning path over it
        for (int i = 0; i < _predecessors.Length; i++)
        {
            if (_predecessors[i] != Unvisited)
                PaintNode(i, exploredColor);
        }

        var path = solved ? ReconstructWinningPath() : null;
        if (path != null)
        {
            for (int i = 0; i < path.Count; i++)
                PaintNode(path[i], winningColor);
        }

        ApplyPaint();
        Debug.Log(solved
            ? $"Labyrinth (fast) solved in {elapsedNs} ns ({elapsedNs / 1_000_000.0:F3} ms)."
            : $"Labyrinth (fast) found no path in {elapsedNs} ns ({elapsedNs / 1_000_000.0:F3} ms).");
    }

    List<int> ReconstructWinningPath()
    {
        var path = new List<int>();
        int cur = _goalNode;

        while (cur >= 0 && path.Count <= _predecessors.Length)
        {
            path.Add(cur);
            if (cur == _startNode)
            {
                path.Reverse();
                return path;
            }
            cur = _predecessors[cur];
        }

        return null;
    }

    static long ElapsedNanoseconds(long startTimestamp)
    {
        return (long)((Stopwatch.GetTimestamp() - startTimestamp) * (1_000_000_000.0 / Stopwatch.Frequency));
    }

    List<int> GetNextOptions(int node, int previous)
    {
        var result = new List<int>(4);
        GraphEdge[] edges = _graph.adjacency[node];
        for (int i = 0; i < edges.Length; i++)
        {
            int to = edges[i].to;
            if (to == previous)
                continue;
            if (to != _goalNode && _visitedNodes.Contains(to))
                continue;
            result.Add(to);
        }

        if (result.Count == 0 && previous >= 0 && !_visitedNodes.Contains(previous))
            result.Add(previous);

        return result;
    }

    void SpawnAgent(int node, int previous, Color32 color)
    {
        if (_agents.Count >= maxParallelAgents)
            return;

        var runtime = new AgentRuntime
        {
            id = _agentIdCounter++,
            currentNode = node,
            previousNode = previous,
            color = color,
            mcts = new ParallelMCTS
            {
                NumIterations = iterationsPerSearchChunk,
                ParallelTasks = parallelTasks
            }
        };

        if (debugPaintSearch)
        {
            Transform parent = labyrinthRenderer != null ? labyrinthRenderer.transform : transform;
            runtime.marker = CreateMarker(runtime.id, parent, color);
            MoveMarker(runtime.marker.transform, node);
            PaintNode(node, color);
        }

        _agents.Add(runtime);
    }

    void RemoveAgentAt(int index)
    {
        AgentRuntime a = _agents[index];
        if (a.marker != null)
            Destroy(a.marker);
        _agents.RemoveAt(index);
    }

    GameObject CreateMarker(int id, Transform parent, Color color)
    {
        GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Quad);
        marker.name = $"Agent_{id:00}";
        marker.transform.SetParent(parent, false);

        float sx = 0.95f / _gridWidth;
        float sy = 0.95f / _gridHeight;
        marker.transform.localScale = new Vector3(sx, sy, 1f);
        marker.transform.localRotation = Quaternion.identity;

        Material m = new Material(Shader.Find("Unlit/Color"));
        m.color = color;
        Renderer r = marker.GetComponent<Renderer>();
        r.material = m;
        return marker;
    }

    void MoveMarker(Transform marker, int node)
    {
        float ux = (_nodeToGridX[node] + 0.5f) / _gridWidth;
        float uy = (_nodeToGridY[node] + 0.5f) / _gridHeight;
        marker.localPosition = new Vector3(ux - 0.5f, uy - 0.5f, -0.01f);
    }

    int Manhattan(int a, int b)
    {
        return Mathf.Abs(_nodeToGridX[a] - _nodeToGridX[b]) + Mathf.Abs(_nodeToGridY[a] - _nodeToGridY[b]);
    }

    Color32 ForkColor(Color32 c)
    {
        byte r = (byte)Mathf.Clamp(c.r + 40, 0, 255);
        byte g = (byte)Mathf.Clamp(c.g + 25, 0, 255);
        byte b = (byte)Mathf.Clamp(c.b + 25, 0, 255);
        return new Color32(r, g, b, 255);
    }

    void PaintNode(int node, Color32 color)
    {
        int px = Mathf.Clamp(_nodeToGridX[node] * sampleStride + sampleStride / 2, 0, _pixelWidth - 1);
        int py = Mathf.Clamp(_nodeToGridY[node] * sampleStride + sampleStride / 2, 0, _pixelHeight - 1);

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

                _workingPixels[y * _pixelWidth + x] = color;
            }
        }
    }

    void ApplyPaint()
    {
        _workingTexture.SetPixels32(_workingPixels);
        _workingTexture.Apply(false, false);
    }

    static Color32 Blend(Color32 source, float alpha)
    {
        byte r = (byte)Mathf.Lerp(0, source.r, alpha);
        byte g = (byte)Mathf.Lerp(0, source.g, alpha);
        byte b = (byte)Mathf.Lerp(0, source.b, alpha);
        return new Color32(r, g, b, 255);
    }
}
