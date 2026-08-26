# Mic-test script (solo run)

Ett manus att läsa in i micken för ett meningsfullt ensamtest: du spelar säljaren som
"aktivt lyssnar" och återberättar vad den (tysta) kunden säger — så bär varje replik
extraherbar information trots att kundkanalen är tom.

**Före start:** fyll i förkortet — Kund: `Nordfrys AB`, Mål: `följa upp skannerproblem i frysen`
(zon 3 ska då vara ifylld redan innan första repliken).

**Läs en rad i taget. Pausa 2–3 sekunder mellan raderna** så STT hinner slutföra varje
yttrande som en egen tur. Tala naturligt, inte överdrivet tydligt.

| # | Säg | Förvänta dig |
|---|---|---|
| 1 | "Hej, det är Andreas från Duab — kul att du hade tid." | Inget (smalltalk — gaten ska INTE trigga) |
| 2 | "Om jag förstod dig rätt så dör batterierna på era X40-skannrar inne i frysen." | Fakta + tråd om batteriproblem; troligen första rådgivarkörningen → X60 eller batteripaket i panelen |
| 3 | "Och ni har tolv stycken X40 i frysdelen idag, varav två redan är utbytta i år." | Fakta med antal; ev. X40 som "owns" i kundbilden |
| 4 | "Du sa att det är värst tidiga mornar, mellan fem och nio, när plocket är som störst." | Fakta (situation) — ingen ny rådgivning behövs |
| 5 | "Så frågan är om ni ska uppgradera till X60 eller köra arktiska batteripaket i de gamla." | Produktnämningar; rådgivaren kan uppdatera förslag |
| 6 | "Och du nämnde att ni helst vill ha det löst före högsäsongen i november." | Timeline-fakta + köpsignal |
| 7 | "Men du vill inte behöva byta ut alla laddstationer igen, det förstår jag." | Invändningstråd (objection) — chip i kundbilden, ev. LP-dock-relaterat svar |
| 8 | "Din chef undrade också om LP-200-skrivaren klarar de nya märkningskraven för djupfryst." | Ny tråd (märkning); rådgivarkörning → LP-200 i panelen |
| 9 | "Då gör vi så här: jag skickar ett datablad på X60 och en offert på batteripaketet idag." | Två åtaganden (action items med ☐ i kundbilden) |
| 10 | "Och så bokar jag in ett uppföljningssamtal nästa vecka. Tack för idag!" | Tredje åtagandet; därefter Avsluta → sammanfattningen ska nämna det mesta ovan |

**Efteråt, kolla:**
1. Kundbilden: batterifakta, tolv X40, november-timeline, invändningstråd, märkningstråd, 3 åtaganden.
2. Panelen någon gång under samtalet: X60/batteripaket-förslag, ev. LP-200.
3. Sammanfattningen: kort (max ~10 meningar — inte en loop), nämner åtagandena.
4. Loggen (`src/SalesSupport.Backend/logs/backend-*.log`): tick-rader med queue/gate/advisor-tider
   och `Ollama:`-rader med prompt/gen-token-hastighet.
5. `ollama ps` under samtalet: modellen ska ligga **100% GPU**.

Kundkanalen (loopback) är tyst i detta test — det är väntat. Vill du testa båda kanalerna:
spela upp valfritt svenskt tal i hörlurarna samtidigt, så transkriberas det som [customer].
