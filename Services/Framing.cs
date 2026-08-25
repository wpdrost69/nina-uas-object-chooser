using System;

namespace UnderAfricanSkies.ObjectChooser.Services {

    /// <summary>
    /// Recommends the "perfect" telescope focal length for an object: the one from a fixed
    /// ladder of focal lengths that frames the object so it fills roughly HALF (≈40–60%) of the
    /// (short side of the) camera frame — a comfortable composition with margin around the object.
    /// Coverage depends on focal length AND sensor size, so the sensor's short dimension in
    /// millimetres is supplied by the caller (read from NINA's connected camera, or a default).
    /// </summary>
    public static class Framing {

        // Focal-length ladder the user cares about (millimetres).
        public static readonly int[] Focals =
            { 100, 200, 300, 400, 500, 600, 700, 800, 900, 1000, 1500, 2000, 2500, 3000, 4000 };

        private const double ArcminPerRadian = 3437.74677;
        // Aim for the object to fill about half the short frame side, so it sits comfortably in
        // frame with room around it (rather than nearly touching the edges at ~70%).
        private const double TargetLow = 0.40;
        private const double TargetHigh = 0.60;
        private const double TargetIdeal = 0.50;

        /// <summary>Field of view of the short sensor side, in arc-minutes, at a focal length.</summary>
        private static double FovShortArcmin(double sensorShortMm, int focalMm)
            => ArcminPerRadian * sensorShortMm / focalMm;

        /// <summary>Fraction of the short frame side the object spans at a focal length.</summary>
        private static double Coverage(double objSizeArcmin, double sensorShortMm, int focalMm)
            => objSizeArcmin / FovShortArcmin(sensorShortMm, focalMm);

        /// <summary>
        /// True if the object is nicely framed at a GIVEN focal length on the given sensor — i.e. it
        /// spans between <paramref name="lo"/> and <paramref name="hi"/> of the short frame side (default
        /// 25–85%: not a tiny speck, not overflowing). Used by the "show what fits my telescope" filter.
        /// </summary>
        public static bool FramesWellAt(double? objSizeArcmin, double sensorShortMm, int focalMm,
                                        double lo = 0.30, double hi = 0.80) {
            if (!objSizeArcmin.HasValue || objSizeArcmin.Value <= 0 || sensorShortMm <= 0 || focalMm <= 0)
                return false;
            double c = Coverage(objSizeArcmin.Value, sensorShortMm, focalMm);
            return c >= lo && c <= hi;
        }

        /// <summary>
        /// Badge text for how a specific focal length frames the object on this sensor, e.g. "700 mm · 34%".
        /// Used when the user filters by a chosen telescope so the label reflects THAT scope, not the ideal.
        /// Returns "" when size is unknown.
        /// </summary>
        public static string CoverageText(double? objSizeArcmin, double sensorShortMm, int focalMm) {
            if (!objSizeArcmin.HasValue || objSizeArcmin.Value <= 0 || sensorShortMm <= 0 || focalMm <= 0)
                return "";
            int pct = (int)Math.Round(Coverage(objSizeArcmin.Value, sensorShortMm, focalMm) * 100);
            return $"{focalMm} mm · {pct}%";
        }

        /// <summary>
        /// Badge text like "700 mm · 72%", or a helpful note when the object is too big for the
        /// widest focal length or too small for the longest. Returns "" when size is unknown.
        /// </summary>
        public static string Recommend(double? objSizeArcmin, double sensorShortMm) {
            if (!objSizeArcmin.HasValue || objSizeArcmin.Value <= 0 || sensorShortMm <= 0) return "";
            double size = objSizeArcmin.Value;

            int first = Focals[0], last = Focals[Focals.Length - 1];

            // Too big even at the widest focal length -> would overfill the frame.
            if (Coverage(size, sensorShortMm, first) > TargetHigh)
                return $"< {first} mm · use a lens";

            // Too small even at the longest -> planetary / needs a Barlow.
            if (Coverage(size, sensorShortMm, last) < TargetLow)
                return $"> {last} mm";

            // Pick the focal length whose coverage sits in 40–60% and is closest to 50%.
            int best = -1; double bestErr = double.MaxValue; int bestPct = 0;
            foreach (var f in Focals) {
                double c = Coverage(size, sensorShortMm, f);
                if (c < TargetLow || c > TargetHigh) continue;
                double err = Math.Abs(c - TargetIdeal);
                if (err < bestErr) { bestErr = err; best = f; bestPct = (int)Math.Round(c * 100); }
            }

            if (best < 0) {
                // Nothing lands strictly inside the band (gap in the ladder): take the closest overall.
                foreach (var f in Focals) {
                    double c = Coverage(size, sensorShortMm, f);
                    double err = Math.Abs(c - TargetIdeal);
                    if (err < bestErr) { bestErr = err; best = f; bestPct = (int)Math.Round(c * 100); }
                }
            }
            return $"{best} mm · {bestPct}%";
        }
    }
}
