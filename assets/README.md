# assets

## FluentSystemIcons-Subset.ttf

Subset of the Microsoft Fluent System Icons icon font, embedded in the app as the
resource `FrameGlass.FluentIcons.ttf` (see `FrameGlass.csproj`).

- Source repo: https://github.com/microsoft/fluentui-system-icons
- Source file: `fonts/FluentSystemIcons-Regular.ttf` (2,900,836 bytes)
- Pinned commit: `9eb184e96268d3293462f9d9d19225b4c7b07774`
- Date: 2026-10-30 (commit date). Added to this repo: 2026-11-01.
- Licence: MIT, see `LICENSE-FluentSystemIcons.txt` (copied from the repo `LICENSE` at the same commit).
- Subset size: 2780 bytes, 12 glyphs, no hinting, no layout features.

### Glyphs (20 px variants, drawn at any size)

Codepoints are read from `fonts/FluentSystemIcons-Regular.json` at the pinned commit.

| Use | Glyph name | Codepoint |
|---|---|---|
| Row on | `ic_fluent_checkbox_checked_20_regular` | U+F28D |
| Row off | `ic_fluent_checkbox_unchecked_20_regular` | U+F291 |
| Spinner up | `ic_fluent_chevron_up_20_regular` | U+F2B6 |
| Spinner down | `ic_fluent_chevron_down_20_regular` | U+F2A3 |
| Tray: overlay visible | `ic_fluent_eye_20_regular` | U+E5F2 |
| Tray: overlay hidden | `ic_fluent_eye_off_20_regular` | U+E5F5 |
| Tray: Settings | `ic_fluent_settings_20_regular` | U+F6A9 |
| Tray: Exit | `ic_fluent_dismiss_20_regular` | U+F369 |
| Sidebar: Overlay | `ic_fluent_layer_20_regular` | U+F4CB |
| Sidebar: Rows | `ic_fluent_text_bullet_list_ltr_20_regular` | U+ECD9 |
| Sidebar: General | `ic_fluent_options_20_regular` | U+F587 |
| Sidebar: About | `ic_fluent_info_20_regular` | U+F4A3 |

Codepoints are NOT stable across font releases. Always re-read them from the JSON at the
pinned commit when the font is updated, and update this table and `Icons.cs` together.

### Regeneration commands

Run from the repo root. fonttools lives in a throwaway venv under the git-ignored `tmp/`,
not in the global Python. Delete `tmp/venv` and `tmp/dl` afterwards.

```
python -I -m venv tmp/venv
tmp/venv/Scripts/python.exe -I -m pip install fonttools
tmp/venv/Scripts/python.exe -I -m fontTools.subset tmp/dl/FluentSystemIcons-Regular.ttf --unicodes=U+F28D,U+F291,U+F2B6,U+F2A3,U+E5F2,U+E5F5,U+F6A9,U+F369,U+F4CB,U+ECD9,U+F587,U+F4A3 --no-hinting --layout-features="" --output-file=assets/FluentSystemIcons-Subset.ttf
```

`tmp/dl/` holds the source TTF, JSON and LICENSE downloaded from the pinned commit:

```
https://raw.githubusercontent.com/microsoft/fluentui-system-icons/9eb184e96268d3293462f9d9d19225b4c7b07774/fonts/FluentSystemIcons-Regular.ttf
https://raw.githubusercontent.com/microsoft/fluentui-system-icons/9eb184e96268d3293462f9d9d19225b4c7b07774/fonts/FluentSystemIcons-Regular.json
https://raw.githubusercontent.com/microsoft/fluentui-system-icons/9eb184e96268d3293462f9d9d19225b4c7b07774/LICENSE
```
