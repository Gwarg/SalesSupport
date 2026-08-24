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
dotnet run --project tools/SalesSupport.ReplayHarness -- --live      # real Claude API (needs ANTHROPIC_API_KEY)
```

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