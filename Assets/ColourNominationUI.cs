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
    [Tooltip("The six-colour picker. Shown only while a nomination is owed.")]
    [SerializeField] private GameObject nominationPanel;
    [Tooltip("Prompt inside the picker. Hidden along with the picker once a colour is chosen.")]
    [SerializeField] private TextMeshProUGUI statusLabel;
    [Tooltip("Which ball is on, kept on screen through aiming and the strike. Must live OUTSIDE the " +
             "picker: anything parented under it disappears when the picker collapses.")]
    [SerializeField] private TextMeshProUGUI ballOnLabel;
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
        // The ball-on label is built independently of the picker: the scene wires a picker but its
        // label sits inside it, so without this there is nothing left on screen after a selection.
        if (ballOnLabel == null)
            ballOnLabel = BuildBallOnLabel();
        BindSceneButtons();
        gameManager.OnTargetChanged += HandleTargetChanged;
        Refresh();
    }

    // The scene's picker already holds six ColourNominateButtons wired to OnColourNominated. Binding
    // them here gets the selection highlight working and refreshes the labels inside the same click,
    // so the confirmation appears the instant the player taps rather than a frame later.
    private void BindSceneButtons()
    {
        colourButtons.Clear();
        if (nominationPanel == null) return;

        foreach (var nominate in nominationPanel.GetComponentsInChildren<ColourNominateButton>(true))
        {
            var button = nominate.GetComponent<Button>();
            if (button == null) continue;
            colourButtons[nominate.Colour] = button;
            button.onClick.AddListener(Refresh);
        }
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
        string text = BuildStatusText();
        if (statusLabel != null) statusLabel.text = text;
        if (ballOnLabel != null) ballOnLabel.text = text;
        RefreshPanelVisibility();
        RefreshButtonHighlights();
    }
    private string BuildStatusText()
    {
        if (gameManager.CurrentTargetState == GameManager.TargetBallState.Red)
            return "Ball on: Red";
        if (gameManager.NeedsColourNomination)
            return "Pot a red - now choose your colour";
        if (gameManager.CurrentTargetColour.HasValue)
            return $"{gameManager.CurrentTargetColour.Value} selected";
        return "Ball on: Colour";
    }
    // The picker panel is no longer the way to nominate a colour - the player clicks the actual
    // ball on the table instead (see ColourBallClickTarget), shown via SelectedColourIndicator on
    // the Canvas. Kept as a permanently-hidden method (rather than deleting the panel/this call)
    // so nothing else that references nominationPanel breaks.
    private void RefreshPanelVisibility()
    {
        if (nominationPanel != null)
            nominationPanel.SetActive(false);
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
    // Sits at the canvas root, deliberately not under the picker, so a selection can collapse the
    // picker while this stays on screen as the reminder of which ball is on.
    private TextMeshProUGUI BuildBallOnLabel()
    {
        var canvas = GetComponent<Canvas>() ?? GetComponentInParent<Canvas>();
        if (canvas == null)
        {
            Debug.LogError("ColourNominationUI: Attach this to a Canvas (or child of one).", this);
            return null;
        }

        var go = new GameObject("BallOnStatus", typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(canvas.GetComponent<RectTransform>(), false);
        var rect = go.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 1f);
        rect.anchorMax = new Vector2(0.5f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.anchoredPosition = new Vector2(0f, -20f);
        rect.sizeDelta = new Vector2(520f, 40f);

        var label = go.GetComponent<TextMeshProUGUI>();
        label.alignment = TextAlignmentOptions.Center;
        label.fontSize = 24f;
        label.color = Color.white;
        return label;
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