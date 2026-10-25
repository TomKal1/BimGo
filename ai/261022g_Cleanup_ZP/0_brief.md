# Z-P brief: pre-installer review, cleanup and polish

Agreed 2026-10-22, after Gavin confirmed Z-C (kiosk mode): built, works, no local changes.

- **README:** split into three files at the repo root: a lean **README.md** (overview, features, getting started, the generated controls, guns, files and folders, short limitations), **DEVELOPING.md** (AI / contributor rules, architecture, how it works, the `.bimgo` format, the live protocol, the open build, the full limitations, roadmap, dependencies) and **HISTORY.md** (the build table, trimmed).
- **Code:** a sanity pass with no behaviour changes: dead or unused members, stale comments, small safe de-duplications, obvious bugs; a findings list.
- **Pre-1.0 migrations:** nothing has launched, so cut the redundant ones (Gavin: "if we can afford to cut redundancy, no major harm").
