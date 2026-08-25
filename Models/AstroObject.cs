using System.Text.Json.Serialization;

namespace UnderAfricanSkies.ObjectChooser.Models {

    /// <summary>
    /// One deep-sky object as delivered by the Under African Skies catalogue
    /// endpoint (https://underafricanskies.eu/astro-catalog.json).
    /// Field names match the JSON keys exactly (System.Text.Json is case-sensitive
    /// unless configured otherwise; we configure PropertyNameCaseInsensitive in the
    /// service, but keeping them aligned avoids surprises).
    /// </summary>
    public class AstroObject : System.ComponentModel.INotifyPropertyChanged {

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        private void OnChanged([System.Runtime.CompilerServices.CallerMemberName] string n = null) =>
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));


        [JsonPropertyName("name")]
        public string Name { get; set; }

        /// <summary>Catalogue designation shown in front of the name, e.g. "M33", "NGC 253". May be null.</summary>
        [JsonPropertyName("desig")]
        public string Desig { get; set; }

        /// <summary>Galaxy, Nebula, Open Cluster, Globular, Planetary, SNR, Dark Nebula, ...</summary>
        [JsonPropertyName("type")]
        public string Type { get; set; }

        /// <summary>Right Ascension in decimal HOURS (0..24), J2000.</summary>
        [JsonPropertyName("ra")]
        public double Ra { get; set; }

        /// <summary>Declination in decimal DEGREES (-90..+90), J2000.</summary>
        [JsonPropertyName("dec")]
        public double Dec { get; set; }

        /// <summary>Integrated magnitude, or null when unknown.</summary>
        [JsonPropertyName("mag")]
        public double? Mag { get; set; }

        /// <summary>Major-axis angular size in arcMINutes, or null when unknown.</summary>
        [JsonPropertyName("size")]
        public double? Size { get; set; }

        /// <summary>"SH" (southern) or "NH" (northern), derived from Dec.</summary>
        [JsonPropertyName("hemisphere")]
        public string Hemisphere { get; set; }

        /// <summary>Wikipedia article name (for the preview image + info), same as the website uses.</summary>
        [JsonPropertyName("wiki")]
        public string Wiki { get; set; }

        // ---- Convenience presentation members (not part of the JSON) ----

        /// <summary>What the list shows: "M33 · Triangulum Galaxy" or just the name.</summary>
        [JsonIgnore]
        public string DisplayName =>
            string.IsNullOrWhiteSpace(Desig) ? Name : $"{Desig} · {Name}";

        /// <summary>Broad group used by the type filter: galaxies / nebulae / clusters / other.</summary>
        [JsonIgnore]
        public string Group {
            get {
                var t = (Type ?? string.Empty).ToLowerInvariant();
                if (t == "planet" || t == "moon" || t == "sun") return "planets";
                if (t.Contains("galax")) return "galaxies";
                if (t.Contains("cluster") || t.Contains("globular") || t.Contains("open") || t.Contains("association")) return "clusters";
                if (t.Contains("nebula") || t.Contains("planetary") || t.Contains("snr") || t.Contains("supernova")
                    || t.Contains("hii") || t.Contains("emission") || t.Contains("reflection") || t.Contains("dark")) return "nebulae";
                return "other";
            }
        }

        /// <summary>True for Sun/Moon/planets (they move, and get a symbol instead of a sky photo).</summary>
        [JsonIgnore]
        public bool IsSolarSystem => Group == "planets";

        [JsonIgnore]
        public bool IsSouthern => Dec < 0;

        // ---- Observer-dependent values, filled in by the view-model from NINA's profile location ----

        /// <summary>Highest altitude this object reaches from the observer (transit), in degrees. Null until computed.</summary>
        [JsonIgnore]
        public double? MaxAltitude { get; set; }

        /// <summary>Current altitude right now from the observer, in degrees. Null until computed.</summary>
        [JsonIgnore]
        public double? CurrentAltitude { get; set; }

        /// <summary>Current azimuth right now, degrees from North clockwise (N=0,E=90,S=180,W=270). Null until computed.</summary>
        [JsonIgnore]
        public double? CurrentAzimuth { get; set; }

        /// <summary>
        /// Where to point right now, ASIAIR-style: compass heading + height, e.g. "SE  45°".
        /// Shows "below horizon" when the object is currently down. "" until computed.
        /// </summary>
        [JsonIgnore]
        public string DirectionText {
            get {
                if (!CurrentAltitude.HasValue || !CurrentAzimuth.HasValue) return "";
                double alt = CurrentAltitude.Value;
                if (alt < 0) return "below horizon";
                string comp = UnderAfricanSkies.ObjectChooser.Services.Visibility.Compass(CurrentAzimuth.Value);
                return $"{comp}  {System.Math.Round(alt)}°";
            }
        }

        /// <summary>▲ while the object is climbing (eastern sky), ▼ while it is descending (western sky), "" if down.</summary>
        [JsonIgnore]
        public string DirectionTrend {
            get {
                if (!CurrentAltitude.HasValue || !CurrentAzimuth.HasValue || CurrentAltitude.Value < 0) return "";
                return CurrentAzimuth.Value < 180.0 ? "▲" : "▼";   // east = rising, west = setting
            }
        }

        /// <summary>Tooltip explaining the heading, e.g. "In the south-east, 45° above the horizon · rising".</summary>
        [JsonIgnore]
        public string DirectionTooltip {
            get {
                if (!CurrentAltitude.HasValue || !CurrentAzimuth.HasValue) return "Point your scope this way";
                double alt = CurrentAltitude.Value;
                if (alt < 0) return "Currently below the horizon — not observable right now";
                string comp = UnderAfricanSkies.ObjectChooser.Services.Visibility.Compass(CurrentAzimuth.Value);
                string trend = CurrentAzimuth.Value < 180.0 ? "rising" : "setting";
                return $"Look {comp} ({System.Math.Round(CurrentAzimuth.Value)}°), {System.Math.Round(alt)}° above the horizon · {trend}";
            }
        }

        /// <summary>Hidden when there is no current-position data yet.</summary>
        [JsonIgnore]
        public System.Windows.Visibility DirectionVisibility =>
            (CurrentAltitude.HasValue && CurrentAzimuth.HasValue)
                ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        // ---- Altitude-through-the-night mini chart (filled in by the view-model) ----

        /// <summary>Polyline of the object's altitude across tonight's dark hours, in a 200×70 box.</summary>
        [JsonIgnore] public System.Windows.Media.PointCollection AltitudeCurve { get; set; }

        /// <summary>Top-left of the green "now" dot inside the 200×70 chart box.</summary>
        [JsonIgnore] public double DotLeft { get; set; }
        [JsonIgnore] public double DotTop { get; set; }

        /// <summary>Green "now" dot — always shown while a chart exists (the plot window always includes now).</summary>
        [JsonIgnore] public System.Windows.Visibility DotVisibility { get; set; } = System.Windows.Visibility.Collapsed;

        /// <summary>Shaded night band (dusk→dawn) inside the 200×70 chart box.</summary>
        [JsonIgnore] public double BandLeft { get; set; }
        [JsonIgnore] public double BandWidth { get; set; }
        [JsonIgnore] public System.Windows.Visibility BandVisibility { get; set; } = System.Windows.Visibility.Collapsed;
        /// <summary>X positions for the dusk / dawn labels (band edges).</summary>
        [JsonIgnore] public double DuskLeft { get; set; }
        [JsonIgnore] public double DawnLeft { get; set; }

        /// <summary>Chart is shown only once a curve has been computed (hidden under polar day).</summary>
        [JsonIgnore]
        public System.Windows.Visibility ChartVisibility =>
            (AltitudeCurve != null && AltitudeCurve.Count > 1)
                ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        /// <summary>
        /// Tell the UI that every time-dependent value has just been recomputed, so bindings refresh.
        /// Called by the view-model's minute timer (and after each recompute) so the "Now" heading,
        /// the green dot and the altitude curve track the moving sky without rebuilding the list.
        /// </summary>
        public void RaiseTimeDependentChanged() {
            OnChanged(nameof(DirectionText));
            OnChanged(nameof(DirectionTrend));
            OnChanged(nameof(DirectionTooltip));
            OnChanged(nameof(DirectionVisibility));
            OnChanged(nameof(AltitudeCurve));
            OnChanged(nameof(DotLeft));
            OnChanged(nameof(DotTop));
            OnChanged(nameof(DotVisibility));
            OnChanged(nameof(ChartVisibility));
            OnChanged(nameof(BandLeft));
            OnChanged(nameof(BandWidth));
            OnChanged(nameof(BandVisibility));
            OnChanged(nameof(DuskLeft));
            OnChanged(nameof(DawnLeft));
        }

        /// <summary>True if the object is well placed tonight from the observer's location.</summary>
        [JsonIgnore]
        public bool WellPlaced { get; set; }

        /// <summary>"63°" style string for the grid, or "" when unknown.</summary>
        [JsonIgnore]
        public string MaxAltText => MaxAltitude.HasValue ? $"{System.Math.Round(MaxAltitude.Value)}°" : "";

        /// <summary>Per-object thumbnail shown in the list; loaded lazily from cache/Wikipedia.</summary>
        [JsonIgnore]
        private System.Windows.Media.ImageSource _thumbnail;
        [JsonIgnore]
        public System.Windows.Media.ImageSource Thumbnail {
            get => _thumbnail;
            set { _thumbnail = value; OnChanged(); }
        }

        /// <summary>True once a real Wikipedia photo has been loaded (vs. a type placeholder).</summary>
        [JsonIgnore] public bool HasRealImage { get; set; }
        /// <summary>True once we have tried (and failed) to fetch a photo, so we don't retry endlessly.</summary>
        [JsonIgnore] public bool ImageAttempted { get; set; }

        /// <summary>Recommended focal length badge, e.g. "700 mm · 72%". Filled in by the view-model.</summary>
        [JsonIgnore]
        private string _focalText = "";
        [JsonIgnore]
        public string FocalText {
            get => _focalText;
            set { _focalText = value; OnChanged(); OnChanged(nameof(FocalVisibility)); }
        }

        /// <summary>Hidden when there is no focal recommendation (unknown size).</summary>
        [JsonIgnore]
        public System.Windows.Visibility FocalVisibility =>
            string.IsNullOrEmpty(_focalText) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

        /// <summary>Second line shown under the name in the list.</summary>
        [JsonIgnore]
        public string ListLine {
            get {
                var parts = new System.Collections.Generic.List<string>();
                if (!string.IsNullOrEmpty(Type)) parts.Add(Type);
                if (!string.IsNullOrEmpty(Hemisphere)) parts.Add(Hemisphere);
                if (MaxAltitude.HasValue) parts.Add("max " + System.Math.Round(MaxAltitude.Value) + "°");
                if (Mag.HasValue) parts.Add("mag " + Mag.Value.ToString("0.0"));
                if (Size.HasValue) parts.Add(Size.Value.ToString("0.#") + "'");
                return string.Join("  ·  ", parts);
            }
        }
    }

    /// <summary>Top-level shape of the catalogue JSON document.</summary>
    public class AstroCatalog {
        [JsonPropertyName("catalog")] public string Catalog { get; set; }
        [JsonPropertyName("version")] public string Version { get; set; }
        [JsonPropertyName("generated")] public string Generated { get; set; }
        [JsonPropertyName("count")] public int Count { get; set; }
        [JsonPropertyName("objects")] public System.Collections.Generic.List<AstroObject> Objects { get; set; }
    }
}
