# Copilot panel — wireframe spec

**Status:** draft for review, 2026-08-24. Drill-down of D10, D11, D15, D16, D18, D22, D26. UI copy examples are Swedish (installation locale, D7); the layout is locale-neutral.

## Window

- WPF, always-on-top, default **384 × ~700 px**, docked to the right screen edge; resizable within limits (min 340 wide), remembers position/size per user.
- One window, four states (a strict state machine): **idle → pre-call → live → post-call → idle**.
- Light theme in POC; dark theme tracked for L2 (the panel is stared at all day — worth doing, not worth doing first).
- Global configurable hotkey (default `Ctrl+Alt+Space`): brings the panel to front and focuses the ask input — the rep never has to mouse over from Teams to type.

## Live state — zone order and rationale

Top-to-bottom priority = glance priority: what should the rep's eye hit first?

| # | Zone | Why here |
|---|---|---|
| — | Status strip | thin, always visible: proof-of-life + controls |
| 1 | Att fråga (questions) | the next thing to *say* — the panel's primary output |
| 2 | Föreslå (products) | second action: what to raise |
| 3 | Kundbild (picture) | reference state, read when needed |
| 4 | Ask (input + answer) | input-at-bottom convention; answer bubble above it |

### Status strip

- **Listening dot + timer** — green while both capture streams deliver audio.
- **Mic + speaker level meters** — tiny, live. Not decoration: they are the trust/debug surface that proves capture is alive and the headset is routed correctly (D22). A dead meter mid-call is the #1 support question answered before it's asked.
- **Language chip** (`SV`) — active STT language. On `language_flag` from the gate (D7, prompts.md): chip turns amber with a one-click switch ("Engelska? byt").
- **Stop button** — ends the call → post-call state. Deliberately small; ending by accident is worse than reaching for it.
- **Advisor pulse** — the listening dot pulses briefly while an advisor call is in flight, so silence never reads as "broken".

### Zone 1 — Att fråga (≤ 3 items)

Item anatomy: question text (14 px, weight 500) · thread chip · dismiss ×.

States:
- **fresh** — accent border + tint + "ny" tag for ~2 s after arrival, then decays to active.
- **active** — neutral card.
- **asked** — checkmark, dimmed to ~55%; auto-set via the gate's `questions_addressed`, or manually by clicking the item. Slides out on the next panel delta to free the slot.
- **dismissed** — removed immediately; recorded in the asked/dismissed history the advisor receives (prompts.md), so it never comes back.

### Zone 2 — Föreslå (≤ 3 items)

Card anatomy: product name · thread chip · expand chevron · one-line why (13 px) · price line (12 px, always suffixed "indikativt", D6).

- **Expand** opens the full product card from the knowledge pack (markdown render) as an inline flyout — the rep's 5-second deep dive without leaving the panel.
- **fresh** = 2 px accent border, decaying like questions. **Dismiss** behaves as in zone 1 (dismissed ≠ customer-rejected; the picture's `product_interest` stance is the gate's job, not the UI's).

### Zone 3 — Kundbild

- Company header: name + one muted line (industry · size · customer since) from the picture's `company` block.
- **Thread chips row** — the D26 visibility dividend: open = accent tint, objection = warning tint + triangle icon, parked = dashed outline with "parkerad". Click a chip → filter/highlight related items (L1-optional; POC may ship chips as display-only).
- **Facts** — newest 3–4 as one-liners; "Visa allt" expands the full picture grouped by category.
- **Action items** — checkboxes; the rep can tick done mid-call (state stored with the call, feeds the summary).
- This zone updates quietly: no motion, no accents — it is reference, not alert.

### Zone 4 — Ask

- Single-line input, Enter sends (D15); send button for mouse users. While the advisor runs: input stays enabled, a spinner replaces the send icon; a new Enter preempts (one in flight, latest wins).
- **Answer bubble** above the input: latest answer only, 13 px, with a muted "Svar · nyss" stamp. Older answers scroll within the bubble area (small, capped height) — no chat thread UI.
- Typed text is also the picture-correction channel (D15); no separate mode or syntax — the extractor sorts questions from statements.

## Motion and damping (D11 made visual)

1. **Position stability beats ordering.** Kept items never move. New items fill empty slots; a replacement swaps in place. No reflow, ever, while the rep might be mid-glance.
2. **Fresh accent, then quiet**: ~2 s tinted border on arrival, no animation loops, no sound, no toasts.
3. Peripheral-vision contract: the rep should *detect* "something new" from the corner of an eye (accent appears) without being able to say what it was until they look.

## Degradation states

| Condition | Panel behavior |
|---|---|
| STT stream drops | amber banner "Transkribering avbruten — återansluter…"; capture meters keep running |
| Backend unreachable | panel freezes with banner + "senast uppdaterad 07:41"; ask input disabled |
| Advisor errors/timeouts | silent retry once; on repeat, subtle "rådgivaren släpar efter" note in zone 1; stale suggestions stay visible rather than blanking |
| Capture device lost | red banner + device picker shortcut; call keeps recording state so re-plug resumes |

Never blank a zone because of a transient error — stale guidance beats an empty panel mid-call.

## Pre-call and post-call states

**Pre-call** (D16): company field · free-text "vad vet du / mål med samtalet" · language selector (installation set, default last used) · device check with the same live meters · start button. All skippable except start.

**Post-call** (D18): summary + next steps (checkboxes carried from action items) · copy button (plain text, CRM-paste-friendly) · call duration · "nytt samtal". Transcript viewer is deliberately absent until L2.

## Interaction telemetry (feeds D24)

Every suggestion logs its lifecycle with timestamps: `shown → asked | dismissed | expired`, product cards `shown → expanded | dismissed`, ask-lane usage counts, action-item ticks. Stored with the call (same retention, D17). This is the quantitative half of "reps want to keep it" — asked-rate and dismissed-rate per suggestion are the L0/L1 tuning signals for gate strictness and advisor prompt changes.

## Open items

1. Dark theme (L2) and a **compact mode** — collapsed strip showing only zone 1 for single-monitor reps.
2. Thread-chip click behavior (filter vs. highlight vs. nothing) — decide after watching real L1 usage.
3. Answer history UX beyond the capped scroll.
4. Whether zone 3's "Visa allt" opens inline or as a second column when the window is widened.
5. The idle state later hosts the prioritized worklist (D30): scored customer rows with why-now + suggested angle; clicking a row pre-fills the pre-call card and transitions to pre-call.
