# UAS Object Chooser — NINA plugin (v0.2 starter)

A native NINA plugin that brings the **Under African Skies** object catalogue directly
into NINA. It adds a dockable panel — **"UAS Object Chooser"** — that mirrors the website
tool: same 675 objects, same options (search, type filter, hemisphere, sort), the same
**preview images from Wikipedia**, and **Set in Framing** / **Slew & center** actions —
all inside NINA, no browser, no copied coordinates.

## Answers to your three questions

**1. "UAS Object Chooser" as the name** — done. That's the panel title and the plugin's
display name in NINA. (The internal assembly is `UnderAfricanSkies.ObjectChooser`.)

**2. "Must work everywhere in the world, like the website" — yes, genuinely possible, and
it's actually cleaner here than on the website.** NINA already knows the observer's
latitude/longitude (from the active profile's Astrometry settings). The plugin reads that
and computes each object's altitude/visibility for wherever you actually are — Namibia,
Chile, Spain, Australia — with **no location picker needed**. The catalogue itself is
global (both hemispheres). See `Services/Visibility.cs`, which is a direct C# port of the
website's altitude maths, and `ComputeVisibility()` in the panel.

**3. "Show the pictures like it does now" — yes, same source, and now built — AND it works
offline.** The website pulls its images from the **Wikipedia REST summary API** by article
name. The plugin does the same (`Services/AstroImageService.cs`), but with a **persistent
local cache**: images and descriptions are stored under
`%localappdata%\NINA\Plugins\UASObjectChooser\images\`. A **"Download images for offline
use"** button bulk-downloads every object's picture once (while online); after that the
tool needs **no internet at the telescope**. We deliberately do NOT bundle the images into
the product: Wikipedia/Commons images have mixed licences (many CC-BY-SA, some non-free),
so bundling would be a redistribution with per-image obligations. Per-install local caching
keeps that clean while giving you full offline use.

## What's in v0.2

- **Same data**: the 675-object catalogue, fetched live from `underafricanskies.eu/astro-catalog.json` with an offline cache.
- **Same options**: search (name or catalogue number), type filter (galaxies/nebulae/clusters), hemisphere filter, **sort** (max altitude / name / magnitude / size), plus a **"Well placed tonight"** filter that uses your NINA location.
- **Pictures**: preview image + description + "Read more on Wikipedia" for the selected object.
- **Worldwide**: max-altitude and visibility computed from NINA's profile location.
- **Actions**: Set in Framing, Slew & center.

## Files

| File | Purpose |
|------|---------|
| `Models/AstroObject.cs` | Object model (incl. `wiki`, computed altitude fields). Pure C#. |
| `Services/CatalogService.cs` | Live fetch + offline cache. Pure C#. |
| `Services/Visibility.cs` | Altitude / visibility maths (port of the website). Pure C#. |
| `Services/AstroImageService.cs` | Wikipedia image + description fetch. Pure C#. |
| `ObjectChooserPlugin.cs` | Plugin entry point (MEF export). |
| `ObjectChooserDockable.cs` | The panel view-model, actions, visibility, image loading. |
| `ObjectChooserTemplates.xaml(.cs)` | The panel UI (DataTemplate exported to NINA). |
| `Properties/AssemblyInfo.cs` | Plugin manifest (name "UAS Object Chooser", GUID, version, description). |
| `UAS.ObjectChooser.csproj` | .NET 8 / WPF project referencing `NINA.Plugin`. |

## Build & install (Windows)

1. Prove the loop first: generate an empty plugin from the official template
   (isbeorn/nina.plugin.template), build it, drop the DLL in `%localappdata%\NINA\Plugins\...`,
   and confirm its panel shows in NINA. That's your known-good compilable base.
2. Open `UAS.ObjectChooser.csproj` in Visual Studio 2022; restore packages
   (`Update-Package NINA.Plugin -IncludePrerelease` to match your NINA build).
3. Build (x64), copy `UnderAfricanSkies.ObjectChooser.dll` to
   `%localappdata%\NINA\Plugins\UnderAfricanSkies.ObjectChooser\` (or uncomment the auto-copy target).
4. Start NINA → the **UAS Object Chooser** dockable panel is available.
5. Deploy `astro-catalog.json` to your website root so the live endpoint resolves.

## The only version-sensitive lines (`// VERIFY`)

Reconcile these against your NINA build if the compiler complains — everything else is plain C#:

- `_profileService.ActiveProfile.AstrometrySettings.Latitude / .Longitude`
- `new Coordinates(ra, dec, Epoch.J2000, Coordinates.RAType.Hours)`
- `new DeepSkyObject(name, coords, imageRepositoryPath, horizon)`
- `IFramingAssistantVM.SetCoordinates(dso)`
- `IApplicationMediator.ChangeApplicationTab(ApplicationTab.FRAMINGASSISTANT)`
- `ITelescopeMediator.GetInfo().Connected` and `SlewToCoordinatesAsync(coords, token)`

## Next iterations

- Real plate-solve **centering** for "Slew & center" (a Center container / solver) instead of a plain slew.
- **Add to sequence** as a third action.
- Catalogue URL as a **plugin option** (IPluginOptionsVM).
- Match the website's exact "tonight" dark-hours window for the well-placed test.
