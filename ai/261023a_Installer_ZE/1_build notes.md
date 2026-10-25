# Z-E build notes: installer and first run

Not compiled here (no .NET SDK); the PowerShell script and the Inno script were not run (no PowerShell or Inno Setup in
the sandbox). C# script-checked (brackets, strings, char literals); the props, project files and the bundle template
parse as XML; new GameSession member names checked for clashes; UI strings Latin-1 / atlas only.

## Version and project files
- **`src/Directory.Build.props`** (new): `<BimGoVersion>1.0.0</BimGoVersion>` → Version, AssemblyVersion,
  FileVersion (`1.0.0.0`), plus Company / Authors / Copyright. Removed from the three .csproj files. The one place to
  bump a version.
- **BimGo.App:** `InstallBimGoApp` (copy to `%LocalAppData%\Programs\BimGo\`) now runs for **Debug** only. Stale
  header comment about GameHost fixed.
- **BimGo.Revit `CopyToRevitAddins`:** every build still lays out `bin\<config>\<year>\BimGo.addin + BimGo\` (no pdb).
  **Debug** then deploys into `%AppData%\Autodesk\ApplicationPlugins\BimGo.bundle\Contents\<year>\`, writes the bundle's
  `PackageContents.xml` from `installer\PackageContents.xml` (version filled in; `[MSBuild]::Escape` so `?` / `;` in
  the XML survive `WriteLinesToFile`), and deletes `Revit\Addins\<year>\BimGo.addin` + `BimGo\` (duplicate AddInId).
  **Release** copies nothing.

## Installer
- **`installer/PackageContents.xml`**: the Autodesk bundle (ProductCode `{DF913CCB-…}`, UpgradeCode `{CACC322A-…}`,
  never change), one component per Revit year (`SeriesMin` = `SeriesMax` = R2025 / R2026 / R2027, module
  `./Contents/<year>/BimGo.addin`, so the `.addin`'s relative `BimGo/BimGo.Revit.dll` stays valid).
- **`installer/BimGo.iss`** (Inno Setup 6): AppId `{A11D053D-9F32-4324-A8B8-C93ADD9E3AB1}` (never changes);
  `PrivilegesRequired=lowest`; app fixed to `{localappdata}\Programs\BimGo` (`DisableDirPage`); x64, Windows 10+;
  licence page = `LICENSE`; types **full / app only / custom**; component **revit** (all built years, ~1 MB) offered
  only when `Revit.exe` 2025–2027 is found under Program Files. `[InstallDelete]` cleans the app folder, the old bundle
  and developer copies under `Revit\Addins\2025–2027`. `[Run]` `--register --quiet` (+ an unticked "Start BimGo");
  `[UninstallRun]` `--unregister --quiet`. `[Code]`: WMI check for `BimGo.exe` / `Revit.exe` with Retry / Cancel at
  install and uninstall (never closes them: unsaved work); after uninstalling, a Yes / No (default No) "Also remove your
  BimGo settings and logs?" removes `%AppData%\BimGo`, and `%LocalAppData%\BimGo\Logs`, `App`, `Sessions`, `Families`
  and `LastLive.json`; `Models` and `Comments` always stay. (Inno's uninstaller has no custom pages, so the "tick box"
  is a message box; silent uninstalls keep everything.)
- **`build/publish.ps1`**: version from the props → Core tests (stop on failure) → `dotnet publish` BimGo.App
  self-contained win-x64, folder, untrimmed, to `artifacts\app\` (checks BimGo.exe, the icon, licences, places.tsv) →
  `dotnet build src\BimGo.sln -c "Release R2x"` per year, copied into `artifacts\bundle\BimGo.bundle\Contents\<year>\`,
  failing if any dll other than BimGo.Revit / BimGo.Core is there → `PackageContents.xml` with the version and only
  the years built → optional signtool (`-Sign -CertThumbprint`, off) → ISCC (found in the usual folders, or
  `-InnoCompiler`) → `artifacts\BimGo-Setup-<version>.exe` + a SHA-256 file. `-Years 2025,2026` leaves a year out.

## First run
- **Welcome card** (`GameSession.Welcome.cs`): first walkthrough on a PC (settings `WelcomeShown`; not in kiosk mode
  or a safe start): four lines naming the keys as bound (move / look / run; jump / crouch / fly; guns; F1 / Esc) and
  **ONLINE GUIDE** (opens `docs/` on GitHub) / **START WALKING**. Esc closes it. It has the keys before everything but
  kiosk mode (`UpdateFrame`), freezes the player, frees the cursor, and the gamepad cursor works on it.
- **Safe start:** Shift held as BimGo.exe starts (`GetAsyncKeyState`), or `--safe`. `LaunchSettings.SafeStart`
  (static, process-only): `LoadOrDefault` returns defaults with the Basic profile, `Save` does nothing (logged). An
  important toast says so. The fatal-error message box now suggests a safe start (or, after one, the driver).
- **Version check** (Core `Live/VersionCheck`: major.minor.patch, a 4th part or `+suffix` ignored, unreadable = fine):
  - Revit, at Go: BimGo.exe's file version (`App_Utils.ExeVersion`) vs the add-in's; on a mismatch a Yes / No
    "BimGo versions differ … Go anyway?" (logged).
  - App, joining a session: `SessionInfo.AddinVersion` vs `Program.Version` → `SessionOptions.StartNotice` → an
    important toast (not on reloads).
- Tests: `ReleaseTests` (version comparison and message; safe start gives Basic defaults and saves nothing).

## Docs
- **`docs/`** (new): `README.md` (guide home) and pages: getting started, moving around, guns, live sessions and Revit,
  comments and BCF, review tools, studies, media, clash detection, kiosk mode, settings and files, troubleshooting,
  **releasing** (version → PR → build from `main` → test → `git tag -a vX.Y.Z` → GitHub Release with the setup; `gh`
  alternative; pre-releases; fixing a bad release with a new patch).
- **`docs/controls.md`**: the generated tables (markers unchanged) plus the notes, guns table and pause menu moved out of
  the README. `ControlsTests.Readme_ControlsTablesAreCurrent` now looks for `docs/controls.md` (above the test folder,
  beside `src/BimGo.Core`). Pause-menu tab names corrected (DISPLAY · REFLECTIONS · APPEARANCE · LIGHTS).
- **`docs/images/`**: 38 placeholders (dark cards with the caption, what to capture and the file name; PNG 1280 × 720,
  GIF 960 × 540) and a README table of file → page → what to capture, with tips for screenshots and GIFs. Replacing a
  file under the same name is all it takes.
- README: Install (setup) vs Build from source (Debug deploys, Release copies nothing, publish script), the command line
  gains `--safe`, add-in location updated, links to the guide; the hero GIF placeholder. DEVELOPING: rules (docs pages
  and pictures, `docs/controls.md`), the Install overview row, project structure (docs / installer / build), **Install
  and first run** under How it works, the open build, install limitations, roadmap (live sharp reflections → later as
  "live sharp mirrors"; next: beta, pictures, 1.0). HISTORY: the Z-E row.

## For Gavin
- Add `artifacts/` to `.gitignore`.
- Install **Inno Setup 6** and the **.NET 10 SDK** (R27) on the build PC.
- First Debug build after this: your old `Revit\Addins\<year>\BimGo*` copies are removed and the add-in loads from the
  bundle. If a Revit year you never build shows a missing-add-in message, build that year once (or delete its
  `<Components>` entry from the dev bundle's `PackageContents.xml`).
- To check in Revit: that each year loads bundles from `%AppData%\Autodesk\ApplicationPlugins\` (per user) and only its
  own component.
