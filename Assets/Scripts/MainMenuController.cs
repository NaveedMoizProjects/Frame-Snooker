using UnityEngine;
using UnityEngine.SceneManagement;

// Wires the Main Menu's Play/Settings/Exit buttons. Attached to MainMenuCanvas; finds its own
// buttons by name under MainMenuButtonPanel so no scene wiring is required beyond adding this
// component to the canvas.
public class MainMenuController : MonoBehaviour
{
    [SerializeField] private string playSceneName = "Medium";

    void Start()
    {
        var panel = transform.Find("MainMenuButtonPanel");
        if (panel == null) return;

        Wire(panel, "PlayButton", OnPlayClicked);
        Wire(panel, "SettingsButton", OnSettingsClicked);
        Wire(panel, "ExitButton", OnExitClicked);
    }

    private static void Wire(Transform panel, string buttonName, UnityEngine.Events.UnityAction action)
    {
        var buttonTransform = panel.Find(buttonName);
        if (buttonTransform == null) return;
        var button = buttonTransform.GetComponent<UnityEngine.UI.Button>();
        if (button == null) return;
        button.onClick.AddListener(action);
    }

    private void OnPlayClicked()
    {
        SceneManager.LoadScene(playSceneName);
    }

    private void OnSettingsClicked()
    {
        // No settings system exists in the project yet - placeholder hook for when one is added.
        Debug.Log("[MainMenu] Settings clicked - no settings screen implemented yet.");
    }

    private void OnExitClicked()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }
}
