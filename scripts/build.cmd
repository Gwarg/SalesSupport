@echo off
rem Builds everything (backend, client, tools, tests). Stop a running backend/client first - they lock the binaries.
cd /d "%~dp0.."
dotnet build SalesSupport.slnx --nologo
