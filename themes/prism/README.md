# Prism

A theme pack for Polarys: every app gets an icon for what it is (browser, terminal, music, chat, code, …) on a lit squircle plate, and the dock, widgets, menus and Settings take a graphite style with a violet-to-sky accent.

Prism uses no application logos. Its glyphs are the duotone icons of [Phosphor](https://phosphoricons.com) under the MIT license; everything else is MIT too. See [LICENSE](LICENSE).

## Install

1. Copy this `prism` folder to `%LOCALAPPDATA%\OpenTaskbar\Themes\`.
2. Open **Settings → Appearance → Theme** and pick **Prism** under **Theme pack**.
3. Restart Polarys once so the rounder corners and the font apply; colors and icons change at once.

Apps Prism does not know are placed on a rounded plate tinted from their own icon. Each category has its own hue; saturation and lighting are shared, so the set reads as one family.

## Add an app

Add its executable name or Store package name to a category in [`generate.mjs`](generate.mjs) and run it:

```powershell
npm install --no-save @phosphor-icons/core@2.1.1 @resvg/resvg-js@2.6.2
node path\to\themes\prism\generate.mjs
```

The script rewrites `icons\pack.json` and the images in `icons\icons\`. To map an app by hand instead, edit `icons\pack.json`; the format is in [docs/themes.md](../../docs/themes.md).
