@echo off
rem Checks everything the parts need. Run this first when something does not start.
cd /d "%~dp0.."
echo == .NET SDK ==
dotnet --version
echo.
echo == Environment variables (set with setx NAME "value", then open a NEW terminal) ==
if defined ANTHROPIC_API_KEY (echo   ANTHROPIC_API_KEY      set     - Claude: LlmProvider claude, replay --live, DocExtract --allow-api) else (echo   ANTHROPIC_API_KEY      MISSING - Claude: LlmProvider claude, replay --live)
if defined OPENAI_COMPAT_API_KEY (echo   OPENAI_COMPAT_API_KEY  set     - OpenRouter: LlmProvider openai-compat, replay --compat-model) else (echo   OPENAI_COMPAT_API_KEY  MISSING - OpenRouter: LlmProvider openai-compat, replay --compat-model)
if defined OPENAI_COMPAT_BASE_URL (echo   OPENAI_COMPAT_BASE_URL %OPENAI_COMPAT_BASE_URL%) else (echo   OPENAI_COMPAT_BASE_URL MISSING - should be https://openrouter.ai/api/v1 for replay --compat)
if defined AZURE_SPEECH_KEY (echo   AZURE_SPEECH_KEY       set     - live transcription ^(Kalla = Live in the panel^)) else (echo   AZURE_SPEECH_KEY       MISSING - only needed for live calls, not for replay)
if defined AZURE_SPEECH_REGION (echo   AZURE_SPEECH_REGION    %AZURE_SPEECH_REGION%) else (echo   AZURE_SPEECH_REGION    MISSING - e.g. swedencentral; only needed for live calls)
echo.
echo == Knowledge packs (packs\) ==
dir /b packs\*.pack.sqlite 2>nul || echo   none built yet - run scripts\pack-testpower.cmd
echo.
echo == Backend config (src\SalesSupport.Backend\appsettings.json) ==
findstr /C:"LlmProvider" /C:"PackPath" /C:"CompanyName" /C:"OpenAiCompatModel" src\SalesSupport.Backend\appsettings.json
echo.
echo == Services ==
curl -s -m 2 http://localhost:5155/healthz 2>nul && echo. || echo   backend: not running ^(scripts\backend.cmd^)
curl -s -m 2 -o nul http://localhost:11434/api/tags 2>nul && (echo   ollama: reachable) || (echo   ollama: not running ^(only needed for LlmProvider ollama / replay --ollama^))
echo.
echo Logs: src\SalesSupport.Backend\logs\backend-YYYYMMDD.log   Harness runs: runs\
