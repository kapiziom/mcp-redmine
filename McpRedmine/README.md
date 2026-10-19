# RedmineMcp

A local [MCP](https://modelcontextprotocol.io/) server (stdio) that lets AI agents work with **one or more Redmine
instances** through the standard Redmine REST API. No Redmine plugin and no administrator rights needed.

- Several Redmine instances in one server (different versions, VPN-only hosts, company CA). Every tool takes a `target`.
- People are shown as initials, not names; e-mails, tokens, passwords and similar identifiers in text are masked (both configurable per target).
- Read-only by default. Write tools are rejected server-side unless a target is explicitly `read_only: false`.
- The API key is stored in your OS credential store (Windows Credential Manager, macOS Keychain, Secret Service) and
  is sent only in the `X-Redmine-API-Key` header. It never appears in a client config, a tool parameter or a log.

## Requirements

.NET 10 SDK (MCP clients start the server through `dnx`).

## Quick start

Replace `X.Y.Z` with the version you want (it is pinned in your MCP client).

```
dnx kapiziom.RedmineMcp --version X.Y.Z --yes -- setup           # register in Claude Code / Claude Desktop / Cursor
dnx kapiziom.RedmineMcp --version X.Y.Z --yes -- accounts add    # add a Redmine instance; the key is asked for with hidden input
dnx kapiziom.RedmineMcp --version X.Y.Z --yes -- doctor          # check .NET, config, REST API and the key
```

`setup --dry-run` shows what would change without touching anything; `--client claude-code|claude-desktop|cursor`
limits it to one client; re-running it is safe and moves the pinned version forward.
Restart your MCP client afterwards.

Use a **dedicated Redmine bot account with a restricted role**: an API key has the full permissions of its account.

## Tools

| Tool | What it does | Writes |
|---|---|---|
| `list_targets` | Configured targets and their modes | |
| `whoami` | Account behind the key on a target | |
| `list_projects` | Visible projects (paged, optional name filter) | |
| `search_issues` | Filter by project, status, assignee, tracker, text, update time (paged) | |
| `get_issue` | One issue in full, optionally with journal and attachment list | |
| `get_attachment` | Content of an attachment: Word/Excel/PowerPoint/text as text, screenshots as images, PDFs and others saved to disk | |
| `list_enumerations` | Trackers, statuses, priorities, custom fields, time-entry activities (ids differ per instance) | |
| `create_issue` | New issue | yes |
| `add_issue_note` | Add a comment | yes |
| `log_time` | Log spent time | yes |

Lists are capped by the target's `max_rows` and return `next_offset` for paging. Errors are explained in plain words.

## Accounts

```
accounts add [--name N] [--url U] [--read-write] [--ca FILE] [--max-rows N] [--project ID]... [--api-key-env VAR] [--allow-insecure-http]
accounts list
accounts remove <name>
accounts test [name]
```

`--api-key-env VAR` stores no key at all: the server reads it from that environment variable instead.
Without an OS credential store (e.g. a headless Linux box) the key falls back to a file readable only by you;
`doctor` warns about it.

## Configuration

`%APPDATA%\RedmineMcp\config.json` (Windows) or `~/.config/redmine-mcp/config.json`. It holds no secrets.

```json
{
  "targets": [
    {
      "name": "main",
      "url": "https://redmine.example.com",
      "read_only": true,
      "ca_path": "C:/certs/company-ca.pem",
      "max_rows": 50,
      "project_allowlist": ["web", "api"],
      "api_key_env": "REDMINE_MAIN_KEY"
    }
  ]
}
```

| Field | Meaning |
|---|---|
| `name` | Letters, digits, `-`, `_`. Used as the `target` argument. |
| `url` | Base URL, `https://` unless `allow_insecure_http` is set. A sub-path such as `/redmine` is fine. |
| `read_only` | Default `true`. |
| `ca_path` | PEM/DER CA for this target. Certificate validation is never disabled. |
| `max_rows` | Max rows per list result (default 50, hard cap 200). |
| `project_allowlist` | Only these projects (identifier or id) are readable/writable on this target. |
| `api_key_env` | Environment variable to read the key from when no stored key exists. |
| `allow_insecure_http` | Permit plain `http://` (the key would travel unencrypted). |
| `redact_people` | Default `true`: people appear as initials plus id (`J.K. (#12)`). |
| `redact_text` | Default `true`: e-mail addresses, keys/tokens/passwords and checksum-valid PESEL/NIP/IBAN/card numbers in free text become placeholders such as `[email-1]`, `[secret-1]`. Images and saved PDFs are not scanned. |
| `injection_guard` | Default `true`: hidden characters are stripped, known prompt-injection phrases are reported in `security_warnings`, and long texts are wrapped in `[[UNTRUSTED-...]]` markers. This reduces risk but does not make injection impossible; keep write tools unapproved by default. |
| `keep_email_domains` | E-mail domains (and their subdomains) that stay visible, e.g. `["example.com"]`. |

Override the location with the `REDMINE_MCP_CONFIG` environment variable.

## Redmine compatibility

Only core, long-standing REST endpoints are used (`/issues.json`, `/projects.json`, `/users/current.json`,
`/trackers.json`, `/issue_statuses.json`, `/enumerations/*`, `/time_entries.json`). Instances that lack one
(older versions) get a clear "not supported" message instead of a failure. The REST API must be enabled:
Administration → Settings → API → *Enable REST web service*.

## Troubleshooting

Run `doctor`. A `401` means the key is wrong **or** the REST API is disabled; Redmine does not distinguish the two.
Behind a corporate proxy or private CA, set `ca_path` on the target.

## Develop

```
dotnet build McpRedmine.sln
dotnet test McpRedmine.sln
dotnet pack McpRedmine/McpRedmine.csproj -c Release
```

Built on the [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk).
