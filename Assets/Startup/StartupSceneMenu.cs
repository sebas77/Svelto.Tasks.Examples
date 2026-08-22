using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class StartupSceneMenu : MonoBehaviour
{
    private const float PanelWidth = 520f;
    private const float PanelPadding = 24f;
    private const float TitleHeight = 38f;
    private const float ButtonHeight = 44f;
    private const float ButtonSpacing = 12f;

    private string _startupScenePath;

    private void Awake()
    {
        _startupScenePath = gameObject.scene.path;
    }

    private void OnGUI()
    {
        var availableSceneCount = SceneManager.sceneCountInBuildSettings - 1;
        var panelHeight = PanelPadding * 2f + TitleHeight + availableSceneCount * (ButtonHeight + ButtonSpacing);
        var panel = new Rect(
            (Screen.width - PanelWidth) * 0.5f,
            (Screen.height - panelHeight) * 0.5f,
            PanelWidth,
            panelHeight);

        GUI.Box(panel, GUIContent.none);

        var title = new Rect(
            panel.x + PanelPadding,
            panel.y + PanelPadding,
            panel.width - PanelPadding * 2f,
            TitleHeight);
        GUI.Label(title, "Select a scene", GUI.skin.label);

        var buttonY = title.yMax + ButtonSpacing;
        for (var buildIndex = 0; buildIndex < SceneManager.sceneCountInBuildSettings; buildIndex++)
        {
            var scenePath = SceneUtility.GetScenePathByBuildIndex(buildIndex);
            if (scenePath == _startupScenePath)
            {
                continue;
            }

            var button = new Rect(
                panel.x + PanelPadding,
                buttonY,
                panel.width - PanelPadding * 2f,
                ButtonHeight);

            if (GUI.Button(button, Path.GetFileNameWithoutExtension(scenePath)))
            {
                SceneManager.LoadScene(buildIndex);
            }

            buttonY += ButtonHeight + ButtonSpacing;
        }
    }
}
