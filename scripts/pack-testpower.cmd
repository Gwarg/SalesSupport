@echo off
rem Builds the Test Power knowledge pack from the canonical catalog (offline, seconds, no API).
rem Output: packs\testpower_demo.pack.sqlite - the fixed name appsettings.json PackPath points at.
rem Stop the backend first: it holds the pack file open.
cd /d "%~dp0.."
dotnet run --project src\SalesSupport.Pipeline -- --input samples\catalog\testpower-yokogawa.canonical.jsonl --company testpower --version demo --embedder e5
