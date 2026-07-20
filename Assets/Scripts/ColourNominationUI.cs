using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Shows colour nomination UI after potting a red (doc section 5.1).
/// Supports manual buttons (ColourNominateButton on each Button) or auto-build into an empty panel.
/// </summary>
public class ColourNominationUI : MonoBehaviour
{
    [SerializeField] private GameManager gameManager;

    [Header("Manual references (drag from Hierarchy)")]
    [SerializeField] private GameObject nominationPanel;
    [SerializeField] private TextMeshProUGUI statusLabel;

    [Header("Auto-build")]
    [Tooltip("If the panel is empty at Start, colour buttons are created automatically.")]
    [SerializeField] private bool autoFillEmptyPanel = true;

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

        TryAutoFindReferences();
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

        SetupButtons();
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

    private void TryAutoFindReferences()
    {
        var canvas = GetComponent<Canvas>() ?? GetComponentInParent<Canvas>();
        if (canvas == null) return;

        if (statusLabel == null)
        {
            var statusTf = canvas.transform.Find("BallOnStatus");
            if (statusTf != null)
                statusLabel = statusTf.GetComponent<TextMeshProUGUI>();
        }

        if (nominationPanel == null)
        {
            var panelTf = canvas.transform.Find("ColourNominationPanel");
            if (panelTf != null)
                nominationPanel = panelTf.gameObject;
        }
    }

    private void SetupButtons()
    {
        colourButtons.Clear();

        if (nominationPanel == null)
        {
            BuildFullUi();
            return;
        }

        // Manual buttons already in scene?
        var manual = nominationPanel.GetComponentsInChildren<ColourNominateButton>(true);
        foreach (var mb in manual)
        {
            var btn = mb.GetComponent<Button>();
            if (btn != null)
                colourButtons[mb.Colour] = btn;
        }

        if (colourButtons.Count == 0 && autoFillEmptyPanel)
            PopulatePanel(nominationPanel);

        ConfigurePanelLayout(nominationPanel);
        nominationPanel.SetActive(false);
    }

    private void HandleTargetChanged(GameManager.TargetBallState state, BallType? colour) => Refresh();

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
            if (NominatableColours[i] == type) return ButtonColours[i];
        return Color.gray;
    }

    private void BuildFullUi()
    {
        var canvas = GetComponent<Canvas>() ?? GetComponentInParent<Canvas>();
        if (canvas == null)
        {
            Debug.LogError("ColourNominationUI: No Canvas found.", this);
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

        nominationPanel = new GameObject("ColourNominationPanel", typeof(RectTransform), typeof(Image), typeof(HorizontalLayoutGroup));
        nominationPanel.transform.SetParent(root, false);

        var panelRect = nominationPanel.GetComponent<RectTransform>();
        panelRect.anchorMin = new Vector2(0.5f, 0f);
        panelRect.anchorMax = new Vector2(0.5f, 0f);
        panelRect.pivot = new Vector2(0.5f, 0f);
        panelRect.anchoredPosition = new Vector2(0f, 120f);
        panelRect.sizeDelta = new Vector2(640f, 70f);

        nominationPanel.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.55f);
        PopulatePanel(nominationPanel);
        ConfigurePanelLayout(nominationPanel);
        nominationPanel.SetActive(false);
    }

    private void PopulatePanel(GameObject panel)
    {
        // "Nominate:" label
        if (panel.transform.Find("Prompt") == null)
        {
            var titleGo = new GameObject("Prompt", typeof(RectTransform), typeof(TextMeshProUGUI));
            titleGo.transform.SetParent(panel.transform, false);
            var title = titleGo.GetComponent<TextMeshProUGUI>();
            title.text = "Nominate:";
            title.fontSize = 18f;
            title.color = Color.white;
            title.alignment = TextAlignmentOptions.MidlineRight;
            var titleLayout = titleGo.AddComponent<LayoutElement>();
            titleLayout.minWidth = 90f;
            titleLayout.preferredWidth = 90f;
        }

        for (int i = 0; i < NominatableColours.Length; i++)
        {
            BallType ballType = NominatableColours[i];
            if (panel.transform.Find(ballType.ToString()) != null) continue;
            CreateColourButton(panel.transform, ballType, ButtonColours[i]);
        }
    }

    private void ConfigurePanelLayout(GameObject panel)
    {
        var layout = panel.GetComponent<HorizontalLayoutGroup>();
        if (layout == null)
            layout = panel.AddComponent<HorizontalLayoutGroup>();

        layout.spacing = 8f;
        layout.padding = new RectOffset(12, 12, 8, 8);
        layout.childAlignment = TextAnchor.MiddleCenter;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = true;
    }

    private void CreateColourButton(Transform parent, BallType ballType, Color tint)
    {
        var buttonGo = new GameObject(ballType.ToString(), typeof(RectTransform), typeof(Image), typeof(Button));
        buttonGo.transform.SetParent(parent, false);

        var image = buttonGo.GetComponent<Image>();
        image.color = tint;

        var button = buttonGo.GetComponent<Button>();
        button.targetGraphic = image;

        // ColourNominateButton wires OnClick in Awake — set type via serialized field workaround:
        // use a small helper MonoBehaviour we configure through SendMessage or duplicate Nominate calls.
        // Simplest: add listener here too.
        button.onClick.AddListener(() =>
        {
            if (gameManager != null) gameManager.OnColourNominated(ballType);
            Refresh();
        });

        colourButtons[ballType] = button;

        var labelGo = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
        labelGo.transform.SetParent(buttonGo.transform, false);
        var labelRect = labelGo.GetComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = Vector2.zero;
        labelRect.offsetMax = Vector2.zero;

        var label = labelGo.GetComponent<TextMeshProUGUI>();
        label.text = ballType.ToString();
        label.fontSize = 14f;
        label.alignment = TextAlignmentOptions.Center;
        label.color = ballType == BallType.Black ? Color.white : Color.black;
    }
}
