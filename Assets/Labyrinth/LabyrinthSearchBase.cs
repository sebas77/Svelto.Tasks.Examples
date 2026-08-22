using System.Collections.Generic;
using UnityEngine;

public abstract class LabyrinthSearchBase : MonoBehaviour
{
    [Header("References")]
    [SerializeField] protected string labyrinthResourcePath = "Labyrinths/Labyrinth_1024";
    [SerializeField] protected Renderer labyrinthRenderer;

    [Header("Grid")]
    [SerializeField, Range(2, 16)] protected int sampleStride = 4;
    [SerializeField, Range(0f, 1f)] protected float wallThreshold = 0.5f;

    [Header("Painting")]
    [SerializeField, Range(1, 8)] protected int paintRadiusPixels = 2;
    [SerializeField] protected Color exploredColor = new Color32(60, 120, 255, 255);
    [SerializeField] protected Color winningColor = new Color32(255, 60, 60, 255);

    // IMGUI stats window: shows each live solver's resolution time. Drawn once per frame by the
    // first registered instance. OnGUI is allocation-free: the "solving..." line is a constant;
    // the result line is built once at completion (RecordCompletion) and stored in _resultLine, so
    // OnGUI only blits stored strings via a cached GUIContent. No per-frame strings or GUIContents.
    // The label style (bold, orange, slightly larger) is built once from GUI.skin.label and cached
    // in _lineStyle, so the render path allocates nothing.
    static readonly List<LabyrinthSearchBase> _instances = new List<LabyrinthSearchBase>();

    const int WindowId = 0x1AB1;
    const string SolvingSuffix = ": solving...";
    const string SolvedSuffix = " ms (solved)";
    const string NoPathSuffix = " ms (no path)";

    static readonly GUIContent _windowTitle = new GUIContent("Resolution Times");
    static readonly GUIContent _lineContent = new GUIContent();
    static readonly GUILayoutOption[] _noOptions = new GUILayoutOption[0];
    static GUIStyle _lineStyle;

    Rect _windowRect = new Rect(10, 10, 280, 120);
    GUI.WindowFunction _drawWindow;

    enum SolveStatus { NotStarted, Solving, Solved, NoPath }

    SolveStatus _status;
    string _resultLine = string.Empty;

    // Short display name set by each subclass for the stats window line.
    protected abstract string DisplayName { get; }

    protected Color32[] workingPixels;
    protected int pixelWidth;
    protected int pixelHeight;
    protected int gridWidth;
    protected int gridHeight;
    Texture2D _workingTexture;
    Material _originalMaterial;
    Material _runtimeMaterial;

    protected bool TryInitializeTexture()
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
        pixelWidth = readable.width;
        pixelHeight = readable.height;
        _workingTexture = new Texture2D(pixelWidth, pixelHeight, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Point
        };

        workingPixels = readable.GetPixels32();
        Destroy(readable);
        _workingTexture.SetPixels32(workingPixels);
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

    protected bool TryBuildPassableGrid(out bool[] passable, out int passableCount)
    {
        gridWidth = Mathf.Max(2, pixelWidth / Mathf.Max(1, sampleStride));
        gridHeight = Mathf.Max(2, pixelHeight / Mathf.Max(1, sampleStride));
        passable = new bool[gridWidth * gridHeight];
        passableCount = 0;

        for (int y = 0; y < gridHeight; y++)
        {
            for (int x = 0; x < gridWidth; x++)
            {
                int px = Mathf.Clamp(x * sampleStride + sampleStride / 2, 0, pixelWidth - 1);
                int py = Mathf.Clamp(y * sampleStride + sampleStride / 2, 0, pixelHeight - 1);
                Color32 color = workingPixels[py * pixelWidth + px];
                float luma = (0.2126f * color.r + 0.7152f * color.g + 0.0722f * color.b) / 255f;
                bool isPassable = luma >= wallThreshold;
                passable[y * gridWidth + x] = isPassable;
                if (isPassable)
                    passableCount++;
            }
        }

        return passableCount >= 2;
    }

    protected void PaintGridCell(int x, int y, Color color)
    {
        int px = Mathf.Clamp(x * sampleStride + sampleStride / 2, 0, pixelWidth - 1);
        int py = Mathf.Clamp(y * sampleStride + sampleStride / 2, 0, pixelHeight - 1);
        Color32 paintColor = color;

        for (int dy = -paintRadiusPixels; dy <= paintRadiusPixels; dy++)
        {
            int pixelY = py + dy;
            if (pixelY < 0 || pixelY >= pixelHeight)
                continue;

            for (int dx = -paintRadiusPixels; dx <= paintRadiusPixels; dx++)
            {
                int pixelX = px + dx;
                if (pixelX < 0 || pixelX >= pixelWidth)
                    continue;

                workingPixels[pixelY * pixelWidth + pixelX] = paintColor;
            }
        }
    }

    protected bool TryPickDeterministicStartGoal(bool[] passable, out int startNode, out int goalNode)
    {
        startNode = FindFirstPassableNearBorder(passable, left: true);
        goalNode = FindFirstPassableNearBorder(passable, left: false);
        if (startNode >= 0 && goalNode >= 0 && startNode != goalNode)
            return true;

        startNode = -1;
        for (int i = 0; i < passable.Length; i++)
        {
            if (passable[i])
            {
                startNode = i;
                break;
            }
        }

        if (startNode < 0)
        {
            goalNode = -1;
            return false;
        }

        goalNode = startNode;
        int startX = startNode % gridWidth;
        int startY = startNode / gridWidth;
        int bestDistance = -1;
        for (int i = 0; i < passable.Length; i++)
        {
            if (!passable[i])
                continue;

            int y = i / gridWidth;
            int distance = Mathf.Abs(startX - i % gridWidth) + Mathf.Abs(startY - y);
            if (distance > bestDistance)
            {
                bestDistance = distance;
                goalNode = i;
            }
        }

        return goalNode != startNode;
    }

    protected static List<int> BuildWinningPath(int[] predecessors, int goalNode, int unvisited)
    {
        var path = new List<int>();
        int node = goalNode;
        while (node >= 0 && node != unvisited)
        {
            path.Add(node);
            if (path.Count > predecessors.Length)
                break;
            node = predecessors[node];
        }

        path.Reverse();
        return path;
    }

    protected void ApplyPaint()
    {
        _workingTexture.SetPixels32(workingPixels);
        _workingTexture.Apply(false, false);
    }

    protected void DisposeRendering()
    {
        if (labyrinthRenderer != null && labyrinthRenderer.sharedMaterial == _runtimeMaterial)
            labyrinthRenderer.sharedMaterial = _originalMaterial;
        if (_runtimeMaterial != null)
            Destroy(_runtimeMaterial);
        if (_workingTexture != null)
            Destroy(_workingTexture);
    }

    int FindFirstPassableNearBorder(bool[] passable, bool left)
    {
        int startX = left ? 0 : gridWidth - 1;
        int direction = left ? 1 : -1;
        for (int offset = 0; offset < gridWidth; offset++)
        {
            int x = startX + offset * direction;
            for (int y = 0; y < gridHeight; y++)
            {
                int node = y * gridWidth + x;
                if (passable[node])
                    return node;
            }
        }

        return -1;
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

    // Register with the live-solver registry so the stats window can iterate all solvers.
    // Main-thread only (Unity calls Awake/OnDestroy on the main thread); no locking needed.
    protected virtual void Awake()
    {
        _instances.Add(this);
    }

    protected virtual void OnDestroy()
    {
        int index = _instances.IndexOf(this);
        if (index >= 0)
            _instances.RemoveAt(index);
    }

    // Called by subclasses right when the search actually starts (after _timerStart is stamped).
    protected void MarkSolving()
    {
        _status = SolveStatus.Solving;
        _resultLine = DisplayName + SolvingSuffix;
    }

    // Called by subclasses from Finish()/FinishAndReportNoPath() with the elapsed ms they already
    // computed (elapsedNs / 1_000_000.0). Builds the result line once so OnGUI stays allocation-free.
    protected void RecordCompletion(bool solved, double elapsedMs)
    {
        _status = solved ? SolveStatus.Solved : SolveStatus.NoPath;
        // Format once at completion; F3 keeps the three-decimal style used by the existing Debug.Log.
        _resultLine = string.Format("{0}: {1:F3}{2}", DisplayName, elapsedMs, solved ? SolvedSuffix : NoPathSuffix);
    }

    // Single OnGUI for the whole base class: only the first registered instance draws, so the
    // window appears exactly once even though both solver components are attached to the scene.
    void OnGUI()
    {
        if (_instances.Count == 0 || !ReferenceEquals(this, _instances[0]))
            return;

        if (_drawWindow == null)
            _drawWindow = DrawStatsWindow;

        _windowRect = GUILayout.Window(WindowId, _windowRect, _drawWindow, _windowTitle, _noOptions);
    }

    // WindowFunction: one line per registered solver in a bold (cached in _lineStyle
    // on first use). Each line reuses the cached _lineContent GUIContent; the text is whatever the
    // solver pre-built in _resultLine (no per-frame strings). Solvers that never started (failed
    // init, status NotStarted) are skipped.
    void DrawStatsWindow(int id)
    {
        if (_lineStyle == null)
            _lineStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 14,
                normal = { textColor = new Color32(255, 255, 0, 255) }
            };

        for (int i = 0; i < _instances.Count; i++)
        {
            LabyrinthSearchBase solver = _instances[i];
            if (solver == null || solver._status == SolveStatus.NotStarted)
                continue;

            _lineContent.text = solver._resultLine;
            GUILayout.Label(_lineContent, _lineStyle, _noOptions);
        }

        GUI.DragWindow();
    }
}
