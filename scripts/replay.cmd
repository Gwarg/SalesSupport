@echo off
rem Console replay harness (no UI). Runs a scripted call through gate -> merge -> retrieval -> advisor and logs to runs\.
cd /d "%~dp0.."
if "%~1"=="" (
  echo Usage: replay [call.jsonl] [--quick ^| --all] [mode] [--pack packs\NAME.pack.sqlite] [--map compact^|full]
  echo.
  echo   Modes  ^(default: fake heuristics, plumbing only^):
  echo     --fixtures      golden responses from samples\fixtures - free
  echo     --ollama        local qwen3:8b via Ollama - free, slow
  echo     --live          Claude API - paid ^(ANTHROPIC_API_KEY^)
  echo     --compat-model google/gemini-3.7-flash --compat-reasoning low   OpenRouter - paid, cents ^(OPENAI_COMPAT_API_KEY^)
  echo.
  echo   Examples:
  echo     replay --all                       whole DUAB corpus on fixtures ^(regression, free^)
  echo     replay --quick --ollama            5-tick smoke test on the local model
  echo     replay --quick --compat-model google/gemini-3.7-flash --compat-reasoning low
  echo     replay-testpower                   the Test Power demo call on Gemini ^(see that script^)
  exit /b 0
)
dotnet run --project tools\SalesSupport.ReplayHarness -- %*
