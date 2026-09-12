using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Spin hit-point selector (SPIN_LOGIC.md sections 2 and 6): a large translucent cue ball in the
/// bottom-left where the player drags the red-ringed dot, plus a small always-visible preview on the
/// left edge that reopens it. Writes GameManager.SpinOffset, which is only editable during free aim
/// and locks once aim is confirmed.
///
/// Lives on an always-active container (not on the widget itself) so the preview keeps updating
/// while the full widget is closed. Manual Inspector references throughout, same as ScoreboardUI.
/// </summary>
public class SpinSelectorUI : MonoBehaviour, IPointerDownHandler, IDragHandler
{
    [SerializeField] private GameManager gameManager;

    [Header("Full widget (manual references)")]
    [SerializeField] private GameObject widgetRoot;
    [Tooltip("The circular cue-ball image the dot is positioned inside.")]
    [SerializeField] private RectTransform ballArea;
    [SerializeField] private RectTransform dot;
    [SerializeField] private Button closeButton;

    [Header("Mini preview (manual references)")]
    [SerializeField] private GameObject miniRoot;
    [SerializeField] private RectTransform miniBallArea;
    [SerializeField] private RectTransform miniDot;
    [SerializeField] private Button openButton;

    // Closed by default: per SPIN_LOGIC.md section 6 the always-visible element is the mini preview,
    // and the full widget only expands when the player taps it open.
    private bool wantsOpen = false;
    private bool dragging;

    void Awake()
    {
        if (gameManager == null) gameManager = GameManager.Instance;

        if (openButton != null) openButton.onClick.AddListener(() => wantsOpen = true);
        if (closeButton != null) closeButton.onClick.AddListener(() => wantsOpen = false);
    }

    void Start()
    {
        if (gameManager == null) gameManager = GameManager.Instance;
        if (gameManager == null)
        {
            Debug.LogError("SpinSelectorUI: No GameManager found.", this);
            enabled = false;
        }
    }

    void Update()
    {
        bool freeAim = IsFreeAim();
        if (!freeAim) dragging = false;

        if (widgetRoot != null) widgetRoot.SetActive(wantsOpen && freeAim);
        if (miniRoot != null) miniRoot.SetActive(!gameManager.IsAwaitingPlacement);
        if (openButton != null) openButton.interactable = freeAim;

        // Reading it back each frame means RequestStrike's reset shows up here as the dot
        // returning to the centre, with no extra plumbing.
        Vector2 spin = gameManager.SpinOffset;
        if (dot != null && ballArea != null) dot.anchoredPosition = spin * Radius(ballArea);
        if (miniDot != null && miniBallArea != null) miniDot.anchoredPosition = spin * Radius(miniBallArea);
    }

    // Spin is chosen before the shot: free aim only, and locked from Confirm onward.
    private bool IsFreeAim()
    {
        return !gameManager.IsConfirmMode
            && !gameManager.IsAwaitingPlacement
            && !gameManager.IsFrameOver
            && gameManager.isNextPlay();
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        // Pointer events bubble up from the label and the rest of the widget too, so a drag only
        // starts when the press actually lands inside the ball.
        dragging = false;
        if (widgetRoot == null || !widgetRoot.activeInHierarchy) return;
        if (!TryGetOffset(eventData, true, out Vector2 offset)) return;

        dragging = true;
        gameManager.SetSpinOffset(offset);
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (dragging && TryGetOffset(eventData, false, out Vector2 offset))
            gameManager.SetSpinOffset(offset);
    }

    // Pointer position as a fraction of the ball's radius: +x right, +y top. GameManager clamps it
    // to the usable 85% so the outer miscue ring can't be reached.
    private bool TryGetOffset(PointerEventData eventData, bool requireInside, out Vector2 offset)
    {
        offset = Vector2.zero;
        if (ballArea == null) return false;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                ballArea, eventData.position, eventData.pressEventCamera, out Vector2 local))
            return false;

        offset = (local - ballArea.rect.center) / Radius(ballArea);
        return !requireInside || offset.sqrMagnitude <= 1f;
    }

    private static float Radius(RectTransform rect)
    {
        return Mathf.Min(rect.rect.width, rect.rect.height) * 0.5f;
    }
}
