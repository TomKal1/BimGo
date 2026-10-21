# Round X, build X-C1: sharp reflections (stills) + all fixtures (2026-10-19, revision 4)

Revision 2 after Gavin's first test (SSR on glass / mirrors mixed with the probes and vanished up close). Built on the
X-B3 zip + the InputCatalog fix. **Not compiled here** (no .NET SDK in the sandbox): build Core and App (the Revit add-in is
unchanged), then the Core tests. Checked by script: brackets / strings / char literals, UI strings against the font atlas,
new member names across the partials, BOMs kept. **Shaders:** every program compiled in headless WebGL2 (ANGLE /
SwiftShader, GLSL ES 3.00): 35 / 35, the scene program links (16 samplers, the 15 KB far-lights block). Two functional
tests: `BimGo_Reflections_XC1_planar_test.png` (the C# planar maths: a mirror wall showing the red box and the green box
*behind the camera*, not the blue box behind the mirror, faces keeping their sides) and `BimGo_Reflections_XC1_ssr_test.png`
(the SSR march on a floor: medium, debug colours, low, high, roughness 0.35, off). **No new keys.**

## Revision 4 (Gavin's third test: panes popping between views live, a sliding door's second panel not matching, the roof patchwork with probes on)
Gavin's call: **the live view as X-B3, sharp reflections in stills.**
- **Live view:** probes as before (no SSR, no mirrored views: zero cost). *Colour by SSR* still turns them on live for
  checking.
- **Stills** (photos, videos, batch stills, 360s; photo mode's preview with 2 planes at half resolution): mirrored views of
  the planes that look biggest, **Low 2** at half resolution, **Medium 4** at full, **High 8** at full (each at most
  2048 px; freed after the shot), within 40 m; SSR on flat floors and water. A still is first drawn small (≤ 1600 px, no
  mirrored views) from its own camera so the floors have a previous frame to reflect (bookmarks and 360 faces are far
  from the live view). Thumbnails skip it.
- **Sliding doors / thick glass:** parallel planes up to 16 cm apart merge when their boxes touch, keeping how far their
  panes lie either side (*slack*); the shader matches within it (+ 3 cm) and the mirrored view's cut sits beyond it, so
  both panels of a sliding door show the same reflection.
- **Roof patchwork:** up-facing surfaces outside rooms (roofs, decks, paths) reflect the sky instead of their fallback
  probe (an 8 m grid of probes, each box-projected differently: the blocks you saw).
- Setting row: **Sharp reflections (photos, videos)** Off / Low / Medium / High.

## Revision 3 (Gavin's second test: blocky patches on a night roof, mirrors odd up close)
- Screen-space reflections only on **flat** surfaces facing up (normal z > 0.9: floors, tops, water); sloped roofs and
  ramps keep the probes (half-resolution depth on a grazing roof gave blocky patches).
- **Mirrors look like mirrors:** a surface showing a mirrored (planar) view reflects at least 85 % (× strength), untinted
  by a metal colour (a grey "metal" mirror material had made it a dim tinted mix).
- **Plane choice:** scored by how big it looks (area × facing ÷ (distance² + 0.25)), so a mirror at arm's length beats a
  far window; last frame's planes are favoured (× 1.5) so they don't flicker between near-equal panes.


| Item | What it does | Where |
|---|---|---|
| **Planar reflections (glass and mirrors)** | The model's glass and mirror-like surfaces are grouped by plane at load (both faces of a pane and coplanar panes are one plane; at least 0.3 m²; walls only). Each frame the best 1–2 planes in view within 30 m (big and near wins) are drawn again from the camera **mirrored in the plane**, everything behind the plane cut away, so a pane or mirror shows the room **behind you**, sharp and stable, up close or far. Other glass keeps the probes / sky. | Core `ReflectivePlanes`; `SceneRenderer.PlanarReflections.cs`, `PLANAR_GLSL`, `SCENE_FS` |
| **Screen-space (floors and water)** | Surfaces facing up (water, polished floors and tops) still sharpen their probe reflection with what is on screen (as revision 1). Glass and walls no longer use it (that's what looked "wack"). | `SSR_GLSL` |
| **Setting** | Esc → Reflections → **Sharp reflections**: Off / **Low** (1 plane at half resolution + SSR 12 steps) / **Medium** (2 planes at half, SSR 24; default) / **High** (2 planes at full, SSR 48). Saved; the Realistic quality profile sets Medium. | `GameSession.Reflections` |
| **Debug** | Esc → Appearance → COLOUR BY → **SSR**: blue = a mirrored (planar) view, green = SSR found a surface, magenta = probes / sky, grey = not reflective. | `SCENE_FS` |
| **All fixtures** | Photos (and photo mode's preview), videos, batch stills and time-lapses light **every fixture in view**: the nearest 32 with their shadows as before (no fading at the edge of the 32), the rest unshadowed, nearest first, up to 480. Live: SUN, SHADOWS & LIGHTS → **All fixtures live (slower)** (saved). | `GameSession.Lights`, `ArtificialLighting`, `LIGHTS_GLSL` (`FarLights` block) |

### Cost
- Planar: each plane draws the scene again (sky, opaque, ground, billboards, glass; no grass, AO or reflections inside),
  at half resolution on Low / Medium. Expect roughly a 20–40 % frame-rate drop while near big glass; nothing when none is
  in view. High (full resolution) costs more.
- All fixtures live: every pixel loops over the unshadowed lights in view (each skipped quickly outside its reach). With
  hundreds of fixtures in view expect a large drop; photos / videos just take a little longer.

### Limits
- Unshadowed (far) lights pass through walls; their reach is short (2.5–9 m), so it shows mainly where a far fixture is
  just behind a wall.
- Only the 1–2 best planes are mirrored at a time; when another pane becomes the best, the switch is instant.
- Curved glass, small panes (under 0.3 m²) and glass beyond 30 m keep the probes.

### Tests
`ReflectivePlaneTests.Find_MergesPaneFacesAndCoplanarPanes_SkipsOthers` (new); `QualityProfileTests`: the Realistic
profile sets Medium, another choice reads as Custom.

## Not in (X-C2 next)
Stills-only extras: 4 planes at full resolution for photos / videos / batch stills (about 2 s a still).

## How to check it
1. **Build** Core and App; run the Core tests.
2. Realistic mode, Esc → Reflections: *All* (or Some), *Probes*, **Sharp reflections (photos, videos): Medium**.
3. **Live view:** walk round as in X-B3: no popping panes, no blocky steps; the frame rate as X-B3. The **roof** at
   night: no patchwork (it reflects the night sky evenly).
4. **Photo mode** in the bedroom: the sliding door (both panels) and the wardrobe mirror show the room behind you in the
   preview; take the photo: the same, sharper. Then a photo of the facade from your last screenshots: every big pane
   facing you shows the opposite wing.
5. **Batch stills** from a few bookmarks and a **360**: glass reflects in each; polished floors show furniture.
6. **Video:** a few seconds past glass and a pond: reflections stay put (planes may change as the view changes; tell me
   if that shows).
7. **Night shot:** sun below the horizon, Lights *Glow + light*; a photo across the site: distant fixtures light their
   surroundings. Tick **All fixtures live (slower)**: the live view matches; note the frame rate.
8. **Colour by SSR** (Esc → Appearance): blue planar, green SSR, magenta probes / sky (live too, for checking).
9. **Restart:** both settings remembered.
