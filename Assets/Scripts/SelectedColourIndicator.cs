using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// A small circular UI element on the Canvas that shows which colour is currently "on" - updated
// the instant the player picks one by clicking a ball on the table (ColourBallClickTarget) or the
// AI nominates internally. Replaces the old picker panel's own status feedback for this purpose.
public class SelectedColourIndicator : MonoBehaviour
{
    [SerializeField] private GameManager gameManager;
    [Tooltip("The circular Image whose colour this updates. Defaults to one on this GameObject.")]
    [SerializeField] private Image indicatorImage;
    [Tooltip("Shown while no colour is nominated (on Red, or Colour with nothing chosen yet).")]
    [SerializeField] private Color noColourSelected = new Color(1f, 1f, 1f, 0.15f);

    private static readonly Dictionary<BallType, Color> ColourFor = new Dictionary<BallType, Color>
    {
        { BallType.Yellow, new Color(1f, 0.84f, 0f) },
        { BallType.Green, new Color(0.13f, 0.55f, 0.13f) },
        { BallType.Brown, new Color(0.55f, 0.27f, 0.07f) },
        { BallType.Blue, new Color(0.1f, 0.1f, 0.8f) },
        { BallType.Pink, new Color(1f, 0.41f, 0.71f) },
        { BallType.Black, new Color(0.12f, 0.12f, 0.12f) },
    };

    void Awake()
    {
        if (indicatorImage == null) indicatorImage = GetComponent<Image>();
    }

    void Start()
    {
        if (gameManager == null) gameManager = GameManager.Instance;
        if (gameManager == null || indicatorImage == null)
        {
            Debug.LogError("SelectedColourIndicator: needs a GameManager and an Image. Disabling.", this);
            enabled = false;
            return;
        }
        gameManager.OnTargetChanged += HandleTargetChanged;
        Refresh(gameManager.CurrentTargetColour);
    }

    void OnDestroy()
    {
        if (gameManager != null) gameManager.OnTargetChanged -= HandleTargetChanged;
    }

    private void HandleTargetChanged(GameManager.TargetBallState state, BallType? colour) => Refresh(colour);

    private void Refresh(BallType? colour)
    {
        Color c;
        indicatorImage.color = (colour.HasValue && ColourFor.TryGetValue(colour.Value, out c)) ? c : noColourSelected;
    }
}
