@echo off
rem Runs the unit tests (needs the backend stopped - the test project references it).
cd /d "%~dp0.."
dotnet test tests\SalesSupport.Core.Tests\SalesSupport.Core.Tests.csproj --nologo
