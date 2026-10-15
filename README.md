# LotR MP Event updater

Installer / updater for the LotR MP Event Bannerlord modules. Plan and principles: `docs/PLAN.md`.
Shared formats between workstreams: `docs/CONTRACTS.md`. Local Git only, never pushed.

## Game detection (step 1)

| Project | Target | What |
|---|---|---|
| `src/LotR.GameDetection` | netstandard2.0, net48 | finds Bannerlord (Steam, Epic, GOG, Xbox PC) and its `Modules` folder — `GameLocator.FindAll()` / `GameLocator.Validate(path)` |
| `src/LotR.Detect.Cli` | net48 | `lotr-detect.exe`: prints the detection report as JSON (for support) |
| `tests/LotR.GameDetection.Tests` | net8.0, net48 | xUnit test bench: simulated PCs (in-memory disks, registry, processes, shortcuts) + a skippable read-only smoke test on the real PC |

Details, sources and research links: `docs/DETECTION.md`.

**Read-only guarantee.** The detection library and `lotr-detect` never write to the registry and never create, modify
or delete anything on disk, with one exception: the Modules write check, which by default creates a hidden 0-byte
`.lotr-write-probe-<guid>.tmp` with `FILE_FLAG_DELETE_ON_CLOSE` and closes it at once, so Windows removes it
immediately (even if the process crashes). Use `--write-check accesscheck` (ask Windows about the permissions, write
nothing) or `--write-check none` to avoid even that. Nothing ever asks for administrator rights.

```
dotnet build src/LotR.Detect.Cli -c Release
src/LotR.Detect.Cli/bin/Release/net48/lotr-detect.exe [--deep] [--write-check tempfile|accesscheck|none] [--no-process] [--mask-user] [--pick <path>]
dotnet test tests/LotR.GameDetection.Tests          # both frameworks; set LOTR_SKIP_SMOKE=1 to skip the real-PC tests
```
Exit code: 0 found, 1 not found, 2 error.
