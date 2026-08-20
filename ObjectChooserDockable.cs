using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NINA.Astrometry;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using NINA.WPF.Base.ViewModel;
using UnderAfricanSkies.ObjectChooser.Models;
using UnderAfricanSkies.ObjectChooser.Services;

namespace UnderAfricanSkies.ObjectChooser {

    /// <summary>
    /// The "UAS Object Chooser" dockable panel. Loads the Under African Skies catalogue
    /// (live + offline cache), shows the same data, options and preview images as the
    /// website, and — using NINA's own profile location — works anywhere in the world.
    /// Push any object to the Framing Assistant or slew the mount, without leaving NINA.
    ///
    /// NINA API touchpoints are marked // VERIFY (only lines to reconcile with your build).
    /// </summary>
    [Export(typeof(IDockableVM))]
    [PartCreationPolicy(CreationPolicy.NonShared)]
    public class ObjectChooserDockable : DockableVM {

        private readonly ITelescopeMediator _telescope;
        private readonly IFramingAssistantVM _framing;
        private readonly IApplicationMediator _application;
        private readonly IProfileService _profileService;
        private readonly ICameraMediator _camera;
        private readonly CatalogService _catalog = new CatalogService();
        private readonly AstroImageService _images = new AstroImageService();

        private List<AstroObject> _all = new List<AstroObject>();
        private CancellationTokenSource _imageCts;
        private readonly object _collectionLock = new object();

        [ImportingConstructor]
        public ObjectChooserDockable(
            IProfileService profileService,
            IApplicationMediator application,
            ITelescopeMediator telescope,
            ICameraMediator camera,
            IFramingAssistantVM framing) : base(profileService) {

            _profileService = profileService;
            _application = application;
            _telescope = telescope;
            _camera = camera;
            _framing = framing;

            Title = "UAS Object Chooser";
            var icon = new System.Windows.Media.GeometryGroup();
            icon.Children.Add(System.Windows.Media.Geometry.Parse("M12,2 L14,10 L22,12 L14,14 L12,22 L10,14 L2,12 L10,10 Z"));
            if (icon.CanFreeze) icon.Freeze();   // allow cross-thread use (VM may be built off the UI thread)
            ImageGeometry = icon;

            Objects = new ObservableCollection<AstroObject>();
            // Allow the list to be updated from a background thread (NINA builds the VM off the UI thread).
            try { System.Windows.Data.BindingOperations.EnableCollectionSynchronization(Objects, _collectionLock); } catch { }

            RefreshCommand = new AsyncCommand<bool>(() => LoadAsync());
            SetInFramingCommand = new RelayCommand(o => SetInFraming(o as AstroObject ?? SelectedObject), o => (o ?? SelectedObject) != null);
            SlewCommand = new AsyncCommand<bool>(o => SlewAsync(SelectedObject), o => SelectedObject != null);
            OpenWikiCommand = new RelayCommand(_ => OpenWiki(), _ => !string.IsNullOrEmpty(PreviewPageUrl));
            OpenWebsiteCommand = new RelayCommand(_ => OpenUrl("https://underafricanskies.eu"));
            PrepareOfflineCommand = new AsyncCommand<bool>(() => PrepareOfflineAsync());

            _ = LoadAsync();
        }

        // ---------------- Bound collections / properties ----------------

        public ObservableCollection<AstroObject> Objects { get; }

        private AstroObject _selectedObject;
        public AstroObject SelectedObject {
            get => _selectedObject;
            set {
                _selectedObject = value;
                RaisePropertyChanged();
                _ = LoadPreviewAsync(value);
            }
        }

        private string _statusText = "Loading catalogue…";
        public string StatusText { get => _statusText; set { _statusText = value; RaisePropertyChanged(); } }

        private string _locationText = "";
        public string LocationText { get => _locationText; set { _locationText = value; RaisePropertyChanged(); } }

        private string _sensorText = "";
        public string SensorText { get => _sensorText; set { _sensorText = value; RaisePropertyChanged(); } }

        // ---- empty-state hint (shown over the list when filters leave nothing) ----
        private string _emptyHint = "";
        public string EmptyHint { get => _emptyHint; set { _emptyHint = value; RaisePropertyChanged(); } }

        private System.Windows.Visibility _emptyVisibility = System.Windows.Visibility.Collapsed;
        public System.Windows.Visibility EmptyVisibility {
            get => _emptyVisibility;
            set { _emptyVisibility = value; RaisePropertyChanged(); }
        }

        // ---- preview (Wikipedia) ----
        private System.Windows.Media.ImageSource _previewImage;
        public System.Windows.Media.ImageSource PreviewImage { get => _previewImage; set { _previewImage = value; RaisePropertyChanged(); } }

        private string _previewText;
        public string PreviewText { get => _previewText; set { _previewText = value; RaisePropertyChanged(); } }

        private string _previewPageUrl;
        public string PreviewPageUrl { get => _previewPageUrl; set { _previewPageUrl = value; RaisePropertyChanged(); } }

        // ---- offline image download ----
        private string _offlineStatus = "";
        public string OfflineStatus { get => _offlineStatus; set { _offlineStatus = value; RaisePropertyChanged(); } }

        private bool _isPreparing;
        public bool IsPreparing {
            get => _isPreparing;
            set { _isPreparing = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(IsNotPreparing)); }
        }
        public bool IsNotPreparing => !_isPreparing;

        // ---- filters / sort (same options as the website) ----
        private string _typeFilter = "all";
        public string TypeFilter { get => _typeFilter; set { _typeFilter = value; RaisePropertyChanged(); ApplyFilter(); } }

        private string _search = "";
        public string Search { get => _search; set { _search = value; RaisePropertyChanged(); ApplyFilter(); } }

        private bool _southernOnly;
        public bool SouthernOnly { get => _southernOnly; set { _southernOnly = value; RaisePropertyChanged(); ApplyFilter(); } }

        private bool _wellPlacedOnly;
        public bool WellPlacedOnly { get => _wellPlacedOnly; set { _wellPlacedOnly = value; RaisePropertyChanged(); ApplyFilter(); } }

        private string _sortBy = "altitude"; // altitude | name | magnitude | size
        public string SortBy { get => _sortBy; set { _sortBy = value; RaisePropertyChanged(); ApplyFilter(); } }

        // ---------------- Commands ----------------

        public IAsyncCommand RefreshCommand { get; }
        public System.Windows.Input.ICommand SetInFramingCommand { get; }
        public IAsyncCommand SlewCommand { get; }
        public System.Windows.Input.ICommand OpenWikiCommand { get; }
        public System.Windows.Input.ICommand OpenWebsiteCommand { get; }
        public IAsyncCommand PrepareOfflineCommand { get; }

        // ---------------- Location (worldwide, from NINA's profile) ----------------

        private (double lat, double lon) GetObserverLocation() {
            var astro = _profileService.ActiveProfile.AstrometrySettings; // VERIFY property path
            return (astro.Latitude, astro.Longitude);                     // degrees; lon East-positive
        }

        // ASI585MC Pro (Sony IMX585): 3840 x 2160 px, 2.9 µm -> 11.14 x 6.26 mm. Short side used for framing.
        private const double DefaultSensorShortMm = 6.26;

        /// <summary>
        /// Short side of the camera sensor in mm — read from NINA's connected camera when
        /// available, otherwise the ASI585MC Pro default. Also updates SensorText for the UI.
        /// </summary>
        private double SensorShortMm() {
            try {
                var ci = _camera?.GetInfo();
                if (ci != null && ci.Connected && ci.PixelSize > 0 && ci.XSize > 0 && ci.YSize > 0) {
                    double wMm = ci.XSize * ci.PixelSize / 1000.0;
                    double hMm = ci.YSize * ci.PixelSize / 1000.0;
                    double shortMm = Math.Min(wMm, hMm);
                    var cam = string.IsNullOrWhiteSpace(ci.Name) ? "camera" : ci.Name;
                    SensorText = $"Framing for {cam}  ·  sensor {wMm:0.0}×{hMm:0.0} mm";
                    return shortMm;
                }
            } catch { /* fall through to default */ }
            SensorText = "Framing for ASI585MC Pro (no camera connected)  ·  sensor 11.1×6.3 mm";
            return DefaultSensorShortMm;
        }

        // ---------------- Logic ----------------

        internal static void DebugLog(string msg) {
            try {
                var p = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NINA", "Plugins", "UASObjectChooser", "debug.log");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p));
                System.IO.File.AppendAllText(p, DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + "\n");
            } catch { }
        }

        private async Task<bool> LoadAsync() {
            // 1) Show data instantly from the built-in catalogue / cache (no network).
            PopulateFrom(_catalog.LoadImmediate());
            // 2) Then try a live refresh in the background (hard 8s timeout; can't hang the UI).
            try {
                var live = await _catalog.TryRefreshLiveAsync();
                if (live != null) PopulateFrom(live);
            } catch { /* keep the immediate data */ }
            return true;
        }

        // The VM may be created on a background thread by NINA's plugin loader, so all work
        // that touches WPF (the ObservableCollection, bound properties) is marshalled to the UI thread.
        private void PopulateFrom(List<AstroObject> list) {
            RunOnUI(() => {
                try {
                    // The catalogue plus the Sun, Moon and planets computed for right now.
                    _all = new List<AstroObject>(list);
                    try { _all.AddRange(Ephemeris.SolarSystem(DateTime.UtcNow, includeSun: true)); }
                    catch (Exception pex) { DebugLog("SolarSystem THREW: " + pex); }
                    ComputeVisibility();
                    StatusText = _catalog.StatusMessage;
                    var cached = _images.CachedCount();
                    OfflineStatus = cached > 0
                        ? $"{cached} images cached offline. Use 'Download images' to complete the set."
                        : "Tip: click 'Download images' once (online) so previews work offline at the telescope.";
                    ApplyFilter();
                } catch (Exception ex) {
                    DebugLog("PopulateFrom THREW: " + ex);
                }
            });
        }

        private static void RunOnUI(Action action) {
            var app = System.Windows.Application.Current;
            if (app?.Dispatcher != null && !app.Dispatcher.CheckAccess())
                app.Dispatcher.BeginInvoke(action);   // non-blocking: worker threads never wait on the UI
            else
                action();
        }

        private double _lat, _lon;

        private void ComputeVisibility() {
            try {
                var (lat, lon) = GetObserverLocation();
                _lat = lat; _lon = lon;
                LocationText = $"Your location (from NINA): {lat:0.0}°, {lon:0.0}°";
                // The dark-hours window depends only on location + date, so compute it ONCE
                // (not per object) — matches the website's "tonight" logic.
                var night = Visibility.NightSampleTimes(lat, lon, DateTime.UtcNow);
                var now = DateTime.UtcNow;
                double shortMm = SensorShortMm();
                foreach (var o in _all) {
                    o.MaxAltitude = Visibility.MaxAltitude(o.Dec, lat);
                    o.CurrentAltitude = Visibility.Altitude(o.Ra, o.Dec, lat, lon, now);
                    o.WellPlaced = Visibility.IsWellPlaced(o.Ra, o.Dec, lat, lon, night);
                    o.FocalText = Framing.Recommend(o.Size, shortMm);
                }
            } catch (Exception ex) {
                LocationText = "Location unavailable: " + ex.Message;
            }
        }

        private void ApplyFilter() {
            IEnumerable<AstroObject> q = _all;

            if (_typeFilter != "all") q = q.Where(o => o.Group == _typeFilter);
            if (_southernOnly) q = q.Where(o => o.IsSouthern);
            if (_wellPlacedOnly) q = q.Where(o => o.WellPlaced);

            if (!string.IsNullOrWhiteSpace(_search)) {
                var s = _search.Trim().ToLowerInvariant();
                q = q.Where(o => (o.Name ?? "").ToLowerInvariant().Contains(s)
                              || (o.Desig ?? "").ToLowerInvariant().Contains(s));
            }

            switch (_sortBy) {
                case "name": q = q.OrderBy(o => o.Name); break;
                case "magnitude": q = q.OrderBy(o => o.Mag ?? 99); break;
                case "size": q = q.OrderByDescending(o => o.Size ?? 0); break;
                case "altitude":
                default: q = q.OrderByDescending(o => o.MaxAltitude ?? -999); break;
            }

            lock (_collectionLock) {
                Objects.Clear();
                foreach (var o in q) Objects.Add(o);
            }
            // Show a neat type icon immediately; the loader replaces it with a real photo where available.
            foreach (var o in Objects)
                if (!o.HasRealImage) o.Thumbnail = PlaceholderFor(o.Group);
            UpdateEmptyState();
            StartThumbnailLoad();
        }

        /// <summary>Explain WHY the list is empty so an empty panel never looks broken.</summary>
        private void UpdateEmptyState() {
            if (Objects.Count > 0) {
                EmptyVisibility = System.Windows.Visibility.Collapsed;
                EmptyHint = "";
                return;
            }
            EmptyVisibility = System.Windows.Visibility.Visible;

            int lat = (int)System.Math.Round(_lat);
            if (_southernOnly && _wellPlacedOnly) {
                EmptyHint = $"No matches. From your latitude ({lat}°N) southern-sky objects stay very low, "
                          + "so almost none are ever ‘well placed’.\n\nTurn off ‘Southern only’ or ‘Well placed tonight’.";
            } else if (_wellPlacedOnly) {
                EmptyHint = "Nothing is well placed in tonight's dark hours from your location yet.\n\n"
                          + "Turn off ‘Well placed tonight’ to see the whole catalogue, or check again later tonight.";
            } else if (_southernOnly) {
                EmptyHint = $"No southern-sky objects match. From latitude {lat}°N they barely rise.\n\n"
                          + "Turn off ‘Southern only’ to see the full catalogue.";
            } else if (!string.IsNullOrWhiteSpace(_search)) {
                EmptyHint = $"No object matches “{_search.Trim()}”.\n\nTry a catalogue number (M31, NGC 253) or a name.";
            } else {
                EmptyHint = "No objects match the current filters.";
            }
        }

        private async Task LoadPreviewAsync(AstroObject o) {
            RunOnUI(() => { PreviewImage = null; PreviewText = null; PreviewPageUrl = null; });
            if (o == null) return;
            _imageCts?.Cancel();
            _imageCts = new CancellationTokenSource();
            var token = _imageCts.Token;
            try {
                var info = await _images.GetAsync(o.Wiki, allowNetwork: true, token: token);
                if (token.IsCancellationRequested) return;
                // Prefer the locally cached file (works offline); fall back to the remote URL.
                var src = !string.IsNullOrEmpty(info.ImagePath) ? info.ImagePath : info.ImageUrl;
                var bmp = BuildFrozenImage(src);   // built + frozen here -> safe to hand to the UI thread
                RunOnUI(() => {
                    PreviewImage = bmp;
                    PreviewText = info.Extract;
                    PreviewPageUrl = info.PageUrl;
                });
            } catch { /* ignore */ }
        }

        private static System.Windows.Media.ImageSource BuildFrozenImage(string src, int decodeWidth = 0) {
            if (string.IsNullOrEmpty(src)) return null;
            try {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreImageCache;
                if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;   // decode small -> saves memory for list thumbnails
                bmp.UriSource = new Uri(src, UriKind.Absolute);   // works for both a local file path and an http URL
                bmp.EndInit();
                if (bmp.CanFreeze) bmp.Freeze();                  // frozen -> usable from any thread
                return bmp;
            } catch {
                return null;
            }
        }

        // ---------------- Type placeholders (for objects without a Wikipedia photo) ----------------

        private static readonly System.Collections.Generic.Dictionary<string, System.Windows.Media.ImageSource> _placeholders
            = new System.Collections.Generic.Dictionary<string, System.Windows.Media.ImageSource>();

        private static System.Windows.Media.ImageSource PlaceholderFor(string group) {
            lock (_placeholders) {
                if (_placeholders.TryGetValue(group, out var img)) return img;
                var di = BuildPlaceholder(group);
                _placeholders[group] = di;
                return di;
            }
        }

        private static System.Windows.Media.ImageSource BuildPlaceholder(string group) {
            const double W = 150;
            System.Windows.Media.Color accent;
            switch (group) {
                case "galaxies": accent = System.Windows.Media.Color.FromRgb(0x7f, 0xb0, 0xff); break;
                case "nebulae":  accent = System.Windows.Media.Color.FromRgb(0xff, 0x8f, 0xa8); break;
                case "clusters": accent = System.Windows.Media.Color.FromRgb(0xff, 0xd3, 0x6f); break;
                case "planets":  accent = System.Windows.Media.Color.FromRgb(0xff, 0xc2, 0x6b); break;
                default:         accent = System.Windows.Media.Color.FromRgb(0x9f, 0xe0, 0xb0); break;
            }
            var bg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x15, 0x15, 0x1f));
            var fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xC0, accent.R, accent.G, accent.B));
            var dg = new System.Windows.Media.DrawingGroup();
            dg.Children.Add(new System.Windows.Media.GeometryDrawing(bg, null,
                new System.Windows.Media.RectangleGeometry(new System.Windows.Rect(0, 0, W, W), 10, 10)));
            dg.Children.Add(new System.Windows.Media.GeometryDrawing(fill, null, SymbolFor(group, W)));
            var di = new System.Windows.Media.DrawingImage(dg);
            di.Freeze();
            return di;
        }

        private static System.Windows.Media.Geometry SymbolFor(string group, double W) {
            double c = W / 2;
            var gg = new System.Windows.Media.GeometryGroup();
            switch (group) {
                case "galaxies": {
                    var e = new System.Windows.Media.EllipseGeometry(new System.Windows.Point(c, c), 52, 20);
                    e.Transform = new System.Windows.Media.RotateTransform(28, c, c);
                    gg.Children.Add(e);
                    gg.Children.Add(new System.Windows.Media.EllipseGeometry(new System.Windows.Point(c, c), 9, 9));
                    return gg;
                }
                case "nebulae": {
                    gg.Children.Add(new System.Windows.Media.EllipseGeometry(new System.Windows.Point(c - 14, c), 34, 26));
                    gg.Children.Add(new System.Windows.Media.EllipseGeometry(new System.Windows.Point(c + 16, c - 6), 28, 22));
                    gg.Children.Add(new System.Windows.Media.EllipseGeometry(new System.Windows.Point(c + 4, c + 16), 24, 18));
                    return gg;
                }
                case "clusters": {
                    double[,] p = { {c,c-30},{c-26,c-8},{c+26,c-8},{c-18,c+22},{c+18,c+22},{c,c+2},{c-9,c-14},{c+11,c+4} };
                    for (int i = 0; i < p.GetLength(0); i++)
                        gg.Children.Add(new System.Windows.Media.EllipseGeometry(new System.Windows.Point(p[i, 0], p[i, 1]), 7, 7));
                    return gg;
                }
                case "planets": {
                    // a ringed planet: flat ellipse (ring) behind, disk on top
                    var ring = new System.Windows.Media.EllipseGeometry(new System.Windows.Point(c, c), 58, 18);
                    ring.Transform = new System.Windows.Media.RotateTransform(-18, c, c);
                    gg.Children.Add(ring);
                    gg.Children.Add(new System.Windows.Media.EllipseGeometry(new System.Windows.Point(c, c), 34, 34));
                    return gg;
                }
                default:
                    return System.Windows.Media.Geometry.Parse("M75,20 L86,64 L130,75 L86,86 L75,130 L64,86 L20,75 L64,64 Z");
            }
        }

        // ---------------- Per-row thumbnails ----------------

        private CancellationTokenSource _thumbCts;

        /// <summary>Kick off loading thumbnails for the currently shown objects (cache first, then network).</summary>
        private void StartThumbnailLoad() {
            _thumbCts?.Cancel();
            _thumbCts = new CancellationTokenSource();
            var token = _thumbCts.Token;
            var items = Objects.ToList();   // snapshot on the UI thread
            _ = Task.Run(() => LoadThumbnailsAsync(items, token));
        }

        private async Task LoadThumbnailsAsync(List<AstroObject> items, CancellationToken token) {
            using var gate = new System.Threading.SemaphoreSlim(4);   // at most 4 concurrent image loads
            var tasks = new List<Task>();
            foreach (var o in items) {
                if (token.IsCancellationRequested) break;
                if (o.IsSolarSystem) { o.ImageAttempted = true; continue; }  // planets move -> keep the symbol, no sky photo
                if (o.HasRealImage || o.ImageAttempted) continue;     // done with this one already
                try { await gate.WaitAsync(token).ConfigureAwait(false); } catch { break; }
                tasks.Add(Task.Run(async () => {
                    try {
                        // A real DSS sky image centred on the object (correct for every object).
                        var path = _images.GetSkyCachedPath(o.Name)
                                   ?? await _images.GetSkyImageAsync(o.Ra, o.Dec, o.Size, o.Name, 150, token);
                        if (!string.IsNullOrEmpty(path) && !token.IsCancellationRequested) {
                            var bmp = BuildFrozenImage(path, 150);
                            if (bmp != null) RunOnUI(() => { if (!token.IsCancellationRequested) { o.Thumbnail = bmp; o.HasRealImage = true; } });
                            else o.ImageAttempted = true;
                        } else {
                            o.ImageAttempted = true;                  // download failed -> keep the type placeholder
                        }
                    } catch { o.ImageAttempted = true; }
                    finally { gate.Release(); }
                }, token));
            }
            try { await Task.WhenAll(tasks); } catch { }
        }

        private async Task<bool> PrepareOfflineAsync() {
            var items = _all;
            if (items == null || items.Count == 0) return false;
            IsPreparing = true;
            int total = items.Count, done = 0, ok = 0;
            OfflineStatus = $"Downloading images… 0 / {total}";
            try {
                using var gate = new System.Threading.SemaphoreSlim(4);
                var tasks = new List<Task>();
                foreach (var o in items) {
                    if (o.IsSolarSystem) { System.Threading.Interlocked.Increment(ref done); continue; }
                    await gate.WaitAsync();
                    tasks.Add(Task.Run(async () => {
                        try {
                            var path = _images.GetSkyCachedPath(o.Name)
                                       ?? await _images.GetSkyImageAsync(o.Ra, o.Dec, o.Size, o.Name, 150, CancellationToken.None);
                            if (!string.IsNullOrEmpty(path)) System.Threading.Interlocked.Increment(ref ok);
                        } catch { }
                        finally {
                            var d = System.Threading.Interlocked.Increment(ref done);
                            if (d % 10 == 0 || d == total) OfflineStatus = $"Downloading images… {d} / {total}";
                            gate.Release();
                        }
                    }));
                }
                await Task.WhenAll(tasks);
                OfflineStatus = $"Offline ready: {ok} images cached. No internet needed at the telescope.";
                Notification.ShowSuccess($"Cached {ok} object images for offline use");
                RunOnUI(StartThumbnailLoad);   // refresh list thumbnails from the freshly cached images
                return true;
            } catch (Exception ex) {
                OfflineStatus = "Download stopped: " + ex.Message;
                return false;
            } finally {
                IsPreparing = false;
            }
        }

        private void OpenWiki() => OpenUrl(PreviewPageUrl);

        private static void OpenUrl(string url) {
            if (string.IsNullOrEmpty(url)) return;
            try {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                    FileName = url, UseShellExecute = true
                });
            } catch { /* ignore */ }
        }

        private Coordinates ToCoordinates(AstroObject o) {
            return new Coordinates(o.Ra, o.Dec, Epoch.J2000, Coordinates.RAType.Hours); // VERIFY ctor
        }

        private void SetInFraming(AstroObject o) {
            if (o == null) return;
            try {
                var coords = ToCoordinates(o);
                var dso = new DeepSkyObject(
                    o.DisplayName, coords,
                    _profileService.ActiveProfile.ApplicationSettings.SkyAtlasImageRepository,
                    _profileService.ActiveProfile.AstrometrySettings.Horizon);   // VERIFY ctor
                dso.Name = o.DisplayName;
                _framing.SetCoordinates(dso);
                Notification.ShowSuccess($"'{o.DisplayName}' set in the Framing Assistant");
            } catch (Exception ex) {
                Notification.ShowError("Could not set target in Framing Assistant: " + ex.Message);
            }
        }

        private async Task<bool> SlewAsync(AstroObject o) {
            if (o == null) return false;
            if (!_telescope.GetInfo().Connected) {                               // VERIFY GetInfo().Connected
                Notification.ShowWarning("No mount connected in NINA.");
                return false;
            }
            try {
                var coords = ToCoordinates(o);
                Notification.ShowInformation($"Slewing to '{o.DisplayName}'…");
                var ok = await _telescope.SlewToCoordinatesAsync(coords, CancellationToken.None); // VERIFY
                if (ok) Notification.ShowSuccess($"Mount slewed to '{o.DisplayName}'");
                else Notification.ShowError($"Slew to '{o.DisplayName}' did not complete");
                return ok;
            } catch (Exception ex) {
                Notification.ShowError("Slew failed: " + ex.Message);
                return false;
            }
        }
    }
}
