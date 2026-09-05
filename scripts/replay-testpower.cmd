@echo off
rem Console rehearsal of the Test Power demo call on Gemini via OpenRouter - PAID, roughly $0.25-0.40 per run.
rem Needs OPENAI_COMPAT_API_KEY and OPENAI_COMPAT_BASE_URL. Extra arguments pass through (e.g. --map full).
cd /d "%~dp0.."
if not exist packs\testpower_demo.pack.sqlite (
  echo Pack missing - run scripts\pack-testpower.cmd first.
  exit /b 1
)
echo Running the Test Power demo call on google/gemini-3.7-flash via OpenRouter ^(paid, cents^). Ctrl+C to abort.
dotnet run --project tools\SalesSupport.ReplayHarness -- --pack packs\testpower_demo.pack.sqlite --compat-model google/gemini-3.7-flash --compat-reasoning low --map compact samples\calls\testpower\ev-inverter-lab.jsonl %*
