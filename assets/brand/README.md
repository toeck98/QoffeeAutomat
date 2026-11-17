# Brand assets (placeholder)

These files are placeholders generated together with the project plan. The owner
replaces them with final artwork before release 1.0 (PLAN.md S18, item O-05; task T606).
Keep the file names so that nothing else has to change.

| File | Size | Used for |
|------|------|----------|
| `icon.svg` | vector master | source of all icon sizes |
| `icon.png`, `icon@2x.png` | 256, 512 px | Home Assistant `brand/`, UI favicon and app icon |
| `logo.svg`, `logo.png`, `logo@2x.png` | 128, 256 px high | README, docs, Home Assistant `brand/` (light backgrounds) |
| `dark_logo.svg`, `dark_logo.png`, `dark_logo@2x.png` | 128, 256 px high | same, for dark backgrounds |
| `app.ico` | 16 to 256 px | Windows executables, installer, tray icon |

The web UI bundles copies of `icon.svg`, `icon.png`, `icon@2x.png` and `app.ico` (as `favicon.ico`) in
`src/QoffeeAutomat.Ui/wwwroot/`, because the UI is served as local static files and loads nothing from another
host. When you replace a brand file, copy it again; `BrandAssetTests` in `tests/QoffeeAutomat.Ui.Tests` fails
while a copy differs from its source.

Colours: background `#17424F`, accent `#F2A65A` (on light backgrounds `#C9772B`), white `#FFFFFF`.
The logo SVGs use the font Inter (bold) with system fallbacks; the PNGs are rendered with Inter.
