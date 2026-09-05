@echo off
rem Demo launcher: backend and client in two windows.
rem Then in the panel: Kalla = Replay, pick testpower/ev-inverter-lab.jsonl, open Logg, Starta samtal.
cd /d "%~dp0.."
start "SalesSupport backend" cmd /k "cd /d src\SalesSupport.Backend && dotnet run"
timeout /t 8 /nobreak >nul
start "SalesSupport client" cmd /k "cd /d src\SalesSupport.Client && dotnet run"
