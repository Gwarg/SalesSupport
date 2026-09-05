@echo off
rem Starts the panel (WPF client). It connects to the backend URL shown on the pre-call card (default http://localhost:5155).
cd /d "%~dp0..\src\SalesSupport.Client"
dotnet run
