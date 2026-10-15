# Proxyz — agent instructions

## Read first

Read `README.md`, `docs/CLI.md`, `docs/SPEC.md`, `docs/ARCHITECTURE.md`, `docs/SECURITY.md`, `docs/ACCESSIBILITY.md`, and `docs/CODEX_HANDOFF.md` before changing behavior. This is a Windows 10/11 proxy-settings utility, not a VPN or proxy server. Source-generation history is in `VERIFICATION.md`; later build/runtime evidence and gaps are in `docs/TEST_RESULTS.md`.

## Scope

Owner instruction (2026-10-15): CLI + task tray, with the previous WPF GUI archived outside the active solution. Preserve core functionality, safety, recovery, and current-user default LAN WinINet scope. C# / .NET 10; WinForms for tray/minimal dialogs, no WPF in the active app. Windows 10 22H2 x64 and Windows 11 x64 remain compatibility targets. Do not raise the minimum to Windows 11 or add a web view. WinHTTP is authorized only for read-only diagnostics and explicit URL probes, never configuration management.

Do not add WinHTTP management, privileged services, VPN management, arbitrary commands, proxy credentials, DNS/firewall/certificate changes, telemetry, cloud accounts, app-specific configuration, or new dependencies without the owner's explicit approval. Prefer fixing a small local module over rewriting everything. Accessibility and conservative recovery are release blockers.

## Safe commands

Use a Windows terminal with the .NET 10 SDK for CLI/tray/native verification. Disable SDK development-certificate generation (`DOTNET_GENERATE_ASPNET_CERTIFICATE=false`) before dotnet. Begin with:

```powershell
dotnet restore .\Proxyz.slnx
dotnet build .\Proxyz.slnx -c Release --no-restore
dotnet run --project .\tests\Proxyz.Core.Tests -c Release --no-build
dotnet run --project .\tests\Proxyz.Cli.Tests -c Release --no-build
dotnet run --project .\tests\Proxyz.Windows.Tests -c Release --no-build
dotnet run --project .\src\Proxyz.App -c Release --no-build -- --demo tray
```

These tests use an executable harness, **not `dotnet test` discovery**. Each returns nonzero on failure. Read native state only after describing what is read, using `--read-os` on the Windows test executable. No test in the default suites writes Windows proxy settings.

`python scripts/verify-source.py` performs structural checks only, not compilation or execution. Do not present it as proof of product functionality.

## Actions requiring explicit user permission

Before changing a real Windows proxy, startup registration, OS policy, Wi-Fi/location consent, network connection, or certificate store, explain the exact target and obtain permission. The same rule applies to non-demo UI automation. Do not elevate, run as SYSTEM, create a service, reset WinHTTP, stop another program, or run unrestricted network diagnostics. Do not weaken security tooling or execution policy. The source is designed not to need elevation.

Build/test is not permission to reconfigure networking. Test a fake backend first. Use a disposable Windows VM/account for real write/rollback cases after approval. Show the current state, back it up, define the rollback, and do not bypass the in-app confirmation. Do not send PAC URLs, internal hosts, SSIDs, `config.json`, `journal.json`, `.bak`, or logs to external services. Never embed real organizational settings in committed examples.

## Invariants to preserve

- Automation and SSID access are opt-in. Unknown networks keep settings by default.
- Profile/rule saves do not write Windows settings and pause automation.
- Manual apply, restore, and external-state adoption are explicit and previewed.
- Every OS write is serialized and has a durable pending journal first.
- Read back and compare actual settings. Apply != connectivity success.
- External changes and policy restrictions win over automatic overwrites.
- Do not erase pending recovery when restoration fails.
- Persistent storage errors occur before OS writes where possible.
- Same-network failed automation must not retry endlessly on self-generated events.
- No automatic direct-connection fallback on PAC/authentication failures.
- WinINet strings/COM objects/native handles have clear ownership.
- A tray icon is optional for operating every feature; keyboard/Narrator paths exist.

## Changes and evidence

Fix compile errors first, then run both executable suites. Add a regression test for any safety/core bug. Keep native side effects behind `IProxyBackend` and network observation adapters. Manually validate UIA, keyboard, contrast themes, DPI/text scaling, and Windows 10 compatibility. A passing mock suite is not native validation; a passing demo is not a browser proxy test.

Record command, OS/build, SDK/runtime, exit status, observed output, and unresolved gaps in `docs/TEST_RESULTS.md`. Do not overwrite the historical `VERIFICATION.md` to imply tests ran during source generation. Mark human-only or unavailable-environment cases unverified. Do not manufacture screenshots, memory figures, test totals, or a release signature. Do not commit, push, or publish without user authorization.
