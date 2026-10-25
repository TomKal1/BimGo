# Settings and files

← [Docs home](README.md)

## Settings

- **Quality profile** (pause menu): Basic, Medium or Realistic; any change of your own shows CUSTOM.
- The menu's tabs: **DISPLAY** (colour mode, anti-aliasing, field of view, mouse, VSync, frame rate limit, ambient
  occlusion, light quality, performance details), **REFLECTIONS**, **APPEARANCE** (water, grass, colour profiles,
  materials) and **LIGHTS** (fixture light profiles).
- **Units**: metric or imperial (feet-inches or decimal feet).
- **CONTROLS**: every key and gamepad button (see [Controls](controls.md)).
- In Revit, the **Options** window (opened by Go and Export) holds what is extracted and the player options.

![The CONTROLS panel](images/settings-controls-panel.png)

## Where things are kept

| What | Where |
|---|---|
| The app | `%LocalAppData%\Programs\BimGo\` |
| BimGo for Revit | `%AppData%\Autodesk\ApplicationPlugins\BimGo.bundle\` |
| Settings and key bindings | `%AppData%\BimGo\settings.json` |
| Logs | `%LocalAppData%\BimGo\Logs\` |
| A live model's comments, bookmarks, paths, measurements, clashes… | `%LocalAppData%\BimGo\Models\<model>_<id>\` (Options → Player can share them beside the model) |
| A `.bimgo` file's | inside the file |
| Pictures and videos | `Pictures\BimGo`, `Videos\BimGo` |

Uninstalling BimGo always keeps your per-model data, and asks whether to remove settings and logs too.
