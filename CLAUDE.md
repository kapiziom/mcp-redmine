# CLAUDE.md — RedmineMcp

## Cel projektu
Lokalny serwer MCP (transport stdio) dający agentom AI dostęp do **wielu instancji Redmine** przez standardowe REST API.
Bez pluginów po stronie Redmine i bez uprawnień administratora. Jeden serwer obsługuje wiele „targetów” (różne wersje,
część za VPN-em lub z firmowym CA). Dystrybucja: NuGet.org, uruchamianie `dnx Kapiziom.RedmineMcp --version X.Y.Z --yes`,
jedna binarka z podkomendami (`serve`, `setup`, `doctor`, `accounts`).

**Stan:** wszystkie fazy planu (`.plans/implementation-plan.md`, lokalny, w .gitignore) zaimplementowane i przetestowane
na atrapach. **Nie sprawdzone z prawdziwą instancją Redmine** ani na macOS/Linux (patrz „Niezweryfikowane”).

## Stos
.NET 10 (`net10.0`, SDK przypięty w `global.json`), C# 14, nullable, `TreatWarningsAsErrors` + analyzers (`Directory.Build.props`,
wyjątki w `.editorconfig`). `ModelContextProtocol` 1.1.0, `Microsoft.Extensions.Hosting` 10.0.10,
`Microsoft.Extensions.Http.Resilience` 10.11.0. Testy: xUnit, własny `StubHandler`, Kestrel w procesie jako fałszywy Redmine.
Pakiet jest self-contained, single-file, 6 RID (`dotnet pack` daje 7 plików po ~33 MB).

## Komendy (zweryfikowane)
```
dotnet build McpRedmine.sln
dotnet test McpRedmine.sln                    # 304 testy, ~12 s; zawiera e2e na prawdziwej binarce przez stdio
dotnet pack McpRedmine/McpRedmine.csproj -c Release -o <dir>
dnx Kapiziom.RedmineMcp --version 0.1.0-beta --source <dir> --yes -- <podkomenda>   # z lokalnego źródła; bez -- też działa
dotnet McpRedmine/bin/Debug/net10.0/win-x64/McpRedmine.dll [serve|setup|doctor|accounts ...]
```
Niezweryfikowane: publikacja na NuGet.org (`dotnet nuget push`), `dnx` z NuGet.org, zachowanie na macOS/Linux
(`MacKeychainStore`, `LibSecretStore` — tylko testy z fałszywym runnerem), `custom_fields` i filtr `subject=~` na prawdziwym Redmine.

## Architektura
```
McpRedmine.sln, global.json, Directory.Build.props, .editorconfig
McpRedmine.Config/    TargetConfig, ConfigStore (JSON snake_case, zapis atomowy), ConfigValidator, TargetResolver, UserFacingException
McpRedmine.Secrets/   ApiKey (ToString zamaskowany), ISecretStore: WindowsCredentialStore (P/Invoke), MacKeychainStore,
                      LibSecretStore (CLI, sekret przez stdin), FileSecretStore (600), ApiKeyResolver (OS → env → plik)
McpRedmine.Redmine/   RedmineClient (GET z paginacją, POST/PUT, pobieranie plików z limitem), PersonRedactor, Redaction/TextRedactor (maskowanie maili, sekretów, PESEL/NIP/IBAN/kart), Attachments/ (OfficeText: docx/xlsx/pptx bez bibliotek, AttachmentReader, AttachmentStore), ApiKeyHandler (jedyne miejsce z kluczem), RedmineQuery,
                      IssueSearch, ProjectScope (allowlista), CustomCaValidator, AddRedmine() (klient HTTP per target + resilience)
McpRedmine.Clients/   ClaudeCli, ClaudeCodeInstaller, JsonConfigInstaller (Claude Desktop, Cursor), ServerRegistration
McpRedmine/           Program → Cli/CommandRouter → ServeCommand | SetupCommand | DoctorCommand | AccountsCommand; Tools/
McpRedmine.Tests/
```
Zależności: `McpRedmine` → wszystko; `Redmine` → `Config`, `Secrets`; `Clients` → `Config`, `Secrets`.

## Konfiguracja i sekrety
- Config: `%APPDATA%\RedmineMcp\config.json` / `~/.config/redmine-mcp/config.json` (nadpisanie: `REDMINE_MCP_CONFIG`). Tylko dane niewrażliwe.
- Target: `name`, `url`, `read_only` (domyślnie `true`), `ca_path`, `max_rows` (domyślnie 50, sufit 200), `project_allowlist`,
  `api_key_env`, `allow_insecure_http`, `redact_people` (domyślnie `true`). Domyślny target tylko przy dokładnie jednym.
- Klucz: OS (Credential Manager / Keychain / libsecret) → zmienna z `api_key_env` → plik `keys/<nazwa>.key` (600, ostrzeżenie w `doctor`).

## Narzędzia MCP
`list_targets`, `whoami`, `list_projects`, `search_issues`, `get_issue`, `list_enumerations`, `get_attachment` (odczyt, `ReadOnly = true`);
`create_issue`, `add_issue_note`, `log_time` (zapis, `[WriteTool]`).
- `target` jest w schemacie opcjonalny (`string? target = null`), ale wymagany semantycznie: `TargetResolver` odrzuca brak przy >1 targecie.
- Odrzucanie zapisu dla `read_only=true` robi **jeden** filtr `ToolPipeline` (przed ciałem narzędzia). Nowe narzędzie zapisu = `[WriteTool]`;
  test sprawdza, że każde narzędzie ma `[WriteTool]` albo `ReadOnly = true` i jawną nazwę.
- Listy: limit = `min(parametr, max_rows targetu, 200)`, odpowiedź ma `has_more`/`next_offset`. Teksty ucinane (opis 8000, notatka 4000, 50 wpisów dziennika).
- Błędy = `UserFacingException` → `isError` z komunikatem dla modelu. Zapis, który mógł się udać mimo błędu, mówi o tym i nie jest ponawiany.
- `get_attachment(issue_id, attachment_id)`: docx/xlsx/pptx/tekst → tekst (Markdown), png/jpeg/gif/webp → `ImageContentBlock`, PDF i reszta → zapis do
  `<config>/cache/attachments/<target>/` (7 dni) i zwrot ścieżki. Plik musi należeć do podanego zgłoszenia (stąd allowlista i dokładna nazwa w URL).
  Limity: pobranie 25 MB, obraz inline 4 MB, część OOXML 40 MB po rozpakowaniu, tekst 40 tys. znaków na wywołanie (`offset`/`max_chars`). DTD w XML zabronione.
  Obrazy osadzone w docx/xlsx/pptx: `include_embedded_images` (max 5). Brak stanu „co agent już widział” — decyduje agent; pobrane bajty cache'owane w pamięci 10 min.
- **Redakcja osób** (`redact_people`, domyślnie `true`): autor, przypisany, użytkownik w dzienniku, autor załącznika → inicjały + id (`J.K. (#12)`);
  `whoami` bez loginu i nazwiska.
- **Maskowanie tekstu** (`redact_text`, domyślnie `true`; `keep_email_domains` zostawia wskazane domeny): w opisach, komentarzach, temacie, polach własnych,
  nazwach plików i tekście załączników maile → `[email-N]`, klucze/tokeny/hasła (`password=…`, hasła w URL, Bearer, JWT, klucze prywatne, ghp_/glpat-/AKIA…) → `[secret-N]`,
  PESEL/NIP/IBAN/karty (tylko z poprawną sumą kontrolną) → `[pesel-N]` itd. Ten sam adres ma tę samą maskę w obrębie jednej odpowiedzi (`TextRedactor` na wywołanie).
  Pełne imiona użytkowników tego samego zgłoszenia w treści → `J.K. (#id)`; imion spoza zgłoszenia nie da się wiarygodnie wykryć. Regexy mają timeout (fail-closed).
  **Nie obejmuje obrazów** (zrzuty ekranu, obrazy osadzone w docx) ani zapisanych na dysk PDF-ów. Opisy narzędzi zapisu każą nie przepisywać masek do nowego tekstu.
- Brak RAG, brak bazy lokalnej: serwer jest bezstanowy poza cache'ami (id projektów w pamięci, bajty załączników w pamięci, pliki na dysku 7 dni).
- Allowlista dotyczy samego projektu (bez podprojektów); `search_issues` wymaga wtedy `project_id`, `get_issue`/`add_issue_note`/`log_time` sprawdzają projekt zgłoszenia.

## Konwencje
- Nazwy parametrów narzędzi są snake_case (CA1707 wyłączone tylko w `Tools/`). Odpowiedzi: zwięzły JSON snake_case bez nulli (`ToolJson`).
- Async, `CancellationToken` wszędzie. Tylko odczyty są ponawiane (`DisableForUnsafeHttpMethods`).
- Testy bez prawdziwej sieci poza loopbackiem; testy bezpieczeństwa sprawdzają mutacją (np. `AllowAutoRedirect=true` musi wywalić test przekierowania).
- Podkomendy interaktywne przez `ITerminal`/`CliContext` (testowalne). Stdout poza `serve` wolno używać; w `serve` nigdy.

## Zasady bezpieczeństwa (twarde)
1. Klucz API tylko w nagłówku `X-Redmine-API-Key`, tylko w `ApiKeyHandler`; `RedmineQuery` odrzuca parametry `key`/`api_key`/`token`.
2. Config nie zawiera sekretów. Klucz dodaje się wyłącznie przez `accounts add` (ukryty prompt) — brak opcji `--api-key`.
3. Klucz nie przechodzi przez parametry narzędzi ani kontekst modelu; nie trafia do JSON klienta MCP, `.mcp.json`, `--env`.
4. Nie logujemy nagłówków, kluczy ani treści błędów HTTP. `/users/current.json` zwraca `api_key` — DTO `RedmineUser` go nie ma.
5. Stdout w `serve` tylko dla JSON-RPC; logi na stderr.
6. HTTPS domyślnie; walidacji certyfikatów nigdy nie wyłączamy — tylko `ca_path` (`CustomCaValidator`, odrzuca niezgodność nazwy).
7. Przekierowania HTTP nie są śledzone (`AllowAutoRedirect=false`); klucz nie jest wysyłany poza origin targetu.
8. Helpery CLI (`security`, `secret-tool`) dostają sekret przez stdin, nigdy w argumentach.
9. Zalecaj użytkownikom dedykowane konto bota z ograniczoną rolą.

## Czego NIE robić
- Nie pisać na stdout w `serve`; nie wpisywać kluczy do configu klienta, logów, testów (używać atrap typu `E2E-SECRET-KEY`).
- Nie używać endpointów wymagających pluginów lub admina bez obejścia (`/custom_fields.json` ma fallback `project`).
- Nie dodawać zależności bez uzasadnienia (odrzucono Devlooped.CredentialManager: licencja OSMF). Nie zgadywać API SDK MCP.
- Nie prosić użytkownika o prawdziwy klucz Redmine ani go nie czytać; testy na żywo robi użytkownik.
- Nie uruchamiać `setup --yes` na prawdziwych klientach bez zgody; testować z `CLAUDE_CONFIG_DIR` w katalogu tymczasowym.

## Definition of done
`dotnet build` bez ostrzeżeń, `dotnet test` zielone (z testami nowej logiki), brak sekretów w kodzie/logach/wynikach narzędzi,
zapis nadal odrzucany dla `read_only`, CLAUDE.md zaktualizowany przy zmianie architektury lub zasad.

## Założenia do potwierdzenia
1. **Org/PackageId:** `Kapiziom.RedmineMcp` (z git user). `server.json` wskazuje repo https://github.com/kapiziom/mcp-redmine.
2. **Pakowanie:** zostaje self-contained RID-specific z szablonu (7 pakietów, ~230 MB/release). Alternatywa: jeden pakiet framework-dependent.
3. **Windows:** rejestracja w klientach używa `cmd /c dnx ...`, bo `dnx` to `dnx.cmd` (odstępstwo od `-- dnx` z opisu zadania; potwierdzone, że bezpośrednie uruchomienie bez shella nie działa).
4. **`http://`:** odrzucane, chyba że `allow_insecure_http`.
5. **Allowlista** nie obejmuje podprojektów; `search_issues` wymaga wtedy `project_id`.
6. **macOS Keychain** przez `security -i` (sekret na stdin), **Linux** przez `secret-tool` — niezweryfikowane na prawdziwych systemach.
7. **Format tekstu** (Textile/Markdown) przekazywany bez konwersji; konwerter z CodeHomie nie użyty.
8. **README** po angielsku (publiczna paczka NuGet); pozostałe dokumenty po polsku.
9. Minimalna wspierana wersja Redmine nieustalona; brak wykrywania wersji (REST jej nie podaje).
10. Docx/xlsx/pptx sprawdzone tylko na plikach generowanych w testach, nie na prawdziwych dokumentach z Worda/Excela (np. komentarze, nagłówki i stopki docx są pomijane).
11. Historia ma 12 lokalnych commitów (moduły: config, secrets, redmine, clients, app, testy, build, docs); nic nie wypchnięto.
