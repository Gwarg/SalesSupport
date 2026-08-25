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
```

Ollama mode (D14/D27 local path): install from ollama.com, `ollama pull qwen2.5:7b`,
then run with `--ollama` (optionally `--all --ollama` to run the whole corpus with real
local inference and compare against the fixture goldens). Output shape is enforced by
passing the same JSON schemas as Ollama's `format` parameter.

Fixtures live in samples/fixtures/*.fixtures.json — authored during Claude Code sessions
(subscription-funded development, D27) and doubling as the golden corpus that live runs
are diffed against. Add `--dump <dir>` to any mode to write every prompt and response
to files. Run the whole corpus as a regression suite with:

```
dotnet run --project tools/SalesSupport.ReplayHarness -- --all
```

Sample format: JSONL with an optional first meta line `{"language":"en","customer":"…"}`,
then `{"speaker":"rep|customer","text":"…"}` utterances and `{"ask":"…"}` for typed
rep queries mid-call. Corpus scenarios: happy-path discovery (nordfrys), multi-thread
allocation/parking (vaxholm), rejected-product stance + budget pivot (kylgrossisten),
English call (danfrost), and a no-fit call where correct output is nothing (stålgrossisten).

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
