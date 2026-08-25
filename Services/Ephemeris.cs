using System;
using System.Collections.Generic;
using NINA.Astrometry;
using NINA.Sequencer.Utility;
using UnderAfricanSkies.ObjectChooser.Models;

namespace UnderAfricanSkies.ObjectChooser.Services {

    /// <summary>
    /// Sun, Moon and planets computed by N.I.N.A.'s own NOVAS-backed astrometry (NINA.Astrometry),
    /// NOT a local approximation:
    ///   • Sun &amp; Moon → <c>ItemUtility.CalculateSunRADec</c> / <c>CalculateMoonRADec</c>, which are
    ///     topocentric (they take the observer's lat/lon/elevation) and return slew-ready JNOW coordinates,
    ///     so lunar parallax is handled correctly.
    ///   • Planets → <c>NOVAS.PlanetApparentCoordinates</c> (apparent, equinox of date).
    /// Positions are refreshed on every panel tick (see ObjectChooserDockable.ComputeVisibility), so the
    /// fast-moving Moon never goes stale while the panel is open. All results are stored as <c>Epoch.JNOW</c>.
    ///
    /// Angular sizes are nominal mean values (arc-minutes) used only for the framing hint; the framing
    /// recommendation for planets is dominated by focal length, not the exact disc diameter.
    /// </summary>
    public static class Ephemeris {

        private class BodyDef {
            public string Name; public string Type; public double SizeArcmin; public NOVAS.Body Novas; public bool IsSunMoon;
        }

        // Seven classical planets plus the Sun and Moon.
        private static readonly BodyDef[] Bodies = new[] {
            new BodyDef{ Name="Sun",     Type="Sun",    SizeArcmin=31.99, Novas=NOVAS.Body.Sun,     IsSunMoon=true },
            new BodyDef{ Name="Moon",    Type="Moon",   SizeArcmin=31.10, Novas=NOVAS.Body.Moon,    IsSunMoon=true },
            new BodyDef{ Name="Mercury", Type="Planet", SizeArcmin=0.11,  Novas=NOVAS.Body.Mercury },
            new BodyDef{ Name="Venus",   Type="Planet", SizeArcmin=0.42,  Novas=NOVAS.Body.Venus },
            new BodyDef{ Name="Mars",    Type="Planet", SizeArcmin=0.15,  Novas=NOVAS.Body.Mars },
            new BodyDef{ Name="Jupiter", Type="Planet", SizeArcmin=0.66,  Novas=NOVAS.Body.Jupiter },
            new BodyDef{ Name="Saturn",  Type="Planet", SizeArcmin=0.31,  Novas=NOVAS.Body.Saturn },
            new BodyDef{ Name="Uranus",  Type="Planet", SizeArcmin=0.06,  Novas=NOVAS.Body.Uranus },
            new BodyDef{ Name="Neptune", Type="Planet", SizeArcmin=0.04,  Novas=NOVAS.Body.Neptune },
        };

        /// <summary>The fixed set of solar-system objects (positions are filled in by UpdatePositions).</summary>
        public static List<AstroObject> CreateBodies() {
            var list = new List<AstroObject>();
            foreach (var b in Bodies) {
                list.Add(new AstroObject {
                    Name = b.Name, Desig = null, Type = b.Type,
                    Size = b.SizeArcmin > 0 ? (double?)b.SizeArcmin : null,
                    Hemisphere = "NH",
                    Wiki = b.Name
                });
            }
            return list;
        }

        private static double JdTt(DateTime utc) {
            double hour = utc.Hour + utc.Minute / 60.0 + utc.Second / 3600.0;
            double jdUt = NOVAS.JulianDate((short)utc.Year, (short)utc.Month, (short)utc.Day, hour);
            return jdUt + 69.0 / 86400.0;   // TT ≈ UT + ΔT (~69 s in 2026); ΔT is immaterial at these angular scales
        }

        /// <summary>
        /// Recompute RA/Dec (JNOW) for every body from N.I.N.A.'s astrometry.
        /// <paramref name="live"/> = computing for ~now → Sun/Moon use the topocentric ItemUtility helpers;
        /// otherwise (previewing another date) all bodies use apparent NOVAS coordinates for that instant.
        /// </summary>
        public static void UpdatePositions(List<AstroObject> bodies, double lat, double lon, double elevation, DateTime utc, bool live) {
            if (bodies == null) return;
            var obs = new ObserverInfo { Latitude = lat, Longitude = lon, Elevation = elevation };
            double jdTt = JdTt(utc);
            for (int i = 0; i < bodies.Count && i < Bodies.Length; i++) {
                var o = bodies[i];
                var b = Bodies[i];
                try {
                    Coordinates c;
                    if (b.IsSunMoon && live) {
                        c = (b.Novas == NOVAS.Body.Sun)
                            ? ItemUtility.CalculateSunRADec(obs)
                            : ItemUtility.CalculateMoonRADec(obs);
                    } else {
                        c = NOVAS.PlanetApparentCoordinates(jdTt, b.Novas, NOVAS.Accuracy.Reduced);
                    }
                    c = c.Transform(Epoch.JNOW);
                    o.Ra = ((c.RA % 24) + 24) % 24;
                    o.Dec = c.Dec;
                    o.Hemisphere = o.Dec < 0 ? "SH" : "NH";
                } catch { /* keep last-known position on any transient failure */ }
            }
        }
    }
}
