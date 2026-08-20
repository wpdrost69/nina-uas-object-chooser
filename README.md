# UAS Object Chooser — a NINA plugin

A dockable **N.I.N.A.** panel that lists the **Under African Skies** deep-sky catalogue
(675+ objects) together with the **Sun, Moon and the seven planets**, computed for the
current date and your NINA **profile location** — so it works anywhere in the world.

## Features
- 675+ deep-sky objects plus Sun, Moon and planets (positions computed for tonight)
- Real DSS sky image for every object; type + hemisphere badges
- Tonight's max altitude and a "well placed during dark hours" test, from your location
- Recommended **telescope focal length** to frame each object at 60–80% of the sensor,
  auto-detected from the connected camera
- Catalogue is **embedded** — instant, works fully offline; images can be pre-downloaded
- Push any target to the **Framing Assistant** or **slew & centre** the mount

## Install (users)
Once published, install it from **NINA → Plugins → Available → UAS Object Chooser**.
Manual install: copy `UnderAfricanSkies.ObjectChooser.dll` into
`%localappdata%\NINA\Plugins\3.0.0\UAS Object Chooser\` and restart NINA.

## Build (developers)
Requires the .NET 8 SDK (Windows).
```
dotnet build -c Release
```
The plugin DLL is produced under `bin/Release/net8.0-windows/`.

## License
GPL-3.0 — see the LICENSE file.

Made by [Under African Skies](https://underafricanskies.eu).
