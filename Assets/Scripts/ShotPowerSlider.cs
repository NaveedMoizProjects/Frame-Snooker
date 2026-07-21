using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Attach to the same GameObject as your Slider (SHOTSLIDER). Dragging it sets power live;
// releasing it (lifting finger/mouse) fires the shot immediately at whatever power the
// slider was at - no separate Strike button needed.
[RequireComponent(typeof(Slider))]
public class ShotPowerSlider : MonoBehaviour, IPointerUpHandler
{
    [SerializeField] private GameManager gameManager;

    [Header("Power Mapping")]
    [Tooltip("Force value when the slider is at its minimum.")]
    [SerializeField] private float minPower = 2f;
    [Tooltip("Force value when the slider is at its maximum.")]
    [SerializeField] private float maxPower = 15f;

    [Tooltip("Reset the slider back to minimum after each shot, ready for the next one.")]
    [SerializeField] private bool resetAfterShot = true;

    private Slider slider;

    void Awake()
    {
        slider = GetComponent<Slider>();
        if (gameManager == null) gameManager = GameManager.Instance;
    }

    void Start()
    {
        if (gameManager == null) gameManager = GameManager.Instance;
        if (gameManager == null)
        {
            Debug.LogError("ShotPowerSlider: No GameManager found.", this);
            enabled = false;
            return;
        }

        if (resetAfterShot)
            gameManager.OnAllBallsStopped += HandleAllBallsStopped;
    }

    void OnDestroy()
    {
        if (gameManager != null)
            gameManager.OnAllBallsStopped -= HandleAllBallsStopped;
    }

    void Update()
    {
        if (gameManager == null || slider == null) return;

        // Only let the player touch the power slider once aim is actually locked in,
        // and not while balls from the previous shot are still settling.
        slider.interactable = gameManager.IsConfirmMode && gameManager.isNextPlay();
    }

    // Called by Unity's EventSystem the instant the pointer/touch is released, regardless
    // of whether it's still over the slider or was dragged off it.
    public void OnPointerUp(PointerEventData eventData)
    {
        if (gameManager == null || slider == null) return;

        if (!gameManager.IsConfirmMode)
        {
            Debug.Log("ShotPowerSlider: lock your aim with Confirm first, then pull the power slider.");
            return;
        }

        float power = Mathf.Lerp(minPower, maxPower, slider.normalizedValue);
        gameManager.SetStrikeForce(power);
        gameManager.RequestStrike();
    }

    private void HandleAllBallsStopped()
    {
        if (slider != null) slider.value = slider.minValue;
    }
}