@echo off
rem Demo launcher: backend and client in two windows.
rem If a recording exists (samples\recordings\testpower-demo.jsonl) the backend starts in REPLAY mode:
rem zero cost, no keys needed, recorded responses with their recorded pacing. Otherwise it runs live on the
rem provider in appsettings.json. Then in the panel: Kalla = Replay, pick testpower/ev-inverter-lab.jsonl,
rem open Logg, Starta samtal.
cd /d "%~dp0.."
if exist samples\recordings\testpower-demo.jsonl (
  echo Recording found - backend starts in REPLAY mode ^(zero cost^).
  start "SalesSupport backend (REPLAY)" cmd /k "cd /d src\SalesSupport.Backend && dotnet run -- --Backend:Recording=replay"
) else (
  echo No recording yet - backend starts LIVE on the configured provider. Use scripts\record-demo.cmd to record one.
  start "SalesSupport backend" cmd /k "cd /d src\SalesSupport.Backend && dotnet run"
)
timeout /t 8 /nobreak >nul
start "SalesSupport client" cmd /k "cd /d src\SalesSupport.Client && dotnet run"
