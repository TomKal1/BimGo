# Calculated appearance and interactive lighting design

Status: implementation plan; no solver or rendering changes implemented by this document.
Date: 2026-10-21. Applies to TomKal1/BimGo and the WW Tools lighting calculation integration.

## 1. Intended result

The designer opens ISO View on the room they occupy, switches to **Calculated appearance**, and sees the model's materials illuminated by a solved lighting model. They can change a wall or floor finish, move or rotate a luminaire, or add another luminaire. The scene and calculation update automatically. Illuminance labels, statistics, and the rendered appearance refer to the same design revision.

**Product direction: two independently usable lighting calculators.** BimGo gets the full calculation capability inside its own installation: create calculation sets, choose rooms/spaces and work planes, configure photometry and physical materials, run and update the solution, inspect calculated appearance and numerical results, and save/export results. It must work with a complete `.bimgo` calculation scene without WW Tools installed, running, or having produced a prior calculation.

WW Tools retains its full calculator inside Revit: create sets, solve, annotate, and produce reports without BimGo installed or running. Each application ships the calculation runtime it needs. Both use a maintained, versioned engine implementation and shared physical contracts, while installation, execution, release, and reporting remain independent.

Revit remains the place to commit changes to the Revit project and issue its Revit annotations/reports. BimGo can issue its own clearly identified calculation exports directly. Exchanging inputs/results or applying a BimGo design to Revit is an integration workflow; it is not a prerequisite for either calculator to function.

This is a substantial lighting-engine and rendering feature. Coloring the existing patches with their saved values is an early milestone; it does not complete the interactive calculation requirement.

## 2. What “exact” means for this work

There are three separate acceptance questions:

1. **Data agreement:** imported WW Tools values retain their meaning and match the saved run to serialization and display-rounding precision.
2. **Calculation agreement:** the same scene, fixtures, photometry, reflectances, settings, engine version, and sampling produce matching BimGo and WW Tools results within a documented numerical tolerance. Reuse one engine rather than independently reproduce its equations.
3. **Appearance fidelity:** the displayed image comes from a documented physical light-transport and camera model. Surface luminance, materials, color, shadows, and reflections must be validated separately from point illuminance.

No finite simulation or ordinary monitor can guarantee an exact reproduction of a real room. The quality claim is agreement with a specified, tested physical model, with known input uncertainty and convergence limits. Matching WW Tools alone also does not establish field accuracy or agreement with a separate validated simulator.

Immediate interaction means edits appear immediately and trigger calculation automatically. A completed, fully converged solution on every drag frame is a performance objective to investigate, not a promised property. While a solve is running, the app must identify the displayed results as updating or belonging to the previous revision. Never substitute the current artistic live lighting and call it calculated.

## 3. Findings that determine the architecture

### BimGo, inspected locally

- `GameSession.Lights.cs` and `Shaders.cs` implement a responsive fixture preview, with light selection, finite reach, brightness and daylight multipliers, approximate bounce fill, and bloom. This path is unsuitable as the reference calculation.
- `LightingCalcs.cs` contains solved point values, surface patches, scalar reflectances, luminaire summaries, and run metadata. `CalcPatch.Luminance` already computes diffuse luminance as reflectance times illuminance in lux divided by pi.
- `CalcOverlay.cs` maps results to false colors or a normalized gray luminance view. Its display lifting and corner smoothing serve an overlay; they are not a physically calibrated material renderer.
- `LightingCalcFiles.cs` reads the v1 results exchange. It does not supply a complete editable calculation scene: summaries omit full candela distributions, luminous opening geometry, complete source frames, full transport geometry, and stable face/material mappings.
- `LightProfiles.cs` uses a compact GPU distribution table. Retain full photometry for calculation; the byte table is only a presentation optimization.
- `MaterialData.cs` provides material and texture identity. Appearance color and texture data do not establish measured diffuse reflectance, roughness, or transmission.
- Geometry edits exist in `EditMessages.cs` and the journal. Physical material edits require an explicit scenario model and a supported Revit application path; they are not already provided by the texture override UI.

### WW Tools, inspected through GitHub

The default-branch `Lighting/Engine/LightingSolver.cs` performs direct IES illumination, near-field source subdivision, sampled diffuse form factors, iterative radiosity, and final gathering. Its core uses math, threading, and engine DTOs rather than Revit API calls. The extraction and reporting adapters remain host-specific.

The solver already separates direct patch lighting, geometry-dependent form-factor hits, reflectances, and final gathering internally. Refactoring those stages into reusable cached state is the principal route to fast, faithful updates.

Its current material transport is scalar and diffuse. It does not represent arbitrary colored interreflection, glossy reflection, realistic glazing, or a complete directional radiance field. These are required extensions for the full appearance target, not effects to imitate with the existing approximate bounce fill.

The WW Tools results-format document referenced by BimGo was unavailable on the WW Tools default branch during planning. Locate the actual producer/integration branch before implementing the exchange upgrade. Do not assume default-branch documentation describes every deployed build.

## 4. Architecture decisions

### One maintained engine implementation, independently deployed in both applications

Extract the reusable WW Tools engine and geometry/math dependencies into a versioned shared library, provisionally `Lighting.Engine`, with plain-data contracts in `Lighting.Contracts`. Names and repository placement are proposals. Each product bundles its own pinned engine release and can install, run offline, and upgrade independently. No calculation request is sent to the other application, and no runtime DLL is resolved from the other product's install directory. Use one maintained engine source and shared test corpus; avoid independently maintained solver forks.

Use matching engine/model versions for parity certification. Different installed versions remain usable: detect and explain version differences when exchanging a run, rather than requiring a simultaneous product upgrade or claiming their answers are identical.

- **Revit adapter:** extraction, saved calculation sets, material/fixture resolution, transactions, annotations, and reports.
- **BimGo adapter:** native calculation-set and point-grid authoring, photometry/material configuration, full solve and incremental updates, scenario edits, visualization, results/report exports, and file persistence.
- **Shared engine:** full photometry, source placement/orientation and factors, visibility, patch construction, interreflection, point sampling, convergence, and physical output.

Audit target frameworks, supported Revit years, dependencies, licensing, and packaging before extraction. Preserve existing Revit behavior through characterization tests. Use a background solve worker; keep calculation off the UI/GL thread and all Revit API calls on Revit's supported thread. A separate worker process is an option if deployment or native rendering backends require it.

### Preserve a compatibility model; version physical extensions

First reproduce the current WW Tools diffuse model exactly within numerical tolerance. Then extend the shared model for the appearance requirements: color-aware transport, emission, and supported directional material behavior. Both hosts must understand the same physical-model version when numerical equality is claimed.

Do not add physically significant glass/specular/color-bounce behavior only in the BimGo picture while continuing to claim its numbers match the older diffuse WW Tools model. Where an advanced material exceeds the supported shared model, identify that limitation in the UI and the exported basis. The complete fidelity milestone requires the advanced transport path and validation, not just a diffuse demonstration.

### Keep calculation geometry separate from display geometry

Define a canonical calculation scene and extraction policy for both applications. The BimGo Revit exporter must produce the complete scene directly from Revit without invoking WW Tools or requiring a previous WW Tools run. Share extraction rules/adapters where practical and compare their outputs for parity. Revit is needed to extract a Revit model; it is not needed to solve a complete standalone `.bimgo` afterward.

BimGo may have a different visual mesh or proxy representation. Stable source IDs and surface mappings connect it to calculation geometry. Preserve the same geometry and simplification policy when comparing against a WW Tools run.

ISO cutaways, section boxes, hidden reference grids, camera moves, and presentation-only visibility must not open the calculation enclosure or change the solve. A ceiling cut away for viewing still blocks and reflects light in the physical scene. Explicit physical removal or geometry edits invalidate the calculation.

### One revision for appearance and analysis

Every input change creates an immutable scenario revision with a content hash. Results carry the scenario hash, engine/model version, quality settings, convergence status, coverage, and source run. Numerical overlays, room statistics, and material shading consume one atomically published result bundle. An old worker can never overwrite a newer revision.

## 5. Exchange and persistence

Add a versioned **calculation-scene payload** alongside the existing read-only results exchange. Keep old `.bimgo` files readable and v1 result files unchanged. A solved-result-only file can show its saved appearance but cannot promise editable recalculation.

The primary producer for standalone BimGo calculation is its own Revit extraction/export path. WW Tools import is an additional producer. A new BimGo export must support its first calculation without any imported results. For older files, validate whether geometry, materials, and photometry are sufficient; offer a BimGo re-export or explicit input completion when needed, rather than requiring a WW Tools calculation.

The editable scene needs:

- Model, host/link, zone, element, face, material, fixture-instance, and fixture-type identities. Use stable composite IDs for links and pending BimGo clones.
- Coordinate frame and unit declarations. Preserve the existing engine's feet/double-precision basis initially; convert at the BimGo boundary with origin-offset round-trip tests.
- Full calculation triangles, normals, sidedness, physical surface/material assignments, obstruction classifications, zone boundaries, connected openings, point locations and normals, and work-plane/region settings.
- Raw/resolved IES data or a lossless full-precision representation, source frames including horizontal orientation, luminous dimensions, near-field rules, LLF, dimming/user factors, type overrides, photometry provenance, and any resolved source-position corrections.
- Physical material properties and their provenance: effective reflectance, linear color interpretation, supported roughness/specular/transmission/emission properties, and whether the value was measured, explicitly chosen, taken from a space parameter, or estimated.
- Solver settings, seeds, patch identity/order, convergence configuration, geometry simplification, engine/physical-model versions, and all content hashes needed to reproduce a run.
- Results with separate direct/indirect contributions where supported, point illuminance, surface illuminance/luminance or radiance data, diagnostics, and full coverage metadata.

Reject unit mismatches, inconsistent hashes, unsupported model versions, corrupt ranges, or incomplete physical inputs with a useful status. Never fill missing source photometry with an undisclosed default in a result presented as verified.

Persist calculation-set definitions, scenarios, overrides, and cached results as additive optional entries. Keep imported calculations immutable and identify result origin as **BimGo calculation** or **WW Tools calculation**, along with revision and updating status. Reopening a file must preserve which result belongs to which design. A BimGo calculation does not need an imported baseline.

### Native BimGo calculation workspace

Expose calculation authoring even when no WW Tools data or solved results exist. The current UI's imported-results availability must not gate the new calculator. Provide:

- Create, duplicate, rename, and delete calculation sets; choose one or more rooms/spaces, floors, or connected multi-level domains.
- Work-plane height, grid spacing and offsets, point normals/placement policy, reflectance basis and physical-material overrides, quality and convergence settings, and targets.
- A fixture/photometry manager with IES input, source orientation/size, LLF and output controls, and input provenance.
- Run, cancel, automatic recalculation, progress/convergence details, comparison of saved scenarios, and undo/redo.
- Calculated appearance, illuminance/luminance analysis, point grids and room statistics from the same result revision.
- Save/reopen sets and results in `.bimgo`; export point CSV, structured results, annotated images, and a standalone calculation report with its physical basis, assumptions, solver version and scenario identity.

Use the shared kernel and numerical settings for every native calculation. An occupied-room automatic solve is the quick entry into the complete calculator, not its only supported workflow.

## 6. Calculated appearance rendering

Add **Appearance: Live preview / Calculated** to Model Review. Keep Materials / Illuminance / Luminance as presentation choices within the applicable source, with an unambiguous source/revision label.

For the first diffuse compatibility milestone:

1. Map solved physical surfaces to the visual material mesh using stable face identity and validated correspondence, not nearest-plane matching alone. Respect opposite wall faces, linked overlaps, openings, thin walls, and coverage boundaries.
2. Shade diffuse material appearance from the solved lighting. Use `L = rho * E_lux / pi` for the scalar diffuse reference. Do not multiply by the same material reflectance again in a later shader stage.
3. Retain absolute linear lighting values through the render pipeline. Use a fixed, explicit exposure and display transform. Remove per-room/per-set percentile normalization from Calculated appearance; keep normalized gray as a clearly separate analysis option if useful.
4. Disable the current artificial direct-light pass, approximate bounce fill, generic ambient/sun contribution, distance fading, and aesthetic brightness multipliers on calculated surfaces. Treat included daylight as an explicit shared solver input.
5. Keep an unprocessed linear luminance/HDR capture for validation. Bloom and optional display effects operate only after the physical solution and never change fc values.
6. Show unsolved surfaces/regions as lacking calculated coverage. Do not silently blend the old live light into the same allegedly calculated room.

For the full appearance milestone:

- Extend the shared transport to resolve colored diffuse reflection with a documented photometric/color model. CCT or RGB tinting must preserve the stated luminous output; do not infer spectral accuracy from a scalar fc field.
- Resolve supported glossy and transmissive appearance from directional incident lighting through an energy-conserving material model. Scalar patch irradiance alone cannot reproduce mirrors, clear glazing, or view-dependent highlights.
- Evaluate a progressive physically based rendering backend using the same scene and emitters, with a high-quality reference path for convergence and comparison. Select the production CPU/GPU backend after a measured prototype and compatibility/dependency review, not by assuming the current OpenGL renderer can provide exact transport.
- If specular/transmissive paths materially change illuminance, integrate that physical-model upgrade into both hosts' calculations. Preserve the older diffuse mode as a versioned compatibility option.
- Increase surface sampling/refinement as required by near-field fixtures and small shadows. No smoothing across obstructions, opposite surfaces, or material boundaries; interpolation must not invent light through a wall.

Imported scalar results can therefore deliver a useful calculated diffuse view first. They cannot, by themselves, satisfy the final realistic material/render target.

## 7. Material and fixture editing

### Materials

Allow picking a physical wall/floor face and choosing the scope: selected face, element, or all uses of a material. Provide an appearance swatch plus explicit calculation properties, including diffuse reflectance. State the active reflectance basis: fixed zone values, space parameters, or physical materials.

Changing a JPEG or display tint alone must not silently become a measured reflectance change. A finish preset can intentionally change both appearance and calculation properties, with their values visible. Physical face overrides take precedence in the scenario and remain reproducible in Revit through the shared input contract.

Changing reflectance affects the light returned to every coupled surface. Re-solve interreflection and update point illuminance, ratios, statistics, and appearance. Recoloring a previously solved patch completes only the immediate visual response, not the lighting calculation.

Reuse geometry, the BVH, patch layout, form-factor hits, and final-gather mappings when valid. In the diffuse model, retain direct incident light when only reflectance changes, update reflectance/exitance, and iterate the coupled solution again. Roughness/transmission or geometry changes require broader invalidation.

### Fixtures

Support move, rotate/aim, copy, add, delete, output/dimming, and type/photometry changes. New fixtures need complete solver-ready photometry and emitter geometry, including those placed from the family library. Missing or estimated data remain visible as input limitations.

Cache direct contributions by source where the memory budget permits. For a moved/replaced source, remove its old contribution and evaluate its new contribution, then re-solve all affected interreflection and final gathers. For an added source, add its physical contribution; source deletion removes it. Validate the cached result against an independent full solve.

Reuse diffuse surface transport only when the occluding physical geometry is unchanged. A visible fixture housing that blocks light in the supported physical model, a moved obstruction, or a change in transmission invalidates the corresponding geometry-dependent caches. Mirror the current WW Tools policy of excluding fixture geometry when running its compatibility model.

### Connected rooms and whole-building operation

Recalculate the complete transport domain that can exchange light with the edit. Reuse WW Tools' existing domain/margin rules in compatibility mode. For the advanced physical model, explicitly model connected rooms, openings, and cross-level shafts. A room-only cache must not silently discard incoming light from a connected space.

Prioritize the occupied/selected room for responsiveness, then update all coupled domains and affected calculation sets. Result coverage and revision status remain explicit while other areas are pending.

## 8. Scheduling and responsiveness

An edit immediately updates the model, invalidates dependent results, and queues a solve. Coalesce rapid drag updates, cancel superseded work, and publish only matching revisions. Undo/redo restores physical inputs and can reuse a matching cached result.

Suggested UX states: **BimGo calculation**, **Imported WW Tools calculation**, **Calculating design changes**, and **Calculation incomplete**. Show convergence/quality and input limitations in details, with concise source/status in the main view. Numerical results from a previous revision must be marked accordingly or temporarily hidden.

While dragging, progressively compute the current revision when feasible. After release, automatically finish the selected target quality. Early iterations remain calculated-but-incomplete; the approximate live preview is available only as an explicit alternative. Increasing presentation quality must not silently change the calculation basis or the numerical report.

Benchmark material updates, fixture edits, source additions, undo, cold starts, and geometry changes on a small room, the Snowdon stair/atrium, an open office, and connected spaces. Record p50/p95 solve times, time to first valid result, convergence, memory, and UI frame time on named hardware. Set release latency budgets from these measurements; if the requested immediacy is missed, optimize or state the limit rather than relabel an approximation as exact.

## 9. Revit workflow and design ownership

Keep the existing WW Tools Revit calculation and reports available throughout. BimGo scenario solves do not silently replace an issued Revit run.

BimGo's local calculations and exports are complete without this integration. WW Tools likewise remains complete without BimGo. Each product can create its own calculation from source inputs, with no request to the other product to perform the solve.

Provide an explicit **Apply design to Revit** path for material/finish and fixture edits. Scope linked-model changes correctly; do not attempt to edit an unavailable linked document through the host. Add material-application capabilities to the edit protocol and journal with capability negotiation, stable face/type mapping, conflicts, failure reporting, and undoable Revit transactions.

When the user chooses the Revit integration workflow, re-extract the canonical calculation scene after applying changes and run WW Tools with the same engine/settings. Compare the accepted scenario with the resulting Revit inputs and numbers. If Revit hosting, parameter resolution, material mapping, or source corrections changed anything, show the difference and recalculate instead of retaining a stale match label.

Live Revit sessions can reload new solved results as today. Standalone BimGo can calculate and issue its own outputs without Revit or WW Tools. Applying its changes to a Revit project and issuing Revit-native outputs happens when that project is available.

## 10. Delivery sequence and release gates

| Build | Work | Required gate |
|---|---|---|
| CA-0: contracts and baseline | Locate the actual WW Tools producer branch; audit engine boundaries, frameworks and redistribution; export reproducible test scenes; benchmark existing solves; agree physical-model and numerical targets. | A golden scene reproduces the current Revit result outside Revit. Document missing inputs and the backend decision prototype. |
| CA-1: shared engine, independent deployment | Extract and version the existing CPU photometric/diffuse kernel and adapters without changing its math; bundle the runtime in each product. | Each product solves with the other absent; matching versions pass identical characterization cases at the same settings and seeds. |
| CA-2: native calculator and editable scene | BimGo's own export supplies full inputs; add local set/grid/photometry configuration, solve, persistence and reports; preserve v1 readers/import. | Start from a BimGo export with no WW Tools results, author a set, solve and export offline; a matching reference agrees and old files still open. |
| CA-3: calculated diffuse appearance | Material mapping, physical diffuse shading, fixed exposure, coverage and source labels; keep analysis views consistent. | Raw luminance matches analytic fixtures and exported surface values, with no duplicated lighting. This is a clearly bounded diffuse milestone. |
| CA-4: interactive calculated edits | Material/fixture scenario UI; reusable transport state; asynchronous updates; undo/redo and persistence. | Every edit produces the same converged answer as a fresh full solve; no stale publication or invisible domain truncation. |
| CA-5: full material appearance | Shared color-aware transport, supported directional/glossy/transmissive materials, progressive rendering backend and high-quality reference. | Physical-model and reference-render comparisons pass; any unsupported material/path is disclosed. A diffuse-only implementation does not pass this gate. |
| CA-6: independent release and optional Revit round-trip | Validate separate installs, native reports and version compatibility; complete apply-design, canonical re-extraction and Revit rerun integration. | Both calculators work independently; optional round-trip results reconcile; measured responsiveness and appearance limits are documented. |

CA-0 should produce an implementation estimate and benchmark-based latency targets. This plan deliberately makes no calendar promise before that audit. No engine replacement or new runtime dependency is committed by this planning document.

## 11. Validation and definition of done

Keep the following tests distinct:

- **Photometric laws:** inverse-square/cosine cases; full IES distributions and symmetry, tilt, orientation, LLF/dimming, near-field source size, and obstructions. Explicitly test asymmetric wall washers and sources embedded above ceilings.
- **Engine parity:** identical scene/patch/sample order, settings and seeds. Establish a tight double-precision equivalence tolerance from baseline runs; use separate documented absolute/relative tolerances for serialized floats. Do not compare image pixels to fc values.
- **Product independence:** on a clean machine with WW Tools absent, BimGo opens a complete input-only `.bimgo`, authors its first set, calculates, edits, recalculates, saves/reopens and exports a report. On a Revit machine with BimGo absent, WW Tools creates/runs/reports its calculations. Neither product loads files or launches workers from the other's installation; test mismatched independently installed engine versions too.
- **Incremental parity:** for each material and fixture operation, compare direct arrays, converged indirect arrays, point grids, surface values, and statistics with a complete independent recomputation. Include adding/removing sources, undo, linked instances, and connected spaces.
- **Convergence:** record iteration residual and sampling resolution separately. A small iterative residual does not prove spatial discretization or ray sampling is accurate. Test refinement and near-zero cases without misleading percentage errors.
- **Physical reference:** analytic scenes, energy-balance checks, and a separately validated lighting simulator/reference renderer under matching assumptions. Maintain known differences for glazing, specular effects, RGB/spectral behavior, domain truncation and geometry simplification. WW Tools itself requires independent validation.
- **Appearance:** verify unprocessed surface luminance, linear color, exposure invariance across rooms/scenarios, material reflectance handling, colored bounce, grazing light, narrow beams, reflection/transmission where supported, and no double counting. Display exposure changes the image, never the calculated fc.
- **Cutaway invariance:** camera, ISO/plan orientation, section box, and presentation-only hiding leave physical results unchanged.
- **Robustness:** rapid edits, cancellation, late worker completion, disconnected Revit, corrupt/missing inputs, insufficient memory, partial coverage, save/reopen, and old `.bimgo` compatibility.
- **User verification:** real Revit projects and BimGo walkthroughs at fixed cameras; controlled photographs/field measurements only where inputs, exposure and measurement conditions are known. Do not equate a visually pleasing screenshot with accuracy.

The requested feature is complete when BimGo can author and run its full calculation from a complete input scene, change a physical finish or move/add a fully specified light, automatically obtain a current calculated appearance and matching numerical results, save/reopen the design, and issue its own outputs with WW Tools absent. WW Tools must retain its full Revit calculation/reporting capability with BimGo absent. The optional integration reproduces an accepted design through Revit under matching engine versions and inputs. The shared engine, physical-model coverage, convergence and validation determine the accuracy claim.

## 12. Source references

Inspected 2026-10-21. WW Tools references are default-branch snapshots and must be re-audited against the deployed integration branch before implementation.

- [WW Tools solver](https://github.com/TomKal1/WWTools/blob/main/src/WWTools/Lighting/Engine/LightingSolver.cs), file SHA `e97f2a166bb28823e070a24c4653a64616316614`: direct, patch, radiosity, final gather and current quality settings.
- [WW Tools luminaire model](https://github.com/TomKal1/WWTools/blob/main/src/WWTools/Lighting/Engine/Luminaire.cs), file SHA `ca8fe7fec6437813447534ef9bc6360ae22aab22`: full source frames, photometry, multipliers and near-field subdivision.
- [WW Tools user guide](https://github.com/TomKal1/WWTools/blob/main/docs/lighting-calcs/user-guide.md), file SHA `afa05e99f15889373ad84674af5926a8729edf8f`: workflow, geometry/reflectance assumptions and current limitations.
- BimGo local sources: `src/BimGo.Core/Scene/LightingCalcs.cs`, `Scene/LightingData.cs`, `Scene/LightProfiles.cs`, `Scene/MaterialData.cs`, `Format/LightingCalcFiles.cs`, `Edits/EditMessages.cs`; App `Game/GameSession.Lights.cs`, `Game/GameSession.LightProfiles.cs`, `Game/GameSession.LightingReview.cs`, `Rendering/CalcOverlay.cs`, `Rendering/Shaders.cs`.
- [PBRT: diffuse reflection](https://www.pbr-book.org/4ed/Reflection_Models/Diffuse_Reflection): Lambertian assumptions and reflectance normalization.
- [PBRT: film and imaging](https://pbr-book.org/4ed/Cameras_and_Film/Film_and_Imaging): image formation and exposure.
- [PBRT: Monte Carlo integration](https://www.pbr-book.org/4ed/Monte_Carlo_Integration): numerical light-transport estimates and convergence.
- [Radiance](https://www.radiance-online.org/about/detailed-description.html): candidate independent physical reference; its use is to be evaluated, not an adopted runtime dependency.
