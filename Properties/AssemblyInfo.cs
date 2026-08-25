using System.Reflection;
using System.Runtime.InteropServices;

// ===========================================================================
//  NINA plugin manifest.
//  NINA reads these attributes to show the plugin in the Plugins manager and
//  (if you submit it) in the online plugin repository.
// ===========================================================================

// The plugin's unique identifier. Keep this GUID stable for the life of the plugin
// (changing it makes NINA treat it as a different plugin).
[assembly: Guid("fcb5d1b9-b3d7-48da-b170-f4b2a10e6fdc")]

// Display name shown in NINA.
[assembly: AssemblyTitle("UAS Object Chooser")]

// Author / organisation.
[assembly: AssemblyCompany("Under African Skies")]

// Short description (one line, shown in the plugin list).
[assembly: AssemblyDescription("Browse the Under African Skies deep-sky catalogue inside NINA — 675+ objects plus the Sun, Moon and planets, with real sky images, live visibility from your profile location, and the ideal telescope focal length for each target.")]

// Product name (grouping).
[assembly: AssemblyProduct("UAS Object Chooser")]

// Plugin version. Bump this on every release.
[assembly: AssemblyVersion("1.0.0.27")]
[assembly: AssemblyFileVersion("1.0.0.27")]

// ---- NINA-specific manifest metadata (AssemblyMetadata key/value pairs) ----

// Minimum NINA version this plugin requires.
[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.0.0.0")]

// Marketing / long description shown on the plugin detail page (Markdown allowed).
[assembly: AssemblyMetadata("LongDescription",
    @"**UAS Object Chooser** brings the Under African Skies observing catalogue directly into NINA.

You'll find the panel in NINA's **Imaging tab** (add it from the panel menu if it isn't already shown). It lists 675+ deep-sky objects together with the **Sun, Moon and the seven planets**, whose positions come from N.I.N.A.'s own NOVAS-backed astrometry (topocentric Sun/Moon, apparent planets) computed for your profile location and refreshed continuously, so they stay accurate. For equipment safety, slewing to the Sun is disabled. Every object shows a real DSS sky image, its type and hemisphere (SH/NH), tonight's maximum altitude and whether it is well placed during the dark hours, plus a **recommended telescope focal length** that frames it to fill about half of your camera's sensor (auto-detected from the connected camera).

The full catalogue is **embedded**, so the list appears instantly and works fully offline; object images can be pre-downloaded for use at the telescope. Select any target to read a short description, then:

* **Set in Framing** — drop it into NINA's Framing Assistant, or
* **Slew & center** — send the mount straight to it.

**Direction filter** — show only objects that are in a chosen compass sector (N…NW) right now, so you can point where the sky is clear.

**Position & rotation** — a per-target panel to set the exact RA/Dec and the camera rotation (position angle), with a live schematic of how the sensor frame sits over the object. Push it to the Framing Assistant, or *Slew, center & rotate* (plate-solve + rotator). Rotation can also be set per scheduled slot.

**Schedule + automatic mode** — press **+** on any object to plan it for a date, start and end time. Slots appear on the right grouped per night, sorted by time, with overlapping slots flagged in red. Turn on **Automatic mode** and, whenever NINA is idle, the plugin slews to and images each slot at its time until the end time — then stops. It can **park the mount** when the session is done, **unpark** it at the start of a slot, and it only ever unparks/images while the **Safety Monitor reports safe** — stopping and parking automatically if conditions turn unsafe. Every run also **stops before dawn** automatically (astronomical twilight at your location), whatever end time you set, and never starts a slot in daylight.

Made for the Bortle 1 skies of [Under African Skies](https://underafricanskies.eu).")]

[assembly: AssemblyMetadata("Homepage", "https://underafricanskies.eu")]
[assembly: AssemblyMetadata("Repository", "https://github.com/wpdrost69/nina-uas-object-chooser")]
[assembly: AssemblyMetadata("License", "GPL-3.0")]
[assembly: AssemblyMetadata("Tags", "Deep Sky,Catalogue,Targets,Framing,Planning,Planets")]

[assembly: ComVisible(false)]
