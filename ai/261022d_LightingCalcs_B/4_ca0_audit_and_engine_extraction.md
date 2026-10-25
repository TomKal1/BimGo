# CA-0 audit and initial CA-1 extraction

2026-10-21, America/New_York. BimGo base: `Tom-Main`,
`fdea1c49ac7794287025ce40f2af1cd2de272347`.

## Status

The reusable compatibility engine now builds outside Revit, ships in the BimGo
development output, and solves/reports through an isolated binary consumer.
Five complete synthetic inputs and full numerical goldens are checked in. Saved
Snowdon result data agreement is also verified. **The CA-0 release gate remains
open:** a canonical scene captured immediately before a real Revit solve and its
double-precision results are still needed. CA-1 is an extraction prototype; the
WWTools product has not yet switched to the shared binary.

No calculator workspace, automatic-edit scheduler, new `.bimgo` scene payload,
calculated material renderer, or advanced transport is implemented here. Existing
review rendering and its interactive verification status are unchanged.

## Producer branch, verified through GitHub

Inspected all 44 branch heads and their recursive trees; evidence is in
`ca0-branch-audit.json`. The results producer is on
[`claude/zealous-brahmagupta-n6ies6`](https://github.com/TomKal1/WWTools/tree/7c307ebed155dacba0a567e23ee639b2b793c54d),
commit **`7c307ebed155dacba0a567e23ee639b2b793c54d`**.
`claude/optimistic-cori-g3qor9` has an earlier BimGo viewer plan but no results
producer. Default `main` was `a6d723e32541faf580d44c85d008001f7c48a141`.

Producer additions and pinned adapter versions used for this audit:

| File | Git blob SHA |
|---|---|
| `docs/lighting-calcs/results-format.md` | `26d771bb69b9c9cff8bbbe7d34b66ba87a4b1127` |
| `Lighting/Engine/CalcResultsFile.cs` | `9a40c1a0345a9d1d8e8d64719ca1ec98acd82746` |
| `Lighting/LightingCalcRunner.cs` | `ddd77d382dfe7dbfa736e85028ce6688fd83d9cf` |
| `Lighting/LightingSceneBuilder.cs` | `0e403326e2f0d72aabf5cbb6b50655176e1d8215` |
| `Lighting/PhotometryResolver.cs` | `6b84ba408109d494fcf89b84c6f101668cb7d9f9` |

Paths abbreviated above are relative to `src/WWTools/`. The producer
[format document](https://github.com/TomKal1/WWTools/blob/7c307ebed155dacba0a567e23ee639b2b793c54d/docs/lighting-calcs/results-format.md)
confirms v1 is saved results, not a complete editable scene.
The results-format document and CalcResultsFile are absent from main; the runner,
scene builder and resolver are existing adapters whose integration-branch versions
are pinned above.

The integration and main solver blobs both equal
`e97f2a166bb28823e070a24c4653a64616316614`. The plan's earlier solver inspection
therefore applies to its math, but not to the deployed producer/adapter files.

## Engine boundary and deployment

`src/Lighting.Engine` compiles 37 **unchanged** upstream source files:
`Lighting/Engine/*.cs`, `Calcs/Engine/*.cs`, `Calcs/ICalcSet.cs`,
`Lighting/LightingSettings.cs`, and `Lighting/StoredPhotometry.cs`.
`upstream.lock.json` preserves all original paths and Git blob hashes. LF checkout
rules and `tools/check-lighting-engine.ps1` protect byte-level provenance.
`EngineIdentity` adds version `0.1.0`, physical model `wwtools-diffuse-v1`, and the
upstream commit without changing the kernel.

This is deliberately the complete pure subsystem, including grid/walking-surface
generation, source correction, layouts, statistics, reports/PNG generation, saved
settings, and the v1 writer. Extracting only LightingSolver leaves CalcZone's
target/report dependencies unresolved. Keeping public upstream namespaces also
minimizes host adapter changes. Revit collectors, API objects/transactions,
annotation, UI, and launch commands remain outside the library.

The library targets **.NET Standard 2.0**, with no new explicit NuGet/runtime
dependencies. The audited WWTools project targets net48 for R21–R24, net8 Windows
for R25/R26, and net10 Windows for R27. This shared target can be referenced by
those frameworks, but net48 and actual Revit loading are **not validated** here.
BimGo App remains net8 Windows; its current Revit R25/R26/R27 net10 settings are
pre-existing and remain unvalidated in this session.

BimGo App has a project reference and its Release output contains its own
`Lighting.Engine.dll`, provenance manifest, and notice. It never resolves an
engine from WWTools' install. `tools/Lighting.HostProbe` consumes the DLL through
the candidate `Lighting.Engine.WWTools.targets` adapter and solves all five
scenes plus CSV reports on .NET 8 and 10 with **no BimGo/Core, WWTools, or Revit
assemblies in its output**. This proves the binary's host independence, not a
clean-machine installation test for the finished products.

The candidate targets remove the original pure definitions as a group, then
reference a supplied engine artifact with CopyLocal. Calcs math also serves
WWTools airflow; failing to remove/reconcile it would create duplicate public
types. Import the targets at the end of WWTools.csproj only after its host build
and original tests pass. Its existing source-linked unit tests must likewise
consume the binary (with an appropriate test-access policy for internal helpers).
The remote WWTools branch was read only; no code or report behavior was changed
there by this work.

There is no WWTools LICENSE/redistribution grant in the inspected tree; its
project declares Wiley & Wilson copyright and all rights reserved. Ownership is
preserved in `UPSTREAM-NOTICE.md` and the product notices. Public package creation
is disabled pending shared-engine terms. The import is a pinned development
snapshot, not an independently maintained solver fork. The next adoption step is
one maintained release source/artifact consumed by both hosts; future kernel
fixes must not be duplicated separately in BimGo and WWTools.

## Solver-ready inputs and host extraction policy

| Input | Compatibility requirement / adapter policy |
|---|---|
| Coordinates | Double-precision Revit host feet. Link instances are transformed into host coordinates. BimGo scene metres and local origin are boundary conversions only. |
| Geometry | Ordered triangles with winding, class, explicit reflectance or negative class fallback, and element tag. Complete domain must remain closed where physically enclosed. |
| Zones | Footprint loops, bottom/top, point positions/order and shared normal, workplane/grid/offset, level/stair mappings, and reflectance basis. |
| Sources | Full raw/resolved IES, source position, nadir, horizontal axis, luminous dimensions, multiplier, type identity, watts, and resolution/correction provenance. |
| Quality | Interreflection flag, patch size/ray counts, gather rays, max patches/iterations and convergence threshold. Fixed index-derived seeds make triangle/patch/point ordering significant. |
| Materials | Scalar diffuse reflectance; class defaults ceiling .8, wall .5, floor .2, furniture .5 unless resolved otherwise. Explicit face reflectance wins. |
| Domain | Runner expands each zone's XY by RegionMargin (default 15 ft), bottom by 1.5 ft and top by 3 ft. Preserve its filtering and source-selection policy for parity. |

`LightingSceneBuilder` extracts host and optionally linked building/furniture
categories. Lighting fixture housing geometry is excluded by its category list.
Simplified geometry can replace selected doors/casework/furniture/equipment with
boxes, while floors/stairs/ramps retain geometry. Surface class depends on
orientation/category. Its material policy **omits faces with transparency >=50**;
this is an opening approximation, not glass transport. Material mode estimates
scalar reflectance from linearized sRGB luminance clamped to .05–.9. Zone mode
uses space parameters where supplied. These are assumptions/provenance, not
measured optical properties.

`PhotometryResolver` resolves full photometry and source/type factors before the
kernel. It can use estimated distributions; canonical scenes must preserve that
status explicitly, never silently substitute them for missing verified IES.
Runner source correction moves qualifying downward sources below nearby ceiling
undersides (up to 1 ft reach, placing the source 1 inch below). Preserve the
resolved photometric center as well as its original placement/correction.

For BimGo's complete input export, share these extraction rules where practical;
do not reconstruct solver geometry from visible ISO meshes or saved patch
coverage. A ceiling hidden for a cutaway still participates in physical transport.
The v1 output has no stable face/material identity and SurfacePatch drops input
tags, so calculated material mapping needs an additive stable-identity contract.

## Compatibility limitations found

- Direct uses full IES interpolation/orientation, cosine/inverse-square,
  occlusion, and near-field equal-intensity sub-sources. It offsets surface ray
  origins .01 ft and stops rays .05 ft short of emitters. Preserve these constants
  while characterizing; analytic Solve checks must account for that offset.
- IES parser flags but **ignores tilt factors**, including TILT=INCLUDE. External
  tilt references are not resolved. BimGo's existing preview may use a different
  policy. Compatibility results must disclose this rather than claiming complete
  LM-63 tilt fidelity.
- The engine rebuilds patches, BVH, direct fields, form factors, iterations and
  final gather for every solve. There is no reusable transport cache yet. Material
  changes need the coupled indirect solve; source edits need new direct light and
  then indirect/final gathering. These full solves are the future cache oracle.
- Jacobi stops when global maximum patch change / max(current patch value,
  1e-9) falls below Convergence, or the iteration cap. It exposes count but no
  residual, explicit convergence flag, or progressive result bundle. Treat a run
  at the cap conservatively as incomplete until diagnostics are added in the one
  maintained kernel. Sampling error is separate from that stopping criterion.
- MaxPatches is a soft mesh-size adjustment, not a hard memory guarantee when
  input triangles themselves exceed the limit. Form-factor hit storage alone is
  `4 * patchCount * max(8, PatchRays)` bytes. Validate budgets before allocation.
- Solve mutates a caller's CalcZone before completion, including on cancellation.
  Never solve into the currently published UI object. A worker needs isolated
  immutable inputs and an atomic, revision-checked publication wrapper.
- Output is scalar diffuse. There is no colored bounce, directional incident
  field, glossy/specular/glass transport, or renderer validation. RoomRender's
  existing report images and BimGo's live/normalized overlays do not meet CA-3/5.
- v1 caps output meshes at 1,000,000 patches and can omit covered/furniture/whole
  zone meshes; it does not preserve full enclosure or full emitter photometry.
  BimGo's current reader also discards the direct value stored in point records.

## Reproducible numerical baseline

Corpus: `tests/Lighting.Corpus`, generated by a test-only replay schema with all
ordered triangles, full IES and frames, units, grid settings and solver options.
Full point/direct/surface arrays, statistics and counts are saved, not just
rounded averages. Every golden pins the input hash and upstream implementation.

| Synthetic scene | Points | Patches | Iterations | Average fc |
|---|---:|---:|---:|---:|
| Small 12 x 10 x 9 ft room, one 4000 lm source | 30 | 384 | 11 | 17.448749669 |
| 40 x 30 x 9 ft office, 20 x 4000 lm, LLF .8 | 300 | 3072 | 10 | 49.767459355 |
| Near-field 2 x 4 ft emitter and casework | 30 | 488 | 10 | 29.366295796 |
| Asymmetric source | 30 | 384 | 10 | 9.309434558 |
| Two rooms with an opening, one physical domain | 60 | 744 | 10 | 9.306995467 |

All passed on Windows x64 .NET **8.0.31 and 10.0.12** against the same goldens at
**1e-10 fc absolute + 1e-10 relative**, with explicit NaN/infinity handling.
Geometry, order, counts, surface fields and statistics are compared too. This is
a tight extraction-equivalence target; certify other CPU/OS combinations before
claiming universal bitwise agreement.

Separate physics checks cover inverse-square/cosine, LLF, obstruction, zero and
near-zero sources (absolute fc bounds, without misleading percentage errors), and a
closed diffuse cube's indirect energy estimate (10% spatial/sampling allowance,
following the upstream analytic test). Material tests require unchanged direct
arrays and changed total light. Move/add/remove/undo, deterministic re-solve,
cancellation and standalone report generation also pass. This is full rebuild
behavior; no incremental cache parity is claimed.

Iteration study on the small room, compared with the same discretization at
1e-9 threshold: threshold .01 stops after 5 iterations with max point difference
.072535 fc; .0002 stops after 11 with .001007 fc; 1e-6 stops after 18 with
.00000684 fc. A one-iteration cap yields 16.270826335 fc and must be incomplete.
Draft/Normal/High averages are 17.486358275 / 17.448749669 / 17.432543168 fc at
96 / 384 / 1536 patches. These separate spatial/ray differences are not a
residual claim, monotonic-accuracy guarantee, or physical-reference certification.

## Saved Snowdon data agreement, separate from numerical parity

An existing local Snowdon Electrical v1 pair was inspected read only. Run:
2026-10-21 23:42:10 UTC, WW Tools 1.0.0.0, Normal, interreflection enabled.
`ca0-snowdon-data-agreement.json` records both file hashes, sizes and findings;
the 15.4 MB project-specific pair is not copied into the repository.

BimGo accepted all **80 zones, 11,673 points (436 NaN), 252,629 patches** with no
dropped zones/patches. Total point fc, patch fc, reflectance and flags agree
exactly with float32 records. Recomputed point statistics match saved JSON under
`1e-6 fc + 2e-6 relative`, maximum observed absolute difference .00000763 fc.
Maximum feet-to-metres coordinate roundoff was .00000232 m at origin zero.
This validates saved data agreement only. It cannot reproduce the Revit solve:
the pair omits photometry, full transport geometry and extraction settings.

Required real golden capture: immediately before runner calls Solve, serialize
each resolved CalcZone, exact selected luminaires/full IES, ordered transport
triangles after all extraction/simplification/link/source corrections, options,
domain/settings/provenance and physical-model version. Immediately afterward,
capture double point/direct/surface arrays, statistics, diagnostics and an input
hash. Re-run that input offline, then compare under the extraction tolerance.
Include Snowdon stair/atrium, a room with an embedded ceiling fixture, and a
connected/multi-level domain. A future public scene schema must add composite
source/face/material/link identities; this test-only SceneCase is insufficient.

## Performance baseline and provisional budgets

`ca0-benchmark.json` contains 20 repetitions per scenario/operation, per-stage
engine timings, allocations, process peak working set, iteration/sample study,
runtime/OS/CPU identifiers and core count. Hardware identity reported by the
environment: **Intel64 Family 6 Model 151 Stepping 2, GenuineIntel; 16 logical
processors**, Windows 10.0.26200 x64, .NET 8.0.31. A marketing CPU name was not
available through the sandbox. Use this identifier, not an inferred SKU.

| Scenario / operation | p50 ms | p95 ms |
|---|---:|---:|
| Small room full rebuild | 7.26 | 8.85 |
| Small room material / moved source / added source | 7.96 / 8.88 / 9.58 | 9.76 / 11.67 / 11.90 |
| Office full rebuild | 74.27 | 90.67 |
| Office material / moved source / added source | 78.01 / 81.59 / 77.52 | 93.73 / 89.05 / 93.54 |
| Connected opening full rebuild | 12.26 | 14.67 |
| Near-field obstruction full rebuild | 8.05 | 12.92 |

Allocation medians: about .67 MB per small room solve and 7.83 MB per office
solve. Process peak working set reached about 86 MB cumulatively in the harness,
not a per-solve memory peak. First-valid times are first measured calls after
corpus verification, **not fresh-process cold start**; they include any remaining
scenario JIT. Time to first progressive result equals completion because the
engine has no progressive publication. UI frame time, real Snowdon solve times,
cold process startup, drag scheduling, GPU upload and renderer times remain
unmeasured. Performance can vary under system load and with input ordering.

Provisional CPU regression budgets for these specific Normal synthetic fixtures:
small room p95 <=100 ms, office p95 <=250 ms, full rebuild including edit cases.
The margin allows host overhead but must be remeasured with the worker and UI.
Provisional interaction goal: mark edits/updating within one displayed frame;
coalesce drags by roughly 50–100 ms and complete after release. No whole-building,
Snowdon, high-quality, or full-appearance latency promise is justified yet.

## Backend decision and implementation estimate

Use this unchanged CPU diffuse implementation for compatibility characterization
and CA-1. Measurements support trying background full solves for selected small
domains before adding caches. The cache work should prioritize geometry/BVH,
patch-hit transport and final-gather reuse; verify every optimized path against
the saved full-solve oracle. Do not replace a slow solve with preview lighting.

The full material backend decision remains open. This scalar CPU prototype
cannot evaluate directional/glossy/transmissive appearance. Before choosing a
production GPU/CPU backend, prototype shared emitter/geometry/material inputs and
compare raw radiance plus point illuminance against a high-sample independent
reference. No new native renderer dependency or visual accuracy claim is adopted
by this extraction. That uncompleted prototype is another reason CA-0's full gate
is not closed.

Work-size estimate, expressed as reviewable implementation slices rather than a
calendar promise; confidence is medium for CA-1 and low for the physical backend:

| Stage | Remaining slices / exit evidence |
|---|---|
| CA-0 closeout | 2–3: real pre/post-Revit scene capture and parity, named-hardware cold/UI benchmark, backend comparison prototype. |
| CA-1 | 3–4: ownership/release source and artifact, WWTools replacement and original characterization tests, revision-safe shared contracts/diagnostics, independent deployment matrix. |
| CA-2 | 5–8: canonical schema/validation, native Revit exporter parity, set/grid authoring, photometry/material configuration, offline worker, persistence/undo, reports and old-file coverage. |
| CA-3 | 3–5: stable surface mapping/coverage, raw diffuse shader and fixed exposure, revision bundle integration, raw-HDR/analytic validation, cutaway/UI verification. |
| CA-4 | 4–7: physical scenario edits, invalidation/caches, cancellation/coalescing/atomic publication, fixture operations, undo/persistence, connected-domain and full-recompute comparisons. |
| CA-5/6 | Re-estimate after backend prototype: colored/directional transport, independent reference scenes, clean product installs/reports/version mismatch handling, optional Revit application/re-extraction. |

## Commands and checks

From the repository root in PowerShell:

```powershell
./tools/check-lighting-engine.ps1
./tools/check-lighting-engine.ps1 -Framework net10.0
./tools/check-lighting-engine.ps1 -Benchmark -BenchmarkOutput ./tools/Lighting.Baseline/bin/new-benchmark.json
dotnet build tools/Lighting.HostProbe -c Release --disable-build-servers -m:1
dotnet tools/Lighting.HostProbe/bin/Release/net8.0/Lighting.HostProbe.dll
dotnet tools/Lighting.HostProbe/bin/Release/net10.0/Lighting.HostProbe.dll
```

`Lighting.Baseline` also supports `exchange <saved-results.json> <audit.json>`;
it reads the pair and verifies BimGo's actual reader without reconstructing inputs.
The normal verifier checks source hashes and never overwrites golden files.

Validation this session: App Release build succeeds (engine and notice copied);
**543 Core tests passed, 0 failed/skipped**, including 10 new engine/integration
tests; both runtime corpus replays and binary consumer probes pass; all 37 source
hashes agree; `git diff --check` passes. Existing XML/analyzer warnings remain.
NuGet vulnerability metadata was unreachable during one restore (NU1900); cached
package restores/builds succeeded. No Revit build, Revit numerical scene replay,
native calculator UI, rendered appearance, field accuracy, or fresh-machine
product installation verification is claimed.

The existing HUD ISO mouse-capture issue and unvalidated Revit .NET retargeting
remain separate follow-ups. Continue next with real WWTools scene capture and
the shared release/adapter migration; keep appearance and numerical acceptance
separate throughout.
