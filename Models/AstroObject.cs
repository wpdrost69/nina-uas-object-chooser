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
