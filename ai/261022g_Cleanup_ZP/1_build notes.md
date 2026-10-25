# Z-P build notes: pre-installer cleanup

Not compiled here (no .NET SDK in the sandbox). Brackets, strings and char literals script-checked in every changed `.cs`
file; the three .csproj files and OptionsWindow.xaml parse as XML; a script found no private member left unreferenced
(Program.Main aside) and no reference left to anything removed.

## Docs
- **README.md** (170 KB → 21 KB): intro, features at a glance, getting started with a command-line table (now listing
  `--kiosk`), the controls (the generated block between the markers is unchanged, so `Readme_ControlsTablesAreCurrent`
  still finds it), shorter hand-written notes, guns, a one-paragraph pause menu, files and folders, short limitations.
  "Formerly RvtGo" is gone (nothing migrates from it any more).
- **DEVELOPING.md** (new, 66 KB): the AI rules (updated for the three files; new rules for UI text, the font atlas,
  Revit API gotchas, "no migrations before v1", and `Utilities.FileNames`), overview, project structure (round tags
  out), **How it works** rewritten by area (Revit side and sessions, engine, walkthrough and review tools, studies,
  media, clash detection, input / UI / shell), about a third of the old length; the round-by-round detail stays in the
  `ai/` notes. The `.bimgo` format and live protocol tables kept as the spec, with round tags and the removed legacy
  fields taken out. Open build, known limitations (full list), roadmap (current), dependencies.
- **HISTORY.md** (new): the build table; revision tails ("· r2: …") dropped and long rows cut at ~240 characters (the
  folder holds the rest); Z-C and Z-P rows added.
- Code comments that pointed at "README §3" now say "the README's controls".
- THIRD-PARTY-NOTICES.txt and OptionsWindow.xaml: round names removed.

## Pre-1.0 migrations removed
- **Settings:** the one-time copy of `%AppData%\RvtGo\settings.json`; a settings file without `ControlsDefaultsVersion`
  no longer reads as version 0 (new installs start at the current version anyway).
- **Comments:** the `.rvtgo.json` sidecar (`CommentFiles.LegacyPathFor` / `MigrateLegacy`,
  `BimGoFormat.LEGACY_SIDECAR_SUFFIX`, the calls in `CommentStore.Load` and the Revit Export).
- **Model folders:** `ModelFolders.Prepare(folder, info)` no longer copies sidecars in from beside the model or the old
  `Comments` folder (`MigrateInto` removed). Sharing beside the model (`PullNewer`, mirrored writes) is unchanged; the
  `%LocalAppData%\BimGo\Comments` path stays as the fallback when the folder can't be created.
- **Texture choices:** `TextureOverrideSet.LoadFromModelFolder(folder, hostKey, fallbackFolder)` no longer copies the
  older per-key file in; without a folder it still uses the per-key file.
- **Shared coordinates:** files without the captured internal → shared transform (pre-v5.1) no longer derive one from the
  survey point (`SharedTransform.Approximate` and the "≈" readout removed). `ChooseAngleSign` stays (the Revit side's
  `VerifySharedAngle` uses it).
- **Clash set rules:** `SetRule.Contains` / `Value` / `Exclude` (the C-A / C-B1 form, still written for older builds) and
  `RuleOp.FromLegacy` / `IsKnown` removed; `Operator`, `ValueCount`, `ValueAt` read `Op` / `Values` only; a rule without
  an operator reads as "is". Tests rewritten to build rules with `Op` / `Values`; `Sets_OlderRulesReadAsOperators` became
  `Sets_RulesRoundTripAsOperators`.
- **Family library:** `LibraryPlacement.FACE_BASED` (an older name for work-plane types, kept for listing) removed.
- Kept on purpose: optional fields reading as their defaults (the format's additive rule), and the live protocol's
  checks for an older add-in (the app and add-in are installed separately, so they can still be out of step).

## Code sanity pass
- **Unused members removed** (no callers anywhere): `GameSession` `IsSunPanelOpen`, `IsTourActive`, `IsSunHoursOpen`,
  `IsRecordingVideo`, `ClashesActive`, `ClashModelChangedSince` (the clash panel does the same check inline),
  `ScreenHeight`, `IsUiHidden`, `IsUserHidden`, `LiveLink`; Core `KeyCodes.IsMouse`, `KeyChord.Of`,
  `KeyChord.ModifierCount`, `Units.AreaSymbol`, `ParameterTableBuilder.DiscardElement`, `EntourageArt.BLACK`; App
  `UnitFormat.LengthSymbol`, `CommentGun.Hovered`, `Gamepad.ControllerNumber`, `HomeScreen.DrawStatus`,
  `UiBatch.ClipActive`, `SceneRenderer.Effects` / `BillboardCount` / `HasSection` / `ReflectivePlaneCount` /
  `ScreenReflectionsReady`, `ShadowMaps.DepthTexture` / `TransmitTexture`, `GridOverlay.SegmentCount`,
  `GameWindow.RequestFocus` / `RequestClose`. Kept: Win32 / GL / joystick struct fields and constants (layout and
  reference), the Revit template's `Globals` and extensions, `UiTheme` tokens, `LiveProtocol` state names.
- **Bug fix: occlusion culling never reset.** `SceneRenderer.ResetOcclusion` existed but nothing called it, so after a
  cut or hiding something, chunks that had been hidden behind it appeared a frame late. `SetSection` now resets when the
  cut changes, and `GameSession.RefreshMasks` (category / link / isolate / show all) resets too. Scan's per-element hide
  doesn't (it would reset once per element in loops; one frame late is fine there).
- **De-duplication:** `BimGo.Utilities.FileNames.Safe(name, fallback = "Model")` (Core) replaces five copies of
  "replace invalid file-name characters" (`GameSession.SafeFileName`, `ModelFolderResolver.MakeSafeFileName`, the Revit
  Export's suggested name, `MaterialScan`'s log name, the kiosk shortcut's name). The ones with other rules (sun study
  names, model folder names, texture override keys, `TextureLocator`) are left alone: changing them would rename files.
- **Publisher placeholders:** the three .csproj files said `<Company>Author</Company>`; now `Aussie BIM Guru` plus
  `<Copyright>Copyright 2026 Aussie BIM Guru (MIT-0)</Copyright>` (shows in the exe / DLL properties, and the installer
  can read it). `Program.Version`'s comment said "3.00.00.01".
- **UI text:** the path editor's group and F1 title were "Path editor (Round K)" → "Path editor" (CONTROLS, F1, README;
  the test was updated and renamed `PathEditor_KeysLiveOnlyInTheEditor`); the Revit-views toast named "build X-E" → "an
  up-to-date BimGo add-in"; stale "reserved for Round K" comments fixed.
- **Checked, nothing to do:** no TODO / FIXME / HACK markers; no `async void`; `.Result` only on finished tasks;
  `Debug.WriteLine` only in the Revit template's ribbon helpers; packages match `THIRD-PARTY-NOTICES.txt` and the
  Dependencies table.

## Findings to consider later (not changed)
- `UiTheme.BUTTON_HEIGHT`, `LABEL_WIDTH` and `MINIMAP_WIDTH` are unused tokens (the minimap code uses `S(220)` directly);
  harmless.
- The Revit template's `Globals`, `String_Ext`, `Double_Ext`, `Document_Ext`… have many unused helpers: they come from
  the shared template / common library, so left for that effort.
- `LiveProtocol` state `busy` is never sent (kept: it is part of the protocol's documented states).
- The README test compares with `Environment.NewLine`: on Windows the generated text is CRLF; if git keeps README.md
  as LF the test may report a regeneration once (it rewrites the block either way).

## Changed files
Docs: `README.md`, `DEVELOPING.md` (new), `HISTORY.md` (new), `THIRD-PARTY-NOTICES.txt`.
Core: `Utilities/FileNames.cs` (new), `Scene/LaunchSettings.cs`, `Scene/InputCatalog.cs`, `Scene/KeyMap.cs`,
`Scene/KeyChord.cs`, `Scene/Units.cs`, `Scene/ModelInfo.cs`, `Scene/FamilyLibrary.cs`, `Scene/EntourageArt.cs`,
`Scene/SiteCoordinates.cs`, `Scene/TextureOverrides.cs`, `Format/CommentFiles.cs`, `Format/BimGoFormat.cs`,
`Format/ModelFolders.cs`, `Clashes/ClashModels.cs`, `Clashes/ClashFiles.cs`, `BimGo.Core.csproj`.
App: `Program.cs`, `Shell/FileAssociation.cs`, `Shell/HomeScreen.cs`, `Platform/GameWindow.cs`, `Platform/Gamepad.cs`,
`Rendering/UiBatch.cs`, `Rendering/SceneRenderer.cs`, `.Billboards.cs`, `.Section.cs`, `.PlanarReflections.cs`,
`.ScreenReflections.cs`, `Rendering/ShadowMaps.cs`, `Rendering/GridOverlay.cs`, `Game/GameSession.cs`, `.Sun.cs`,
`.Tour.cs`, `.SunHours.cs`, `.Video.cs`, `.Visibility.cs`, `.Live.cs`, `.Coordinates.cs`, `.Document.cs`,
`.RevitViews.cs`, `Game/Clashes/GameSession.Clashes.cs`, `Game/CommentStore.cs`, `Game/UnitFormat.cs`,
`Game/Guns/CommentGun.cs`, `BimGo.App.csproj`.
Revit: `Commands/Cmds_BimGo.cs`, `Extraction/ModelFolderResolver.cs`, `Extraction/MaterialScan.cs`,
`Forms/OptionsWindow.xaml`, `BimGo.Revit.csproj`.
Tests: `ModelFolderTests.cs`, `MeasurementTests.cs`, `SidecarTests.cs`, `ClashTests.cs`, `ControlsTests.cs`.

## Revisions
- r2 (2026-10-23): build fix, `SiteCoordinates.AnchorMisfit` still passed `approximate: false` to the `SharedTransform` constructor (line 160); argument removed.
