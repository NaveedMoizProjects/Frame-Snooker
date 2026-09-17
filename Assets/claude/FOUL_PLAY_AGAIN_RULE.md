# Foul: Play or Make Opponent Play Again

## 1. The real rule

After any foul, the non-offending player has a choice, before taking their own shot:

1. **Play** — take the next shot themselves, from the table as it currently lies (plus
   normal placement-in-D if the cue ball was potted, per `BALL_PLACEMENT_D.md`).
2. **Make the fouling player play again** — decline to play, forcing the player who just
   fouled to take the next shot instead, from the same position. This is the real-world
   option a player uses when the foul left the table in an awkward state that's actually
   *worse* for them to inherit than to hand back.

This choice happens for **every foul**, human or AI, in either direction — whoever did
NOT commit the foul makes this decision before the next shot begins.

## 2. State machine addition

New `GameManager` state, same pattern as `awaitingPlacement`/`NeedsColourNomination`:

```
awaitingFoulDecision : bool
foulDecisionForPlayerIndex : int   // the non-offending player who must decide
```

- Set `awaitingFoulDecision = true` (with `foulDecisionForPlayerIndex` = the incoming/
  non-offending player) at the end of `EvaluateFoul()`'s foul branch, **before** any
  placement-in-D or normal-aim flow starts.
- While `true`: block `ConfirmButtonPressed()`/`RequestStrike()` exactly like the other
  gates already do, and don't show normal aim/spin UI.
- Two ways this resolves:
  - **Play** chosen → `awaitingFoulDecision = false`, proceed to normal flow
    (placement-in-D if cue ball was potted, otherwise normal aiming) for
    `foulDecisionForPlayerIndex`.
  - **Make them play again** chosen → `awaitingFoulDecision = false`,
    `currentPlayerIndex` flips back to the player who committed the foul, then that
    player goes through the same normal flow (placement-in-D if applicable, otherwise
    normal aiming) from their end.

## 3. Human UI

When a foul just happened and it's the human's decision: show two buttons —
"Play" and "Make [opponent name] play again" — instead of the normal aim/Confirm UI.
Reuse the same visibility-gating pattern already used for the nomination indicator and
placement panel (tied to `awaitingFoulDecision`).

## 4. AI decision logic (when the human fouled and the AI must decide)

Reuse the AI's existing shot-evaluation pipeline (`AI_SHOT_SELECTION.md` §3-4) — this is
not a new decision system, it's a read of the same candidate-generation the AI already
does before every shot:

```
Generate candidates from the current table state (as if about to take the shot).
If a genuinely makeable pot exists (best candidate clears the AI's own
   minAcceptablePotScore / makeability gate, same thresholds it would use to decide
   pot-vs-safety on a normal turn):
    → choose PLAY
Else (nothing makeable, would have to play a safety/escape anyway):
    → choose MAKE OPPONENT PLAY AGAIN
```

This is a natural, cheap decision — it's the exact same "is there a pot here" check the
AI already runs at the start of every normal turn, just consulted one step earlier,
before committing to actually playing. No new difficulty-specific tuning needed: a
stronger level naturally chooses "play" more often simply because it finds makeable pots
more often, which already matches how a real stronger player would use this option.

## 5. Symmetry

This applies exactly the same way in both directions:
- Human fouls → AI decides Play vs Play-Again, using §4.
- AI fouls → human decides Play vs Play-Again, using §3's UI.

No special-casing either direction — same state machine, same trigger, just whichever
side is non-offending makes the call through whichever interface (human UI or AI logic)
applies to them.
