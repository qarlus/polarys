# Theme Packs

A theme pack restyles Polarys with an **icon pack** and a **widget pack**. Polarys installs its bundled theme library into your user theme folder on first launch. You can also add or edit your own theme folders there.

## Where themes live

```
%LOCALAPPDATA%\OpenTaskbar\Themes\
└── my-theme\
    ├── theme.json          ← name, author, description
    ├── icons\
    │   ├── pack.json       ← icon pack
    │   ├── firefox.png     ← matched to firefox.exe without a mapping
    │   └── icons\…
    └── widgets\
        └── pack.json       ← widget pack
```

Both packs are optional. Pick a theme in **Settings → Appearance → Theme → Theme pack**. **Open folder** opens the `Themes` folder; **Reload** rereads the selected theme after you change its files.

The built-in library includes Catppuccin Mocha, Gruvbox Material, Nord, Prism, Solarized and Tokyo Night. A starter theme is in [theme-template](theme-template). Bundled themes are available in [themes](../themes).

## theme.json

```json
{ "name": "My Theme", "author": "You", "description": "Optional." }
```

## Icon pack: icons\pack.json

```jsonc
{
  "icons": {
    "firefox.exe": "icons/browser.png",
    "Microsoft.WindowsTerminal": "icons/terminal.png",
    "@start": "icons/start.png",
    "@recyclebin": "icons/trash.png",
    "@recyclebin-full": "icons/trash-full.png"
  },
  "fallback": {
    "shape": "Petal",
    "sharpCorner": "TopRight",
    "cornerRadius": 0.42,
    "background": "auto",
    "iconScale": 0.62
  }
}
```

Comments and trailing commas are allowed.

### Icon keys

Applications always keep their real icon. The `icons` map supplies artwork only for the special items
and for apps whose icon could not be resolved, so a pack can never replace an app's own logo with a
generic category image. For each lookup, the first key that matches wins:

| Key | Example | Matches |
| :--- | :--- | :--- |
| AppUserModelID | `Microsoft.WindowsTerminal_8wekyb3d8bbwe!App` | One Store or packaged app |
| Package name | `Microsoft.WindowsTerminal` | The part of the ID before `_` |
| Full path | `C:\Program Files\App\app.exe` | One install of an app |
| File name | `firefox.exe` | Any `firefox.exe` |
| Name | `firefox` | Same, without the extension |
| Special item | `@start`, `@recyclebin`, `@recyclebin-full` | The Start button, the Recycle Bin empty or full |

Keys are not case-sensitive. Values are paths relative to the `icons` folder; a path that leads outside it is ignored.

An image named after the executable, such as `firefox.png` in the `icons` folder or in `icons\icons\`, is found without a mapping.

Supported image formats: PNG, ICO, JPG and BMP. A square PNG of 128 to 256 pixels works best.

### Fallback plate

Every app keeps its own icon. With a fallback `shape`, that icon is drawn on a plate so the whole dock matches the theme:

| Field | Values | Default |
| :--- | :--- | :--- |
| `shape` | `None`, `Leaf`, `Petal`, `Rounded`, `Circle` | `None` |
| `sharpCorner` | `TopLeft`, `TopRight`, `BottomLeft`, `BottomRight` | `TopRight` |
| `cornerRadius` | `0` to `0.5`, a fraction of the icon size | `0.5` |
| `background` | `auto` (a pale tint of the icon) or a color such as `#FFFFFF` | `auto` |
| `iconScale` | `0.2` to `1.0`, the size of the icon on the plate | `0.64` |

`Leaf` keeps one corner square and rounds the other three. `Petal` keeps `sharpCorner` and the opposite corner square, like many Sailfish OS app icons.

## Widget pack: widgets\pack.json

A widget pack replaces **design tokens**: the named resources in [`Design/`](../Design) that the dock, widgets, menus and Settings are drawn with.

```jsonc
{
  "tokens": {                       // both themes
    "WidgetAccent": "#FF1FC2F0",
    "WidgetAccentFill": { "stops": ["#FF5AD9FF", "#FF1595D3"], "direction": "vertical" },
    "WidgetRadius": 16,
    "DockSheenRadius": "21,21,0,0",
    "InterfaceFont": "Segoe UI Variable Display"
  },
  "dark":  { "DockMaterial": { "stops": ["#FF0E2233", "#FF050E17"] } },   // Dark theme only
  "light": { "DockMaterial": { "stops": ["#FFEAF7FD", "#FFCFE6F3"] } }    // Light theme only
}
```

A value's type follows the token it replaces:

| Token type | Value | Examples |
| :--- | :--- | :--- |
| Brush | `"#AARRGGBB"`, or a gradient: `stops` (2 or more colors) and `direction` (`vertical`, `horizontal`, `diagonal`) | `WidgetAccent`, `TextPrimary`, `DockMaterial`, `WidgetCard` |
| Corner radius, thickness | A number, `"x,y"` or `"left,top,right,bottom"` | `WidgetRadius`, `DockRadius`, `WidgetSpacing` |
| Size | A number | `WidgetHeight`, `TextSizeWidgetValue` |
| Font | A font family name | `InterfaceFont` |

Brushes and colors change at once. Sizes, radii and fonts are read when a view is first built, so they take effect after Polarys restarts. Unknown tokens and values of the wrong type are skipped and written to `%LOCALAPPDATA%\OpenTaskbar\log.txt`.

The dock and menu glass (`DockMaterial`, `MenuMaterial`) keep the **Surface opacity** setting: their colors come from the theme and their opacity from Settings.

Token names are listed in [Design/Palettes/Dark.xaml](../Design/Palettes/Dark.xaml), [Design/Theme.xaml](../Design/Theme.xaml) and [Design/Widgets.xaml](../Design/Widgets.xaml).

## Licensing

Only use icons whose license allows you to share them, and prefer category icons to application logos: a logo is its owner's trademark even when an icon set's license allows copying it. Prism follows this rule and is the only theme in this repository.
