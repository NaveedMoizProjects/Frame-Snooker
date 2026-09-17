# Free Ball Rule

## What it is
A free ball is awarded when, as the *direct result* of a foul, the incoming
player is snookered on every ball they are currently on (no ball-on can be
hit first, direct or off a cushion, by any legal cue ball path). The foul
itself does not grant a free ball — only the resulting snooker does.

## When it triggers
Check this immediately after a foul is confirmed, before handing the turn
to the next player:

1. A foul just occurred (any foul type — wrong ball first, no ball hit,
   cue ball potted, etc.).
2. As a result of where the balls now sit, the next player has **no legal
   shot** on any current ball-on — every ball-on is blocked from every
   possible cue ball contact angle (straight line and off-cushion paths
   both count as "reachable").
3. If both are true, the referee (GameManager) declares a free ball for
   the incoming player.

A snooker that already existed *before* the foul, or one the player put
themselves in, does not qualify — it has to be caused by the foul.

## What the player can do with it
- The player may nominate **any ball on the table** as their ball-on for
  this shot, regardless of the normal legal sequence.
- **Reds still on the table (reds phase):** potting the free-ball nomination
  scores 1 point (same as a red), and if successful, play continues exactly
  as if a red had just been potted — a colour must be nominated next.
- **Colours-only phase (all reds gone):** the free ball substitutes for
  whichever colour is next in sequence. The nominated ball must be played
  as if it *were* that colour — potting it scores that colour's value, and
  it respots (or stays down, matching that colour's normal end-of-frame
  behavior) exactly as the real colour would.
- If the player fails to pot the free-ball nomination, or fouls on the
  shot, normal foul rules and penalties apply as usual — the free ball is
  a one-shot opportunity, not a change to the frame's foul rules.
- The player is not required to take the free ball — they can always play
  a safety instead if they judge it's the better shot, same as any other
  turn.

## Player intent
Since the free ball lets the player pick any ball, the AI (or human) needs
to actually choose which ball to nominate before a shot can be planned —
this is a new decision point, not an automatic one. Treat it like normal
ball-on selection, just with the full ball set as candidates instead of
the restricted legal set.

## Scope note for implementation
This rule only changes **what counts as a legal ball-on for one shot** and
**how that shot scores**. It does not change:
- Foul detection itself (what makes a foul a foul)
- Normal ball-on / legal-sequence logic outside of a free-ball shot
- Potting, aiming, or any AI shot-selection scoring (`potScore`,
  `AimErrorRangeFor`, `IsMakeableAtThisSkill`, etc.)

Those stay exactly as they are; free ball sits on top of them as a
one-shot override of "what is a legal target," not a rewrite of how a
shot is aimed, scored for difficulty, or evaluated for position.
