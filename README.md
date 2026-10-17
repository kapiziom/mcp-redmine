# mcp-redmine

A local [Model Context Protocol](https://modelcontextprotocol.io/) (MCP) server that gives AI agents such as Claude Code access to **one or more Redmine instances** through the standard REST API. No Redmine plugin and no administrator rights are required.

[![CI](https://github.com/kapiziom/mcp-redmine/actions/workflows/ci.yml/badge.svg)](https://github.com/kapiziom/mcp-redmine/actions/workflows/ci.yml)

## Features

- **Multiple Redmine instances** in one server (different versions, behind a VPN, private CA). Every tool takes a `target`.
- **Read-only by default.** Write tools are rejected server-side unless a target is explicitly configured with `read_only: false`.
- **Secrets stay out of the model's reach.** The API key lives in the OS credential store (Windows Credential Manager, macOS Keychain, libsecret) and is sent only in the `X-Redmine-API-Key` header. It never appears in MCP client configs, logs, tool parameters or tool results.
- **Privacy filters on by default.** People are shown as initials plus id (`J.K. (#12)`). E-mail addresses, API keys, tokens, passwords and checksum-valid PESEL/NIP/IBAN/card numbers in free text are replaced with placeholders such as `[email-1]`.
- **Attachments reach the agent.** Word, Excel, PowerPoint and text files are returned as text, screenshots as images, everything else is saved to disk.
- **Safe networking.** HTTPS by default, per-target custom CA instead of disabling certificate validation, redirects are never followed, reads are retried and writes never are.

## Tools

| Tool | Description | Writes |
|---|---|---|
| `list_targets` | Configured instances and their modes | |
| `whoami` | The account the API key belongs to | |
| `list_projects` | Visible projects | |
| `search_issues` | Search by project, status, assignee, tracker, text and update time, with paging | |
| `get_issue` | One issue in full, optionally with journal and attachment list | |
| `get_attachment` | Attachment content: Word/Excel/PowerPoint/text as text, images as images, PDFs and others saved to disk | |
| `list_enumerations` | Trackers, statuses, priorities, custom fields, time-entry activities (ids differ per instance) | |
| `create_issue` | Create an issue | yes |
| `add_issue_note` | Add a comment to an issue | yes |
| `log_time` | Log spent time | yes |

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build from source (the published executable is self-contained).
- A Redmine instance with the REST API enabled (*Administration → Settings → API → Enable REST web service*) and an API key. Use a **dedicated bot account with a restricted role**: an API key carries the full permissions of its account.

## Installation

Build a single self-contained executable and register it in Claude Code:

```powershell
# 1. Build
dotnet publish McpRedmine/McpRedmine.csproj -c Release -r win-x64 -o C:\tools\redmine-mcp

# 2. Add a Redmine instance (the API key is requested with hidden input) and verify the setup
C:\tools\redmine-mcp\McpRedmine.exe accounts add
C:\tools\redmine-mcp\McpRedmine.exe doctor

# 3. Register the server in Claude Code (user scope)
claude mcp add --transport stdio --scope user redmine -- C:\tools\redmine-mcp\McpRedmine.exe
```

Restart Claude Code afterwards. To update, repeat step 1; the registration does not change.

For other platforms use the matching runtime identifier (`linux-x64`, `osx-arm64`, ...). `setup` can register the server in Claude Code, Claude Desktop and Cursor through `dnx` once the package is available from a NuGet feed (`setup --dry-run` previews the changes).

## Usage

```
McpRedmine <command>

  serve                          Run the MCP server over stdio (default)
  setup                          Register the server in MCP clients
  doctor                         Diagnose the installation and the configured targets
  accounts add|list|remove|test  Manage Redmine targets and API keys
```

Example prompt once the server is registered:

> A new task came in on Redmine: #12345. Analyze the description, the comments and the attached files.

## Configuration

The config file holds no secrets: `%APPDATA%\RedmineMcp\config.json` on Windows, `~/.config/redmine-mcp/config.json` elsewhere (override with `REDMINE_MCP_CONFIG`). `accounts add` writes it for you.

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
      "redact_people": true,
      "redact_text": true,
      "keep_email_domains": ["example.com"]
    }
  ]
}
```

| Field | Default | Meaning |
|---|---|---|
| `name`, `url` | required | Target name (used as the `target` argument) and base URL. `https://` unless `allow_insecure_http` is set. |
| `read_only` | `true` | Write tools are rejected for this target. |
| `ca_path` | none | PEM/DER CA used to validate this target's certificate. |
| `max_rows` | 50 | Rows per list result (hard cap 200). |
| `project_allowlist` | none | Only these projects (identifier or id) are accessible through this target. Sub-projects are not implied. |
| `redact_people` | `true` | Show people as initials plus id. |
| `redact_text` | `true` | Mask e-mails, secrets and national/financial identifiers in free text. |
| `keep_email_domains` | none | E-mail domains (and subdomains) that stay visible. |
| `api_key_env` | none | Environment variable to read the key from when no stored key exists. |
| `allow_insecure_http` | `false` | Permit plain `http://` (the key would travel unencrypted). |

More detail, including Redmine compatibility notes, is in [McpRedmine/README.md](McpRedmine/README.md).

## Security notes

- The API key is requested only by `accounts add` (hidden input). There is no command-line option, environment variable or tool parameter for pasting it, and it is never written to MCP client configs.
- Masking is a safety net, not a guarantee: names mentioned inside free text are masked only when they belong to users of the same issue, and images (screenshots, pictures embedded in documents) and saved PDFs are passed through unchanged.
- If a project contains data that must not leave your organization, restrict the target with `project_allowlist` or do not expose it at all.

## Development

```
dotnet build McpRedmine.sln
dotnet test McpRedmine.sln      # tests use stubs and an in-process fake Redmine, plus end-to-end runs of the real binary over stdio
```

The solution layout, conventions and security rules are described in [CLAUDE.md](CLAUDE.md). CI builds and tests on Windows, Linux and macOS.

## Status

Covered by tests against stubbed HTTP and a fake Redmine, and used against a real Redmine instance. Not yet verified: macOS and Linux credential stores.
