# CursorBubble

Een Windows-app die een **radiaal glas-menu ("bubbel") rond je muiscursor**
toont via een muisgebaar. In de bubbel kies je een shortcut die een programma
of bestand opent, of een script uitvoert. De layout, transparantie en acties
stel je in de app zelf in.

![De bubbel rond de cursor met glazen segmenten](docs/reference.png)

## Zo werkt het

1. **Houd de rechtermuisknop ingedrukt** en **klik dan met links**.
2. De glazen bubbel verschijnt rond je cursor.
3. **Beweeg** naar een segment en **laat de muisknop los** om die actie uit te
   voeren.
4. Loslaten in het midden ("Annuleren") sluit de bubbel zonder iets te doen.

Een gewone rechtermuisklik (zonder linkerklik erbij) werkt normaal: het
contextmenu verschijnt gewoon zodra je de rechterknop loslaat.

De app draait op de achtergrond met een **icoon in het systeemvak**. Via het
tray-menu open je **Instellingen**, zet je **Met Windows opstarten** aan/uit, of
sluit je de app af.

## Instellingen

Rechtsklik (of dubbelklik) op het tray-icoon → **Instellingen**:

- **Algemeen** — autostart en uitleg.
- **Layout** — buiten-/binnenradius, start-hoek en de ruimte tussen de vakjes.
- **Segmenten** — vakjes toevoegen, bewerken, verwijderen en herordenen. Per
  segment: naam, actie, doel, argumenten en een optioneel icoon.
- **Stijl** — glas-vervaging (acrylic) aan/uit, **animatie bij openen**,
  transparantie, en de glas-, accent- en tekstkleur.

Het instellingenvenster zelf heeft dezelfde glas-look: op **Windows 11** met een
echte acrylic-backdrop, op **Windows 10** een egaal donker thema.

Alles heeft een **live preview**. Instellingen worden bewaard in
`%APPDATA%\CursorBubble\config.json`.

### Soorten acties

| Actie | Doel-veld | Voorbeeld |
|-------|-----------|-----------|
| Openen (bestand/map/URL) | pad, map of URL | `https://google.com`, `%USERPROFILE%\Documents` |
| Programma starten | pad naar `.exe` (+ argumenten) | `calc.exe`, `notepad.exe` |
| Script / commando uitvoeren | `.bat`/`.cmd`/`.ps1` of een commando | `powershell -Command "..."` |

`%VAR%`-omgevingsvariabelen in het doel worden automatisch uitgebreid.

### Script maken met AI

In plaats van zelf een script te schrijven kun je het laten genereren:

1. Vul je **Anthropic API-sleutel** in bij **Instellingen → AI** (aanmaken op
   console.anthropic.com). Kies eventueel een goedkoper model.
2. Ga naar **Segmenten**, kies een vakje en klik **✨ Genereer script met AI…**.
3. Beschrijf in gewone taal wat het script moet doen (bijv. *"maak een back-up
   van mijn documenten naar D:\Backups"*).
4. Claude schrijft een PowerShell-script. **Lees het door**, en klik dan
   **Gebruiken** — het wordt opgeslagen in
   `%APPDATA%\CursorBubble\scripts\` en aan het vakje gekoppeld.

Het script wordt nooit automatisch uitgevoerd; het draait pas als je dat vakje
in de bubbel kiest. De API-sleutel wordt **versleuteld** opgeslagen in
`config.json` met de Windows Data Protection API (DPAPI) — alleen jouw
Windows-account op deze pc kan hem ontsleutelen. Elke generatie kost een kleine
hoeveelheid API-tegoed.

## Claude Code-inbox

Werk je met meerdere Claude Code-sessies (in VS Code of losse terminals)? Dan
kun je ze vanuit de bubbel beantwoorden.

1. **Instellingen → Claude Code → Koppel Claude Code.** Dit voegt `Stop`- en
   `Notification`-hooks toe aan je `~/.claude/settings.json`. Herstart lopende
   sessies zodat de hooks actief worden.
2. Zodra een sessie **stopt** of **een vraag heeft**, verschijnt er een
   tray-melding en een **badge** met het aantal op het "Claude Code"-vakje in de
   bubbel.
3. Kies dat vakje → er opent een glazen venster met de openstaande sessies.
   Bovenin de vraag/status + context, eronder een **typeveld**.
4. Typ je antwoord → **Verstuur** → CursorBubble zet het op het klembord, brengt
   het juiste venster naar voren en plakt + verstuurt het (Enter).

**Hoe het werkt / beperkingen:**
- Detectie loopt via Claude Code-hooks; de hook geeft de laatste boodschap
  (`last_assistant_message`) of de notificatie rechtstreeks mee — het transcript
  wordt niet geparsed (dat formaat is onstabiel).
- Antwoorden terugsturen wordt door Claude Code **niet officieel ondersteund**;
  CursorBubble simuleert toetsaanslagen in het bijbehorende venster. Het juiste
  venster wordt bepaald via het ouder-proces van de sessie (met terugval op de
  projectmap-naam in de venstertitel, alleen bij een **eenduidige** match op een
  terminal-/editorvenster).
- Er wordt **nooit blind getypt**: pas als het doelvenster daadwerkelijk op de
  voorgrond staat, gaan Ctrl+V en Enter eruit. Lukt dat niet, dan blijft de
  sessie openstaan en staat je antwoord op het klembord om zelf te plakken —
  zo belandt een Enter nooit in een verkeerd venster.
- Je vorige klembord-inhoud wordt na een geslaagde verzending teruggezet.
- Een korte focuswissel naar het doelvenster is zichtbaar.
- Gestructureerde keuzevragen komen als tekst binnen; je typt je keuze.

## Kant-en-klare .exe downloaden

Bij elke commit bouwt GitHub Actions de app op een echte Windows-runner en zet
er een **self-contained `CursorBubble.exe`** klaar (geen .NET-installatie nodig):

GitHub → tabblad **Actions** → de bovenste (groene) **Build**-run →
onderaan bij **Artifacts** → **CursorBubble** downloaden en uitpakken.

## Bouwen en draaien

> Vereist **Windows 10/11** en de **.NET 8 SDK** (WPF bouwt alleen op Windows).

```powershell
# In de map met CursorBubble.sln
dotnet run --project src/CursorBubble
```

Een losse, zelfstandige `.exe` maken:

```powershell
dotnet publish src/CursorBubble -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true
```

De `.exe` staat daarna in
`src/CursorBubble/bin/Release/net8.0-windows/win-x64/publish/`.

## Techniek

- **C# / WPF (.NET 8)** — native Windows-overlay.
- **Globale muishook** (`WH_MOUSE_LL`) herkent het gebaar en onderdrukt het
  contextmenu; een gewone rechtsklik wordt opnieuw afgespeeld met `SendInput`.
- **Transparant, top-most, klik-transparant overlay-venster**; hit-testing loopt
  via de globale cursorpositie, dus een vastgehouden muisknop is geen probleem.
- **Frosted glass** via `DwmEnableBlurBehindWindow` met een cirkelvormige regio,
  zodat alleen de cirkel het bureaublad vervaagt.
- **DPI-bewust** (Per-Monitor v2); de bubbel wordt in fysieke pixels op de
  cursor geplaatst.

### Bekende beperkingen

- De blur achter de bubbel gebruikt `DwmEnableBlurBehindWindow`. Op sommige
  Windows-versies is die blur subtiel of uitgeschakeld; zet in dat geval
  **Glas-vervaging (acrylic)** uit voor een egale doorzichtige cirkel. De bubbel
  blijft altijd zichtbaar als translucent glas.
- Terwijl de app draait wordt elke rechtermuisklik heel kort vastgehouden en bij
  loslaten opnieuw afgespeeld (nodig om het gebaar te kunnen detecteren).
- Rechts-slepen (met de rechterknop ingedrukt slepen) wordt niet doorgegeven.

## Projectstructuur

```
src/CursorBubble/
  App.xaml(.cs)            start, tray + hook
  Native/                  P/Invoke, muishook + gebaar, blur-helper
  Overlay/                 transparant venster + radiale glas-tekening
  Settings/                instellingenvenster met live preview + AI-dialoog
  Ai/                      scriptgeneratie via de Claude-API + opslag
  ClaudeCode/              hook-handler, inbox-opslag, hook-installer
  Responder/               glazen venster om sessies te beantwoorden
  Config/                  model + JSON-opslag
  Actions/                 acties uitvoeren
  Tray/                    systeemvak-icoon + autostart
```
