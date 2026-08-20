using System;
using System.Collections.Generic;

namespace UnderAfricanSkies.ObjectChooser.Services {

    /// <summary>
    /// Location-aware visibility maths — a direct C# port of the website's
    /// calculateAltitude(), so the plugin behaves identically anywhere on Earth.
    ///
    /// The observer's latitude/longitude come from NINA's active profile, so the
    /// panel automatically works wherever the user is: Namibia, Chile, Spain,
    /// Australia — no location picker needed (NINA already knows where you are).
    /// </summary>
    public static class Visibility {

        private static double JulianDate(DateTime utc) {
            // Standard JD from a UTC DateTime.
            int y = utc.Year, m = utc.Month;
            double d = utc.Day
                + utc.Hour / 24.0
                + utc.Minute / 1440.0
                + utc.Second / 86400.0;
            if (m <= 2) { y -= 1; m += 12; }
            int A = y / 100;
            int B = 2 - A + A / 4;
            return Math.Floor(365.25 * (y + 4716))
                 + Math.Floor(30.6001 * (m + 1))
                 + d + B - 1524.5;
        }

        /// <summary>Altitude (degrees) of an object at a given UTC time from lat/lon (degrees).</summary>
        public static double Altitude(double raHours, double decDeg, double latDeg, double lonDeg, DateTime utc) {
            double jd = JulianDate(utc);
            double T = (jd - 2451545.0) / 36525.0;
            double gmst = 280.46061837 + 360.98564736629 * (jd - 2451545.0)
                        + 0.000387933 * T * T - T * T * T / 38710000.0;
            gmst = ((gmst % 360) + 360) % 360;
            double lst = (gmst + lonDeg + 360) % 360;
            double ha = lst - (raHours * 15.0);
            ha = ((ha + 180) % 360) - 180;

            double haR = ha * Math.PI / 180.0;
            double decR = decDeg * Math.PI / 180.0;
            double latR = latDeg * Math.PI / 180.0;

            double sinAlt = Math.Sin(decR) * Math.Sin(latR)
                          + Math.Cos(decR) * Math.Cos(latR) * Math.Cos(haR);
            sinAlt = Math.Max(-1, Math.Min(1, sinAlt));
            return Math.Asin(sinAlt) * 180.0 / Math.PI;
        }

        /// <summary>
        /// Highest altitude the object ever reaches from this latitude (its transit altitude).
        /// This is time-independent: 90 - |lat - dec|, clamped to [-90, 90].
        /// </summary>
        public static double MaxAltitude(double decDeg, double latDeg) {
            double alt = 90.0 - Math.Abs(latDeg - decDeg);
            return Math.Max(-90, Math.Min(90, alt));
        }

        // ---------------- Sun position (for the real "tonight" dark window) ----------------

        /// <summary>Low-precision geocentric Sun position (RA hours, Dec degrees) — Astronomical Almanac.</summary>
        public static (double raHours, double decDeg) SunEquatorial(DateTime utc) {
            double jd = JulianDate(utc);
            double n = jd - 2451545.0;
            double L = ((280.460 + 0.9856474 * n) % 360 + 360) % 360;   // mean longitude
            double g = (357.528 + 0.9856003 * n) * Math.PI / 180.0;      // mean anomaly (rad)
            double lambda = (L + 1.915 * Math.Sin(g) + 0.020 * Math.Sin(2 * g)) * Math.PI / 180.0;
            double eps = (23.439 - 0.0000004 * n) * Math.PI / 180.0;     // obliquity
            double ra = Math.Atan2(Math.Cos(eps) * Math.Sin(lambda), Math.Cos(lambda)) * 180.0 / Math.PI;
            double dec = Math.Asin(Math.Sin(eps) * Math.Sin(lambda)) * 180.0 / Math.PI;
            double raHours = ((ra / 15.0) % 24 + 24) % 24;
            return (raHours, dec);
        }

        /// <summary>Altitude of the Sun (degrees) at a given time — negative means below the horizon.</summary>
        public static double SunAltitude(double latDeg, double lonDeg, DateTime utc) {
            var (ra, dec) = SunEquatorial(utc);
            return Altitude(ra, dec, latDeg, lonDeg, utc);
        }

        /// <summary>
        /// Sample times (UTC) across the coming night's dark hours from this location.
        /// Tries astronomical dark (Sun &lt; −12°) first, then relaxes to −6° and 0° so that
        /// short summer nights still produce a usable window. Empty only under a true polar day.
        /// </summary>
        public static IReadOnlyList<DateTime> NightSampleTimes(double latDeg, double lonDeg, DateTime nowUtc) {
            foreach (double sunBelow in new[] { -12.0, -6.0, 0.0 }) {
                var run = FirstDarkRun(latDeg, lonDeg, nowUtc, sunBelow);
                if (run.Count >= 6) return run;   // enough of a window to judge "well placed"
            }
            return Array.Empty<DateTime>();
        }

        /// <summary>First contiguous run of darkness (Sun below the threshold) in the next 24 h.</summary>
        private static List<DateTime> FirstDarkRun(double latDeg, double lonDeg, DateTime nowUtc, double sunBelow) {
            var run = new List<DateTime>();
            bool started = false;
            for (int i = 0; i <= 96; i++) {                 // 24 h in 15-minute steps
                var t = nowUtc.AddMinutes(i * 15);
                bool dark = SunAltitude(latDeg, lonDeg, t) < sunBelow;
                if (dark) { run.Add(t); started = true; }
                else if (started) break;                    // the window that is on now / next has closed
            }
            return run;
        }

        /// <summary>
        /// "Well placed tonight": at or above <paramref name="minAltDeg"/> for at least
        /// <paramref name="minFraction"/> of the real dark hours. Pass the precomputed night
        /// samples (they depend only on location/time, not the object) to keep this cheap.
        /// If there is no darkness at all (polar summer), falls back to the transit-altitude test.
        /// </summary>
        public static bool IsWellPlaced(double raHours, double decDeg, double latDeg, double lonDeg,
                                        IReadOnlyList<DateTime> nightSamples,
                                        double minAltDeg = 25, double minFraction = 0.6) {
            if (nightSamples == null || nightSamples.Count == 0)
                return MaxAltitude(decDeg, latDeg) >= minAltDeg;
            int above = 0;
            foreach (var t in nightSamples)
                if (Altitude(raHours, decDeg, latDeg, lonDeg, t) >= minAltDeg) above++;
            return (double)above / nightSamples.Count >= minFraction;
        }
    }
}
