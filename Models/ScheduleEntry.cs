using System;
using System.Text.Json.Serialization;

namespace UnderAfricanSkies.ObjectChooser.Models {

    /// <summary>
    /// One scheduled imaging slot: a target (by name + fixed J2000 coordinates) to image between a
    /// start and end date/time, with a per-sub exposure length. Times are stored as LOCAL time
    /// (that's what the user types and what the automatic runner compares against DateTime.Now).
    /// The presentation members (grouping label, overlap flag, run status) are not serialised.
    /// </summary>
    public class ScheduleEntry : System.ComponentModel.INotifyPropertyChanged {

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        private void OnChanged([System.Runtime.CompilerServices.CallerMemberName] string n = null) =>
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));

        [JsonPropertyName("name")] public string Name { get; set; }
        /// <summary>Right Ascension, decimal hours, J2000 (fixed — the sky address of the target).</summary>
        [JsonPropertyName("ra")] public double Ra { get; set; }
        /// <summary>Declination, decimal degrees, J2000.</summary>
        [JsonPropertyName("dec")] public double Dec { get; set; }
        [JsonPropertyName("start")] public DateTime Start { get; set; }
        [JsonPropertyName("end")] public DateTime End { get; set; }
        /// <summary>Per-sub exposure length in seconds used by the automatic runner.</summary>
        [JsonPropertyName("exposure")] public double ExposureSeconds { get; set; } = 120;
        /// <summary>Camera gain for the run; &lt; 0 means "leave the camera's default".</summary>
        [JsonPropertyName("gain")] public int Gain { get; set; } = -1;
        /// <summary>Camera position angle in degrees for this slot (0 = no rotation / as-is).</summary>
        [JsonPropertyName("rotation")] public double Rotation { get; set; } = 0;

        // ---- presentation / runtime (not persisted) ----

        /// <summary>The evening a night belongs to: slots after noon → that date; before noon → the previous date.</summary>
        [JsonIgnore]
        public DateTime EveningDate =>
            (Start.Hour >= 12 ? Start.Date : Start.Date.AddDays(-1));

        /// <summary>Group header, e.g. "Nacht van vr 5 sep 2026".</summary>
        [JsonIgnore]
        public string NightLabel => "Nacht van " + EveningDate.ToString("ddd d MMM yyyy");

        /// <summary>Row time text, e.g. "vr 5 sep · 22:30 – 01:15".</summary>
        [JsonIgnore]
        public string TimeText =>
            $"{Start:ddd d MMM} · {Start:HH:mm} – {End:HH:mm}";

        /// <summary>Short target + exposure line, e.g. "M31 · 120 s subs".</summary>
        [JsonIgnore]
        public string DetailText {
            get {
                var dur = End - Start;
                string len = dur.TotalHours >= 1
                    ? $"{(int)dur.TotalHours}u{dur.Minutes:00}"
                    : $"{(int)dur.TotalMinutes} min";
                string g = Gain >= 0 ? $" · gain {Gain}" : "";
                string r = Rotation != 0 ? $" · rot {Rotation:0.#}°" : "";
                return $"{len} · {ExposureSeconds:0} s subs{g}{r}";
            }
        }

        private bool _overlaps;
        /// <summary>True when this slot overlaps another slot in the same night (shown in red).</summary>
        [JsonIgnore]
        public bool Overlaps { get => _overlaps; set { _overlaps = value; OnChanged(); OnChanged(nameof(RowBrush)); OnChanged(nameof(OverlapVisibility)); } }

        [JsonIgnore]
        public System.Windows.Visibility OverlapVisibility =>
            _overlaps ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        private string _status = "";
        /// <summary>Run status shown on the row: "", "wacht", "bezig…", "klaar", "gemist".</summary>
        [JsonIgnore]
        public string Status { get => _status; set { _status = value; OnChanged(); OnChanged(nameof(StatusVisibility)); OnChanged(nameof(RowBrush)); } }

        [JsonIgnore]
        public System.Windows.Visibility StatusVisibility =>
            string.IsNullOrEmpty(_status) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

        /// <summary>True once the automatic runner has completed (or skipped) this slot, so it isn't re-run
        /// (persisted, so a NINA restart mid-night doesn't repeat a slot that already finished).</summary>
        [JsonPropertyName("done")] public bool Done { get; set; }

        /// <summary>Left border colour: red on overlap, green while running, grey otherwise.</summary>
        [JsonIgnore]
        public System.Windows.Media.Brush RowBrush {
            get {
                if (_status == "imaging…") return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x33, 0xff, 0x88));
                if (_overlaps) return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xff, 0x6b, 0x6b));
                return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3a, 0x3a, 0x46));
            }
        }
    }

    /// <summary>Persisted schedule document: the slots plus whether the automatic runner is armed.</summary>
    public class ScheduleFile {
        [JsonPropertyName("autoMode")] public bool AutoMode { get; set; }
        [JsonPropertyName("parkWhenDone")] public bool ParkWhenDone { get; set; }
        [JsonPropertyName("entries")] public System.Collections.Generic.List<ScheduleEntry> Entries { get; set; }
            = new System.Collections.Generic.List<ScheduleEntry>();
    }
}
