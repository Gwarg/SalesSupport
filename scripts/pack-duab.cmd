@echo off
rem Builds the DUAB demo pack (the fictional freezer-scanner catalog the DUAB corpus and fixtures use).
rem Output: packs\duab-demo_demo.pack.sqlite. To run the backend on it, set PackPath and CompanyName in appsettings.json.
cd /d "%~dp0.."
dotnet run --project src\SalesSupport.Pipeline -- --input samples\catalog\duab-demo.canonical.jsonl --company duab-demo --version demo --embedder e5
