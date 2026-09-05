# SalesSupport

Real-time AI sales copilot for PC-based phone calls: listens to both sides, transcribes continuously, and guides the rep with discovery questions and product suggestions grounded in the company's catalog.

See [DESIGN.md](DESIGN.md) for the agreed architecture, decision record, and POC plan; contract drill-downs live in [docs/](docs/).

## L0 bench rig

Requires the .NET 10 SDK.

```
dotnet build SalesSupport.slnx        # build everything
dotnet test SalesSupport.slnx         # merge-rule tests
dotnet run --project tools/SalesSupport.ReplayHarness   # replay a scripted call end-to-end
```

The replay harness runs recorded/scripted calls (samples/calls/*.jsonl) through the full
gate → merge → retrieval → advisor → panel loop — no audio required. Three model modes:

```
dotnet run --project tools/SalesSupport.ReplayHarness                # fake heuristics (plumbing only)
dotnet run --project tools/SalesSupport.ReplayHarness -- --fixtures  # Claude-authored golden responses, zero API cost
dotnet run --project tools/SalesSupport.ReplayHarness -- --ollama    # local models via Ollama (free real inference)
dotnet run --project tools/SalesSupport.ReplayHarness -- --live      # real Claude API (needs ANTHROPIC_API_KEY)
dotnet run --project tools/SalesSupport.ReplayHarness -- --compat    # any OpenAI-compatible endpoint (D31 bench)
```

**Cheap-model bench (D31)** — `--compat` drives any OpenAI-chat-completions endpoint:
OpenRouter (one key, most cheap models), Gemini-compat, or self-hosted vLLM. Configure
via env or flags, then run the same corpus/quick modes as every other backend:

```
set OPENAI_COMPAT_BASE_URL=https://openrouter.ai/api/v1
set OPENAI_COMPAT_API_KEY=sk-or-...
dotnet run --project tools/SalesSupport.ReplayHarness -- --quick --compat-model z-ai/glm-5.3-flash
dotnet run --project tools/SalesSupport.ReplayHarness -- --all --compat-model deepseek/deepseek-chat
```

Output shape uses `response_format: json_schema` (strict); add `--compat-loose` for
endpoints that reject it (json_object + schema in the prompt). Token usage lands in the
runs/ log exactly like `--live`, so cost columns are directly comparable. The backend
runs the same provider with `Backend:LlmProvider = "openai-compat"` plus
`OpenAiCompatBaseUrl`/`OpenAiCompatModel` (key via the env var named in
`OpenAiCompatApiKeyEnv`, default `OPENAI_COMPAT_API_KEY`).

Ollama mode (D14/D27 local path): install from ollama.com, `ollama pull qwen3:8b`,
then run with `--ollama` (optionally `--all --ollama` to run the whole corpus with real
local inference and compare against the fixture goldens). Output shape is enforced by
passing the same JSON schemas as Ollama's `format` parameter.

Fixtures live in samples/fixtures/*.fixtures.json — authored during Claude Code sessions
(subscription-funded development, D27) and doubling as the golden corpus that live runs
are diffed against. Add `--dump <dir>` to any mode to write every prompt and response
to files. Run the whole corpus as a regression suite with:

```
dotnet run --project tools/SalesSupport.ReplayHarness -- --all
dotnet run --project tools/SalesSupport.ReplayHarness -- --quick --ollama   # ~1 min smoke: 5 ticks, 1 advisor fire
```

Sample format: JSONL with an optional first meta line `{"language":"en","customer":"…"}`,
then `{"speaker":"rep|customer","text":"…"}` utterances and `{"ask":"…"}` for typed
rep queries mid-call. Corpus scenarios: happy-path discovery (nordfrys), multi-thread
allocation/parking (vaxholm), rejected-product stance + budget pivot (kylgrossisten),
English call (danfrost), and a no-fit call (stålgrossisten) where the required behavior is no product push — live Opus legitimately probes for alternate fit and future timing there, which the fixture's total silence does not capture; judge that call on +p=0, not on advisor fires.

## Document-source product data — DocExtract (D33)

When a company's catalog arrives as manufacturer brochures instead of a feed
(Test Power: 48 Yokogawa PDFs), the D29 adapter is an LLM extraction at development
time. `tools/SalesSupport.DocExtract` reads each PDF's text layer (PdfPig), asks Opus
for every orderable product under a strict schema — instruments, options, modules,
accessories, software, with typed relations to their base model — and then the code
takes over: every model code must appear verbatim in the source or the row is dropped,
duplicates across brochures merge (longest description wins, relations/aliases/doc refs
union), relations to unextracted targets are pruned, and the result is validated at the
waist before it is written as canonical JSONL.

```
dotnet run --project tools/SalesSupport.DocExtract -- --input testdata/Yokogawa --dry-run
dotnet run --project tools/SalesSupport.DocExtract -- --input testdata/Yokogawa --only WT5000.pdf --out runs/extract/wt5000.jsonl
dotnet run --project tools/SalesSupport.DocExtract -- --input testdata/Yokogawa --out samples/catalog/testpower-yokogawa.canonical.jsonl
dotnet run --project src/SalesSupport.Pipeline -- --input samples/catalog/testpower-yokogawa.canonical.jsonl --company testpower
```

**No paid calls by default.** The tool only merges what is in `testdata/.extract-cache`
(one JSON per document content hash); missing brochures are reported, not fetched. Cache
files can be authored in a Claude Code session on the subscription — the zero-cost
development-time path (D27/D33); the DLM oscilloscope family was done this way and its
generator is kept in `tools/SalesSupport.DocExtract/authored/` (run it with the cache
file name the dry-run prints for that brochure). `--allow-api` enables Opus calls for uncached brochures
and is meant for an explicit, cost-stated go-ahead; re-runs then only pay for changed
brochures (new editions) or a bumped prompt version. Option codes are qualified by host
(`WT5000/G7`) because the same code means different things on different instruments. The
`.report.txt` beside the output lists dropped codes, pruned relations, per-family
counts, the extractor's own notes, and token cost. Prices and availability are never
extracted from documents — they stay null until a structured price list is merged by
model code. `testdata/` (customer-supplied source material) is gitignored; the derived
canonical JSONL is committed.

## Operating guide — the scripts folder

One command per task; each works from any directory (double-click or run from cmd/PowerShell).
Run `scripts\env-check.cmd` first whenever something does not start.

| Script | What it does |
|---|---|
| `env-check.cmd` | Shows .NET version, which API keys are set, packs built, backend config, whether backend/Ollama are running |
| `build.cmd` / `test.cmd` | Build everything / run the unit tests (stop the backend first — it locks binaries) |
| `backend.cmd` | Start the backend on http://localhost:5155 (config in `src\SalesSupport.Backend\appsettings.json`) |
| `client.cmd` | Start the panel |
| `record-demo.cmd` | Backend in **record** mode + client: play the call once live, every model response is saved to `samples\recordings\testpower-demo.jsonl` |
| `demo.cmd` | Backend + client in two windows — **replay** mode (zero cost, no keys) when that recording exists, live otherwise |
| `replay.cmd …` | Console replay harness; no arguments prints the modes and examples |
| `replay-testpower.cmd` | The Test Power demo call in the console on Gemini via OpenRouter (paid, cents) |
| `pack-testpower.cmd` / `pack-duab.cmd` | Build a knowledge pack from a canonical catalog (offline, seconds) |
| `extract-docs.cmd` | Merge cached brochure extractions into the canonical catalog — no paid calls unless `--allow-api` |
| `ring.cmd [number]` | Fake an incoming Telavox call so the panel shows the banner |

**Keys and services.** Everything paid or external comes from environment variables, set once
with `setx NAME "value"` and visible in terminals opened afterwards:

| Variable | Used by |
|---|---|
| `OPENAI_COMPAT_API_KEY` + `OPENAI_COMPAT_BASE_URL` (`https://openrouter.ai/api/v1`) | OpenRouter: backend `LlmProvider: openai-compat` (Gemini and other bench models), `replay --compat-model` |
| `ANTHROPIC_API_KEY` | Claude: backend `LlmProvider: claude`, `replay --live`, `extract-docs --allow-api` |
| `AZURE_SPEECH_KEY` + `AZURE_SPEECH_REGION` | Live transcription (Källa = Live); not needed for replay |
| *(none)* | Ollama: backend `LlmProvider: ollama`, `replay --ollama` — free, needs the Ollama app running |

**Which provider is active** is one line in `appsettings.json`: `LlmProvider` = `openai-compat`
(current: Gemini 3.7 Flash via OpenRouter), `claude`, or `ollama`. `PackPath` points at
`packs\testpower_demo.pack.sqlite`, the fixed name `pack-testpower.cmd` writes, so rebuilding
the pack never needs a config change. Backend log: `src\SalesSupport.Backend\logs\`;
harness logs: `runs\`.

**Typical days.** Code change → `build.cmd`, `test.cmd`, `replay.cmd --all` (free regression).
Demo → `demo.cmd`. New brochures → put them in `testdata\`, `extract-docs.cmd` (missing ones
are reported; extraction is authored in-session or run with `--allow-api` on a decided
budget), then `pack-testpower.cmd` and restart the backend.

## Demo runbook — Test Power replay in the panel

A scripted Test Power call (`samples/calls/testpower/ev-inverter-lab.jsonl`, ~90 utterances,
~9 minutes at speech-rate replay pacing: an EV-inverter lab needing an 8-channel 12-bit scope, probes,
a power analyzer with motor evaluation, IS8000 sync, two typed asks) plays through the
real backend so the panel fills in live. Per-customer corpora live in subfolders of
`samples/calls/`; the harness `--all` corpus stays the top-level DUAB set.

1. Build the Test Power pack once (offline, seconds): `scripts\pack-testpower.cmd`.
2. `appsettings.json` already points at it (`PackPath` = `packs\testpower_demo.pack.sqlite`,
   `CompanyName` = `"Test Power"`) and runs Gemini 3.7 Flash via OpenRouter
   (`LlmProvider: openai-compat`, ~$0.55 per live replay at list price — the first
   recording measured 1.2 M tokens; the replay itself is free); switch to `"claude"` for the
   premium experience when credits allow (~$0.50–0.80 with the cached gate) or `"ollama"`
   for a free but slow run.
3. Record once: `scripts\record-demo.cmd`, play the call through, wait ~30 s after "uppspelning klar"
   before Avsluta. From then on `scripts\demo.cmd` replays the identical responses at zero cost with
   their recorded pacing — no keys, no network. (`backend.cmd` + `client.cmd` run it live instead.)
   In the pre-call card: Källa = Replay, pick
   `testpower/ev-inverter-lab.jsonl` (Kund pre-fills from the script), open **Logg** for the
   scrolling transcript, press Starta samtal.
4. Watch: discovery questions and product cards arrive as the customer states needs; the
   owned WT1800 never gets re-suggested; the budget objection and the november deadline
   land in the picture; the typed asks answer from the pack; Avsluta after "uppspelning
   klar" produces the summary with the offer, demo-unit and trial-link commitments.

The recording is keyed by the exact prompt, so it replays cleanly as long as the script and
the pack are unchanged; a prompt that was never recorded degrades to a neutral response
(empty tick) and is logged as a replay miss rather than failing. Never run real calls in
replay mode — it is a per-launch switch (`--Backend:Recording=replay`), not a config value.

## Capture spike (L1)

Prove dual-channel audio capture on real hardware — mic + speaker loopback (D1), device
picker with communications-role defaults (what Teams uses), conversion to the 16 kHz mono
PCM that STT consumes, zero-fill during loopback silence:

```
dotnet run --project tools/SalesSupport.CaptureSpike -- --list
```

```
dotnet run --project tools/SalesSupport.CaptureSpike -- --seconds 8
```

Speak and play something through the speakers during the run, then listen to
`captures/mic_16k.wav` and `captures/loopback_16k.wav` (gitignored). Select devices with
`--mic <index|name>` / `--speaker <index|name>`.

## Transcription spike (L1)

Azure AI Speech behind `ITranscriptionEngine` (D8). Needs a Speech resource — the free F0
tier works (5 audio-hours/month; create in the Azure portal, e.g. region `swedencentral`) —
and `AZURE_SPEECH_KEY` + `AZURE_SPEECH_REGION` set. Transcribe a recorded capture:

```
dotnet run --project tools/SalesSupport.TranscribeSpike -- --wav captures/mic_16k.wav --language sv
```

Or live dual-channel (mic = rep, loopback = customer), with product-name phrase hints
from a knowledge pack:

```
dotnet run --project tools/SalesSupport.TranscribeSpike -- --live --seconds 20 --pack packs/duab-demo_demo-e5.pack.sqlite
```

Partials render as an overwriting status line, finals as timestamped `[rep]`/`[customer]`
lines. Note: F0 may reject the second concurrent live session (tier limit, not a bug);
WAV mode transcribes one channel at a time and always works on F0.

Two engines behind `ITranscriptionEngine` (D8: the interface decides, not debate):
`--engine azure` (default; `AZURE_SPEECH_KEY` + `AZURE_SPEECH_REGION`) or
`--engine speechmatics` (`SPEECHMATICS_API_KEY`, free tier at portal.speechmatics.com;
optional `SPEECHMATICS_RT_URL` to override the EU endpoint). Benchmark by running the
same WAV through both:

```
dotnet run --project tools/SalesSupport.TranscribeSpike -- --wav captures/mic_16k.wav --engine speechmatics
```

## Knowledge pack pipeline

Build a knowledge pack (docs/knowledge-pack.md) from a canonical import file (D29):

```
dotnet run --project src/SalesSupport.Pipeline -- --input samples/catalog/duab-demo.canonical.jsonl --company duab-demo --version demo
```

Stages: validation at the waist → family taxonomy from category paths → template product
cards + question maps (LLM enrichment slots in later) → embeddings → versioned SQLite pack
with FTS5 + vector indexes, aliases, relations, catalog map, and STT vocabulary. Packs land
in `packs/` (gitignored). Two embedders behind `IEmbedder`: the real one is
`multilingual-e5-small` via ONNX Runtime (semantic + cross-lingual, `--embedder e5`);
`hashing-trigram-v1` remains as the zero-download fallback. Fetch the model files once
(~120 MB, into gitignored `models/`):

```
dotnet run --project src/SalesSupport.Pipeline -- fetch-model
```

The harness auto-selects the matching embedder from pack meta. Retrieval quality is
asserted by RetrievalEvalTests (realistic thread-topic queries + a cross-lingual case);
those tests soft-skip when the model files are absent.

Run the harness against a real pack instead of the in-memory stub:

```
dotnet run --project tools/SalesSupport.ReplayHarness -- --all --pack packs/duab-demo_demo.pack.sqlite
```
## Full-loop live demo (L1)

The complete system on real speech: capture → STT → merger → orchestrator → panel output,
in a console. Needs an STT key (Azure or Speechmatics) and a model backend — Ollama for
free local inference, `claude` when API credits exist, `fake` for plumbing checks:

```
dotnet run --project tools/SalesSupport.LiveDemo
```

Talk (and play customer-side audio); finals tick the orchestrator and panel deltas print
with real gate/advisor latency per tick. Enter ends the call → interactive ask lane →
post-call summary, final panel, threads, and customer picture. `--wav-mic captures/mic_16k.wav`
replays a recorded channel instead of live capture — the deterministic first run.

## Backend (L1)

The thin ASP.NET Core backend (D20): hosts the orchestrator per call, serves the SignalR
contract (`/hub/call`: StartCall → Utterance/Ask → EndCall; events TranscriptAppended,
PictureUpdated, PanelDelta, TickCompleted, AnswerReady, SummaryReady), issues short-lived
Azure STT tokens (`/api/stt-token`, D9 — model and speech keys never reach the desktop),
and stores interactions (transcript + picture + summary, never audio) in SQLite with
`interaction_kind` and retention purging (D17/D30). StartCall carries the pre-call card;
a non-empty card seeds the customer picture via the seeder prompt (D16/D28).

```
dotnet run --project src/SalesSupport.Backend
```

Requires a knowledge pack (newest in `packs/` by default) and a model backend per
`appsettings.json` (`Backend:LlmProvider`: `ollama` | `claude`). Health: `GET /healthz`.

## Panel client (L1)

The WPF copilot panel (docs/panel.md): always-on-top, three states — pre-call card
(devices, language, customer + goal), the live four-zone panel (questions with check-off
and fresh accents, product suggestions, kundbild with thread chips/facts/action items,
ask lane as a scrollable chat of typed questions and answers above the input, D34), and post-call summary with next steps, the chat, + copy. Status strip
shows live mic/speaker meters, call timer, advisor activity, and per-tick latency.

```
dotnet run --project src/SalesSupport.Backend
```

```
dotnet run --project src/SalesSupport.Client
```

The client captures locally, streams audio to Azure STT with the token issued by the
backend (keys never reach the desktop, D9), merges channels client-side, and sends only
text up the SignalR hub. Requires the backend running with a pack + LLM (Ollama/Claude)
and an Azure Speech key on the backend (or AZURE_SPEECH_KEY locally as fallback).

**Replay in the panel** — the pre-call card has a Källa selector: `Live` (capture + STT)
or `Replay`, which plays a `samples/calls/*.jsonl` script through the real hub at
conversation pace (partials, finals, `ask` lines) so the panel fills in exactly as in a
live call — no mic, no STT key. Language/customer come from the sample's meta line when
present. The backend runs its real provider, so replay costs cents on Claude and is free
on Ollama. Press Avsluta after "uppspelning klar" for the summary.

**Transcript log** — the Logg button (live strip) or Transkript (post-call) opens a
side window with the running conversation: every backend-confirmed utterance
(color-coded rep/customer), typed asks (⌨), and the in-flight partial at the bottom.
Follows the tail unless you scroll up; clears on Nytt samtal.

**Themes (D35)** — the panel's whole look is a theme dictionary under
`src/SalesSupport.Client/Themes/` (one file per theme, identical `Theme.*` keys; the views
only use DynamicResource). `Control room` (dark, the direction chosen on the design canvas)
is the default; `Calm instrument` is the warm-light look. Pick one under **Tema** on the
pre-call card — it switches live and persists in `%LOCALAPPDATA%\SalesSupport\client.json`.
A customer's brand is a new theme file, never a fork of the views. Space Grotesk and IBM Plex
Mono ship with the app as resources (`src/SalesSupport.Client/Fonts/`, SIL OFL, licenses
beside the files); the light theme's Plex Sans is not shipped, so Segoe UI stands in there.

## Incoming calls — Telavox (D32)

One telephony contract, a thin adapter per provider. Telavox is the reference: its
Personal Webhooks POST/GET a user-configured URL on "ringing" with `{system.caller}`
substituted in. The backend resolves the number against the installation's customer
index, and the rep's panel gets an `IncomingCall` notice — banner plus a pre-filled
Kund field on the pre-call card, one click to start as the rep answers.

Setup per rep: the pre-call card shows the exact URL to paste into Telavox
(`…/api/telephony/telavox/ring?rep=<windows user>&caller={system.caller}`); the host
must be reachable from Telavox's cloud (server-hosted backend or a tunnel). Without a
`rep` parameter the notice broadcasts to every connected panel. Set
`Backend:TelephonyWebhookSecret` and append `&token=<secret>` to the URL outside
development.

Customer index: `Backend:CustomerIndexPath` (default `data/customers.jsonl` under the
backend) — JSONL rows `{"phone","company","crm_id","notes"}`, the snapshot a CRM
adapter (D28/D30) will write later; reloaded on file change. Numbers normalize to
E.164 with `Backend:DefaultCountryCode` (+46), so `08-555 01 01`, `+46 8 555 01 01`
and `0046…` all match. Try it with the demo index and a fake ring:

```
copy samples\crm\customers.jsonl src\SalesSupport.Backend\data\customers.jsonl
curl "http://localhost:5155/api/telephony/telavox/ring?rep=%USERNAME%&caller=%2B46855501 01"
```
