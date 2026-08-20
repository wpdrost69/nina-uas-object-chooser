using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;

namespace UnderAfricanSkies.ObjectChooser.Services {

    /// <summary>
    /// Preview image + description for an object, from Wikipedia — the same source the
    /// website uses — but with a PERSISTENT LOCAL CACHE so it works fully offline once
    /// downloaded.
    ///
    /// How offline works:
    ///   * Image bytes and text are cached under
    ///     %localappdata%\NINA\Plugins\UASObjectChooser\images\  (+ wikimeta.json).
    ///   * GetAsync() returns the cached copy immediately if present (no network).
    ///   * PrepareOfflineAsync() bulk-downloads everything once (the "Download for offline
    ///     use" button), so at the telescope no internet connection is needed at all.
    ///
    /// This avoids bundling hundreds of mixed-licence Wikipedia images into the product:
    /// each install caches for personal/local use, which keeps licensing clean.
    /// </summary>
    public class AstroImageService {

        private static readonly HttpClient Http = CreateClient();
        private static HttpClient CreateClient() {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("UAS-ObjectChooser/1.0 (https://underafricanskies.eu)");
            return c;
        }

        private readonly string _imagesDir;
        private readonly string _metaPath;
        private Dictionary<string, WikiInfo> _meta;
        private readonly object _metaLock = new object();

        public AstroImageService() {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA", "Plugins", "UASObjectChooser", "images");
            Directory.CreateDirectory(dir);
            _imagesDir = dir;
            _metaPath = Path.Combine(dir, "wikimeta.json");
            LoadMeta();
        }

        public class WikiInfo {
            public string ImagePath;   // absolute local path once cached (what the UI binds to)
            public string ImageUrl;    // original remote URL (kept for reference)
            public string Extract;     // short description
            public string PageUrl;     // link to the article
        }

        private void LoadMeta() {
            try {
                if (File.Exists(_metaPath)) {
                    _meta = JsonSerializer.Deserialize<Dictionary<string, WikiInfo>>(File.ReadAllText(_metaPath))
                            ?? new Dictionary<string, WikiInfo>();
                    return;
                }
            } catch { /* fall through */ }
            _meta = new Dictionary<string, WikiInfo>();
        }

        private void SaveMeta() {
            try {
                lock (_metaLock) {
                    File.WriteAllText(_metaPath, JsonSerializer.Serialize(_meta));
                }
            } catch { /* best effort */ }
        }

        private static string SafeKey(string wiki) {
            foreach (var c in Path.GetInvalidFileNameChars()) wiki = wiki.Replace(c, '_');
            return wiki;
        }

        // ---------------- Real sky images (DSS survey by coordinates) ----------------
        // Every object gets a correct, real image of that patch of sky — no wrong/missing
        // Wikipedia pictures. Rendered on demand by the CDS hips2fits service and cached.

        private const string Hips2Fits = "https://alasky.cds.unistra.fr/hips-image-services/hips2fits";

        private static string SafeName(string s) {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }

        private string SkyFile(string name) => Path.Combine(_imagesDir, "sky_" + SafeName(name) + ".jpg");

        /// <summary>Cached sky-image path for an object, or null (no network).</summary>
        public string GetSkyCachedPath(string name) {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var f = SkyFile(name);
            return File.Exists(f) ? f : null;
        }

        /// <summary>
        /// Downloads (and caches) a real DSS image centred on the object, framed to its size.
        /// Returns the local file path, or null on failure. Uses a hard timeout so it can't hang.
        /// </summary>
        public async Task<string> GetSkyImageAsync(double raHours, double decDeg, double? sizeArcmin, string name, int px, CancellationToken token = default) {
            var cached = GetSkyCachedPath(name);
            if (cached != null) return cached;
            try {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(TimeSpan.FromSeconds(20));

                double raDeg = raHours * 15.0;
                double fov = sizeArcmin.HasValue && sizeArcmin.Value > 0 ? (sizeArcmin.Value / 60.0) * 2.0 : 0.5;
                if (fov < 0.2) fov = 0.2;
                if (fov > 5.0) fov = 5.0;

                string inv(double d) => d.ToString("0.#####", CultureInfo.InvariantCulture);
                var url = $"{Hips2Fits}?hips=CDS/P/DSS2/color&ra={inv(raDeg)}&dec={inv(decDeg)}&fov={inv(fov)}" +
                          $"&width={px}&height={px}&projection=TAN&coordsys=icrs&format=jpg";

                var bytes = await Http.GetByteArrayAsync(url, cts.Token).ConfigureAwait(false);
                if (bytes != null && bytes.Length > 200) {
                    var f = SkyFile(name);
                    File.WriteAllBytes(f, bytes);
                    return f;
                }
            } catch { /* offline / timeout -> caller keeps the placeholder */ }
            return null;
        }

        /// <summary>How many sky images are cached on disk.</summary>
        public int SkyCachedCount() {
            try {
                return System.Array.FindAll(Directory.GetFiles(_imagesDir, "sky_*.jpg"), _ => true).Length;
            } catch { return 0; }
        }

        /// <summary>Local cached image path for an object, or null if not cached (no network).</summary>
        public string GetCachedPath(string wiki) {
            if (string.IsNullOrWhiteSpace(wiki)) return null;
            lock (_metaLock) {
                if (_meta.TryGetValue(wiki, out var i) && !string.IsNullOrEmpty(i?.ImagePath) && File.Exists(i.ImagePath))
                    return i.ImagePath;
            }
            return null;
        }

        /// <summary>How many objects already have a cached image on disk.</summary>
        public int CachedCount() {
            lock (_metaLock) {
                int n = 0;
                foreach (var kv in _meta)
                    if (!string.IsNullOrEmpty(kv.Value?.ImagePath) && File.Exists(kv.Value.ImagePath)) n++;
                return n;
            }
        }

        /// <summary>
        /// Returns cached info instantly if available (offline-friendly). If not cached and
        /// <paramref name="allowNetwork"/> is true, fetches from Wikipedia and caches it.
        /// </summary>
        public async Task<WikiInfo> GetAsync(string wiki, bool allowNetwork = true, CancellationToken token = default) {
            if (string.IsNullOrWhiteSpace(wiki)) return new WikiInfo();

            // 1) Serve from cache (works with no internet)
            WikiInfo cached;
            lock (_metaLock) { _meta.TryGetValue(wiki, out cached); }
            if (cached != null && !string.IsNullOrEmpty(cached.ImagePath) && File.Exists(cached.ImagePath))
                return cached;

            if (!allowNetwork)
                return cached ?? new WikiInfo();

            // 2) Fetch summary from Wikipedia (hard timeout so a slow/hung request can never stall the loader)
            var info = cached ?? new WikiInfo();
            try {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                token = cts.Token;
                var url = "https://en.wikipedia.org/api/rest_v1/page/summary/" + Uri.EscapeDataString(wiki);
                var json = await Http.GetStringAsync(url, token).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string remote = null;
                if (root.TryGetProperty("originalimage", out var orig) && orig.TryGetProperty("source", out var os))
                    remote = os.GetString();
                else if (root.TryGetProperty("thumbnail", out var th) && th.TryGetProperty("source", out var ts))
                    remote = ts.GetString();

                info.ImageUrl = remote;
                if (root.TryGetProperty("extract", out var ex)) info.Extract = ex.GetString();
                if (root.TryGetProperty("content_urls", out var cu) && cu.TryGetProperty("desktop", out var dk)
                    && dk.TryGetProperty("page", out var pg)) info.PageUrl = pg.GetString();

                // 3) Download + cache the image bytes for offline use
                if (!string.IsNullOrEmpty(remote)) {
                    var ext = Path.GetExtension(new Uri(remote).AbsolutePath);
                    if (string.IsNullOrEmpty(ext) || ext.Length > 5) ext = ".jpg";
                    var file = Path.Combine(_imagesDir, SafeKey(wiki) + ext);
                    var bytes = await Http.GetByteArrayAsync(remote, token).ConfigureAwait(false);
                    File.WriteAllBytes(file, bytes);
                    info.ImagePath = file;
                }
            } catch {
                // keep whatever we have (possibly a prior cache entry)
            }

            lock (_metaLock) { _meta[wiki] = info; }
            SaveMeta();
            return info;
        }

        /// <summary>
        /// Bulk-download every object's image once, so the tool works offline afterwards.
        /// Reports progress as (done, total). Skips objects already cached.
        /// </summary>
        public async Task<int> PrepareOfflineAsync(IEnumerable<string> wikiNames, IProgress<(int done, int total)> progress, CancellationToken token = default) {
            var list = new List<string>();
            foreach (var w in wikiNames) if (!string.IsNullOrWhiteSpace(w)) list.Add(w);
            int total = list.Count, done = 0, ok = 0;

            foreach (var w in list) {
                token.ThrowIfCancellationRequested();
                var info = await GetAsync(w, allowNetwork: true, token: token).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(info?.ImagePath)) ok++;
                done++;
                progress?.Report((done, total));
            }
            return ok;
        }
    }
}
