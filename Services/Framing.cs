using System;

namespace UnderAfricanSkies.ObjectChooser.Services {

    /// <summary>
    /// Recommends the "perfect" telescope focal length for an object: the one from a fixed
    /// ladder of focal lengths that frames the object so it fills roughly 60–80% of the
    /// (short side of the) camera frame. Coverage depends on focal length AND sensor size,
    /// so the sensor's short dimension in millimetres is supplied by the caller (read from
    /// NINA's connected camera, or a sensible default).
    /// </summary>
    public static class Framing {

        // Focal-length ladder the user cares about (millimetres).
        public static readonly int[] Focals =
            { 100, 200, 300, 400, 500, 600, 700, 800, 900, 1000, 1500, 2000, 2500, 3000, 4000 };

        private const double ArcminPerRadian = 3437.74677;
        private const double TargetLow = 0.60;
        private const double TargetHigh = 0.80;
        private const double TargetIdeal = 0.70;

        /// <summary>Field of view of the short sensor side, in arc-minutes, at a focal length.</summary>
        private static double FovShortArcmin(double sensorShortMm, int focalMm)
            => ArcminPerRadian * sensorShortMm / focalMm;

        /// <summary>Fraction of the short frame side the object spans at a focal length.</summary>
        private static double Coverage(double objSizeArcmin, double sensorShortMm, int focalMm)
            => objSizeArcmin / FovShortArcmin(sensorShortMm, focalMm);

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

            // Pick the focal length whose coverage sits in 60–80% and is closest to 70%.
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
