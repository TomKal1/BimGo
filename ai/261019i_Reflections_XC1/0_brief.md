# Round X, build X-C1: screen-space reflections (brief, 2026-10-19)

X-B3 confirmed (with the `HoldScale` defaults-table fix); no drawings coming from Gavin for now. No working-copy changes.
Gavin away during the build: recommended options taken.

| Question | Answer |
|---|---|
| SSR control | Off / low / medium / high (medium default), Realistic mode only |
| Surfaces | Glass + water + polished floors (the existing reflectivity tiers decide which floors) |
| Planar budget (X-C2) | The 4 largest glass planes per still, about 2 s extra |
| Baseline | The X-B3 zip + the InputCatalog fix |

X-C is split: **X-C1** live screen-space reflections (this build), **X-C2** planar (mirrored-view) reflections of the
largest glass planes for photos, videos and batch stills.

Defaults taken: SSR in the Reflections tab under Source; the Realistic quality profile sets medium (the other profiles
don't touch it: they don't use the Realistic mode); rough surfaces over 0.6 keep the probes; a Debug → SSR colour mode
to see where it finds hits.

## Revision 2 (Gavin's first test)
SSR on glass and mirrors looked wrong: a mix of probe and screen-space reflections, vanishing up close (a pane facing
you reflects what's behind you: off screen). Gavin's answers:
- **Live planar for glass** (recommended): the nearest big glass / mirror planes get a true mirrored render live (Low 1
  plane at half resolution, Medium 2 at half, High 2 at full); SSR only on floors and water.
- **All fixtures** (asked mid-build: "force distant lights on for a night shot"): photos, videos and stills always light
  every fixture in view; live as an opt-in ("All fixtures live (slower)"). Same build.
