# Z-E brief: installer and first run (with the user guide)

Agreed 2026-10-23 (Gavin: Z-P confirmed; the live sharp reflections retry is moved to "later"; features frozen for v1).
Builds on the 2026-10-09 installer handoff (`ai/261009e_Installer`), whose settled decisions stand: Inno Setup, per user,
no admin, no auto-update, self-contained app, publisher Aussie BIM Guru, no code signing for 1.0, the app owns the
association and Start-menu shortcut.

Answers today:
- **Folders:** `docs/` (the user guide, `docs/controls.md` with the generated tables, `docs/images/` for Gavin's
  screenshots and GIFs, placeholders until then), `installer/`, `build/`, `artifacts/` (output, never committed or
  zipped).
- **Dev PC:** Debug builds deploy into the bundle (only one copy of the add-in ever exists); Release builds copy
  nothing.
- **Uninstall:** ask whether to remove settings and logs (per-model data always kept).
- **First run:** welcome card, safe start (Shift), add-in / app version check. No sample model.
- Also asked mid-round: a guide to packaging a release with git (he PRs `dev` → `main`): `docs/releasing.md`.
