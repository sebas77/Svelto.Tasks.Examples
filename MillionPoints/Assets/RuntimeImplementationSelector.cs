using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Switches mutually exclusive example implementations at runtime. Disabling the active component before enabling
/// the next one guarantees its OnDisable cleanup completes before the replacement starts a fresh simulation.
/// </summary>
public sealed class RuntimeImplementationSelector : MonoBehaviour
{
    [SerializeField] MonoBehaviour[] implementations;
    [SerializeField] string title = "Implementation";
    [SerializeField] Text titleText;
    [SerializeField] Button[] implementationButtons;

    int _activeIndex = -1;

    void Awake()
    {
        _activeIndex = FindEnabledImplementation();

        for (int i = 0; i < implementations.Length; i++)
        {
            if (i != _activeIndex && implementations[i] != null)
                implementations[i].enabled = false;
        }

        titleText.text = title + " [ and ]";

        for (int i = 0; i < implementationButtons.Length; i++)
        {
            int implementationIndex = i;
            Button button = implementationButtons[i];
            button.onClick.AddListener(() => SwitchTo(implementationIndex));

            string buttonName = implementations[i].GetType().Name;
            const string namePrefix = "MillionPoints";
            if (buttonName.StartsWith(namePrefix))
                buttonName = buttonName.Substring(namePrefix.Length);

            button.GetComponentInChildren<Text>().text = buttonName;
        }

        RefreshButtons();
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.LeftBracket))
            SwitchTo(_activeIndex - 1);
        else if (Input.GetKeyDown(KeyCode.RightBracket))
            SwitchTo(_activeIndex + 1);
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
        RefreshButtons();
    }

    void RefreshButtons()
    {
        for (int i = 0; i < implementationButtons.Length; i++)
            implementationButtons[i].interactable = i != _activeIndex;
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
