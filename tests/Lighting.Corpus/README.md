# CA-0 synthetic replay corpus

Five complete CPU compatibility inputs, captured from the unchanged WWTools
integration-branch implementation at `7c307ebed155dacba0a567e23ee639b2b793c54d`.
They are synthetic scenes, not exported Revit golden runs. They exercise a small
room, open office, near-field source with casework, asymmetric photometry, and
connected rooms through an opening. The closed-cube analytic check is generated
by the verifier separately.

Each `.scene.json` preserves ordered triangles with winding, class, reflectance
and tag; full raw IES; source frames and factors; zone/grid settings; explicit feet
and fc; physical-model identifier; and every solver option. Point/patch order and
the engine's fixed index-derived sampling seeds are reproducible. SceneCase is a
test-only replay schema, not the planned public calculation-scene format.

Each `.golden.json` pins the scene's SHA-256 and upstream commit and saves all
point positions/direct/total values, the full retained surface output, statistics,
patch/iteration/inside counts. A surface record has 16 doubles:
`A.xyz B.xyz C.xyz Normal.xyz reflectance illuminance class covered`.
The source scene records already define their generation order.

`tools/check-lighting-engine.ps1` checks every imported source blob and replays
the corpus against **1e-10 fc absolute + 1e-10 relative** tolerance, with NaN and
infinity compared explicitly. Geometry/count/order differences fail too. The
same goldens passed on .NET 8.0.31 and .NET 10.0.12, Windows x64. This tolerance is
an extraction characterization target; other platforms still need certification.

To capture a replacement corpus, build `tools/Lighting.Baseline` and run its DLL
with `capture <new-empty-directory>`. Capture refuses an existing matching input
or golden. Review replacements as a model/engine change; never regenerate goldens
to make an unexplained mismatch pass. The normal verification path never writes
goldens. LF checkout rules preserve input hashes on Windows.

The physics, iteration, sampling, and future appearance tests have separate
acceptance criteria. Replaying this corpus does not certify real-world accuracy,
Revit extraction parity, incremental caching, or rendered appearance.
