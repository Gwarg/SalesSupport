# Customer picture — schema and merge rules

**Status:** draft for review, 2026-08-24. Drill-down of D12, D26, D28. The machine-readable JSON Schema lands in code; this document is its source of truth.

## Role

The customer picture is the live working state of a call: what the system currently believes about the customer. Five consumers:

1. **Gate input** — the gate reads it on every utterance and emits a diff.
2. **Advisor grounding** — the advisor reasons over it (with the brief, D28).
3. **Panel zone 3** — rendered directly to the rep.
4. **Damping** — "did anything meaningful change" is computed from picture diffs.
5. **Post-call summary** — the summarizer's main input; the picture *is* ~80% of the summary.

It is **state, not an event log**: it holds current beliefs, not the history of how they arrived. (Signals, the rolling summary, and suggestion lifecycle live elsewhere — see Non-goals.)

## Design principles

- **Atomic items with stable IDs.** Every entry in every list has an orchestrator-assigned `id`. Stable IDs are what make diffs, damping, and zone-3 partial re-rendering possible.
- **Typed lists over polymorphism.** Separate lists with clear semantics (`facts`, `threads`, `product_interest`, `action_items`) rather than one clever polymorphic collection — small and local models comply far more reliably with flat, boring shapes (D14).
- **Diff protocol, not full rewrites.** The gate emits upserts/removes, never the whole picture. Full-state rewrites cost tokens every tick and invite drift (renumbering, dropped items) that breaks damping.
- **Provenance on everything** (`crm` / `call` / `rep`), with rank **rep > call > crm** on conflict (D28: an explicit typed correction beats what was heard, which beats background data).
- **Token-bounded.** Rendered picture target ≤ ~1,200 tokens; hard caps per list (below).
- **Free text in the call language** (it's shown to the rep and quotes what was said); enums and structure in English (machine-facing).

## Stored schema

Annotated example (running example: Nordfrys AB, mid-call):

```json
{
  "schema_version": 1,
  "company": {
    "name": "Nordfrys AB",
    "industry": "wholesale frozen foods",
    "size_hint": "~180 employees",
    "location_hint": "Malmö, 3 warehouses",
    "source": "crm"
  },
  "facts": [
    {"id": "f1", "category": "situation", "text": "opening a new warehouse in 2027", "source": "crm", "confidence": "high", "turn": 0},
    {"id": "f2", "category": "pain", "text": "X40 batteries drain fast in cold storage", "source": "call", "confidence": "high", "turn": 9},
    {"id": "f3", "category": "stakeholder", "text": "COO Karin decides equipment purchases", "source": "crm", "confidence": "medium", "turn": 0},
    {"id": "f4", "category": "timeline", "text": "wants cold storage fixed before peak season (Nov)", "source": "call", "confidence": "medium", "turn": 12}
  ],
  "threads": [
    {"id": "t1", "topic": "cold storage scanner reliability", "kind": "discovery", "status": "open", "salience": "high", "note": "pain confirmed; device count unknown", "turn": 9},
    {"id": "t2", "topic": "new warehouse fit-out 2027", "kind": "discovery", "status": "parked", "salience": "medium", "note": "mentioned in passing, not explored", "turn": 3},
    {"id": "t3", "topic": "worried new devices need new charging docks", "kind": "objection", "status": "open", "salience": "medium", "note": "cost concern, unhandled", "turn": 14}
  ],
  "product_interest": [
    {"id": "p1", "product_ref": "prod:x40", "name_as_said": "X40", "stance": "owns", "reason": "12 units bought 2026-03; cold storage issues", "source": "crm", "turn": 0},
    {"id": "p2", "product_ref": "prod:x60", "name_as_said": "X60", "stance": "interested", "reason": "asked if it handles -30 °C", "source": "call", "turn": 11}
  ],
  "action_items": [
    {"id": "a1", "text": "send X60 cold-rating datasheet", "owner": "rep", "source": "call", "turn": 13}
  ]
}
```

### Field reference

**`company`** — the only non-list section. All fields optional strings except `name`. Seeded from the brief envelope + body (D28), correctable live.

**`facts[]`** — atomic learned statements.

| Field | Type | Notes |
|---|---|---|
| `id` | string | orchestrator-assigned (`f1`, `f2`, …) |
| `category` | enum | `situation` \| `need` \| `constraint` \| `budget` \| `timeline` \| `stakeholder` \| `pain` \| `preference` \| `other` |
| `text` | string | one statement, call language, ≤ ~20 words |
| `source` | enum | `crm` \| `call` \| `rep` |
| `confidence` | enum | `low` \| `medium` \| `high` |
| `turn` | int | utterance index it was learned at; orchestrator-stamped |

**`threads[]`** — lines of questioning (D26). Objections and unanswered customer questions are threads too (`kind`), so the advisor's budget allocation covers them without a separate mechanism.

| Field | Type | Notes |
|---|---|---|
| `id` | string | `t1`, `t2`, … |
| `topic` | string | short, call language |
| `kind` | enum | `discovery` \| `objection` \| `customer_question` |
| `status` | enum | `open` \| `addressed` \| `parked` |
| `salience` | enum | `low` \| `medium` \| `high` |
| `note` | string | current state of the thread in one line |
| `turn` | int | last touched |

**`product_interest[]`** — products discussed or owned, and the customer's stance. Ownership lives here (not in facts): the brief-seeding pass converts resolved order lines (D28) into `stance: "owns"` entries, and the advisor's rules — don't re-suggest `rejected`, reason about successors to `owns` — read from one place.

| Field | Type | Notes |
|---|---|---|
| `id` | string | `p1`, `p2`, … |
| `product_ref` | string \| null | knowledge-pack ID (`prod:` / `fam:`) when resolved, null when only heard |
| `name_as_said` | string | verbatim-ish name used on the call / in CRM |
| `stance` | enum | `owns` \| `interested` \| `neutral` \| `rejected` |
| `reason` | string | one line |
| `source`, `turn` | | as above |

**`action_items[]`** — commitments made during the call. Feeds the summary's next-steps directly.

| Field | Type | Notes |
|---|---|---|
| `id` | string | `a1`, … |
| `text` | string | the commitment |
| `owner` | enum | `rep` \| `customer` |
| `source`, `turn` | | as above |

## Gate diff schema

The gate never outputs the picture — only changes. Annotated example (utterance 14: *"…helst innan högsäsongen i november, men jag vill inte behöva byta alla laddstationer igen"*):

```json
{
  "signals": [
    {"type": "buying_signal", "note": "timeline stated: before November peak"},
    {"type": "objection_raised", "note": "cost of replacing charging docks"}
  ],
  "company_update": null,
  "facts_upsert": [
    {"id": null, "category": "timeline", "text": "wants cold storage fixed before peak season (Nov)", "source": "call", "confidence": "medium"}
  ],
  "facts_remove": [],
  "threads_upsert": [
    {"id": null, "topic": "worried new devices need new charging docks", "kind": "objection", "status": "open", "salience": "medium", "note": "cost concern, unhandled"}
  ],
  "product_interest_upsert": [],
  "action_items_upsert": [],
  "questions_addressed": [],
  "summary_append": "Wants cold storage solved before November; raised concern about replacing charging docks.",
  "advice": {"needed": true, "reason": "new unhandled objection on active topic", "topics": ["t1", "charging dock compatibility"]},
  "language_flag": null
}
```

Rules:

- `id: null` = new item (orchestrator assigns the real ID on merge); a known `id` = update of that item, with only changed fields required to be meaningful.
- `advice.topics` are **hints, not foreign keys**: thread IDs where they exist, plain topic text for brand-new threads. The orchestrator uses them to seed per-thread retrieval queries (D13); the advisor sees all open threads regardless.
- `signals[].type`: `buying_signal` | `objection_raised` | `question_from_customer` | `topic_shift` | `correction` | `smalltalk`. Signals are **transient events** — logged for gating heuristics and analysis, never stored in the picture. Durable consequences must land as facts/threads in the same diff.
- `questions_addressed`: IDs of active panel suggestions the rep just asked (see Non-goals — suggestions are session state the gate receives as a side input).
- `summary_append`: optional one-liner for the rolling conversation summary (separate log, orchestrator-compacted).
- `language_flag`: null normally; set to the observed language if the transcript clearly isn't the session's call language — surfaces STT misconfiguration to the client for a manual language switch (D7).

## Merge rules (orchestrator, plain code)

1. **IDs and turns**: orchestrator assigns IDs on add and stamps `turn` on every touched item. An upsert referencing an unknown ID is logged and treated as an add.
2. **Provenance guard**: a `call`-sourced diff never overwrites a `rep`-sourced item (explicit corrections via the ask lane, D15, arrive with `source: "rep"` through the same diff shape). `call` may overwrite `crm`. The gate is also prompted to update existing items rather than add near-duplicates.
3. **Caps** (hard backstop; gate is told to consolidate when a list is at cap): facts ≤ 30 · threads ≤ 15 (≤ 8 open) · product_interest ≤ 15 · action_items ≤ 10.
4. **Removal is archival**: removed/consolidated items are tombstoned in session storage (audit, summary quality) but excluded from all rendered prompts.
5. **Prompt rendering**: threads sorted status-then-salience; facts newest-first; if the rendered picture exceeds ~1,200 tokens, lowest-value items (`other`/`preference`, low confidence, oldest) are elided from the prompt while remaining stored.
6. **Damping link**: zone 3 re-renders only items whose content changed; `advice.needed` is still subject to the orchestrator's minimum-interval floor (D11).

## Seeding (call start)

If a brief exists (pre-call card or CRM adapter, D28): one gate-class extraction call converts it into the initial picture — `source: "crm"` (typed card text: `"rep"`), `turn: 0`, order lines → `product_interest` with `stance: "owns"` and resolved `product_ref`s. The rep sees a populated zone 3 before the first ring.

## Non-goals — deliberately NOT in the picture

- **Panel suggestion lifecycle** (shown/asked/dismissed questions and product cards): orchestrator session state. The gate gets active suggestions as a side input and reports `questions_addressed`; the picture stays about the *customer*, not about our UI.
- **Rolling conversation summary**: separate append-log fed by `summary_append`, compacted by the orchestrator.
- **Signals**: transient, logged, not state.
- **Transcript**: separate store (D17).

## Open items

1. Zone-3 display language is the call language (POC); revisit if reps on English calls want Swedish state display.
2. `confidence` must earn its tokens — measure in L0 whether it changes advisor/damping behavior; cut if not.
3. Cap and token-budget numbers are starting guesses; tune against L0 replays.
