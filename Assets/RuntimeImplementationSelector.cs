using UnityEngine;

/// <summary>
/// Switches mutually exclusive example implementations at runtime. Disabling the active component before enabling
/// the next one guarantees its OnDisable cleanup completes before the replacement starts a fresh simulation.
/// </summary>
public sealed class RuntimeImplementationSelector : MonoBehaviour
{
    [SerializeField] MonoBehaviour[] implementations;
    [SerializeField] string title = "Implementation";

    int _activeIndex = -1;
    GUIContent _titleContent;
    GUIContent[] _implementationContents;

    void Awake()
    {
        _activeIndex = FindEnabledImplementation();

        for (int i = 0; i < implementations.Length; i++)
        {
            if (i != _activeIndex && implementations[i] != null)
                implementations[i].enabled = false;
        }
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.LeftBracket))
            SwitchTo(_activeIndex - 1);
        else if (Input.GetKeyDown(KeyCode.RightBracket))
            SwitchTo(_activeIndex + 1);
    }

    // IMGUI repaints every frame, so nothing here may allocate: the label strings and
    // GUIContents are cached once and the row is laid out with fixed rects instead of
    // GUILayout (whose params arrays and layout caches allocate on every call). IMGUI
    // still churns internally, so collections observed across this method are excluded
    // from the PerformanceProfiler GC count.
    void OnGUI()
    {
        if (implementations == null || implementations.Length == 0)
            return;

        int collectionsBefore = System.GC.CollectionCount(0);
        try
        {
            DrawImplementationBar();
        }
        finally
        {
            PerformanceCheker.PerformanceProfiler.AttributeCollectionsToGui(
                System.GC.CollectionCount(0) - collectionsBefore);
        }
    }

    void DrawImplementationBar()
    {
        if (_titleContent == null)
            BuildGuiContents();

        const float rowHeight = 36f;
        const float controlHeight = 30f;
        const float padding = 8f;
        const float titleWidth = 170f;
        const float buttonWidth = 230f;

        var rowRect = new Rect(10, Screen.height - 46, Screen.width - 20, rowHeight);
        GUI.Box(rowRect, GUIContent.none);

        var controlRect = new Rect(rowRect.x + padding, rowRect.y + (rowHeight - controlHeight) / 2, titleWidth, controlHeight);
        GUI.Label(controlRect, _titleContent);

        controlRect.x += titleWidth + padding;
        for (int i = 0; i < implementations.Length; i++)
        {
            MonoBehaviour implementation = implementations[i];
            if (implementation == null)
                continue;

            bool active = i == _activeIndex && implementation.enabled;
            controlRect.width = buttonWidth;
            if (GUI.Toggle(controlRect, active, _implementationContents[i], GUI.skin.button) && !active)
                SwitchTo(i);

            controlRect.x += buttonWidth + padding;
        }
    }

    void BuildGuiContents()
    {
        // buttons show the implementation type name without the common example prefix
        const string namePrefix = "MillionPoints";

        _titleContent = new GUIContent(title + " [ and ]");
        _implementationContents = new GUIContent[implementations.Length];
        for (int i = 0; i < implementations.Length; i++)
        {
            string buttonName = implementations[i] != null ? implementations[i].GetType().Name : "<missing>";
            if (buttonName.StartsWith(namePrefix))
                buttonName = buttonName.Substring(namePrefix.Length);

            _implementationContents[i] = new GUIContent(buttonName);
        }
    }

    public void SwitchTo(int index)
    {
        if (implementations == null || implementations.Length == 0)
            return;

        index = (index % implementations.Length + implementations.Length) % implementations.Length;
        if (implementations[index] == null || index == _activeIndex && implementations[index].enabled)
            return;

        if (_activeIndex >= 0)
        {
            MonoBehaviour current = implementations[_activeIndex];
            if (current != null)
                current.enabled = false;
        }

        implementations[index].enabled = true;
        _activeIndex = index;
    }

    int FindEnabledImplementation()
    {
        for (int i = 0; i < implementations.Length; i++)
        {
            if (implementations[i] != null && implementations[i].enabled)
                return i;
        }

        return -1;
    }
}
