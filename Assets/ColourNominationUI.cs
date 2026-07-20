using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
/// <summary>
/// Phase 5 UI hook for doc section 5.1: after potting a red the player must nominate
/// which colour they intend to pot next. Shows colour buttons while
/// GameManager.NeedsColourNomination is true and calls OnColourNominated.
/// </summary>
public class ColourNominationUI : MonoBehaviour
{
    [SerializeField] private GameManager gameManager;
    [Header("Runtime UI")]
    [Tooltip("When true, builds a simple picker on this Canvas at Start if references are empty.")]
    [SerializeField] private bool buildUiAtRuntime = true;
    [Header("Optional manual references")]
    [SerializeField] private GameObject nominationPanel;
    [SerializeField] private TextMeshProUGUI statusLabel;
    private readonly Dictionary<BallType, Button> colourButtons = new();
    private static readonly BallType[] NominatableColours =
    {
        BallType.Yellow, BallType.Green, BallType.Brown,
        BallType.Blue, BallType.Pink, BallType.Black
    };
    private static readonly Color[] ButtonColours =
    {
        new Color(1f, 0.84f, 0f),
        new Color(0.13f, 0.55f, 0.13f),
        new Color(0.55f, 0.27f, 0.07f),
        new Color(0.1f, 0.1f, 0.8f),
        new Color(1f, 0.41f, 0.71f),
        new Color(0.12f, 0.12f, 0.12f)
    };
    void Awake()
    {
        if (gameManager == null)
            gameManager = GameManager.Instance;
    }
    void Start()
    {
        if (gameManager == null)
            gameManager = GameManager.Instance;
        if (gameManager == null)
        {
            Debug.LogError("ColourNominationUI: No GameManager found.", this);
            enabled = false;
            return;
        }
        if (buildUiAtRuntime && nominationPanel == null)
            BuildRuntimeUi();
        gameManager.OnTargetChanged += HandleTargetChanged;
        Refresh();
    }
    void OnDestroy()
    {
        if (gameManager != null)
            gameManager.OnTargetChanged -= HandleTargetChanged;
    }
    void Update()
    {
        if (gameManager == null) return;
        RefreshPanelVisibility();
    }
    private void HandleTargetChanged(GameManager.TargetBallState state, BallType? colour)
    {
        Refresh();
    }
    private void Refresh()
    {
        if (statusLabel != null)
            statusLabel.text = BuildStatusText();
        RefreshPanelVisibility();
        RefreshButtonHighlights();
    }
    private string BuildStatusText()
    {
        if (gameManager.CurrentTargetState == GameManager.TargetBallState.Red)
            return "Ball on: Red";
        if (gameManager.NeedsColourNomination)
            return "Pot a red — now choose your colour";
        if (gameManager.CurrentTargetColour.HasValue)
            return $"Ball on: {gameManager.CurrentTargetColour.Value}";
        return "Ball on: Colour";
    }
    private void RefreshPanelVisibility()
    {
        if (nominationPanel != null)
            nominationPanel.SetActive(gameManager.NeedsColourNomination);
    }
    private void RefreshButtonHighlights()
    {
        foreach (var pair in colourButtons)
        {
            if (pair.Value == null) continue;
            var colours = pair.Value.colors;
            bool selected = gameManager.CurrentTargetColour == pair.Key;
            colours.normalColor = selected ? Color.white : ButtonColourFor(pair.Key);
            pair.Value.colors = colours;
        }
    }
    private static Color ButtonColourFor(BallType type)
    {
        for (int i = 0; i < NominatableColours.Length; i++)
        {
            if (NominatableColours[i] == type)
                return ButtonColours[i];
        }
        return Color.gray;
    }
    private void BuildRuntimeUi()
    {
        var canvas = GetComponent<Canvas>();
        if (canvas == null)
            canvas = GetComponentInParent<Canvas>();
        if (canvas == null)
        {
            Debug.LogError("ColourNominationUI: Attach this to a Canvas (or child of one).", this);
            return;
        }
        var root = canvas.GetComponent<RectTransform>();
        var statusGo = new GameObject("BallOnStatus", typeof(RectTransform), typeof(TextMeshProUGUI));
        statusGo.transform.SetParent(root, false);
        var statusRect = statusGo.GetComponent<RectTransform>();
        statusRect.anchorMin = new Vector2(0.5f, 1f);
        statusRect.anchorMax = new Vector2(0.5f, 1f);
        statusRect.pivot = new Vector2(0.5f, 1f);
        statusRect.anchoredPosition = new Vector2(0f, -20f);
        statusRect.sizeDelta = new Vector2(520f, 40f);
        statusLabel = statusGo.GetComponent<TextMeshProUGUI>();
        statusLabel.alignment = TextAlignmentOptions.Center;
        statusLabel.fontSize = 24f;
        statusLabel.color = Color.white;
        statusLabel.text = "Ball on: Red";
        nominationPanel = new GameObject("ColourNominationPanel", typeof(RectTransform), typeof(Image), typeof(HorizontalLayoutGroup));
        nominationPanel.transform.SetParent(root, false);
        var panelRect = nominationPanel.GetComponent<RectTransform>();
        panelRect.anchorMin = new Vector2(0.5f, 0f);
        panelRect.anchorMax = new Vector2(0.5f, 0f);
        panelRect.pivot = new Vector2(0.5f, 0f);
        panelRect.anchoredPosition = new Vector2(0f, 120f);
        panelRect.sizeDelta = new Vector2(640f, 70f);
        var panelImage = nominationPanel.GetComponent<Image>();
        panelImage.color = new Color(0f, 0f, 0f, 0.55f);
    }
}