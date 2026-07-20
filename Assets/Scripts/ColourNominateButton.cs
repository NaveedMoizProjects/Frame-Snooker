using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Attach to each colour nomination Button. Set the Ball Type in the Inspector,
/// then wire the Button's OnClick to Nominate().
/// </summary>
[RequireComponent(typeof(Button))]
public class ColourNominateButton : MonoBehaviour
{
    [SerializeField] private BallType colour = BallType.Yellow;

    public BallType Colour => colour;

    private Button button;

    void Awake()
    {
        button = GetComponent<Button>();
        button.onClick.AddListener(Nominate);
    }

    public void Nominate()
    {
        if (GameManager.Instance == null)
        {
            Debug.LogWarning("ColourNominateButton: No GameManager in scene.", this);
            return;
        }

        GameManager.Instance.OnColourNominated(colour);
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        if (colour == BallType.Red || colour == BallType.Cue)
            colour = BallType.Yellow;
    }
#endif
}
