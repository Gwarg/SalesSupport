@echo off
rem Fakes an incoming Telavox call to the running backend so the panel shows the banner and pre-fills Kund.
rem   ring                 rings from +4685550101 (Nordfrys AB in the demo customer index)
rem   ring 0705550000      rings from any number (unresolved numbers show as just the number)
rem The demo index is samples\crm\customers.jsonl - copy it to src\SalesSupport.Backend\data\customers.jsonl once.
set NUMBER=%~1
if "%NUMBER%"=="" set NUMBER=+4685550101
curl -s -o nul -w "backend answered HTTP %%{http_code}\n" "http://localhost:5155/api/telephony/telavox/ring?rep=%USERNAME%&caller=%NUMBER%"
