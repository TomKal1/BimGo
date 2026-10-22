# Source provenance and ownership

The files in `Upstream/` are an unchanged development snapshot of TomKal1/WWTools,
branch `claude/zealous-brahmagupta-n6ies6`, commit
`7c307ebed155dacba0a567e23ee639b2b793c54d`. `upstream.lock.json` records every
original path and Git blob SHA. `tools/check-lighting-engine.ps1` checks these bytes.

The upstream project declares **Copyright (C) 2026 Wiley & Wilson. All rights
reserved.** Its project identifies Thomas Ames as author. No LICENSE or other
redistribution grant was present in the inspected branch. Importing this snapshot
for the requested development work does not relicense it under BimGo's MIT-0
license. This notice preserves that distinction; it is not a license grant.

Public packaging is disabled (`IsPackable=false`). Establish the shared engine's
ownership/redistribution terms before publishing a package or distributing the
combined product. Keep this notice with development builds containing the engine.

Do not maintain a second solver here. Keep the snapshot unchanged during
characterization; move maintenance into one shared release source, update both
hosts to consume that pinned artifact, and run the common corpus before adoption.
The original namespaces are retained to minimize WWTools adapter changes. No
Revit, WPF, OpenGL, Nice3point, or installed WWTools assemblies are dependencies.
