# WPF GUI archive

2026-10-15: the owner requested CLI + tray operation. `Proxyz.Wpf` preserves the previous WPF source, including the stage-4 safety fixes, outside the active solution. Core and Windows adapters remain shared; this is a source archive, not an immutable release.

Build explicitly with `dotnet build archive/Proxyz.Wpf/Proxyz.App.csproj -c Release`. Run only with `--demo` for initial inspection. Do not run the archived and active applications against real data simultaneously. Historical GUI verification is in `docs/TEST_RESULTS.md`; it does not verify the new CLI/tray.
