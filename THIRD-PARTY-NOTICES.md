# Third-party notices

Flyt's self-contained Windows packages redistribute the components below. The
release workflow produces an SPDX SBOM with the exact package versions and file
hashes for each architecture. Every release archive also includes the complete
license texts in `licenses/` plus the license and third-party notices shipped by
the installed .NET SDK/runtime distribution used to build the executable.

| Component | Copyright / attribution | Purpose | License | Bundled text |
|---|---|---|---|---|
| [.NET runtime](https://github.com/dotnet/dotnet) | © Microsoft Corporation | Self-contained runtime | MIT | Build distribution `LICENSE` and `THIRD-PARTY-NOTICES` files |
| [Microsoft.Data.Sqlite](https://www.nuget.org/packages/Microsoft.Data.Sqlite) | © Microsoft Corporation | SQLite provider | MIT | `licenses/MIT.txt` |
| [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) | Copyright 2014–2024 SourceGear, LLC | Native SQLite bindings | Apache-2.0 | `licenses/Apache-2.0.txt` |
| [SQLite](https://sqlite.org/copyright.html) | The SQLite authors | Embedded database engine | Public domain | This notice and the linked public-domain dedication |
| [Spectre.Console](https://github.com/spectreconsole/spectre.console) | Patrik Svensson, Phil Scott, Nils Andresen, and Cédric Luthi | Terminal rendering | MIT | `licenses/MIT.txt` |

The package build verifies these license expressions against the exact restored
NuGet metadata and fails closed if they change. SPDX `NOASSERTION` values are
inventory limitations in the generated SBOM; they do not replace this verified
license mapping or the bundled legal texts.

[psmux](https://github.com/psmux/psmux) is required at runtime but installed
separately. Flyt does not redistribute it. It is licensed under MIT.

Claude Code, OpenAI Codex, and OpenCode are external provider tools. Flyt does
not redistribute them; each remains governed by its own license and service
terms.
