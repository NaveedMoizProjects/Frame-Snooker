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

        RefreshAll();
    }

    void OnDestroy()
    {
        if (gameManager == null) return;
        gameManager.OnScoreChanged -= HandleScoreChanged;
        gameManager.OnTurnChanged -= HandleTurnChanged;
    }

    private void HandleScoreChanged(int playerIndex, int newScore)
    {
        var label = playerIndex == 0 ? player1Label : player2Label;
        if (label != null) label.text = $"Player {playerIndex + 1}: {newScore}";
    }

    private void HandleTurnChanged(int newCurrentPlayerIndex)
    {
        RefreshHighlight(newCurrentPlayerIndex);
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