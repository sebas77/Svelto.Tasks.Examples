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

    void OnGUI()
    {
        if (implementations == null || implementations.Length == 0)
            return;

        GUILayout.BeginArea(new Rect(10, Screen.height - 46, Screen.width - 20, 36));
        GUILayout.BeginHorizontal(GUI.skin.box);
        GUILayout.Label(title + " [ and ]", GUILayout.ExpandWidth(false));

        for (int i = 0; i < implementations.Length; i++)
        {
            MonoBehaviour implementation = implementations[i];
            if (implementation != null && GUILayout.Toggle(i == _activeIndex && implementation.enabled, implementation.GetType().Name, GUI.skin.button))
                SwitchTo(i);
        }

        GUILayout.EndHorizontal();
        GUILayout.EndArea();
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
