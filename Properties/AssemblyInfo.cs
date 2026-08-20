using System.Reflection;
using System.Runtime.InteropServices;

// ===========================================================================
//  NINA plugin manifest.
//  NINA reads these attributes to show the plugin in the Plugins manager and
//  (if you submit it) in the online plugin repository.
// ===========================================================================

// The plugin's unique identifier. Keep this GUID stable for the life of the plugin
// (changing it makes NINA treat it as a different plugin). Generated once for you:
[assembly: Guid("fcb5d1b9-b3d7-48da-b170-f4b2a10e6fdc")]

// Display name shown in NINA.
[assembly: AssemblyTitle("UAS Object Chooser")]

// Author / organisation.
[assembly: AssemblyCompany("Under African Skies")]

// Short description (one line, shown in the plugin list).
[assembly: AssemblyDescription("Under African Skies object chooser — a curated deep-sky catalogue with Wikipedia previews. Works anywhere in the world using your NINA location. Send any object to the Framing Assistant or slew the mount, without leaving NINA.")]

// Product name (grouping).
[assembly: AssemblyProduct("UAS Object Chooser")]

// Plugin version. Bump this on every release.
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

// ---- NINA-specific manifest metadata (AssemblyMetadata key/value pairs) ----

// Minimum NINA version this plugin requires.
[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.0.0.0")]

// Marketing / long description shown on the plugin detail page (Markdown allowed).
[assembly: AssemblyMetadata("LongDescription",
    @"**UAS Object Chooser** brings the Under African Skies observing catalogue directly into NINA.

The panel loads a curated list of 675 deep-sky objects — optimised for the southern Namibian sky — live from underafricanskies.eu, with an offline cache so it keeps working at the telescope. Every object shows its catalogue number, type, hemisphere (SH/NH), magnitude and size. Search by name or catalogue number, filter by type, then:

* **Set in Framing** — drop the object into NINA's Framing Assistant to plan your composition, or
* **Slew & center** — send the mount straight to it.

No browser, no copy-pasting coordinates — it all happens inside NINA.")]

// Optional metadata (fill in as you like):
[assembly: AssemblyMetadata("Homepage", "https://underafricanskies.eu")]
[assembly: AssemblyMetadata("Repository", "https://underafricanskies.eu")]
[assembly: AssemblyMetadata("License", "MIT")]
[assembly: AssemblyMetadata("Tags", "Namibia,Southern Sky,Targets,Framing,DSO")]

[assembly: ComVisible(false)]
