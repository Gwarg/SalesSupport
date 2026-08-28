# SalesSupport — design record

**Status:** agreed 2026-08-24, pre-implementation. This document is the shared understanding reached before writing any code. Change it deliberately, not accidentally.

## 1. What this is

A real-time sales copilot for phone calls made from a Windows PC. It listens to both sides of the call (microphone = rep, speaker loopback = customer), transcribes continuously, and uses AI grounded in the selling company's product catalog to show the rep a glanceable panel: suggested discovery questions, product suggestions with a one-line "why", a live picture of the customer's needs, and answers to questions the rep types silently mid-call. After the call it produces a summary.

Built single-company first but product-ready: at least two companies are planned soon, each with roughly 5,000–10,000 SKUs and extensive product listings. The call copilot is surface #1 of an intended sales assistant platform — executable action points, email handling, prioritized worklists — sharing the same knowledge and customer spine (D30).

## 2. Decision record

Each decision is numbered for reference in future discussions. Rationale is recorded so we don't re-litigate by accident.

### Context and audience

- **D1 — Calls happen on the PC** (Teams/softphone). Audio is captured locally on the rep's machine; no telephony/PBX integration. This is the premise the whole capture design rests on.
- **D2 — Single-tenant now, product-ready later.** One concrete company first, but all product knowledge is swappable data (never baked into prompts or code). No tenant machinery yet.
- **D3 — Catalog shape:** 5,000–10,000 SKUs per company, fairly extensive listings. Too big for context, small enough for a high-quality enriched knowledge layer.

### Product knowledge

- **D4 — "Prelearning" = offline preprocessing, not fine-tuning.** Fine-tuning weights on catalog facts is rejected (hallucination, staleness, per-company retraining). Instead, before any call:
  - an **enrichment pass** where an LLM writes a structured *product card* per product (normalized attributes, who it's for, use cases, differentiators, natural cross-sells);
  - a **structure pass** that builds the category taxonomy and, per category, a *discovery-question map* — the questions that distinguish products within that family;
  - an **in-context catalog map**: ~100–500 product families summarized into a prompt-sized text so the live AI always knows the full shape of what the company sells, and uses search only for SKU-level detail. *"It always knows the map; it searches for the street address."*
- **D5 — Sources:** structured feed (ERP/PIM/e-commerce export) as the backbone; datasheets/PDFs/price lists as enrichment. One import adapter per company; delivery format varies freely — file export or API — via the canonical import model (D29).
- **D6 — Freshness:** nightly snapshot. Prices/availability shown as indicative; the rep quotes exact figures from the ERP they already have open. The product store keeps a slot for a per-company live-lookup tool later.

### Language and transcription

- **D7 — Multilingual from day one:** Swedish + English at launch, more addable via config. Language set is per **installation**; the active language is per **call** (selected or auto-detected within the set, with manual override). Suggested questions render in the call language (they're meant to be read aloud); UI chrome follows installation locale.
- **D8 — STT:** cloud engine for v1 behind an `ITranscriptionEngine` interface so a self-hosted engine can be swapped per customer. Default: Azure AI Speech (Sweden Central) — strong sv/en, ~100 languages by config, real-time partials, custom vocabulary. Speechmatics is the benchmark challenger; the interface decides, not debate. The ingestion pipeline auto-generates the per-company vocabulary boost list (product names, brands).
- **D9 — Audio routing:** client streams audio **directly to the STT service**; the backend only issues short-lived STT tokens. Audio never transits our backend and is never stored anywhere. Self-hosted-STT customers stream to their own endpoint via the same interface.

### Guidance loop

- **D10 — UX paradigm: glanceable copilot panel**, four zones: (1) up to 3 suggested next questions, phrased ready-to-say; (2) up to 3 product suggestions with a one-line why + SKU refs; (3) the live customer picture (extracted needs/constraints, including open conversation threads — see D26); (4) the answer zone for typed queries. No transcript wall, no chat thread.
- **D11 — Update model: utterance-driven with damping.** Analysis fires on finalized utterances (floor of a few seconds between runs). Shown suggestions stay put; asked questions get checked off (the gate sees the rep's channel, so it detects this); new items slide in. Target: suggestion visible within 3–5 s of the triggering statement.
- **D12 — Two-tier model roles.** A small fast **gate** model runs on every utterance: updates the customer picture (including its active threads, D26), detects signals, decides `advice_needed`. The strong **advisor** model runs only when the gate fires or the rep asks. The cheap call shields the expensive one.
- **D13 — Retrieve-then-advise.** The orchestrator runs hybrid retrieval (keyword + vector over the pack) *before* the advisor call — **one search per open thread, in parallel** (searches cost ~50 ms; fanning them is free) — and hands the advisor the top cards per thread. Single model round-trip (~3 s), no agentic search loop (~8 s). Revisit only if quality demands.
- **D14 — LLM: `ILLMProvider` is a hard boundary.** Contract = chat completion + strict JSON-schema output; nothing provider-specific leaks through. Claude API first (gate = Haiku 4.5, advisor = Sonnet 5/Opus 5, EU processing via `inference_geo`; caching/adaptive thinking are internal optimizations of that provider). **Fully local models (vLLM/Ollama, schema-constrained output) are a first-class future target** — some prospective customers require it. Context budgets (e.g. catalog-map size) are per-provider config.
- **D15 — The "ask" lane.** A text input is always visible in the panel. Typed queries fire the advisor directly (gate bypassed) and preempt any in-flight proactive tick — one advisor call in flight, rep wins. Answers are short and glanceable in zone 4, grounded in retrieval + call context. Typed input also flows through the extractor: questions are signals, statements are direct customer-picture corrections — the rep can silently steer the AI's understanding.
- **D16 — Pre-call card.** Optional 10-second entry at call start: customer company, industry, free-text goal. Skippable. This is the seam where CRM data plugs in later (see roadmap) — its free text is a degenerate customer brief (D28).

### Data and privacy

- **D17 — Store transcript + summary, never audio.** Audio: streamed, transcribed, discarded — never written to disk anywhere in the system. Transcripts, customer pictures, summaries, and typed queries are stored with per-installation retention (e.g. 30/90 days) and delete-on-request. Each company owns its own legal/GDPR assessment; the architecture provides the controls (retention, deletion, export, EU processing).
- **D18 — Post-call summary** shows in-app with a copy button in the POC. CRM push comes with the CRM phase.

### Platform and deployment

- **D19 — Stack: .NET end-to-end.** WPF desktop client with NAudio (WASAPI loopback + mic capture), always-on-top panel. ASP.NET Core backend. SignalR between client and backend. C# batch ingestion pipeline. One language, one deploy story.
- **D20 — Orchestrator lives in the backend**, not the client. Keys, knowledge, and storage stay server-side; the client stays thin and **identical across all installations** — for local-model customers, the backend deploys into their infrastructure and swaps providers.
- **D21 — Knowledge pack = versioned build artifact**, not a database server. One self-contained SQLite file per company per version: product cards, taxonomy, discovery-question maps, catalog-map text, FTS + vector indexes, STT vocabulary. Backend loads it in memory. Rollback = load previous file. Onboarding company #2 = new adapter + run pipeline.
- **D22 — Call lifecycle: manual start/stop** button in the POC (explicit, consent-friendly). **Headsets are required** — with open speakers, mic bleed degrades speaker-tagging; echo cancellation is a later problem, not a POC problem.

### POC

- **D23 — POC = L0 then L1.**
  - **L0 — bench rig:** replay harness feeds recorded/scripted transcripts through pipeline + orchestrator + a bare panel view. Iterate on the only genuinely uncertain thing — suggestion quality — in seconds per run, no phone required. The ask lane is included (same advisor path).
  - **L1 — live POC:** WPF client with real capture, STT, pre-call card, panel, post-call summary. One company, 2–3 friendly reps, real calls.
  - **L2 — pilot-ready** (installer/auto-update, auth hardening, retention config, monitoring): only after company #1 commits.
- **D24 — Success bar:** reps use it on real calls for a week or two and *object to having it taken away*. Not a leadership demo, not (yet) sales-impact statistics.
- **D25 — Team & timeline:** mainly Andreas + Claude Code, possibly one more developer. No hard date — quality first; the failure mode to avoid is demoing a mediocre advisor early.

### Amendments

- **D26 (2026-08-24) — Conversation threads, not parallel advisors.** Customers open multiple lines of questioning at once; we handle this with threads as a first-class concept rather than parallel advisor agents. The customer picture carries an `active_threads` list (topic, status open/addressed/parked, salience), maintained by the gate on every utterance. Retrieval fans out per open thread (D13), but **one** advisor call synthesizes: it sees all threads with their retrieved cards plus the panel budget, and *allocates* — which thread gets question slots vs product slots this tick, which gets explicitly parked. Rationale: the panel (3+3 slots, D10) is the bottleneck, not the model — parallel advisors create a merge/rank problem, re-serialize latency through a merger step, multiply cost, and break per-suggestion damping, while cross-thread prioritization is a synthesis judgment that wants one context. Open threads are shown to the rep in zone 3. Truly parallel per-thread agents are deferred to the post-POC "background deep-dive" feature (roadmap #7), which runs outside the tick loop's latency budget.
- **D27 (2026-08-24) — Runtime inference is API or local only; subscription LLMs are not an engine.** The tick loop requires programmatic inference: cloud API models or customer-hosted local models behind `ILLMProvider` (D14). Subscription assistants (Claude Pro/Max, ChatGPT Plus, Copilot) cannot drive it — **mechanically** (the loop is machine-driven at ~100+ model calls per phone call with strict JSON schemas and a 3–5 s latency budget; MCP's reverse "sampling" channel is rarely implemented, human-approval-gated, and carries no latency/schema guarantees) and **contractually** (consumer/seat subscriptions license individual interactive use, not backend inference for a commercial product; their usage caps would throttle real call volume anyway). Unit economics: ~$0.50–1.00 per 30-min call ≈ $100–400 per heavy rep per month — treated as a pass-through input cost in product pricing; the cost dials are gate strictness (the biggest), Haiku-class ticks, and caching. Where a customer wants fixed-cost economics, the answer is local models (a GPU box has zero marginal cost per call), not subscription accounts. Legitimate subscription/MCP roles: Claude Code on a personal subscription for development; the Batch API (50% price) for replay-harness eval sweeps; and post-POC, exposing our own MCP server so customer assistants can query summaries/knowledge (roadmap #8) — MCP fits data-out, not inference-in.
- **D28 (2026-08-24) — Customer brief contract.** Pre-call/CRM customer information enters the system as a **rendered text brief**, never a universal CRM schema (that mapping tarpit is explicitly rejected). Per-CRM adapters (same pattern as the feed adapters, D5) each render: (a) a prose/markdown **body** — company facts, curated recent orders, open items, notes from prior calls — under an adapter-owned **token budget** (~1,500 tokens default, per-provider configurable per D14); curation beats completeness, because the brief rides along in every advisor call and irrelevant context costs money, latency, and model focus; (b) a tiny **typed envelope** for code, not models: CRM customer ID (for phase-2 write-back), display name, locale/currency; (c) **order lines resolved to knowledge-pack product IDs**, making ownership history structural — the advisor can reason about successors and cross-sells, and retrieval can boost related product families before the call starts. The brief is distinct from the customer picture (D26): brief = static per-call input (sits in the cached prompt prefix, near-zero marginal cost), picture = live working state with our fixed schema. At call start an extractor pass seeds the picture from the brief; picture facts carry **provenance** (`crm` / `call` / `rep`) and live-call facts outrank background facts on conflict. The brief snapshot is stored with the transcript under the same retention rules (D17). POC: the pre-call card (D16) is simply a brief with an empty envelope — the orchestrator contract exists from day one, and phase-2 CRM adapters (roadmap #1) start filling it properly.
- **D29 (2026-08-24) — Canonical import model; no translation engine.** Product data arrives in whatever form a company has — CSV/XML/Excel export, REST API, webshop — and a thin **per-company adapter** maps it into one versioned canonical import format: JSONL of raw product records (`external_id, sku, name, category_path_raw, description_raw, attributes_raw, price, currency, availability_raw, doc_refs, relations_raw`). The pipeline consumes only this narrow waist; source formats never leak past the adapter. A generic mapping engine/DSL is explicitly rejected (the integration-platform trap). Adapters are deterministic code with golden-file regression tests, **authored with Claude Code at onboarding time** — the "translation engine" is AI at development time, never machinery at runtime. API-only sources are just a fetch strategy inside the adapter (still nightly snapshot builds, D6); connectors for common platforms (Business Central, Visma, Shopify, …) accumulate into a reusable library naturally. Adapters stay thin because the enrichment pass (D4) absorbs semantic mess from raw fields — under one hard rule: **IDs, SKUs, prices, currency, and availability never pass through an LLM**; they travel deterministically from feed to pack. Two build mechanics lock in with this: **incremental enrichment** (per-record content hash; only changed rows are re-enriched and re-embedded, the previous pack's cards are reused — nightly LLM cost ≈ changed products only) and **validation at the waist** (adapter output checked before enrichment — required fields, price sanity, duplicate IDs, row-count drift vs the previous snapshot; per-row error reports, build fails past a threshold).
- **D30 (2026-08-24) — Platform trajectory: from call copilot to sales assistant platform.** The call copilot is surface #1 of an intended universe of functions — post-call action execution (create offer, draft email, book meeting), inbound email handling, and a prioritized worklist — all sharing one spine: knowledge pack → retrieval → role-based LLM calls → structured artifacts attributed to customers. Principles locked now:
  - **Channel-agnostic interaction model.** Calls, emails, and meetings are interaction kinds flowing through the same extractor → picture → threads → action-items machinery; the picture schema (docs/customer-picture.md) is already channel-neutral. The interaction store carries `interaction_kind` from day one (`'call'` in the POC) so new channels are rows, not migrations.
  - **Customer dossier; CRM remains the system of record.** Our store accumulates AI-derived artifacts keyed by `crm_id` (briefs, pictures, summaries, action points, drafts) and pushes results back to the CRM. We never become the customer-truth store — that would be accidentally building a CRM.
  - **Action points become executable.** Action items gain a `kind` (`offer` | `email` | `meeting` | `follow_up` | `other`), classified by the summarizer; each kind can spawn a drafter workflow via the background deep-dive machinery (D26) — "skapa offert", "utkast till mejl", "boka möte".
  - **Drafter is a named `ILLMProvider` role** beside gate/advisor/summarizer.
  - **Words-not-numbers, platform-wide** (extends D29): in offers, emails, and worklists the LLM writes prose; prices, line items, sums, dates, and scores travel deterministically from ERP/pack/code. The single most important guardrail for the offer feature.
  - **Prioritized worklist.** A nightly batch scores customers from a CRM index snapshot + dossier signals: commitments due (open rep action items), cadence-aware dormancy (quiet relative to the customer's own order rhythm), new prospects without first contact, stalled opportunities, parked threads worth reviving, seasonal windows. Code computes scores with per-installation weights; the LLM annotates only the top slice (why-now + suggested opening per candidate). The panel's idle state renders the list — click a row → brief auto-filled → call — closing the loop worklist → call → artifacts → next worklist. Row outcomes (called/skipped/snoozed) are telemetry that tunes the weights (mirrors D24). The CRM adapter therefore grows two outputs: per-customer briefs (D28) and the customer index snapshot.
  - **Email is a GDPR step-change.** D17's assessment covers call transcripts; reading customer correspondence needs its own per-company legal basis before that phase ships.

- **D31 (2026-08-27) — LLM cost is a first-order constraint; tiered provider strategy.** Measured (runs/, 2026-08-26, Haiku gate + Opus advisor at effort=low): ~$0.08 per 5-turn test call, extrapolating to **~$0.85–0.90 per real 15-min call** (~70 utterances; gate `cached=0` on every call, advisor cost output-dominated) → **~$250/rep/month** at ~300 conversations. That is unaffordable for ~90% of the target market, which **amends D27's "pass-through input cost" stance**: inference cost is now a design constraint on par with quality and latency. Decisions:
  - **Per-installation provider + residency choice** (extends D14/D27): premium tier on Claude (max quality, US cloud); standard tier on flash-class API models or self-hosted; residency (US cloud / EU-hosted / on-prem) is per-customer configuration, not a global stance. The D14 boundary is the mechanism; `Backend:LlmProvider` is the switch.
  - **"Enough quality" is defined empirically, never by price list.** The replay harness + judge + golden corpus (upgraded to real pilot transcripts when they arrive) scores every candidate against the Opus baseline. A model qualifies for the standard tier by clearing the quality floor at target cost — the same method that settled qwen3-vs-gemma3 and Opus effort levels.
  - **One OpenAI-compatible provider is the bench gateway.** A single `ILlmProvider` over the OpenAI chat-completions dialect covers OpenRouter (and through it GLM/DeepSeek/Qwen/…), Gemini-compatible endpoints, and self-hosted vLLM — every cheap-model candidate becomes harness-testable with config, not code.
  - **Cost engineering that pays regardless of provider:** restructure the gate prompt so the stable prefix crosses Haiku's 2048-token cache minimum (measured: 0 cached tokens today — ~80% of gate input is re-billed at full price every tick); advisor output discipline (effort/verbosity — advisor cost is almost entirely output tokens); input diet on transcript tail and picture.
  - **Open:** the actual per-rep-month ceiling — set after pilot pricing conversations. The bench produces the quality-cost curve so that when the ceiling lands, it picks a provider from the curve rather than triggering a rebuild.
  - **Bench round 1 (2026-08-27, runs/20260827-*):** Full corpus on candidates via OpenRouter. **Gemini 3.7 Flash @ reasoning=low** is the standard-tier front-runner: gate 2.5–4.6 s, advisor 4–5 s (faster than Opus), near-golden quality incl. the kylgrossisten rejected-product pivot and pack-grounded spec claims; one soft miss — a generic product card on stålgrossisten's no-fit call where the bar is +p=0 (prompt-hardening candidate). Corpus cost $0.13 vs Claude $0.75, uncached (OpenRouter's Gemini route passes no caching — vendor-direct should cut the ~60% input share). **GLM-5.3-Flash**: excellent quality at full thinking but 20–50 s ticks (reasoning is mandatory on its endpoint; @low still 2–24 s erratic) → fits the latency-free **summarizer** slot, where full-thinking summaries were outstanding at ~$0.001/call. **Disqualified:** Mistral Small 2603 (fastest, but invented words, junk questions, panel churn), Qwen3-30B-A3B (hallucinated product behavior, invented dates); DeepSeek v3.1 mid-pack, erratic latency. Candidate standard config: Gemini gate+advisor @low + GLM-thinking summarizer ≈ **$40–45/rep/month uncached** vs Claude config ~$250; premium tier stays Claude. Still open: vendor-direct latency/caching verification, an EU-resident candidate (Mistral failed quality), judge pass on real pilot transcripts. (Stålgrossisten hardening landed 2026-08-28: advisor rule — no product cards while relevance is disputed or no need confirmed; Gemini re-run hits +p=0 with probe questions intact, snabbtest keeps its cards.)
  - **Bench round 2 (2026-08-27):** No challenger displaced Gemini 3.7 Flash. GPT-5.4-mini competent but 2× Gemini's price with an anglicism slip; Mistral Medium 3.1 clean Swedish but thin pushy discovery + self-contradicting summary (EU slot still open — pursue EU-hosted open weights at vendor-direct stage, not more Mistral); Kimi K2.5 out (19k thinking tokens/snabbtest ≈ Claude-priced, slowest, invented customer acceptance); Gemini 3.1 Flash-Lite gate struck (reasoning burn at lite output prices costs more than the 3.7 gate, no faster). **All-Haiku corpus** (advisor override fix: Haiku-class drops the unsupported effort param): $0.41 corpus ≈ $130–150/rep/month uncached — the only cheap config to hit stålgrossisten's +p=0 exactly, but heavy panel churn (kylgrossisten +q7 −q5 +p6) and it re-pushed rejected X60 twice. **Key finding: FilterProducts is evadable by name variance** ("X60 (frysklassad handskannar)" ≠ name_as_said "X60") — the rejected/owns guard needs normalized/fuzzy matching; affects all providers. Ladder stands: GLM ~$4.5 (latency-broken live, superb summarizer), Gemini config ~$40, all-Haiku ~$130, Claude premium ~$250.

## 3. Runtime architecture

### Audio path (per call)

```
Mic (rep) ──────────┐                    ┌─→ partial/final text ─┐
                    ├─ WASAPI capture ───┤   (per channel)       ├─→ transcript merger (client)
Speaker loopback ───┘   client-side      └── streamed direct     │        speaker-tagged,
(customer)              16 kHz mono          to STT service ─────┘        time-ordered
                                                                              │ utterance events
                                                                              ▼ (SignalR)
                                                                       backend call session
```

Backend issues an STT token at call start; audio goes client→STT directly. Only text reaches our backend.

### Tick loop (per finalized utterance)

```
utterance ─→ GATE (small, fast; every utterance)
              │  updates customer picture, detects signals
              ├─ advice_needed = no  ─→ picture update only ─→ small panel delta
              └─ advice_needed = yes ─→ retrieval per open thread (parallel, hybrid, ~50 ms)
                                        └─→ ADVISOR (one call; allocates panel budget across threads)
                                             └─→ panel delta: questions, products+why, picture+threads
rep typed query ─→ (bypasses gate, preempts in-flight tick) ─→ retrieval ─→ ADVISOR ─→ answer zone
call end ─→ summarizer ─→ summary + next steps (stored, shown, copyable)
```

### Model call contracts (sketch — final schemas live in code)

| | Gate | Advisor |
|---|---|---|
| Fires | every finalized utterance | gate says yes, or rep asks |
| Model (Claude provider) | Haiku 4.5 | Sonnet 5 / Opus 5 |
| Input | customer picture incl. threads (JSON), last ~10 utterances, new utterance | picture with active threads, customer brief (D28), rolling summary + recent window, catalog map, top-k product cards **per open thread**, (typed query) |
| Output (strict JSON) | picture diff, thread updates (open/addressed/parked, salience), signals, `advice_needed` + topics, `language_flag` | desired panel state (≤3 questions + ≤3 products, id-reuse for stability), thread re-prioritization, `answer` (on-demand mode) |
| Latency budget | 0.5–1 s | 1.5–3 s |

**End-to-end latency target:** statement → panel change in 3–5 s (STT finalization 0.5–1 s + gate 0.5–1 s + retrieval ~50 ms + advisor 1.5–3 s).

*Measured (2026-08-26, live Claude API):* the Haiku 4.5 gate runs 2–4 s and the Opus 5 advisor 7–9 s — and the advisor floor is generation-bound, not config-bound (Sonnet 5 was no faster; effort low saves ~15%). Real statement→panel is therefore ~10–14 s today. The target stands as the goal; closing the gap is panel-UX work (streaming/staged arrival, visible thinking state) and future model speed — not something to buy with quality downgrades. Also measured: Haiku prompt caching needs a ≥2048-token prefix (our gate system is ~1250, so gate calls do not cache; advisor's catalog block caches correctly).

**Cost estimate:** with prompt caching over transcript + catalog map, roughly $0.50–1.00 per 30-minute call at current list prices, dominated by advisor calls — which is why the gate exists.

### Interfaces (the seams that make this product-ready)

- `ITranscriptionEngine` — streaming audio in, timestamped partial/final utterances out. Implementations: Azure Speech (v1), self-hosted (later).
- `ILLMProvider` — messages + JSON schema in, validated JSON out. Implementations: Claude API (v1), local vLLM/Ollama (later). Per-provider context budgets.
- **Customer picture** — the live working state of a call; schema, gate diff protocol, and merge rules in [docs/customer-picture.md](docs/customer-picture.md).
- **Prompts** — gate, advisor (proactive + on-demand), seeder, and summarizer drafts with input assembly and caching layout in [docs/prompts.md](docs/prompts.md).
- **Panel** — window states, zone specs, motion/damping rules, degradation states, and interaction telemetry in [docs/panel.md](docs/panel.md).
- **Knowledge pack format** — versioned SQLite artifact (see D21); full DDL, retrieval flow, and load contract in [docs/knowledge-pack.md](docs/knowledge-pack.md).
- **SignalR messages** — `TranscriptAppended`, `PictureUpdated`, `PanelDelta`, `AnswerReady`, `SummaryReady` (client←backend); `StartCall`, `EndCall`, `PreCallCard`, `UtteranceEvent`, `RepQuery` (client→backend).

## 4. Offline pipeline (per company, nightly/weekly)

```
feed / API + documents → per-company adapter → canonical import (JSONL, D29)
  → validation at the waist → enrichment pass (LLM → product cards, changed rows only)
  → structure pass (taxonomy + discovery-question maps) → embeddings + FTS index
  → versioned knowledge pack → published to backend
```

Uses batch pricing; incremental enrichment (content hashes, D29) makes the nightly LLM cost proportional to changed products only — the full-catalog enrichment happens once per company, not once per night. The pipeline is the reusable product asset — company #2 is an adapter, not a rewrite.

## 5. POC scope

**In (L0):** ingestion pipeline v1 for company #1 · orchestrator with gate/advisor/retrieval · replay harness (recorded/scripted calls in, panel output out; eval sweeps run through the Batch API at 50% cost, D27) · ask lane · bare panel view.

**In (L1):** WPF client — manual start/stop, audio device picker, language pick sv/en, pre-call card, four-zone panel, ask input, post-call summary + copy · thin backend — config, pack serving, STT tokens, LLM routing, storage with retention.

**Out (explicitly):** CRM anything · live price/stock · auto call detection · echo cancellation / speakerphone support · multi-tenant admin UI · local LLM/STT implementations (interfaces only) · call-list prioritization · installer polish, SSO, monitoring (all L2).

## 6. Risks and mitigations

| Risk | Mitigation |
|---|---|
| Suggestion quality is mediocre (the existential risk) | L0 harness exists to iterate on exactly this before anyone sees a demo; discovery-question maps give the advisor precomputed structure |
| Swedish STT accuracy on real call audio | Benchmark Azure vs Speechmatics on recorded Swedish sales calls early; custom vocabulary from the pipeline |
| Panel flicker destroys rep trust | Damping rules in the orchestrator (D11); suggestions are stable objects with lifecycle, not re-renders |
| Latency creep | Per-stage budgets (see table); retrieve-then-advise keeps one model round-trip |
| Mic/speaker crosstalk | Headset required in POC (D22) |
| GDPR/data concerns block adoption | No audio ever stored (D17); EU processing; per-installation retention; backend deployable on-prem |
| Prompt injection via call audio (customer says something manipulative) | Transcript is data, never instructions: structured outputs, no tool execution driven by transcript content, system prompts scoped per role |
| Teams routes audio to unexpected devices | Device picker in client; test loopback against Teams/headset combos early in L1 |

## 7. Roadmap after POC

Phased (D30); each phase rides on the previous one's data.

**Phase A — CRM foundation**
1. **CRM context** — per-CRM adapter renders customer briefs (D28) automatically by contact/number, replacing manual pre-call entry; adapter also produces the customer index snapshot (D30).
2. **CRM write-back** — summaries and action points pushed as CRM activities. This plus A1 establishes the customer dossier.
3. **Live price/stock tool** — per-company ERP/e-com lookup at advisor time (slot exists, D6).

**Phase B — executable action points (the universe begins)**
4. **Drafter workflows** — action-point kinds spawn drafts: offer (words-not-numbers rule, D30), informational email, meeting booking (Graph calendar). Runs on the background deep-dive machinery.
5. **Background deep-dive agents during calls** — a thread spawns an async task (detailed comparison, quote prep) landing in a drill-down view; shares the ask-lane machinery (D26).

**Phase C — inbound channels**
6. **Email channel** — attribute inbound mail to customers (sender → CRM lookup), run it through the extractor/picture machinery as `interaction_kind: 'email'`, draft grounded replies. Requires its own GDPR basis per company (D30) before shipping.

**Phase D — analytics tier**
7. **Prioritized worklist / work orders** (D30) — nightly scoring job + panel idle-state list; team assignment view for managers later.

**Phase E — platform breadth (as needed, any time)**
8. **Self-hosted STT and local LLM providers** — for data-restricted customers (interfaces exist, D8/D14). Named STT candidate for Swedish installations: **KBLab's kb-whisper** (the Swedish National Library's Swedish-fine-tuned Whisper, measurably better than vanilla Whisper on Swedish) in a streaming harness on a GPU box — the fixed-cost economics of D27 applied to STT.
9. **Auto call detection**, echo cancellation, multi-tenant admin, second company onboarding.
10. **MCP server for customer assistants** — expose the dossier and knowledge pack via MCP, data-out only, never an inference engine (D27).
