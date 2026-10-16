# mcp-redmine

Lokalny serwer [MCP](https://modelcontextprotocol.io/) (stdio), dzięki któremu agent AI (np. Claude Code) pracuje z **jedną lub wieloma instancjami Redmine** przez standardowe REST API. Bez pluginów po stronie Redmine i bez uprawnień administratora.

- Wiele instancji w jednym serwerze (różne wersje, za VPN-em, z firmowym CA). Każde narzędzie przyjmuje `target`.
- Domyślnie **tylko odczyt**. Narzędzia zapisu działają wyłącznie na targetach z `read_only: false`, a odrzucenie następuje po stronie serwera.
- Klucz API trzymany w magazynie systemowym (Windows Credential Manager, macOS Keychain, libsecret), wysyłany tylko nagłówkiem `X-Redmine-API-Key`. Nie trafia do configu klienta MCP, logów ani kontekstu modelu.
- Osoby w wynikach są podawane jako inicjały (`J.K. (#12)`), a treść załączników (docx, xlsx, pptx, tekst, zrzuty ekranu) trafia do agenta przez MCP.

## Narzędzia

| Narzędzie | Opis | Zapis |
|---|---|---|
| `list_targets` | skonfigurowane instancje i ich tryby | |
| `whoami` | konto, do którego należy klucz | |
| `list_projects` | widoczne projekty | |
| `search_issues` | wyszukiwanie zgłoszeń (projekt, status, osoba, tracker, tekst, data), ze stronicowaniem | |
| `get_issue` | zgłoszenie w całości, opcjonalnie z komentarzami i listą załączników | |
| `get_attachment` | zawartość załącznika: Word/Excel/PowerPoint/tekst jako tekst, obrazy jako obrazy, PDF i reszta zapisane na dysk | |
| `list_enumerations` | trackery, statusy, priorytety, pola własne, aktywności (ID różnią się między instancjami) | |
| `create_issue` | nowe zgłoszenie | tak |
| `add_issue_note` | komentarz do zgłoszenia | tak |
| `log_time` | czas pracy | tak |

## Instalacja z kodu źródłowego

Wymagane: .NET 10 SDK.

```powershell
# 1. zbuduj jeden plik wykonywalny (self-contained)
dotnet publish McpRedmine/McpRedmine.csproj -c Release -r win-x64 -o C:\tools\redmine-mcp

# 2. dodaj instancję Redmine; adres podajesz w terminalu, klucz API w ukrytym prompcie
C:\tools\redmine-mcp\McpRedmine.exe accounts add
C:\tools\redmine-mcp\McpRedmine.exe doctor      # sprawdza konfigurację, REST API i klucz

# 3. zarejestruj w Claude Code (zakres user)
claude mcp add --transport stdio --scope user redmine -- C:\tools\redmine-mcp\McpRedmine.exe
```

Potem uruchom ponownie Claude Code. Zalecane jest **dedykowane konto bota w Redmine z ograniczoną rolą**: klucz API ma pełne uprawnienia konta.

Podkomendy: `serve` (domyślna), `setup`, `doctor`, `accounts add|list|remove|test`. Szczegóły konfiguracji (`config.json`, pola targetu, kompatybilność z Redmine): [McpRedmine/README.md](McpRedmine/README.md).

## Rozwój

```
dotnet build McpRedmine.sln
dotnet test McpRedmine.sln      # testy na atrapach, w tym uruchomienie prawdziwej binarki przez stdio
```

Układ rozwiązania i zasady bezpieczeństwa opisuje [CLAUDE.md](CLAUDE.md).

## Stan

Zaimplementowane i przetestowane na atrapach HTTP. Jeszcze niesprawdzone: prawdziwa instancja Redmine, macOS i Linux (magazyny kluczy), prawdziwe dokumenty z Worda i Excela.
