using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Attach to a Canvas (or a child of one). Shows Player 1 / Player 2 scores, updating live
// via GameManager's OnScoreChanged event, and highlights whichever player's turn it is.
// Manual references only - wire player1Label / player2Label in the Inspector to the
// text objects you've already built under the Canvas.
public class ScoreboardUI : MonoBehaviour
{
    [SerializeField] private GameManager gameManager;

    [Header("Manual references")]
    [SerializeField] private TextMeshProUGUI player1Label;
    [SerializeField] private TextMeshProUGUI player2Label;

    [Header("Active-player highlight")]
    [SerializeField] private Color activeColor = Color.yellow;
    [SerializeField] private Color inactiveColor = Color.white;

    // ---------------- Break tracker (hudmid) ----------------
    [Header("Break tracker (hudmid)")]
    [Tooltip("The 'Break:' text inside MidHud.")]
    [SerializeField] private TextMeshProUGUI breakLabel;
    [Tooltip("Horizontal container under MidHud (Horizontal Layout Group) that holds one icon per ball potted this break.")]
    [SerializeField] private Transform breakBallRow;
    [Tooltip("The existing 'ballui' ball-icon element. Cloned once per potted ball; disabled itself at Start so it only ever acts as a template.")]
    [SerializeField] private GameObject ballIconTemplate;

    [Header("Ball-on indicator (top-left of Canvas)")]
    [Tooltip("Shows 'Ball on: <colour>' (or 'Ball on: Red'), live via GameManager.OnTargetChanged. Positioned separately from hudmid.")]
    [SerializeField] private TextMeshProUGUI ballOnLabel;

    // Real ball colours for the potted-ball dots, so a red pot shows a red dot, a blue pot a blue
    // dot, etc. Mirrors SelectedColourIndicator's colour choices for the colours they share, extended
    // with Red and Cue (which that indicator never needs, since only colours get nominated).
    private static readonly Dictionary<BallType, Color> BallColourFor = new Dictionary<BallType, Color>
    {
        { BallType.Red, new Color(0.8f, 0.1f, 0.1f) },
        { BallType.Yellow, new Color(1f, 0.84f, 0f) },
        { BallType.Green, new Color(0.13f, 0.55f, 0.13f) },
        { BallType.Brown, new Color(0.55f, 0.27f, 0.07f) },
        { BallType.Blue, new Color(0.1f, 0.1f, 0.8f) },
        { BallType.Pink, new Color(1f, 0.41f, 0.71f) },
        { BallType.Black, new Color(0.12f, 0.12f, 0.12f) },
        { BallType.Cue, Color.white },
    };

    private int currentBreakScore = 0;
    private readonly List<GameObject> spawnedBallIcons = new List<GameObject>();

    void Awake()
    {
        if (gameManager == null) gameManager = GameManager.Instance;
    }

    void Start()
    {
        if (gameManager == null) gameManager = GameManager.Instance;
        if (gameManager == null)
        {
            Debug.LogError("ScoreboardUI: No GameManager found.", this);
            enabled = false;
            return;
        }

        if (player1Label == null || player2Label == null)
        {
            Debug.LogWarning("ScoreboardUI: player1Label / player2Label not assigned in the Inspector.", this);
        }

        gameManager.OnScoreChanged += HandleScoreChanged;
        gameManager.OnTurnChanged += HandleTurnChanged;
        gameManager.OnTargetChanged += HandleTargetChanged;

        // The template lives in the scene purely to be cloned - it's never shown itself.
        if (ballIconTemplate != null) ballIconTemplate.SetActive(false);

        RefreshAll();
        RefreshBreakDisplay(); // idle "Break: 0" state on scene start
        HandleTargetChanged(gameManager.CurrentTargetState, gameManager.CurrentTargetColour);
    }

    void OnDestroy()
    {
        if (gameManager == null) return;
        gameManager.OnScoreChanged -= HandleScoreChanged;
        gameManager.OnTurnChanged -= HandleTurnChanged;
        gameManager.OnTargetChanged -= HandleTargetChanged;
    }

    // Mirrors SelectedColourIndicator's own subscription to the same event - "Ball on: Red" while
    // reds are still up, "Ball on: <colour>" once nominated/in the fixed colour sequence, or a
    // neutral placeholder while on Colour with nothing chosen yet (needs nomination).
    private void HandleTargetChanged(GameManager.TargetBallState state, BallType? colour)
    {
        if (ballOnLabel == null) return;
        ballOnLabel.text = state == GameManager.TargetBallState.Red
            ? "Ball on: Red"
            : colour.HasValue ? $"Ball on: {colour.Value}" : "Ball on: Colour";
    }

    private void HandleScoreChanged(int playerIndex, int newScore)
    {
        var label = playerIndex == 0 ? player1Label : player2Label;
        if (label != null) label.text = $"Player {playerIndex + 1}: {newScore}";

        // GameManager.AwardPoints (which raises this event) is used for two different things: the
        // potting player's OWN break continuing (a legal pot), and a foul PENALTY paid to their
        // opponent (LastShotWasFoul true). Only the first case is really "this player potted a ball" -
        // a penalty landing in the opponent's score isn't them potting anything, so it must not add
        // break points or ball icons. Checking both playerIndex-is-the-shooter and !LastShotWasFoul
        // keeps that explicit rather than relying on the two conditions happening to coincide.
        if (playerIndex == gameManager.CurrentPlayerIndex && !gameManager.LastShotWasFoul)
        {
            AddBreakPots();
        }
    }

    private void HandleTurnChanged(int newCurrentPlayerIndex)
    {
        RefreshHighlight(newCurrentPlayerIndex);

        // GameManager only ever passes the turn (raising this event) once the current break has
        // actually ended - a foul, or a legal shot that potted nothing (see EvaluateFoul/PassTurn in
        // GameManager.cs). A continuing break (a legal pot) never passes the turn, so "turn changed"
        // and "break ended" are the same moment here.
        ResetBreak();
    }

    // One icon + its point value per ball potted THIS shot, in potting order (GameManager.PottedThisShot
    // is only cleared on the next RequestStrike, so it still holds this shot's balls here). Called only
    // for a legal, break-continuing pot - see HandleScoreChanged.
    private void AddBreakPots()
    {
        foreach (var ball in gameManager.PottedThisShot)
        {
            var identity = ball.GetComponent<BallIdentity>();
            if (identity == null || identity.Type == BallType.Cue) continue; // cue ball never scores or shows here

            if (GameManager.BallValue.TryGetValue(identity.Type, out int value)) currentBreakScore += value;
            SpawnBallIcon(identity.Type);
        }
        RefreshBreakDisplay();
    }

    private void SpawnBallIcon(BallType type)
    {
        if (ballIconTemplate == null || breakBallRow == null) return;

        GameObject icon = Instantiate(ballIconTemplate, breakBallRow);
        icon.SetActive(true);

        var image = icon.GetComponent<Image>();
        if (image != null && BallColourFor.TryGetValue(type, out Color c)) image.color = c;

        spawnedBallIcons.Add(icon);
    }

    // Clears the break back to its idle state: zero score, no ball icons. Called whenever the
    // current break ends - see HandleTurnChanged.
    private void ResetBreak()
    {
        currentBreakScore = 0;
        foreach (var icon in spawnedBallIcons)
            if (icon != null) Destroy(icon);
        spawnedBallIcons.Clear();
        RefreshBreakDisplay();
    }

    private void RefreshBreakDisplay()
    {
        if (breakLabel == null) return;
        breakLabel.text = currentBreakScore > 0 ? $"Break {currentBreakScore}" : "Break: 0";
    }

    private void RefreshAll()
    {
        if (player1Label != null) player1Label.text = $"Player 1: {gameManager.GetScore(0)}";
        if (player2Label != null) player2Label.text = $"Player 2: {gameManager.GetScore(1)}";
        RefreshHighlight(gameManager.CurrentPlayerIndex);
    }

    private void RefreshHighlight(int activePlayerIndex)
    {
        if (player1Label != null) player1Label.color = (activePlayerIndex == 0) ? activeColor : inactiveColor;
        if (player2Label != null) player2Label.color = (activePlayerIndex == 1) ? activeColor : inactiveColor;
    }
}