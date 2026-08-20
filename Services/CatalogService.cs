using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UnderAfricanSkies.ObjectChooser.Models;

namespace UnderAfricanSkies.ObjectChooser.Services {

    /// <summary>
    /// Fetches the object catalogue from the Under African Skies website and keeps a
    /// local cache so the plugin still works at the telescope without internet.
    ///
    /// Strategy on Load():
    ///   1. Try the live URL (short timeout). On success -> update the local cache and return it.
    ///   2. On any failure -> fall back to the last cached copy on disk.
    ///   3. If there is no cache either -> return an empty list (caller shows a message).
    ///
    /// No browser, no CORS, no mixed-content: this is a plain .NET HttpClient GET,
    /// which is exactly why the native plugin sidesteps every web-page limitation.
    /// </summary>
    public class CatalogService {

        // Where the site serves the catalogue. Deploy astro-catalog.json to the site root.
        public const string CatalogUrl = "https://underafricanskies.eu/astro-catalog.json";

        private static readonly HttpClient Http = new HttpClient {
            Timeout = TimeSpan.FromSeconds(15)
        };

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions {
            PropertyNameCaseInsensitive = true
        };

        private readonly string _cachePath;

        public CatalogService() {
            // %localappdata%\NINA\Plugins\UASObjectChooser\catalog.json
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA", "Plugins", "UASObjectChooser");
            Directory.CreateDirectory(dir);
            _cachePath = Path.Combine(dir, "catalog.json");
        }

        /// <summary>True if the last Load() came from the live site (false = served from cache).</summary>
        public bool LastLoadWasLive { get; private set; }

        /// <summary>Human-readable note about where the data came from, for the UI.</summary>
        public string StatusMessage { get; private set; } = "";

        /// <summary>
        /// Returns catalogue data IMMEDIATELY with no network call: the last cached copy
        /// if present, otherwise the catalogue bundled in the plugin. This guarantees the
        /// list is populated instantly, even if the website endpoint is unreachable.
        /// </summary>
        public List<AstroObject> LoadImmediate() {
            try {
                if (File.Exists(_cachePath)) {
                    var json = File.ReadAllText(_cachePath);
                    var c = JsonSerializer.Deserialize<AstroCatalog>(json, JsonOpts);
                    if (c?.Objects != null && c.Objects.Count > 0) {
                        StatusMessage = $"{c.Objects.Count} objects (cached)";
                        return c.Objects;
                    }
                }
            } catch { /* ignore */ }

            var embedded = LoadEmbedded();
            StatusMessage = embedded.Count > 0
                ? $"{embedded.Count} objects (built-in catalogue)"
                : "No catalogue available.";
            return embedded;
        }

        /// <summary>
        /// Attempts a live refresh from the website, with a hard timeout so it can never
        /// hang the UI. Returns the updated list on success, or null on any failure/timeout.
        /// Runs in the background AFTER LoadImmediate() has already shown data.
        /// </summary>
        public async Task<List<AstroObject>> TryRefreshLiveAsync() {
            try {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                var json = await Http.GetStringAsync(CatalogUrl, cts.Token).ConfigureAwait(false);
                var c = JsonSerializer.Deserialize<AstroCatalog>(json, JsonOpts);
                if (c?.Objects != null && c.Objects.Count > 0) {
                    try { File.WriteAllText(_cachePath, json); } catch { /* best effort */ }
                    LastLoadWasLive = true;
                    StatusMessage = $"{c.Objects.Count} objects (updated from underafricanskies.eu)";
                    return c.Objects;
                }
            } catch { /* offline / not deployed / timeout -> keep the immediate data */ }
            return null;
        }

        /// <summary>Reads the catalogue embedded in the plugin assembly.</summary>
        private List<AstroObject> LoadEmbedded() {
            try {
                var asm = typeof(CatalogService).Assembly;
                var name = System.Array.Find(asm.GetManifestResourceNames(),
                    n => n.EndsWith("astro-catalog.json", System.StringComparison.OrdinalIgnoreCase));
                if (name == null) return new List<AstroObject>();
                using var s = asm.GetManifestResourceStream(name);
                using var r = new StreamReader(s);
                var json = r.ReadToEnd();
                var catalog = JsonSerializer.Deserialize<AstroCatalog>(json, JsonOpts);
                return catalog?.Objects ?? new List<AstroObject>();
            } catch {
                return new List<AstroObject>();
            }
        }
    }
}
