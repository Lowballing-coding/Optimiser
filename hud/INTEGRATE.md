# Wiring hud_overlay.py into trainer.py

Files go into the trainer folder: `hud_overlay.py` and `fonts\` (6 .ttf files, OFL-Inter.txt, README.txt).

1. Run `venv\Scripts\python hud_overlay.py --selftest` first. It must print OK.
2. In trainer.py:
   - Add `from hud_overlay import Hud`. Create `self.hud = Hud()` once when the trainer starts, and call `self.hud.close()` on exit (use atexit).
   - When `self.hud.ok` is True, stop drawing the RLBot text HUD (clear the "hud" render group) and keep the 3D "path" group. When `ok` is False, keep the old RLBot HUD as a fallback.
   - Every packet, call `self.hud.update(state)`. Do this before the early return for non-Kickoff/Active phases, so the menu still works while the game is paused. The state has these fields:
     - `card`: `{"index": i+1, "total": len(SHOTS), "title": heading without the number, "tip": s.tip, "time_left": left, "time_limit": limit` (the keep-up-adjusted limit)`, "made": made, "taken": taken, "urgent": phase=="live" and left<=3, "extended": limit > the base limit}`.
     - `countdown`: only in the countdown phase, else None. `rem = cfg countdown - t`, `n = ceil(rem)`, `{"n": n, "frac": rem-(n-1)}`.
     - `toast`: a flash that is still showing (FLASH_TIME) wins:
       - "Clip saved" gives `{"kind":"clip","text":"CLIP SAVED","sub":"Last 20 s · Optimiser › Clips"}`.
       - "Clip failed: X" gives `{"kind":"error","text":"CLIP FAILED","sub":X}`.
       - Otherwise, in the result phase:
         - GOAL! gives `{"kind":"goal","text":"GOAL","sub":f"{title} · {t:.1f} s"}`.
         - Missed gives `{"kind":"miss","text":"MISSED","sub":title}`.
         - "Bot scored on you" gives `{"kind":"conceded","text":"BOT SCORED","sub":title}`.
       - In the replay phase: `{"kind":"replay","text":"REPLAY","sub":title}`.
       - Otherwise None.
     - `controls`: `self.menu`.
3. The menu toggle (controls show only while the game menu is open):
   - Read XInput START (0x0010). On its rising edge, toggle `self.menu`.
   - B (0x2000) rising while open closes it. Esc (GetAsyncKeyState(0x1B)) rising toggles it.
   - match_phase == Paused also counts as open.
   - Reset to closed when a new shot launches, if the game isn't paused.
   - Remove the old control hints from the countdown.
