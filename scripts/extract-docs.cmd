@echo off
rem Merges the cached brochure extractions in testdata\.extract-cache into the canonical catalog - NO paid calls.
rem A brochure that is not in the cache is reported as missing. Only add --allow-api with a decided budget:
rem   extract-docs --allow-api --only "Brochure X.pdf"     extracts one uncached brochure on Opus (about $0.20-0.90 each)
rem Then rebuild the pack: scripts\pack-testpower.cmd
cd /d "%~dp0.."
dotnet run --project tools\SalesSupport.DocExtract -- --input testdata\Yokogawa --out samples\catalog\testpower-yokogawa.canonical.jsonl %*
