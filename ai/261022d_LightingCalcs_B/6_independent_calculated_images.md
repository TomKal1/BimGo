# Independent calculated lighting images — 2026-10-22

Implements the first bounded calculated-diffuse image milestone, following the user's requirement that WWTools and Tom-Main each work independently with no new external dependency. This does not complete the plan's glossy/glass/colour-aware transport milestone.

## Delivered

- The same `CalculatedImage.cs` source is compiled in WWTools and bundled in BimGo's own Lighting.Engine. No runtime lookup or launch of WWTools is used.
- LIGHTING → RESULTS and Model Review offer CALCULATED IMAGES · PNG…: room, four cutaway corners or Interior perspective, fixed exposure and 1080p/4K export.
- Model Review has a Calculated mode, using saved diffuse luminance and model hues. Saved patches are opaque; other geometry is explicitly preview. Appearance corners do not average across source/material boundaries.
- Native HTML report exports include embedded calculated images, generated on a worker. Numerical reports produced as part of a solve remain lightweight until exported.
- Surface colours survive native extraction, subdivision, v1 results exchange and retained partial zones. Old data remains readable with neutral appearance.
- PNG iTXt metadata carries the physical/display basis, camera, exposure and deterministic surface-snapshot fingerprint.
- Pinned upstream blob provenance remains; changed source files have explicit imaging-overlay SHA-256 hashes. The scalar physical equations are unchanged.

## Physical basis and limits

Luminance is the saved rho E / pi field, converted from fc to lux. Model colour supplies hue normalized to preserve photopic luminance; it is not a measured optical property or colour-aware bounce model. Linear samples are averaged before fixed-exposure tone mapping/sRGB. There is no extra preview ambient/direct lighting in the exported image.

Textures, glossy/specular transport, optical glazing, coloured interreflection, spectral/CCT effects, detailed luminous fixture geometry and daylight are not introduced. Lighting mesh sampling limits image detail. The feature must be called calculated diffuse appearance, not full photorealism.

## Verification

- BimGo App Release builds; no new package references or runtime dependencies.
- All 570 Core/calculation tests pass, including appearance exchange, legacy neutral data, units and partial restoration.
- The independently built WWTools and BimGo renderers produce identical PNG and pixel bytes on the furnished-office parity scene.
- 1280 × 800 PNG SHA-256: `47b0bfea439ddd4e2069f3206a30dab2e3feba10be9c29053fded5d75c2afa6c`.
- 3840 × 2160 export and interior perspective render successfully. Rendered PNGs were visually inspected; a footprint-boundary ray precision issue was fixed and covered by a regression test.
- WWTools builds for R24/net48 and R27/net10; its lighting tests pass. Its full suite has one unrelated concurrent-queue failure, reproduced in an untouched HEAD snapshot.
- Saved Snowdon replay preserves all 2,424 direct and total point floats and its scale. The old saved scenario hash differs under current input-schema serialization; the original strict legacy hash assertion does not pass. A separate diagnostic disclosed the revisions and kept every numerical comparison.
- The normal engine-provenance script verifies source/overlay hashes, then encounters an existing corpus input-hash mismatch at asymmetric-source.scene.json. Neither that scene nor its golden was edited by this work. This gate is not reported as passing.

## Maintenance and remaining checks

WWTools `scripts/SyncLightingImaging.ps1 -BimGoRoot <checkout>` updates the common source overlay; `-VerifyOnly` checks byte identity. Preserve LF bytes. The standalone WWTools `scripts/LightingImageProbe.csproj` builds either from source or against BimGo's engine DLL; use `-t:Rebuild` when switching modes.

Verify the actual dialogs and live GL appearance mode on a real furnished room, linked finishes, multi-level geometry, partial reruns, old files, short windows and exported PDF pagination. No installed app or Revit add-in was replaced.
