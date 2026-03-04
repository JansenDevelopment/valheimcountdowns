# Project Guidelines

## Code Style
- Language: C# in an SDK-style project targeting `net472` (`PlaytimeTimers.csproj`).
- Keep nullable-aware code (`<Nullable>enable</Nullable>`) and explicit `using` directives (`<ImplicitUsings>disable</ImplicitUsings>`).
- Follow existing style in `PlaytimeTimersPlugin.cs`: private fields with `_camelCase`, constants for plugin metadata, and small helper methods.
- Preserve invariant-culture numeric serialization (`CultureInfo.InvariantCulture`) and Base64 timer-name encoding/decoding helpers.

## Architecture
- Single-project solution (`src.sln` -> `PlaytimeTimers.csproj`) with one main runtime class: `PlaytimeTimersPlugin : BaseUnityPlugin`.
- Core responsibilities in `PlaytimeTimersPlugin.cs`:
  - Timer lifecycle/update loop (`Update`, `StartTimer`, timer math)
  - UI (`OnGUI`, `DrawWindow`, HUD overlay)
  - Persistence (`LoadTimers`, `SaveTimers`)
  - Valheim integration via reflection (`Player`, `ZNet`)
- Timer persistence is a single serialized config value (`Storage/Timers`) using `|`-separated fields and `;;`-separated records.

## Build and Test
- Run from this workspace folder:
  - `dotnet restore .\src.sln`
  - `dotnet build .\src.sln -c Debug`
  - `dotnet build .\PlaytimeTimers.csproj -c Release -p:ValheimDir="C:\Program Files (x86)\Steam\steamapps\common\Valheim"`
- There are currently no test projects/files; do not run `dotnet test` unless tests are added.
- Expect build failures if Valheim/Unity DLL references are unavailable (see `PlaytimeTimers.csproj` `HintPath` entries).

## Project Conventions
- Preserve plugin identity constants unless explicitly requested: GUID/name/version in `PlaytimeTimersPlugin.cs`.
- Keep config schema stable where possible (`General`, `Storage` keys). If adding persisted timer fields, keep backward-compatible parsing in `LoadTimers`.
- Avoid introducing extra files/classes unless needed; this plugin currently keeps logic centralized in one file.
- Prefer defensive integration checks and graceful fallback behavior over hard failures.

## Integration Points
- BepInEx attribute/bootstrap: `[BepInPlugin(...)]` in `PlaytimeTimersPlugin.cs`.
- Unity APIs and IMGUI: `UnityEngine`, `GUILayout`, `SceneManager`.
- Valheim internals via reflection against `Assembly-CSharp` (`Player`, `ZNet`) for interior/world-time/player-position data.
- External references are local game/mod paths, not NuGet-managed (`PlaytimeTimers.csproj`).

## Security
- Treat persisted config input as untrusted; validate/guard parsing like current code.
- Reflection calls are version-fragile; always null-check members and keep failure-safe behavior.
- Do not log sensitive user/system data; keep logs limited to timer/plugin operational events.
