# UAS Object Chooser — a NINA plugin

Brings the **Under African Skies** observing catalogue straight into NINA as a dockable
panel, so you can browse targets, see a real image of each one, check tonight's visibility
from your own location, frame/slew to them, and run an unattended schedule — without
leaving NINA or copying coordinates.

Works anywhere in the world: NINA already knows your latitude/longitude from the active
profile, so every object's altitude and visibility is computed for wherever you actually
are — Namibia, Chile, Spain, Australia — with no location picker needed.

## What it does

- **675+ deep-sky objects** plus the **Sun, Moon and the seven planets** (positions from
  NINA's own NOVAS-backed astrometry, refreshed continuously). Slewing to the Sun is
  disabled for equipment safety.
- **A real sky image for every object** — a Digitized Sky Survey (DSS) cutout centred on the
  object's coordinates, fetched from **STScI** and cached locally. Click to zoom. A
  *Download images for offline use* button pre-caches everything, so no internet is needed
  at the telescope.
- **Short description + "Read more on Wikipedia"** for the selected object (text from the
  Wikipedia summary API, cached).
- **Filtering & sorting**: search by name or catalogue number, type (galaxies / nebulae /
  clusters), hemisphere (SH/NH), sort by max altitude / name / magnitude / size, a
  "well placed tonight" filter, and a **direction filter** that shows only objects in a
  chosen compass sector right now.
- **Recommended focal length** per object — framed to fill about half of your camera's
  sensor (auto-detected from the connected camera).
- **Actions**: *Set in Framing* (hand off to NINA's Framing Assistant), *Slew & center*
  (plate-solve + centre), and a **position & rotation** panel with a live sensor-frame
  schematic plus *Slew, center & rotate* (plate-solve + rotator).
- **Schedule + Automatic mode**: plan objects into dated time-slots (grouped per night,
  overlaps flagged). Turn on **Automatic mode** and, whenever NINA is idle, the plugin slews
  to and images each slot at its time. It only unparks/images while the **Safety Monitor
  reports safe**, parks the mount if conditions turn unsafe or when the session is done, and
  always **stops before dawn** (astronomical twilight at your location) — never starting a
  slot in daylight.

The full catalogue is **embedded** in the plugin, so the list appears instantly and works
fully offline; when online it also refreshes from `underafricanskies.eu/astro-catalog.json`.

## Install

See **[BUILD.md](BUILD.md)**. In short: let GitHub Actions build the `.dll` for you (no local
developer setup), download the artifact, copy it to
`%localappdata%\NINA\Plugins\UnderAfricanSkies.ObjectChooser\`, and start NINA — the
**UAS Object Chooser** panel appears in the Imaging tab.

Requires **NINA 3.0** or newer.

## Project layout

| Path | Purpose |
|------|---------|
| `ObjectChooserPlugin.cs` | Plugin entry point (MEF export). |
| `ObjectChooserDockable.cs` | The panel view-model: catalogue, filtering, visibility, framing/rotation, slew & center, schedule + automatic mode. |
| `ObjectChooserTemplates.xaml` / `.xaml.cs` | The panel UI (DataTemplate exported to NINA). |
| `Models/AstroObject.cs` · `Models/ScheduleEntry.cs` | Data models. |
| `Services/CatalogService.cs` | Catalogue: live fetch → disk cache → embedded fallback. |
| `Services/Visibility.cs` | Altitude / visibility maths (a port of the website's). |
| `Services/Ephemeris.cs` | Sun / Moon / planet positions via NINA astrometry. |
| `Services/AstroImageService.cs` | DSS sky images (STScI) + Wikipedia text, with a persistent offline cache. |
| `Services/Framing.cs` | Focal-length / sensor-framing helper. |
| `Services/ScheduleStore.cs` | Persists the schedule between sessions. |
| `Properties/AssemblyInfo.cs` | Plugin manifest (name, GUID, version, description). |
| `astro-catalog.json` | The embedded catalogue. |

## Notes for the maintainer

- **Sky images** come from the STScI DSS cutout service (greyscale POSS2/UKSTU red), chosen
  for reliability. An earlier version used CDS *hips2fits* (colour), which proved slow or
  unreachable on some networks. The cache prefix (`sky4_`/`big4_`) is bumped whenever the
  source changes so clients re-fetch automatically.
- A few NINA API touchpoints are marked `// VERIFY` in `ObjectChooserDockable.cs`; reconcile
  them against the exact NINA build you target if the compiler complains.

## Licence

GPL-3.0. Made for the Bortle 1 skies of [Under African Skies](https://underafricanskies.eu).
