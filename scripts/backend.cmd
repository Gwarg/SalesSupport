@echo off
rem Starts the backend on http://localhost:5155. Config: src\SalesSupport.Backend\appsettings.json
rem (LlmProvider, PackPath, CompanyName). Keys come from environment variables - see env-check.cmd.
rem Log file: src\SalesSupport.Backend\logs\backend-YYYYMMDD.log. Ctrl+C stops it.
cd /d "%~dp0..\src\SalesSupport.Backend"
dotnet run
