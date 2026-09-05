@echo off
rem Records the demo so it can be replayed at zero cost.
rem Starts the backend in RECORD mode (live provider from appsettings.json, every response saved to
rem samples\recordings\testpower-demo.jsonl) and the client. Then: Kalla = Replay, pick
rem testpower/ev-inverter-lab.jsonl, open Logg, Starta samtal. After "uppspelning klar", WAIT until the
rem panel has stopped updating (about 30 s) before Avsluta, so the last ticks and the summary are recorded.
rem Cost: one live run on the configured provider (Gemini via OpenRouter: roughly $0.25-0.40).
rem Re-running is free for prompts already recorded. Afterwards scripts\demo.cmd replays for free.
cd /d "%~dp0.."
if not exist samples\recordings mkdir samples\recordings
start "SalesSupport backend (RECORD)" cmd /k "cd /d src\SalesSupport.Backend && dotnet run -- --Backend:Recording=record"
timeout /t 8 /nobreak >nul
start "SalesSupport client" cmd /k "cd /d src\SalesSupport.Client && dotnet run"
