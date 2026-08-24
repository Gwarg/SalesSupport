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
gate → merge → retrieval → advisor → panel loop using deterministic fake models and an
in-memory knowledge source — no keys, no audio, no network.