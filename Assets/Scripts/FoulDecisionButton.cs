using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Attach to each foul-decision Button (Play / Make opponent play again). Set the Choice in the
/// Inspector, then wire the Button's OnClick to Choose(). See FOUL_PLAY_AGAIN_RULE.md.
/// </summary>
[RequireComponent(typeof(Button))]
public class FoulDecisionButton : MonoBehaviour
{
    public enum FoulChoice { Play, MakeOpponentPlayAgain }

    [SerializeField] private FoulChoice choice = FoulChoice.Play;

    private Button button;

    void Awake()
    {
        button = GetComponent<Button>();
        button.onClick.AddListener(Choose);
    }

    public void Choose()
    {
        if (GameManager.Instance == null)
        {
            Debug.LogWarning("FoulDecisionButton: No GameManager in scene.", this);
            return;
        }

        if (choice == FoulChoice.Play) GameManager.Instance.ChooseFoulPlay();
        else GameManager.Instance.ChooseFoulPlayAgain();
    }
}
