using System;
using System.Collections.Generic;
using UnderAfricanSkies.ObjectChooser.Models;

namespace UnderAfricanSkies.ObjectChooser.Services {

    /// <summary>
    /// Low-precision positions of the Sun, Moon and major planets for a given moment,
    /// good to a few arc-minutes — plenty for "what's up tonight", slewing and framing.
    ///
    /// Planets: Standish "Approximate Positions of the Planets" (Keplerian elements +
    /// per-century rates, J2000, valid 1800–2050). Moon: truncated Meeus series.
    /// Sun: reuses Visibility.SunEquatorial. Angular sizes come from each body's real
    /// distance at the time, so the framing recommendation is meaningful.
    /// </summary>
    public static class Ephemeris {

        private const double Deg = Math.PI / 180.0;
        private const double AuKm = 149_597_870.7;

        private static double Norm360(double d) => ((d % 360) + 360) % 360;
        private static double Wrap180(double d) { d = Norm360(d); return d > 180 ? d - 360 : d; }

        private static double JulianDate(DateTime utc) {
            int y = utc.Year, m = utc.Month;
            double d = utc.Day + utc.Hour / 24.0 + utc.Minute / 1440.0 + utc.Second / 86400.0;
            if (m <= 2) { y -= 1; m += 12; }
            int A = y / 100, B = 2 - A + A / 4;
            return Math.Floor(365.25 * (y + 4716)) + Math.Floor(30.6001 * (m + 1)) + d + B - 1524.5;
        }

        // a(au), e, I(deg), L(deg), longPeri(deg), longNode(deg) + their per-century rates.
        private class Elem {
            public string Name; public double DiamKm;
            public double a, ad, e, ed, I, Id, L, Ld, w, wd, o, od;
        }

        // J2000 elements & rates (Standish, JPL). Order: a e I L longPeri longNode.
        private static readonly Elem Earth = new Elem {
            Name = "Earth", DiamKm = 12742,
            a = 1.00000261, ad = 0.00000562, e = 0.01671123, ed = -0.00004392,
            I = -0.00001531, Id = -0.01294668, L = 100.46457166, Ld = 35999.37244981,
            w = 102.93768193, wd = 0.32327364, o = 0.0, od = 0.0
        };

        private static readonly Elem[] Planets = new[] {
            new Elem{ Name="Mercury", DiamKm=4879,
                a=0.38709927, ad=0.00000037, e=0.20563593, ed=0.00001906,
                I=7.00497902, Id=-0.00594749, L=252.25032350, Ld=149472.67411175,
                w=77.45779628, wd=0.16047689, o=48.33076593, od=-0.12534081 },
            new Elem{ Name="Venus", DiamKm=12104,
                a=0.72333566, ad=0.00000390, e=0.00677672, ed=-0.00004107,
                I=3.39467605, Id=-0.00078890, L=181.97909950, Ld=58517.81538729,
                w=131.60246718, wd=0.00268329, o=76.67984255, od=-0.27769418 },
            new Elem{ Name="Mars", DiamKm=6779,
                a=1.52371034, ad=0.00001847, e=0.09339410, ed=0.00007882,
                I=1.84969142, Id=-0.00813131, L=-4.55343205, Ld=19140.30268499,
                w=-23.94362959, wd=0.44441088, o=49.55953891, od=-0.29257343 },
            new Elem{ Name="Jupiter", DiamKm=139820,
                a=5.20288700, ad=-0.00011607, e=0.04838624, ed=-0.00013253,
                I=1.30439695, Id=-0.00183714, L=34.39644051, Ld=3034.74612775,
                w=14.72847983, wd=0.21252668, o=100.47390909, od=0.20469106 },
            new Elem{ Name="Saturn", DiamKm=116460,
                a=9.53667594, ad=-0.00125060, e=0.05386179, ed=-0.00050991,
                I=2.48599187, Id=0.00193609, L=49.95424423, Ld=1222.49362201,
                w=92.59887831, wd=-0.41897216, o=113.66242448, od=-0.28867794 },
            new Elem{ Name="Uranus", DiamKm=50724,
                a=19.18916464, ad=-0.00196176, e=0.04725744, ed=-0.00004397,
                I=0.77263783, Id=-0.00242939, L=313.23810451, Ld=428.48202785,
                w=170.95427630, wd=0.40805281, o=74.01692503, od=0.04240589 },
            new Elem{ Name="Neptune", DiamKm=49244,
                a=30.06992276, ad=0.00026291, e=0.00859048, ed=0.00005105,
                I=1.77004347, Id=0.00035372, L=-55.12002969, Ld=218.45945325,
                w=44.96476227, wd=-0.32241464, o=131.78422574, od=-0.00508664 },
        };

        // Heliocentric ecliptic rectangular coordinates (au) at time T (Julian centuries past J2000).
        private static (double x, double y, double z, double r) HelioRect(Elem el, double T) {
            double a = el.a + el.ad * T;
            double e = el.e + el.ed * T;
            double I = (el.I + el.Id * T) * Deg;
            double L = el.L + el.Ld * T;
            double wbar = el.w + el.wd * T;
            double omega = (el.o + el.od * T) * Deg;
            double w = (wbar - (el.o + el.od * T)) * Deg;          // argument of perihelion
            double M = Wrap180(L - wbar) * Deg;                    // mean anomaly (rad)

            // Kepler's equation (radians)
            double E = M + e * Math.Sin(M);
            for (int i = 0; i < 8; i++) {
                double dE = (M - (E - e * Math.Sin(E))) / (1 - e * Math.Cos(E));
                E += dE;
                if (Math.Abs(dE) < 1e-9) break;
            }

            double xp = a * (Math.Cos(E) - e);
            double yp = a * Math.Sqrt(1 - e * e) * Math.Sin(E);

            double cw = Math.Cos(w), sw = Math.Sin(w);
            double co = Math.Cos(omega), so = Math.Sin(omega);
            double ci = Math.Cos(I), si = Math.Sin(I);

            double x = (cw * co - sw * so * ci) * xp + (-sw * co - cw * so * ci) * yp;
            double y = (cw * so + sw * co * ci) * xp + (-sw * so + cw * co * ci) * yp;
            double z = (sw * si) * xp + (cw * si) * yp;
            double r = Math.Sqrt(x * x + y * y + z * z);
            return (x, y, z, r);
        }

        private static AstroObject FromEquatorial(string name, double raHours, double decDeg, double sizeArcmin, string type) {
            return new AstroObject {
                Name = name,
                Desig = null,
                Type = type,
                Ra = ((raHours % 24) + 24) % 24,
                Dec = decDeg,
                Size = sizeArcmin > 0 ? (double?)Math.Round(sizeArcmin, 2) : null,
                Hemisphere = decDeg < 0 ? "SH" : "NH",
                Wiki = name   // Wikipedia article title for the description panel
            };
        }

        /// <summary>Sun, Moon and the seven planets as catalogue-shaped objects for the given time.</summary>
        public static List<AstroObject> SolarSystem(DateTime utc, bool includeSun) {
            var list = new List<AstroObject>();
            double jd = JulianDate(utc);
            double T = (jd - 2451545.0) / 36525.0;
            double eps = (23.43929111 - 0.0130042 * T) * Deg;   // obliquity of the ecliptic

            // ----- Sun -----
            if (includeSun) {
                var (sra, sdec) = Visibility.SunEquatorial(utc);
                // Sun–Earth distance from Earth's orbit radius; angular diameter ~1919.3"/r.
                var (_, _, _, er) = HelioRect(Earth, T);
                double sunArcsec = 1919.26 / Math.Max(er, 0.1);
                list.Add(FromEquatorial("Sun", sra, sdec, sunArcsec / 60.0, "Sun"));
            }

            // ----- Moon (truncated Meeus, geocentric) -----
            {
                double Lp = Norm360(218.316 + 481267.8813 * T);
                double M  = Norm360(134.963 + 477198.8676 * T) * Deg;  // Moon anomaly
                double Ms = Norm360(357.529 + 35999.0503 * T) * Deg;   // Sun anomaly
                double D  = Norm360(297.850 + 445267.1115 * T) * Deg;  // elongation
                double F  = Norm360(93.272  + 483202.0175 * T) * Deg;  // arg. of latitude
                double lon = Lp
                    + 6.289 * Math.Sin(M)
                    + 1.274 * Math.Sin(2 * D - M)
                    + 0.658 * Math.Sin(2 * D)
                    + 0.214 * Math.Sin(2 * M)
                    - 0.186 * Math.Sin(Ms)
                    - 0.114 * Math.Sin(2 * F)
                    + 0.059 * Math.Sin(2 * D - 2 * M)
                    + 0.057 * Math.Sin(2 * D - Ms - M);
                double lat =
                      5.128 * Math.Sin(F)
                    + 0.281 * Math.Sin(M + F)
                    + 0.278 * Math.Sin(F - M)
                    + 0.173 * Math.Sin(2 * D - F);
                double distKm = 385000.56
                    - 20905.0 * Math.Cos(M)
                    - 3699.0  * Math.Cos(2 * D - M)
                    - 2956.0  * Math.Cos(2 * D)
                    - 570.0   * Math.Cos(2 * M);
                double lamR = lon * Deg, betR = lat * Deg;
                double sinDec = Math.Sin(betR) * Math.Cos(eps) + Math.Cos(betR) * Math.Sin(eps) * Math.Sin(lamR);
                double dec = Math.Asin(sinDec) / Deg;
                double ra = Math.Atan2(Math.Sin(lamR) * Math.Cos(eps) - Math.Tan(betR) * Math.Sin(eps), Math.Cos(lamR)) / Deg;
                double moonArcsec = 2.0 * Math.Atan(1737.4 / distKm) / Deg * 3600.0;
                list.Add(FromEquatorial("Moon", ra / 15.0, dec, moonArcsec / 60.0, "Moon"));
            }

            // ----- Planets (geocentric = helio(planet) − helio(Earth)) -----
            var (ex, ey, ez, _) = HelioRect(Earth, T);
            foreach (var pl in Planets) {
                var (px, py, pz, _) = HelioRect(pl, T);
                double gx = px - ex, gy = py - ey, gz = pz - ez;     // geocentric ecliptic rect (au)
                // ecliptic -> equatorial
                double xeq = gx;
                double yeq = gy * Math.Cos(eps) - gz * Math.Sin(eps);
                double zeq = gy * Math.Sin(eps) + gz * Math.Cos(eps);
                double delta = Math.Sqrt(gx * gx + gy * gy + gz * gz);   // Earth–planet distance (au)
                double ra = Math.Atan2(yeq, xeq) / Deg;
                double dec = Math.Atan2(zeq, Math.Sqrt(xeq * xeq + yeq * yeq)) / Deg;
                double arcsec = 206265.0 * pl.DiamKm / (delta * AuKm);
                list.Add(FromEquatorial(pl.Name, ra / 15.0, dec, arcsec / 60.0, "Planet"));
            }

            return list;
        }
    }
}
