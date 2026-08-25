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
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.PlateSolving.Interfaces;
using NINA.Core.Utility.WindowService;
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
        // Extra mediators/services needed to reuse NINA's own plate-solve-and-centre routine.
        private readonly IImagingMediator _imaging;
        private readonly IFilterWheelMediator _filterWheel;
        private readonly IGuiderMediator _guider;
        private readonly IDomeMediator _dome;
        private readonly IDomeFollower _domeFollower;
        private readonly IPlateSolverFactory _plateSolverFactory;
        private readonly IWindowServiceFactory _windowServiceFactory;
        // Needed to run NINA's own "take exposure" step from the automatic scheduler.
        private readonly IImageSaveMediator _imageSave;
        private readonly IImageHistoryVM _imageHistory;
        // Rotator: lets "Slew, center & rotate" and the scheduler set the camera position angle.
        private readonly IRotatorMediator _rotator;
        // Safety monitor: automatic mode only unparks/images while it reports "safe".
        private readonly ISafetyMonitorMediator _safety;
        private readonly CatalogService _catalog = new CatalogService();
        private readonly AstroImageService _images = new AstroImageService();

        private List<AstroObject> _all = new List<AstroObject>();
        private List<AstroObject> _solar = new List<AstroObject>();   // Sun/Moon/planets, positions refreshed each tick
        private CancellationTokenSource _imageCts;
        private CancellationTokenSource _slewCts;   // lets the user cancel a running slew / plate-solve
        private bool _isSlewing;
        private readonly object _collectionLock = new object();

        [ImportingConstructor]
        public ObjectChooserDockable(
            IProfileService profileService,
            IApplicationMediator application,
            ITelescopeMediator telescope,
            ICameraMediator camera,
            IFramingAssistantVM framing,
            IImagingMediator imaging,
            IFilterWheelMediator filterWheel,
            IGuiderMediator guider,
            IDomeMediator dome,
            IDomeFollower domeFollower,
            IPlateSolverFactory plateSolverFactory,
            IWindowServiceFactory windowServiceFactory,
            IImageSaveMediator imageSave,
            IImageHistoryVM imageHistory,
            ISafetyMonitorMediator safety,
            IRotatorMediator rotator) : base(profileService) {

            _profileService = profileService;
            _application = application;
            _telescope = telescope;
            _camera = camera;
            _framing = framing;
            _imaging = imaging;
            _filterWheel = filterWheel;
            _guider = guider;
            _dome = dome;
            _domeFollower = domeFollower;
            _plateSolverFactory = plateSolverFactory;
            _windowServiceFactory = windowServiceFactory;
            _imageSave = imageSave;
            _imageHistory = imageHistory;
            _safety = safety;
            _rotator = rotator;
            _selectedCamera = Cameras[0];       // default: auto-detect camera from NINA
            _selectedLocation = Locations[0];   // default: use the NINA profile location (set fields directly, no recompute yet)

            Title = "UAS Object Chooser";
            var icon = new System.Windows.Media.GeometryGroup();
            icon.Children.Add(System.Windows.Media.Geometry.Parse("M12,2 L14,10 L22,12 L14,14 L12,22 L10,14 L2,12 L10,10 Z"));
            if (icon.CanFreeze) icon.Freeze();   // allow cross-thread use (VM may be built off the UI thread)
            ImageGeometry = icon;

            Objects = new ObservableCollection<AstroObject>();
            // Allow the list to be updated from a background thread (NINA builds the VM off the UI thread).
            try { System.Windows.Data.BindingOperations.EnableCollectionSynchronization(Objects, _collectionLock); } catch { }

            RefreshCommand = new AsyncCommand<bool>(() => LoadAsync());
            SetInFramingCommand = new RelayCommand(o => SetInFraming(o as AstroObject ?? SelectedObject), o => (o ?? SelectedObject) != null && !IsPreview);
            SlewCommand = new AsyncCommand<bool>(o => SlewAsync(SelectedObject), o => SelectedObject != null && !IsPreview);
            CancelSlewCommand = new RelayCommand(_ => { try { _slewCts?.Cancel(); } catch { } }, _ => IsSlewing);
            TodayCommand = new RelayCommand(_ => SelectedDate = DateTime.Today);
            OpenWikiCommand = new RelayCommand(_ => OpenWiki(), _ => !string.IsNullOrEmpty(PreviewPageUrl));
            OpenWebsiteCommand = new RelayCommand(_ => OpenUrl("https://underafricanskies.eu"));
            ShowZoomCommand = new RelayCommand(o => { _ = ShowZoomAsync(o as AstroObject); }, o => o is AstroObject);
            CloseZoomCommand = new RelayCommand(_ => { ZoomVisibility = System.Windows.Visibility.Collapsed; ZoomImage = null; });
            PrepareOfflineCommand = new AsyncCommand<bool>(() => PrepareOfflineAsync());

            // ---- Scheduler ----
            Schedule = new ObservableCollection<ScheduleEntry>();
            try { System.Windows.Data.BindingOperations.EnableCollectionSynchronization(Schedule, _scheduleLock); } catch { }
            AddToScheduleCommand = new RelayCommand(o => OpenScheduleDialog(o as AstroObject ?? SelectedObject),
                                                    o => (o ?? SelectedObject) != null);
            SaveScheduleEntryCommand = new RelayCommand(_ => SaveScheduleEntry());
            CancelScheduleCommand = new RelayCommand(_ => { ScheduleDialogVisibility = System.Windows.Visibility.Collapsed; });
            DeleteScheduleEntryCommand = new RelayCommand(o => DeleteScheduleEntry(o as ScheduleEntry));
            ClearScheduleCommand = new RelayCommand(_ => ClearSchedule(), _ => Schedule.Count > 0);
            StopAutoCommand = new RelayCommand(_ => { try { _autoCts?.Cancel(); } catch { } }, _ => _autoRunning);
            ApplyFramingCommand = new RelayCommand(_ => ApplyFraming(), _ => SelectedObject != null && !IsPreview);
            SlewRotateCommand = new AsyncCommand<bool>(o => SlewRotateAsync(), o => SelectedObject != null && !IsPreview);
            LoadSchedule();

            _ = LoadAsync();

            // Live clock: re-compute current position / green dot / heading every minute, so the
            // panel tracks the moving sky without a manual refresh. Runs on NINA's UI dispatcher.
            var disp = System.Windows.Application.Current?.Dispatcher;
            if (disp != null) {
                _tick = new System.Windows.Threading.DispatcherTimer(
                    TimeSpan.FromSeconds(60),
                    System.Windows.Threading.DispatcherPriority.Background,
                    (s, e) => { try { if (_all != null && _all.Count > 0) ComputeVisibility(); } catch { }
                                try { RefreshScheduleStatus(); CheckAutoSchedule(); } catch { } },
                    disp);
                _tick.Start();
            }
        }

        private System.Windows.Threading.DispatcherTimer _tick;

        // ---------------- Bound collections / properties ----------------

        public ObservableCollection<AstroObject> Objects { get; }

        private AstroObject _selectedObject;
        public AstroObject SelectedObject {
            get => _selectedObject;
            set {
                _selectedObject = value;
                RaisePropertyChanged();
                _ = LoadPreviewAsync(value);
                try { OnSelectedFraming(value); } catch { }
                try { EnsureThumbnail(value); } catch { }   // ensure the selected object has its real thumbnail
            }
        }

        private string _statusText = "Loading catalogue…";
        public string StatusText { get => _statusText; set { _statusText = value; RaisePropertyChanged(); } }

        private string _locationText = "";
        public string LocationText { get => _locationText; set { _locationText = value; RaisePropertyChanged(); } }

        private string _sensorText = "";
        public string SensorText { get => _sensorText; set { _sensorText = value; RaisePropertyChanged(); } }

        // ---- Camera picker: lets you plan for a specific sensor even when no camera is connected ----
        /// <summary>A selectable camera/sensor. ShortMm = 0 means "use the camera connected in NINA".</summary>
        public class CameraPreset {
            public string Name { get; set; }
            public double ShortMm { get; set; }   // short side of the sensor, mm (0 = auto/NINA)
            public double LongMm { get; set; }    // long side, mm (for the label)
            public override string ToString() => Name;
        }

        public System.Collections.Generic.List<CameraPreset> Cameras { get; } = new System.Collections.Generic.List<CameraPreset> {
            new CameraPreset { Name = "Auto (connected camera)",      ShortMm = 0,     LongMm = 0 },
            new CameraPreset { Name = "ASI585 MM/MC Pro",             ShortMm = 6.26,  LongMm = 11.2 },
            new CameraPreset { Name = "ASI533 MM/MC Pro",             ShortMm = 11.31, LongMm = 11.31 },
            new CameraPreset { Name = "ASI183 MM/MC",                 ShortMm = 8.8,   LongMm = 13.2 },
            new CameraPreset { Name = "ASI294 MM/MC Pro (4/3)",       ShortMm = 13.0,  LongMm = 19.1 },
            new CameraPreset { Name = "ASI1600 MM/MC (4/3)",          ShortMm = 13.4,  LongMm = 17.7 },
            new CameraPreset { Name = "ASI2600 MM/MC Pro (APS-C)",    ShortMm = 15.7,  LongMm = 23.5 },
            new CameraPreset { Name = "ASI071 / APS-C",               ShortMm = 15.6,  LongMm = 23.6 },
            new CameraPreset { Name = "Canon APS-C (1.6×)",           ShortMm = 14.9,  LongMm = 22.3 },
            new CameraPreset { Name = "Sony/Nikon APS-C (1.5×)",      ShortMm = 15.6,  LongMm = 23.5 },
            new CameraPreset { Name = "ASI2400 / ASI6200 (full-frame)", ShortMm = 24.0, LongMm = 36.0 },
            new CameraPreset { Name = "Full-frame 35 mm",            ShortMm = 24.0,  LongMm = 36.0 },
        };

        private CameraPreset _selectedCamera;
        public CameraPreset SelectedCamera {
            get => _selectedCamera;
            set {
                _selectedCamera = value;
                RaisePropertyChanged();
                try { ComputeVisibility(); ApplyFilter(); } catch { }   // re-frame for the chosen sensor, live
            }
        }

        // ---- "What fits my telescope?" filter: show only objects framed well at a chosen focal length ----
        /// <summary>Short side (mm) of the sensor last used for framing — so the focal filter matches it.</summary>
        private double _sensorShortMm = 6.26;

        public class FocalOption {
            public string Name { get; set; }
            public int Focal { get; set; }   // 0 = any / no filter
            public override string ToString() => Name;
        }

        public System.Collections.Generic.List<FocalOption> FocalOptions { get; } = BuildFocalOptions();
        private static System.Collections.Generic.List<FocalOption> BuildFocalOptions() {
            var list = new System.Collections.Generic.List<FocalOption> {
                new FocalOption { Name = "Any telescope", Focal = 0 }
            };
            foreach (var f in Framing.Focals) list.Add(new FocalOption { Name = $"{f} mm", Focal = f });
            return list;
        }

        private int _focalFilter = 0;
        /// <summary>Selected telescope focal length to filter by (0 = show all).</summary>
        public int FocalFilter {
            get => _focalFilter;
            set {
                _focalFilter = value;
                RaisePropertyChanged();
                try { ComputeVisibility(); } catch { }   // re-label "Telescope:" for the chosen scope
                ApplyFilter();
            }
        }

        // ---- Location picker: preview any site (same list as the website), or use the NINA profile ----
        public class LocationPreset {
            public string Name { get; set; }
            public double Lat { get; set; }
            public double Lon { get; set; }
            public bool IsAuto { get; set; }
            public override string ToString() => Name;
        }

        public System.Collections.Generic.List<LocationPreset> Locations { get; } = new System.Collections.Generic.List<LocationPreset> {
            new LocationPreset { Name = "Auto (NINA profile)", IsAuto = true },
            new LocationPreset { Name = "Steinhausen Plateau, Namibia", Lat = -21.8167, Lon = 18.2333 },
            new LocationPreset { Name = "Etosha (North Namibia)",       Lat = -19.18,   Lon = 15.92 },
            new LocationPreset { Name = "Fish River (South Namibia)",   Lat = -27.58,   Lon = 17.62 },
            new LocationPreset { Name = "Atacama",                      Lat = -23.0234, Lon = -67.7532 },
            new LocationPreset { Name = "Central Chile (Elqui Valley)", Lat = -30.1667, Lon = -70.6833 },
            new LocationPreset { Name = "La Palma",                     Lat = 28.7566,  Lon = -17.8837 },
            new LocationPreset { Name = "Coonabarabran",               Lat = -31.2744, Lon = 149.2803 },
            new LocationPreset { Name = "Sydney",                       Lat = -33.8688, Lon = 151.2093 },
            new LocationPreset { Name = "Sutherland",                   Lat = -32.3792, Lon = 20.8108 },
            new LocationPreset { Name = "Lake Tekapo",                  Lat = -44.0047, Lon = 170.4772 },
            new LocationPreset { Name = "Kühtai",                       Lat = 47.2167,  Lon = 11.0167 },
            new LocationPreset { Name = "Haute Alps",                   Lat = 44.9000,  Lon = 6.6333 },
            new LocationPreset { Name = "Bergen",                       Lat = 60.3913,  Lon = 5.3221 },
            new LocationPreset { Name = "Strömsund",                    Lat = 63.8456,  Lon = 15.5544 },
            new LocationPreset { Name = "Veenendaal",                   Lat = 52.0275,  Lon = 5.5542 },
            new LocationPreset { Name = "Urra",                         Lat = 39.4167,  Lon = -7.4667 },
        };

        private LocationPreset _selectedLocation;
        public LocationPreset SelectedLocation {
            get => _selectedLocation;
            set {
                _selectedLocation = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(IsPreview));
                RaisePropertyChanged(nameof(PreviewVisibility));
                try { ComputeVisibility(); } catch { }   // recompute night, altitude, heading for the site
                ApplyFilter();                           // "well placed" depends on location
                System.Windows.Input.CommandManager.InvalidateRequerySuggested(); // enable/disable Slew & Framing
            }
        }

        // ---- Date picker: preview any night (future or past); today = live "now" ----
        private DateTime? _selectedDate = DateTime.Today;
        public DateTime? SelectedDate {
            get => _selectedDate;
            set {
                _selectedDate = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(IsPreview));
                RaisePropertyChanged(nameof(PreviewVisibility));
                RaisePropertyChanged(nameof(DateText));
                try { ComputeVisibility(); } catch { }
                ApplyFilter();
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }

        /// <summary>True when the selected date is today (or unset) — i.e. showing the live sky.</summary>
        private bool IsToday => !_selectedDate.HasValue || _selectedDate.Value.Date == DateTime.Today;

        /// <summary>
        /// The reference "now" all sky maths use. Today → the real UtcNow (live). Another date → UtcNow
        /// shifted by that many whole days, so the night, altitudes and the moving dot preview that date.
        /// </summary>
        private DateTime ReferenceNow() {
            if (_selectedDate.HasValue && _selectedDate.Value.Date != DateTime.Today) {
                // Preview a specific night → anchor "now" to that night's darkest moment (≈ local
                // midnight), so objects show at their best for that date instead of at whatever
                // wall-clock time it happens to be right now (which could be daytime on that date).
                try {
                    var (lat, lon) = ObserverLatLon();
                    var seed = _selectedDate.Value.Date.AddHours(12);   // local-ish noon of that date
                    var win = Visibility.SunBelowWindow(lat, lon, seed, 0.0);
                    if (win.ok && win.end > win.start)
                        return win.start.AddMinutes((win.end - win.start).TotalMinutes / 2.0);
                    return seed.AddHours(10);
                } catch {
                    return _selectedDate.Value.Date.AddHours(22);
                }
            }
            return DateTime.UtcNow;   // today = live
        }

        /// <summary>Small note shown while previewing a non-today date; empty when live.</summary>
        public string DateText => IsToday ? "" : $"📅 Sky for {_selectedDate.Value:ddd d MMM yyyy} (preview)";

        /// <summary>Reset the date picker back to today (live).</summary>
        public System.Windows.Input.ICommand TodayCommand { get; private set; }

        /// <summary>
        /// PLANNING PREVIEW when a site other than the NINA profile OR a date other than today is picked.
        /// Imaging (Slew, Set in Framing) is blocked, because the mount is at the real place, right now;
        /// only "Auto (NINA profile)" + today reflects the telescope's real observing conditions.
        /// </summary>
        public bool IsPreview => (_selectedLocation != null && !_selectedLocation.IsAuto) || !IsToday;

        public System.Windows.Visibility PreviewVisibility =>
            IsPreview ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        // ---- click-to-zoom lightbox ----
        private System.Windows.Media.ImageSource _zoomImage;
        public System.Windows.Media.ImageSource ZoomImage { get => _zoomImage; set { _zoomImage = value; RaisePropertyChanged(); } }

        private string _zoomTitle = "";
        public string ZoomTitle { get => _zoomTitle; set { _zoomTitle = value; RaisePropertyChanged(); } }

        private System.Windows.Visibility _zoomVisibility = System.Windows.Visibility.Collapsed;
        public System.Windows.Visibility ZoomVisibility { get => _zoomVisibility; set { _zoomVisibility = value; RaisePropertyChanged(); } }

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

        // ---- Direction filter: show only objects that are RIGHT NOW in a chosen part of the sky ----
        /// <summary>All + the 8 compass points; matched against each object's current heading.</summary>
        public System.Collections.Generic.List<string> DirectionOptions { get; } =
            new System.Collections.Generic.List<string> { "All", "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

        private string _directionFilter = "All";
        public string DirectionFilter {
            get => _directionFilter;
            set { _directionFilter = value; RaisePropertyChanged(); ApplyFilter(); }
        }

        // ---------------- Commands ----------------

        public IAsyncCommand RefreshCommand { get; }
        public System.Windows.Input.ICommand SetInFramingCommand { get; }
        public IAsyncCommand SlewCommand { get; }
        public System.Windows.Input.ICommand CancelSlewCommand { get; }

        /// <summary>True while a slew / plate-solve is in progress (shows the Stop button).</summary>
        public bool IsSlewing {
            get => _isSlewing;
            set {
                _isSlewing = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(SlewingVisibility));
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }

        /// <summary>Stop button is visible only while a slew / plate-solve is running.</summary>
        public System.Windows.Visibility SlewingVisibility =>
            _isSlewing ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        public System.Windows.Input.ICommand OpenWikiCommand { get; }
        public System.Windows.Input.ICommand OpenWebsiteCommand { get; }
        public System.Windows.Input.ICommand ShowZoomCommand { get; }
        public System.Windows.Input.ICommand CloseZoomCommand { get; }
        public IAsyncCommand PrepareOfflineCommand { get; }

        // ---------------- Location (worldwide, from NINA's profile) ----------------

        private (double lat, double lon) GetObserverLocation() {
            var astro = _profileService.ActiveProfile.AstrometrySettings; // VERIFY property path
            return (astro.Latitude, astro.Longitude);                     // degrees; lon East-positive
        }

        /// <summary>Location to compute for: the picked site, or the NINA profile when "Auto".</summary>
        private (double lat, double lon) ObserverLatLon() {
            var loc = SelectedLocation;
            if (loc != null && !loc.IsAuto) return (loc.Lat, loc.Lon);
            return GetObserverLocation();
        }

        /// <summary>Observer elevation in metres — the NINA profile value (preset preview sites use sea level).</summary>
        private double ObserverElevation() {
            try {
                var loc = SelectedLocation;
                if (loc != null && !loc.IsAuto) return 0;
                return _profileService.ActiveProfile.AstrometrySettings.Elevation;
            } catch { return 0; }
        }

        // ASI585MC Pro (Sony IMX585): 3840 x 2160 px, 2.9 µm -> 11.14 x 6.26 mm. Short side used for framing.
        private const double DefaultSensorShortMm = 6.26;

        /// <summary>
        /// Short side of the camera sensor in mm — read from NINA's connected camera when
        /// available, otherwise the ASI585MC Pro default. Also updates SensorText for the UI.
        /// </summary>
        private double SensorShortMm() {
            // 1) Manual pick in the camera dropdown wins (lets you plan for e.g. your APS-C
            //    even when that camera isn't connected in NINA right now).
            var pick = SelectedCamera;
            if (pick != null && pick.ShortMm > 0) {
                SensorText = $"Framing for {pick.Name} (selected)  ·  sensor {pick.LongMm:0.0}×{pick.ShortMm:0.0} mm";
                return pick.ShortMm;
            }
            // 2) Otherwise auto-detect from NINA's connected camera.
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
                    try { _solar = Ephemeris.CreateBodies(); _all.AddRange(_solar); }
                    catch (Exception pex) { DebugLog("SolarSystem THREW: " + pex); }
                    ApplyCuratedCenters(_all);
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

        // Better framing for a few famous large nebulae: the catalogue lists their SIZE too small
        // (so the preview field is too tight and the object spills off-centre), and for the Veil the
        // catalogued point sits at the west edge of the whole loop. These give the real size and, for
        // the Veil, the centre of the complete Cygnus Loop — used for both the preview and slewing.
        // Tuple: (matchKey by name or designation, RA hours, Dec degrees, size arc-minutes).
        private static readonly (string key, double ra, double dec, double size)[] CuratedCenters = new[] {
            ("IC 1805",  2.5449, 61.457, 150.0),  // Heart Nebula  (real extent ~150')
            ("IC 1848",  2.8529, 60.403, 120.0),  // Soul Nebula
            ("IC 1396",  21.6493, 57.489, 150.0), // Elephant's Trunk complex (whole nebula ~150')
            ("NGC 6960", 20.8500, 31.000, 200.0), // Veil / Cygnus Loop — centre of the whole complex
        };

        private static void ApplyCuratedCenters(System.Collections.Generic.List<AstroObject> all) {
            foreach (var o in all) {
                foreach (var c in CuratedCenters) {
                    if (string.Equals(o.Desig, c.key, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(o.Name, c.key, StringComparison.OrdinalIgnoreCase)) {
                        o.Ra = c.ra; o.Dec = c.dec; o.Size = c.size;
                        break;
                    }
                }
            }
        }

        private double _lat, _lon;

        private void ComputeVisibility() {
            try {
                var (lat, lon) = ObserverLatLon();
                _lat = lat; _lon = lon;
                var loc = SelectedLocation;
                LocationText = (loc != null && !loc.IsAuto)
                    ? $"Location: {loc.Name}  ·  {lat:0.0}°, {lon:0.0}°  (preview)"
                    : $"Your location (from NINA): {lat:0.0}°, {lon:0.0}°";
                // The dark-hours window depends only on location + date, so compute it ONCE
                // (not per object) — matches the website's "tonight" logic.
                var now = ReferenceNow();   // live today, or the picked date at the same time-of-day
                // Refresh Sun/Moon/planet positions from NINA's astrometry every time we recompute,
                // so the fast-moving Moon tracks live (fixes stale positions while the panel is open).
                try { Ephemeris.UpdatePositions(_solar, lat, lon, ObserverElevation(), now, IsToday); } catch { }
                var night = Visibility.NightSampleTimes(lat, lon, now);
                double shortMm = SensorShortMm();
                _sensorShortMm = shortMm;   // remember for the "what fits my telescope" filter
                // Chart axis = the observable night, sunset → sunrise (Sun below 0°), same for every
                // object. Using sunset (not astronomical dark) means the "now" dot starts moving as
                // soon as the Sun is down — important in summer at high latitude, where true dark is late.
                var (winS, winE, winOk) = Visibility.SunBelowWindow(lat, lon, now, 0.0);
                foreach (var o in _all) {
                    o.MaxAltitude = Visibility.MaxAltitude(o.Dec, lat);
                    var (alt, az) = Visibility.AltAz(o.Ra, o.Dec, lat, lon, now);
                    o.CurrentAltitude = alt;
                    o.CurrentAzimuth = az;
                    o.WellPlaced = Visibility.IsWellPlaced(o.Ra, o.Dec, lat, lon, night);
                    // When a scope is picked in "Fits scope", show the fill on THAT scope; otherwise the ideal.
                    o.FocalText = _focalFilter > 0
                        ? Framing.CoverageText(o.Size, shortMm, _focalFilter)
                        : Framing.Recommend(o.Size, shortMm);
                    BuildAltitudeChart(o, lat, lon, winS, winE, winOk, now);
                    o.RaiseTimeDependentChanged();   // push the fresh values to the UI
                }
            } catch (Exception ex) {
                LocationText = "Location unavailable: " + ex.Message;
            }
        }

        // Chart coordinate box (matches the Canvas size in the XAML): 200 wide × 70 tall, 64 samples.
        private const double ChartW = 200, ChartH = 70;
        private const int ChartN = 64;

        /// <summary>
        /// Builds the altitude polyline (in a 200×70 box) plus the green "now" dot for one object.
        /// The plotted time window ALWAYS includes 'now', so the green dot is always on the curve —
        /// during the day it simply sits to the left of the shaded dusk→dawn night band. Altitude is
        /// clamped to [0,90]; points are frozen so the collection is safe to bind cross-thread.
        /// </summary>
        private static void BuildAltitudeChart(AstroObject o, double lat, double lon,
                DateTime winS, DateTime winE, bool winOk, DateTime now) {
            // The x-axis is FIXED to tonight's dark hours (dusk → dawn). The green dot marks the
            // current time inside that night, so at night it travels left→right as time passes.
            double span = winOk ? (winE - winS).TotalMinutes : 0;
            if (!winOk || span <= 1) {
                o.AltitudeCurve = null;
                o.DotVisibility = System.Windows.Visibility.Collapsed;
                return;
            }

            var pts = new System.Windows.Media.PointCollection();
            for (int i = 0; i <= ChartN; i++) {
                var t = winS.AddMinutes(span * i / ChartN);
                double a = Visibility.Altitude(o.Ra, o.Dec, lat, lon, t);
                if (a < 0) a = 0; else if (a > 90) a = 90;
                pts.Add(new System.Windows.Point(ChartW * i / ChartN, ChartH - (a / 90.0) * ChartH));
            }
            if (pts.CanFreeze) pts.Freeze();
            o.AltitudeCurve = pts;

            // Green "now" dot: clamp the current time into the night so it always sits on the curve.
            // Before the night → parked at dusk (left); during the night → moves across; after → dawn (right).
            var tnow = now < winS ? winS : (now > winE ? winE : now);
            double xn = ChartW * ((tnow - winS).TotalMinutes / span);
            double at = Visibility.Altitude(o.Ra, o.Dec, lat, lon, tnow);
            if (at < 0) at = 0; else if (at > 90) at = 90;
            double yn = ChartH - (at / 90.0) * ChartH;
            o.DotLeft = xn - 4.5;
            o.DotTop = yn - 4.5;
            o.DotVisibility = System.Windows.Visibility.Visible;
        }

        private void ApplyFilter() {
            IEnumerable<AstroObject> q = _all;

            if (_typeFilter != "all") q = q.Where(o => o.Group == _typeFilter);
            if (_southernOnly) q = q.Where(o => o.IsSouthern);
            if (_wellPlacedOnly) q = q.Where(o => o.WellPlaced);
            // "What fits my telescope?": keep only objects nicely framed at the chosen focal length.
            if (_focalFilter > 0) q = q.Where(o => Framing.FramesWellAt(o.Size, _sensorShortMm, _focalFilter));
            // Direction: keep only objects that are ABOVE the horizon right now AND in the chosen compass sector.
            if (_directionFilter != "All")
                q = q.Where(o => o.CurrentAltitude.HasValue && o.CurrentAltitude.Value >= 0
                              && o.CurrentAzimuth.HasValue
                              && Visibility.Compass(o.CurrentAzimuth.Value) == _directionFilter);

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
            // Show a neat type icon immediately; the real DSS photo is loaded LAZILY, only for rows that
            // actually scroll into view (see EnsureThumbnail, called from the row's Loaded handler) or the
            // selected object. This avoids firing hundreds of CDS requests just by opening/filtering the list.
            foreach (var o in Objects)
                if (!o.HasRealImage) o.Thumbnail = PlaceholderFor(o.Group);
            UpdateEmptyState();
            try { _thumbCts?.Cancel(); } catch { }   // stop lazy loads queued for the previous filter
            _thumbCts = new CancellationTokenSource();
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
            if (_directionFilter != "All") {
                EmptyHint = $"No objects are in the {_directionFilter} right now (above the horizon).\n\n"
                          + "Pick another direction, set it back to ‘All’, or check again later as the sky turns.";
            } else if (_focalFilter > 0) {
                EmptyHint = $"No objects frame well at {_focalFilter} mm on this camera.\n\n"
                          + "Pick a different focal length under ‘Fits scope’, choose another camera, or set it back to ‘Any telescope’.";
            } else if (_southernOnly && _wellPlacedOnly) {
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
            RunOnUI(() => { PreviewText = null; PreviewPageUrl = null; });
            if (o == null) return;
            _imageCts?.Cancel();
            _imageCts = new CancellationTokenSource();
            var token = _imageCts.Token;
            try {
                // TEXT ONLY — no image download/decode here (the panel shows only the description + link;
                // row and zoom images come from the bounded DSS path). Fixes the unbounded Wikipedia fetch.
                var info = await _images.GetTextAsync(o.Wiki, token);
                if (token.IsCancellationRequested) return;
                RunOnUI(() => {
                    PreviewText = info.Extract;
                    PreviewPageUrl = info.PageUrl;
                });
            } catch { /* ignore */ }
        }

        // Open the click-to-zoom lightbox for an object: show the thumbnail instantly (offline-safe),
        // then swap in a larger DSS image if one can be fetched/cached.
        private async Task ShowZoomAsync(AstroObject o) {
            if (o == null) return;
            RunOnUI(() => {
                SelectedObject = o;               // keep the row selected + description in sync
                ZoomTitle = o.DisplayName;
                ZoomImage = o.Thumbnail;          // instant, works offline
                ZoomVisibility = System.Windows.Visibility.Visible;
            });
            if (o.IsSolarSystem) return;          // planets have a symbol, no sky photo to enlarge
            try {
                var path = _images.GetSkyBigCachedPath(o.Name)
                           ?? await _images.GetSkyBigImageAsync(o.Ra, o.Dec, o.Size, o.Name, CancellationToken.None);
                if (!string.IsNullOrEmpty(path)) {
                    var bmp = BuildFrozenImage(path, 640);
                    if (bmp != null)
                        RunOnUI(() => { if (ZoomVisibility == System.Windows.Visibility.Visible) ZoomImage = bmp; });
                }
            } catch { /* keep the thumbnail */ }
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

        private CancellationTokenSource _thumbCts = new CancellationTokenSource();
        private readonly System.Threading.SemaphoreSlim _thumbGate = new System.Threading.SemaphoreSlim(4);

        /// <summary>
        /// Lazily load ONE object's DSS thumbnail — called from the list row's Loaded handler (so only rows
        /// that scroll into view fetch) and when an object is selected. Cache first; at most 4 concurrent
        /// network fetches (shared gate); cancelled when the filter changes. Bulk pre-caching stays the
        /// explicit "Download images" action.
        /// </summary>
        public void EnsureThumbnail(AstroObject o) {
            if (o == null || o.IsSolarSystem || o.HasRealImage || o.ImageAttempted) return;
            var token = _thumbCts?.Token ?? CancellationToken.None;
            _ = Task.Run(async () => {
                try {
                    await _thumbGate.WaitAsync(token).ConfigureAwait(false);
                } catch { return; }
                try {
                    if (token.IsCancellationRequested || o.HasRealImage || o.ImageAttempted) return;
                    var path = _images.GetSkyCachedPath(o.Name)
                               ?? await _images.GetSkyImageAsync(o.Ra, o.Dec, o.Size, o.Name, 150, token);
                    if (!string.IsNullOrEmpty(path) && !token.IsCancellationRequested) {
                        var bmp = BuildFrozenImage(path, 150);
                        if (bmp != null) RunOnUI(() => { if (!token.IsCancellationRequested) { o.Thumbnail = bmp; o.HasRealImage = true; } });
                        else o.ImageAttempted = true;
                    } else {
                        o.ImageAttempted = true;
                    }
                } catch { o.ImageAttempted = true; }
                finally { try { _thumbGate.Release(); } catch { } }
            }, token);
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
                // Refresh thumbnails for the rows currently shown (cache-only now, no new network calls).
                RunOnUI(() => { foreach (var o in Objects) EnsureThumbnail(o); });
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
            // Solar-system bodies are stored as apparent JNOW (from NINA's astrometry); deep-sky is J2000.
            var epoch = o.IsSolarSystem ? Epoch.JNOW : Epoch.J2000;
            return new Coordinates(o.Ra, o.Dec, epoch, Coordinates.RAType.Hours);
        }

        private void SetInFraming(AstroObject o) {
            if (o == null) return;
            if (IsPreview) {
                Notification.ShowWarning("Preview location active — set Location back to “Auto (NINA profile)” to use the Framing Assistant at the real site.");
                return;
            }
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

        /// <summary>The Sun — slewing here without proper solar filtration destroys equipment (and eyes).</summary>
        private static bool IsSun(AstroObject o) =>
            o != null && string.Equals(o.Type, "Sun", StringComparison.OrdinalIgnoreCase);
        private const string SunSafetyMessage =
            "Slewing to the Sun is disabled for equipment safety. Point at the Sun only with a proper, "
            + "securely-fitted solar filter and dedicated solar equipment — never through this plugin.";

        private async Task<bool> SlewAsync(AstroObject o) {
            if (o == null) return false;
            if (IsSun(o)) { Notification.ShowError(SunSafetyMessage); return false; }
            if (IsPreview) {
                Notification.ShowWarning("Preview location active — set Location back to “Auto (NINA profile)” to slew/image at the telescope's real site.");
                return false;
            }
            if (!_telescope.GetInfo().Connected) {
                Notification.ShowWarning("No mount connected in NINA.");
                return false;
            }

            var coords = ToCoordinates(o);

            // Is a camera connected? Plate-solving needs to take an exposure, so without a camera
            // we can only do a blind slew.
            bool cameraReady = false;
            try { cameraReady = _camera.GetInfo().Connected; } catch { }

            // Fresh cancellation source so the Stop button can abort this run.
            try { _slewCts?.Cancel(); _slewCts?.Dispose(); } catch { }
            _slewCts = new CancellationTokenSource();
            var token = _slewCts.Token;
            IsSlewing = true;

            try {
                if (cameraReady) {
                    // Reuse NINA's own "Center" instruction: it slews, plate-solves, and re-centres
                    // in a loop until the target is on the requested coordinates — exactly like the
                    // sequencer's Center step, so the mount ends up properly centred, not just slewed.
                    Notification.ShowInformation($"Slewing to '{o.DisplayName}' and plate-solving to centre… (Stop to cancel)");
                    var center = new NINA.Sequencer.SequenceItem.Platesolving.Center(
                        _profileService, _telescope, _imaging, _filterWheel, _guider,
                        _dome, _domeFollower, _plateSolverFactory, _windowServiceFactory) {
                        Coordinates = new InputCoordinates(coords)
                    };
                    await center.Run(new Progress<NINA.Core.Model.ApplicationStatus>(), token);
                    Notification.ShowSuccess($"Centred on '{o.DisplayName}' (plate-solved)");
                    return true;
                }

                // No camera → blind slew only, and say so plainly.
                Notification.ShowInformation($"Slewing to '{o.DisplayName}'… (no camera connected — cannot plate-solve)");
                var ok = await _telescope.SlewToCoordinatesAsync(coords, token);
                if (ok) Notification.ShowSuccess($"Mount slewed to '{o.DisplayName}' — connect a camera to plate-solve & centre");
                else Notification.ShowError($"Slew to '{o.DisplayName}' did not complete");
                return ok;
            } catch (OperationCanceledException) {
                try { _telescope.StopSlew(); } catch { }   // halt the mount if it was mid-slew
                Notification.ShowInformation($"Stopped — slew & center of '{o.DisplayName}' cancelled.");
                return false;
            } catch (Exception ex) {
                Notification.ShowError("Slew & center failed: " + ex.Message);
                return false;
            } finally {
                IsSlewing = false;
                try { _slewCts?.Dispose(); } catch { }
                _slewCts = null;
            }
        }

        // ==================================================================================
        //  FRAMING — exact position (RA/Dec) + camera rotation (position angle), with a live
        //  schematic showing how the sensor frame sits over the object; push to NINA's Framing
        //  Assistant, or slew with plate-solve + rotate (CenterAndRotate) when a rotator exists.
        // ==================================================================================

        private string _frameRaText = "";
        public string FrameRaText { get => _frameRaText; set { _frameRaText = value; RaisePropertyChanged(); } }
        private string _frameDecText = "";
        public string FrameDecText { get => _frameDecText; set { _frameDecText = value; RaisePropertyChanged(); } }

        private double _frameRotation = 0;
        /// <summary>Camera position angle in degrees (0–360, east of north), used for framing + slew-rotate.</summary>
        public double FrameRotation {
            get => _frameRotation;
            set { _frameRotation = ((value % 360) + 360) % 360; RaisePropertyChanged(); RaisePropertyChanged(nameof(FrameRotationText)); RecomputeFramePreview(); }
        }
        public string FrameRotationText => $"{_frameRotation:0.#}°";

        private double _frameFocal = 500;
        public double FrameFocal { get => _frameFocal; set { _frameFocal = value <= 0 ? 1 : value; RaisePropertyChanged(); RecomputeFramePreview(); } }

        private const double FrameBox = 220.0;   // schematic canvas size (px)
        private double _frRectW, _frRectH, _frRectLeft, _frRectTop, _frEllW, _frEllH, _frEllLeft, _frEllTop;
        public double FrRectW { get => _frRectW; set { _frRectW = value; RaisePropertyChanged(); } }
        public double FrRectH { get => _frRectH; set { _frRectH = value; RaisePropertyChanged(); } }
        public double FrRectLeft { get => _frRectLeft; set { _frRectLeft = value; RaisePropertyChanged(); } }
        public double FrRectTop { get => _frRectTop; set { _frRectTop = value; RaisePropertyChanged(); } }
        public double FrEllW { get => _frEllW; set { _frEllW = value; RaisePropertyChanged(); } }
        public double FrEllH { get => _frEllH; set { _frEllH = value; RaisePropertyChanged(); } }
        public double FrEllLeft { get => _frEllLeft; set { _frEllLeft = value; RaisePropertyChanged(); } }
        public double FrEllTop { get => _frEllTop; set { _frEllTop = value; RaisePropertyChanged(); } }

        private string _frameInfo = "";
        public string FrameInfo { get => _frameInfo; set { _frameInfo = value; RaisePropertyChanged(); } }

        public System.Windows.Input.ICommand ApplyFramingCommand { get; private set; }
        public IAsyncCommand SlewRotateCommand { get; private set; }

        /// <summary>Camera sensor (width, height) in mm — manual pick, else NINA's camera, else ASI585.</summary>
        private (double wMm, double hMm) SensorDims() {
            var pick = SelectedCamera;
            if (pick != null && pick.ShortMm > 0 && pick.LongMm > 0) return (pick.LongMm, pick.ShortMm);
            try {
                var ci = _camera?.GetInfo();
                if (ci != null && ci.Connected && ci.PixelSize > 0 && ci.XSize > 0 && ci.YSize > 0)
                    return (ci.XSize * ci.PixelSize / 1000.0, ci.YSize * ci.PixelSize / 1000.0);
            } catch { }
            return (11.14, 6.26);
        }

        private void OnSelectedFraming(AstroObject o) {
            if (o == null) return;
            FrameRaText = o.Ra.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);
            FrameDecText = o.Dec.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
            if (_focalFilter > 0) FrameFocal = _focalFilter;
            RecomputeFramePreview();
        }

        private void RecomputeFramePreview() {
            var o = SelectedObject;
            if (o == null) { FrameInfo = ""; return; }
            var (wMm, hMm) = SensorDims();
            double focal = _frameFocal > 0 ? _frameFocal : 500;
            double fovW = 3437.74677 * wMm / focal;   // arcmin
            double fovH = 3437.74677 * hMm / focal;
            double objArc = (o.Size.HasValue && o.Size.Value > 0) ? o.Size.Value : 30.0;
            double field = 1.35 * Math.Max(objArc, Math.Max(fovW, fovH));
            if (field <= 0) field = 60;
            double scale = FrameBox / field;
            double ew = objArc * scale;
            FrEllW = ew; FrEllH = ew; FrEllLeft = (FrameBox - ew) / 2; FrEllTop = (FrameBox - ew) / 2;
            double rw = fovW * scale, rh = fovH * scale;
            FrRectW = rw; FrRectH = rh; FrRectLeft = (FrameBox - rw) / 2; FrRectTop = (FrameBox - rh) / 2;
            FrameInfo = $"sensor {fovW:0}'×{fovH:0}' @ {focal:0} mm  ·  object {objArc:0.#}'  ·  {_frameRotation:0.#}°";
        }

        private (double raHours, double decDeg, bool ok) ParseFrameCoords() {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            bool a = double.TryParse(_frameRaText, System.Globalization.NumberStyles.Any, ci, out double ra);
            bool b = double.TryParse(_frameDecText, System.Globalization.NumberStyles.Any, ci, out double dec);
            if (!a || !b || ra < 0 || ra >= 24 || dec < -90 || dec > 90) return (0, 0, false);
            return (ra, dec, true);
        }

        private bool RotatorConnected() {
            try { var ri = _rotator?.GetInfo(); return ri != null && ri.Connected; } catch { return false; }
        }

        private void ApplyFraming() {
            if (IsPreview) { Notification.ShowWarning("Preview location active — set Location to “Auto (NINA profile)” first."); return; }
            var (ra, dec, ok) = ParseFrameCoords();
            if (!ok) { Notification.ShowError("Enter a valid RA (0–24 h) and Dec (−90…90°)."); return; }
            try {
                var coords = new Coordinates(ra, dec, Epoch.J2000, Coordinates.RAType.Hours);
                var name = SelectedObject?.DisplayName ?? "Target";
                var dso = new DeepSkyObject(name, coords,
                    _profileService.ActiveProfile.ApplicationSettings.SkyAtlasImageRepository,
                    _profileService.ActiveProfile.AstrometrySettings.Horizon);
                dso.Name = name;
                _framing.SetCoordinates(dso);
                TrySetFramingRotation(_frameRotation);
                Notification.ShowSuccess($"Framing set: {name} · {_frameRotation:0.#}°");
            } catch (Exception ex) { Notification.ShowError("Could not set framing: " + ex.Message); }
        }

        /// <summary>The Framing Assistant's rotation isn't on its interface, so set it by reflection (best effort).</summary>
        private void TrySetFramingRotation(double deg) {
            try {
                foreach (var nm in new[] { "Rotation", "CameraRotation", "Angle", "PositionAngle" }) {
                    var pi = _framing.GetType().GetProperty(nm);
                    if (pi != null && pi.CanWrite && pi.PropertyType == typeof(double)) { pi.SetValue(_framing, deg); return; }
                }
            } catch { }
        }

        private async Task<bool> SlewRotateAsync() {
            if (IsSun(SelectedObject)) { Notification.ShowError(SunSafetyMessage); return false; }
            if (IsPreview) { Notification.ShowWarning("Preview location active — set Location to “Auto (NINA profile)” to slew."); return false; }
            var (ra, dec, ok) = ParseFrameCoords();
            if (!ok) { Notification.ShowError("Enter a valid RA/Dec first."); return false; }
            if (!_telescope.GetInfo().Connected) { Notification.ShowWarning("No mount connected in NINA."); return false; }
            var coords = new Coordinates(ra, dec, Epoch.J2000, Coordinates.RAType.Hours);
            bool cameraReady = false; try { cameraReady = _camera.GetInfo().Connected; } catch { }
            try { _slewCts?.Cancel(); _slewCts?.Dispose(); } catch { }
            _slewCts = new CancellationTokenSource(); var token = _slewCts.Token; IsSlewing = true;
            try {
                if (cameraReady && RotatorConnected()) {
                    Notification.ShowInformation($"Slew, center & rotate to {_frameRotation:0.#}°… (Stop to cancel)");
                    var cr = new NINA.Sequencer.SequenceItem.Platesolving.CenterAndRotate(
                        _profileService, _telescope, _imaging, _rotator, _filterWheel, _guider,
                        _dome, _domeFollower, _plateSolverFactory, _windowServiceFactory) {
                        Coordinates = new InputCoordinates(coords), PositionAngle = _frameRotation };
                    await cr.Run(new Progress<NINA.Core.Model.ApplicationStatus>(), token);
                    Notification.ShowSuccess($"Centred & rotated to {_frameRotation:0.#}°");
                    return true;
                }
                if (cameraReady) {
                    if (_frameRotation != 0) Notification.ShowWarning("No rotator connected — centering without rotation.");
                    var center = new NINA.Sequencer.SequenceItem.Platesolving.Center(
                        _profileService, _telescope, _imaging, _filterWheel, _guider,
                        _dome, _domeFollower, _plateSolverFactory, _windowServiceFactory) { Coordinates = new InputCoordinates(coords) };
                    await center.Run(new Progress<NINA.Core.Model.ApplicationStatus>(), token);
                    Notification.ShowSuccess("Centred (plate-solved)"); return true;
                }
                var okslew = await _telescope.SlewToCoordinatesAsync(coords, token);
                Notification.ShowInformation("Slewed (no camera — no plate-solve / rotation)"); return okslew;
            } catch (OperationCanceledException) {
                try { _telescope.StopSlew(); } catch { }
                Notification.ShowInformation("Stopped."); return false;
            } catch (Exception ex) { Notification.ShowError("Slew/rotate failed: " + ex.Message); return false; }
            finally { IsSlewing = false; try { _slewCts?.Dispose(); } catch { } _slewCts = null; }
        }

        // ==================================================================================
        //  SCHEDULER  —  plan objects onto dates/times, shown per night on the right, with an
        //  optional automatic runner that images each slot at its time when NINA is idle.
        // ==================================================================================

        private readonly object _scheduleLock = new object();

        /// <summary>All scheduled slots (grouped/sorted for display by ScheduleView).</summary>
        public ObservableCollection<ScheduleEntry> Schedule { get; private set; }

        private System.ComponentModel.ICollectionView _scheduleView;
        /// <summary>Grouped-by-night, sorted-by-start view bound by the right-hand schedule panel.</summary>
        public System.ComponentModel.ICollectionView ScheduleView {
            get {
                if (_scheduleView == null) {
                    _scheduleView = System.Windows.Data.CollectionViewSource.GetDefaultView(Schedule);
                    _scheduleView.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(ScheduleEntry.NightLabel)));
                    _scheduleView.SortDescriptions.Add(new System.ComponentModel.SortDescription(nameof(ScheduleEntry.Start), System.ComponentModel.ListSortDirection.Ascending));
                }
                return _scheduleView;
            }
        }

        public System.Windows.Input.ICommand AddToScheduleCommand { get; private set; }
        public System.Windows.Input.ICommand SaveScheduleEntryCommand { get; private set; }
        public System.Windows.Input.ICommand CancelScheduleCommand { get; private set; }
        public System.Windows.Input.ICommand DeleteScheduleEntryCommand { get; private set; }
        public System.Windows.Input.ICommand ClearScheduleCommand { get; private set; }
        public System.Windows.Input.ICommand StopAutoCommand { get; private set; }

        public string ScheduleCountText =>
            Schedule == null || Schedule.Count == 0 ? "No slots yet — pick an object and press +"
            : $"{Schedule.Count} slot" + (Schedule.Count == 1 ? "" : "s");

        // ---- Automatic mode ----
        private bool _autoMode;
        private bool _autoRunning;
        private string _autoCurrentName = "";
        private CancellationTokenSource _autoCts;

        /// <summary>When on, the plugin images each scheduled slot at its time while NINA is otherwise idle.</summary>
        public bool AutoMode {
            get => _autoMode;
            set {
                _autoMode = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(AutoStatus));
                PersistSchedule();
                if (value) { try { CheckAutoSchedule(); } catch { } }
            }
        }

        public string AutoStatus {
            get {
                if (!_autoMode) return "Automatic mode OFF — schedule is planning-only.";
                if (_autoRunning) return $"● Imaging ‘{_autoCurrentName}’ now (automatic)…";
                return "● Automatic mode ARMED — waiting for the next scheduled time.";
            }
        }

        public System.Windows.Visibility AutoRunningVisibility =>
            _autoRunning ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        private bool _parkWhenDone;
        /// <summary>When on, automatic mode parks the mount after the last scheduled slot of the session finishes.</summary>
        public bool ParkWhenDone {
            get => _parkWhenDone;
            set { _parkWhenDone = value; RaisePropertyChanged(); PersistSchedule(); }
        }

        /// <summary>True if any slot is still pending (not done and its end time is still in the future).</summary>
        private bool AnyPendingSlots() {
            var now = DateTime.Now;
            lock (_scheduleLock) { return Schedule.Any(e => !e.Done && now < e.End); }
        }

        /// <summary>
        /// Reads the NINA Safety Monitor. Returns (present, safe): present=false when no monitor is
        /// connected (then automatic mode is NOT gated on safety); when present, safe reflects IsSafe.
        /// </summary>
        private (bool present, bool safe) SafetyState() {
            try {
                var si = _safety?.GetInfo();
                if (si != null && si.Connected) return (true, si.IsSafe);
            } catch { }
            return (false, true);
        }

        /// <summary>
        /// The dawn cut-off (LOCAL time) for the night in progress, using the NINA profile location:
        /// the moment the Sun climbs back above the darkest achievable threshold (astronomical −18°,
        /// falling back to −12/−6/0 at high latitudes / bright summer nights). Automatic mode never
        /// images past this, so a run always stops before it gets light — whatever end time you set.
        /// Returns null if it can't be computed (e.g. polar day), meaning no dawn cap is applied.
        /// </summary>
        private DateTime? DawnCutoffLocal() {
            try {
                var (lat, lon) = GetObserverLocation();
                var nowUtc = DateTime.UtcNow;
                var toLocal = DateTime.Now - nowUtc;   // robust UTC→local (independent of DateTimeKind)
                foreach (var thr in new[] { -18.0, -12.0, -6.0, 0.0 }) {
                    var w = Visibility.SunBelowWindow(lat, lon, nowUtc, thr);
                    if (w.ok && w.end > nowUtc) return w.end + toLocal;
                }
            } catch { }
            return null;
        }

        /// <summary>True when the Sun is currently above the horizon at the profile location (daylight).</summary>
        private bool SunIsUp() {
            try {
                var (lat, lon) = GetObserverLocation();
                return Visibility.SunAltitude(lat, lon, DateTime.UtcNow) > 0.0;
            } catch { return false; }
        }

        // ---- "+" scheduling dialog (an in-panel overlay) ----
        private AstroObject _scheduleTarget;

        private System.Windows.Visibility _scheduleDialogVisibility = System.Windows.Visibility.Collapsed;
        public System.Windows.Visibility ScheduleDialogVisibility { get => _scheduleDialogVisibility; set { _scheduleDialogVisibility = value; RaisePropertyChanged(); } }

        private string _scheduleTargetName = "";
        public string ScheduleTargetName { get => _scheduleTargetName; set { _scheduleTargetName = value; RaisePropertyChanged(); } }

        private DateTime? _scheduleDate = DateTime.Today;
        public DateTime? ScheduleDate { get => _scheduleDate; set { _scheduleDate = value; RaisePropertyChanged(); } }

        private string _scheduleStartText = "22:00";
        public string ScheduleStartText { get => _scheduleStartText; set { _scheduleStartText = value; RaisePropertyChanged(); } }

        private string _scheduleEndText = "01:00";
        public string ScheduleEndText { get => _scheduleEndText; set { _scheduleEndText = value; RaisePropertyChanged(); } }

        private string _scheduleExposureText = "120";
        public string ScheduleExposureText { get => _scheduleExposureText; set { _scheduleExposureText = value; RaisePropertyChanged(); } }

        private string _scheduleGainText = "";
        public string ScheduleGainText { get => _scheduleGainText; set { _scheduleGainText = value; RaisePropertyChanged(); } }

        private string _scheduleRotationText = "0";
        public string ScheduleRotationText { get => _scheduleRotationText; set { _scheduleRotationText = value; RaisePropertyChanged(); } }

        private string _scheduleError = "";
        public string ScheduleError { get => _scheduleError; set { _scheduleError = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(ScheduleErrorVisibility)); } }
        public System.Windows.Visibility ScheduleErrorVisibility =>
            string.IsNullOrEmpty(_scheduleError) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

        private void OpenScheduleDialog(AstroObject o) {
            if (o == null) return;
            if (IsSun(o)) { Notification.ShowError(SunSafetyMessage); return; }
            _scheduleTarget = o;
            ScheduleTargetName = o.DisplayName;
            ScheduleError = "";
            // Default the date to tonight, and prefill sensible times if the fields are empty.
            if (!_scheduleDate.HasValue) ScheduleDate = DateTime.Today;
            // Carry over the rotation currently dialled in the framing panel.
            ScheduleRotationText = _frameRotation.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
            ScheduleDialogVisibility = System.Windows.Visibility.Visible;
        }

        private static bool TryParseHm(string s, out int h, out int m) {
            h = 0; m = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            var parts = s.Trim().Split(':');
            if (parts.Length != 2) return false;
            if (!int.TryParse(parts[0], out h) || !int.TryParse(parts[1], out m)) return false;
            return h >= 0 && h <= 23 && m >= 0 && m <= 59;
        }

        private void SaveScheduleEntry() {
            if (_scheduleTarget == null) { ScheduleDialogVisibility = System.Windows.Visibility.Collapsed; return; }
            var date = _scheduleDate ?? DateTime.Today;
            if (!TryParseHm(_scheduleStartText, out int sh, out int sm)) { ScheduleError = "Start time must be HH:mm (e.g. 22:30)."; return; }
            if (!TryParseHm(_scheduleEndText, out int eh, out int em)) { ScheduleError = "End time must be HH:mm (e.g. 01:15)."; return; }
            var start = date.Date.AddHours(sh).AddMinutes(sm);
            var end = date.Date.AddHours(eh).AddMinutes(em);
            if (end <= start) end = end.AddDays(1);   // crosses midnight into the next morning

            double exp = 120;
            double.TryParse(_scheduleExposureText, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out exp);
            if (exp <= 0) exp = 120;

            int gain = -1;
            if (!string.IsNullOrWhiteSpace(_scheduleGainText)) int.TryParse(_scheduleGainText.Trim(), out gain);

            double rot = 0;
            double.TryParse(_scheduleRotationText, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out rot);
            rot = ((rot % 360) + 360) % 360;

            var entry = new ScheduleEntry {
                Name = _scheduleTarget.DisplayName,
                Ra = _scheduleTarget.Ra,
                Dec = _scheduleTarget.Dec,
                Start = start,
                End = end,
                ExposureSeconds = exp,
                Gain = gain,
                Rotation = rot
            };
            lock (_scheduleLock) { Schedule.Add(entry); }
            RecomputeOverlaps();
            RefreshScheduleStatus();
            PersistSchedule();
            RaisePropertyChanged(nameof(ScheduleCountText));
            ScheduleDialogVisibility = System.Windows.Visibility.Collapsed;
            Notification.ShowSuccess($"Scheduled '{entry.Name}' for {entry.TimeText}");
        }

        private void DeleteScheduleEntry(ScheduleEntry e) {
            if (e == null) return;
            lock (_scheduleLock) { Schedule.Remove(e); }
            RecomputeOverlaps();
            PersistSchedule();
            RaisePropertyChanged(nameof(ScheduleCountText));
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }

        private void ClearSchedule() {
            lock (_scheduleLock) { Schedule.Clear(); }
            PersistSchedule();
            RaisePropertyChanged(nameof(ScheduleCountText));
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }

        /// <summary>Flag every slot that overlaps another slot in the same night (shown in red).</summary>
        private void RecomputeOverlaps() {
            List<ScheduleEntry> list;
            lock (_scheduleLock) { list = Schedule.ToList(); }
            foreach (var e in list) e.Overlaps = false;
            foreach (var g in list.GroupBy(e => e.EveningDate)) {
                var arr = g.OrderBy(e => e.Start).ToList();
                for (int i = 0; i < arr.Count; i++)
                    for (int j = i + 1; j < arr.Count; j++)
                        if (arr[i].Start < arr[j].End && arr[j].Start < arr[i].End) { arr[i].Overlaps = true; arr[j].Overlaps = true; }
            }
        }

        /// <summary>Mark past slots as missed/done (unless one is currently running). Runs on the UI tick.</summary>
        private void RefreshScheduleStatus() {
            bool changed = false;
            List<ScheduleEntry> list;
            lock (_scheduleLock) { list = Schedule.ToList(); }
            var now = DateTime.Now;
            foreach (var e in list) {
                if (!e.Done && e.Status != "imaging…" && now >= e.End) {
                    e.Done = true;
                    if (string.IsNullOrEmpty(e.Status) || e.Status == "wacht") e.Status = "missed";
                    changed = true;
                }
            }
            if (changed) PersistSchedule();
        }

        private void PersistSchedule() {
            try {
                List<ScheduleEntry> list;
                lock (_scheduleLock) { list = Schedule.ToList(); }
                ScheduleStore.Save(new ScheduleFile { AutoMode = _autoMode, ParkWhenDone = _parkWhenDone, Entries = list });
            } catch { }
        }

        private void LoadSchedule() {
            try {
                var doc = ScheduleStore.Load();
                lock (_scheduleLock) {
                    Schedule.Clear();
                    if (doc.Entries != null) foreach (var e in doc.Entries) Schedule.Add(e);
                }
                _autoMode = doc.AutoMode;
                _parkWhenDone = doc.ParkWhenDone;
                RaisePropertyChanged(nameof(AutoMode));
                RaisePropertyChanged(nameof(ParkWhenDone));
                RaisePropertyChanged(nameof(AutoStatus));
                RaisePropertyChanged(nameof(ScheduleCountText));
                RecomputeOverlaps();
                RefreshScheduleStatus();
            } catch { }
        }

        /// <summary>
        /// The heart of automatic mode: if armed and NINA is idle, find the slot whose window contains
        /// 'now' and hasn't run yet, then image it. Called every minute from the UI tick.
        /// </summary>
        private void CheckAutoSchedule() {
            if (!_autoMode || _autoRunning) return;
            if (_isSlewing) return;   // a manual slew/plate-solve is in progress

            ScheduleEntry due;
            var now = DateTime.Now;
            lock (_scheduleLock) {
                due = Schedule.Where(e => !e.Done && now >= e.Start && now < e.End
                                          && !string.Equals(e.Name, "Sun", StringComparison.OrdinalIgnoreCase))
                              .OrderBy(e => e.Start).FirstOrDefault();
            }
            if (due == null) return;

            // Only take over the mount/camera when NINA is genuinely idle.
            bool mountOk = false, camOk = false, camExposing = true;
            try { mountOk = _telescope.GetInfo().Connected; } catch { }
            try { var ci = _camera.GetInfo(); camOk = ci.Connected; camExposing = ci.IsExposing; } catch { }
            if (!mountOk) { due.Status = "waiting (no mount)"; return; }
            if (!camOk) { due.Status = "waiting (no camera)"; return; }
            if (camExposing) { due.Status = "waiting (NINA busy)"; return; }
            // Safety monitor gate: only proceed while it reports safe (no gate if none is connected).
            var (monPresent, isSafe) = SafetyState();
            if (monPresent && !isSafe) { due.Status = "waiting (unsafe)"; return; }
            // Never begin a slot in daylight (e.g. a slot whose window runs past dawn).
            if (SunIsUp()) { due.Status = "waiting (daylight)"; return; }

            _autoRunning = true;
            _autoCurrentName = due.Name;
            due.Status = "imaging…";
            RaisePropertyChanged(nameof(AutoStatus));
            RaisePropertyChanged(nameof(AutoRunningVisibility));
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            try { _autoCts?.Dispose(); } catch { }
            _autoCts = new CancellationTokenSource();
            var token = _autoCts.Token;
            var entry = due;
            _ = Task.Run(() => RunScheduledAsync(entry, token));
        }

        /// <summary>Slew+centre to the slot's target, then loop exposures until its end time.</summary>
        private async Task RunScheduledAsync(ScheduleEntry entry, CancellationToken token) {
            try {
                Notification.ShowInformation($"Automatic mode: starting '{entry.Name}' until {entry.End:HH:mm}");
                var coords = new Coordinates(entry.Ra, entry.Dec, Epoch.J2000, Coordinates.RAType.Hours);

                // Re-check the safety monitor right before touching hardware; if it turned unsafe,
                // abort without unparking (the slot stays pending and retries when safe again).
                var (mon0, safe0) = SafetyState();
                if (mon0 && !safe0) { entry.Status = "waiting (unsafe)"; return; }

                // Unpark the mount if it's parked (e.g. parked after a previous session) — only reached when safe.
                try {
                    var ti = _telescope.GetInfo();
                    if (ti.Connected && ti.AtPark) {
                        Notification.ShowInformation("Automatic mode: unparking the mount (safe to open)…");
                        await _telescope.UnparkTelescope(new Progress<NINA.Core.Model.ApplicationStatus>(), token);
                    }
                } catch (Exception uex) { Notification.ShowWarning("Unpark failed: " + uex.Message); }

                bool cameraReady = false;
                try { cameraReady = _camera.GetInfo().Connected; } catch { }

                // 1) Point at the target — plate-solve & centre (and rotate to the slot's angle when a
                //    rotator is connected and a rotation was set), else blind slew.
                if (cameraReady && entry.Rotation != 0 && RotatorConnected()) {
                    var cr = new NINA.Sequencer.SequenceItem.Platesolving.CenterAndRotate(
                        _profileService, _telescope, _imaging, _rotator, _filterWheel, _guider,
                        _dome, _domeFollower, _plateSolverFactory, _windowServiceFactory) {
                        Coordinates = new InputCoordinates(coords), PositionAngle = entry.Rotation
                    };
                    await cr.Run(new Progress<NINA.Core.Model.ApplicationStatus>(), token);
                } else if (cameraReady) {
                    var center = new NINA.Sequencer.SequenceItem.Platesolving.Center(
                        _profileService, _telescope, _imaging, _filterWheel, _guider,
                        _dome, _domeFollower, _plateSolverFactory, _windowServiceFactory) {
                        Coordinates = new InputCoordinates(coords)
                    };
                    await center.Run(new Progress<NINA.Core.Model.ApplicationStatus>(), token);
                } else {
                    await _telescope.SlewToCoordinatesAsync(coords, token);
                }

                // 2) Best-effort: start guiding (ignored if no guider is set up).
                try {
                    var startGuide = new NINA.Sequencer.SequenceItem.Guider.StartGuiding(_guider);
                    await startGuide.Run(new Progress<NINA.Core.Model.ApplicationStatus>(), token);
                } catch { }

                // 3) Loop exposures until the slot's end time (each sub saved through NINA's pipeline).
                // Stop at the slot's end time OR at dawn, whichever comes first — imaging never runs
                // into daylight even if the end time was set later than the night lasts.
                var dawn = DawnCutoffLocal();
                DateTime hardEnd = entry.End;
                bool cappedByDawn = false;
                if (dawn.HasValue && dawn.Value < hardEnd) { hardEnd = dawn.Value; cappedByDawn = true; }

                int subs = 0;
                bool unsafeStop = false;
                while (DateTime.Now < hardEnd && _autoMode && !token.IsCancellationRequested) {
                    // Bail out of the exposure loop the moment the safety monitor goes unsafe.
                    var (monL, safeL) = SafetyState();
                    if (monL && !safeL) { unsafeStop = true; break; }
                    var take = new NINA.Sequencer.SequenceItem.Imaging.TakeExposure(
                        _profileService, _camera, _imaging, _imageSave, _imageHistory) {
                        ExposureTime = entry.ExposureSeconds,
                        Gain = entry.Gain
                    };
                    await take.Run(new Progress<NINA.Core.Model.ApplicationStatus>(), token);
                    subs++;
                }

                // 4) Best-effort: stop guiding.
                try {
                    var stopGuide = new NINA.Sequencer.SequenceItem.Guider.StopGuiding(_guider);
                    await stopGuide.Run(new Progress<NINA.Core.Model.ApplicationStatus>(), CancellationToken.None);
                } catch { }

                // If we stopped because conditions turned unsafe, park the mount to a safe state and stop.
                if (unsafeStop) {
                    entry.Done = true;
                    entry.Status = $"stopped (unsafe) · {subs} subs";
                    Notification.ShowWarning($"Automatic mode: conditions turned UNSAFE — stopped '{entry.Name}' and parking the mount.");
                    try {
                        bool conn = false; try { conn = _telescope.GetInfo().Connected; } catch { }
                        if (conn) await _telescope.ParkTelescope(new Progress<NINA.Core.Model.ApplicationStatus>(), CancellationToken.None);
                        Notification.ShowSuccess("Mount parked (unsafe).");
                    } catch (Exception pex) { Notification.ShowWarning("Park after unsafe failed: " + pex.Message); }
                    return;
                }

                entry.Done = true;
                entry.Status = cappedByDawn ? $"done at dawn · {subs} subs" : $"done · {subs} subs";
                Notification.ShowSuccess($"Automatic mode: finished '{entry.Name}' ({subs} subs){(cappedByDawn ? " — stopped at dawn" : "")}");

                // Park the mount after the last slot of the session, if the user asked for it.
                if (_parkWhenDone && !AnyPendingSlots()) {
                    try {
                        bool connected = false;
                        try { connected = _telescope.GetInfo().Connected; } catch { }
                        if (connected) {
                            Notification.ShowInformation("Automatic mode: last slot done — parking the mount…");
                            await _telescope.ParkTelescope(new Progress<NINA.Core.Model.ApplicationStatus>(), CancellationToken.None);
                            Notification.ShowSuccess("Mount parked.");
                        }
                    } catch (Exception pex) {
                        Notification.ShowWarning("Could not park the mount: " + pex.Message);
                    }
                }
            } catch (OperationCanceledException) {
                entry.Status = "stopped";
                Notification.ShowInformation($"Automatic capture of '{entry.Name}' stopped.");
            } catch (Exception ex) {
                entry.Status = "error";
                Notification.ShowError($"Automatic capture of '{entry.Name}' failed: " + ex.Message);
            } finally {
                _autoRunning = false;
                _autoCurrentName = "";
                try { _autoCts?.Dispose(); } catch { }
                _autoCts = null;
                RunOnUI(() => {
                    RaisePropertyChanged(nameof(AutoStatus));
                    RaisePropertyChanged(nameof(AutoRunningVisibility));
                    System.Windows.Input.CommandManager.InvalidateRequerySuggested();
                });
                PersistSchedule();
            }
        }
    }
}
