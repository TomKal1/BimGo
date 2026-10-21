# Y-D2 build notes: depth pre-pass, light quality

Not compiled here (no .NET SDK). Brackets, strings and char literals script-checked in every changed `.cs` file, UI
strings against the font atlas. Shaders extracted from `Shaders.cs` and compiled / linked in headless WebGL2
(SwiftShader): scene (with the one-tap change), the new depth pre-pass, ground, grass, billboards: all link.

## What's in
- **Depth pre-pass** (`SceneRenderer.Prepass.cs`, `Shaders.DEPTH_PREPASS_FS`). The main view draws the opaque static
  scene (with occlusion culling's skips) and the moved / cloned elements depth-only first, then the lit opaque draws
  test `EQUAL` with depth writes off: the lighting (32 lights, shadow taps, IES profiles, sun, AO, materials) runs once
  per pixel instead of once per surface drawn over it (chunks aren't drawn front to back). `SCENE_VS` now declares
  `invariant gl_Position` and the pre-pass uses it, so both land on identical depths. The pre-pass drops exactly what
  the lit opaque draw drops (section cuts, hidden profile elements, ghosted / see-through coloured ones). Photos and
  videos use it too; not the clash view (its own drawing, ghost alpha) or the minimap / probes / reflections.
  Options → Display → **Depth pre-pass** (on by default; `LaunchSettings.DepthPrepass`) to compare.
- **Light quality** (Options → Display → Light quality: **Low / Medium / High**; `LaunchSettings.LightQuality`, default
  Medium). Of the live view's picked lights (strongest first, Y-C1's ranking):
  - High: 32 with shadow maps, all 4-tap soft shadows (as before).
  - Medium: the strongest 16 with shadow maps, the first 8 of them 4-tap, the next 8 one tap; the rest unshadowed.
  - Low: the strongest 8 with shadow maps, one tap each; the rest unshadowed.
  - "Unshadowed" = X-C1's far-light loop (no map, no taps, same falloff and profile), so no light goes dark; fewer
    light shadow maps are rendered too. One tap = the hardware's 2 × 2 comparison filter (`sampler2DArrayShadow`,
    linear): harder shadow edges.
  - Photos, videos, photo mode's preview and *All fixtures live* always use High.
  - Shader: `lightVisibility(…, oneTap)` from `uLightShadow[i].z` (1 = one tap); the session sets it per light.
- **Tests:** settings defaults (Medium, pre-pass on; older files: the same; clamped).

## What isn't
- Tiled light culling (each screen tile looping only its lights): next if Medium + pre-pass doesn't reach the target.
- Simplified meshes / small-detail culling (Scale part 3 if big models need it).
- The glass pass isn't pre-passed (blended surfaces can't be); it gets the cheaper lights from the quality setting.

## How to check
1. Same spot as Y-D1, lights on (K → glow + light), Performance details on, occlusion Auto.
2. Options → Display: Light quality **High**, Depth pre-pass **off**: note "opaque" and the GPU total (≈ Y-D1 r2: 16.5 /
   20.5 ms).
3. Depth pre-pass **on** (High): the opaque time should drop (by how much depends on overdraw at that spot).
4. Light quality **Medium**, then **Low** (pre-pass on): opaque drops further; the strongest lights keep soft shadows,
   weaker ones' shadows are harder (Medium) or gone (beyond 16 / 8). Look for lights that switch shadows on / off as
   you walk (a light crossing the 8 / 16 line): say if it's distracting.
5. Look for flicker or speckles on lit surfaces with the pre-pass on (would mean depths don't match: say which GPU).
   Section box cuts, colour profiles with ghosts, moved / cloned elements, highlights: all as before.
6. Take a photo on Low: it should look like High (photos always High).
7. Send the opaque / total ms for: High no pre-pass, High pre-pass, Medium pre-pass, Low pre-pass.

## Result (2026-10-22, Gavin)
Lights on, the Y-D1 test spot: **with the depth pre-pass every quality setting is under 11 ms** (the target); without
it ~18 ms on Medium and High. Overdraw was the bulk of the light cost; Light quality is headroom for slower GPUs.
Y-D1 r3 and Y-D2 confirmed. Tiled light culling and simplified meshes not needed for now.
