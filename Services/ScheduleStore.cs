using System;
using System.IO;
using System.Text.Json;
using UnderAfricanSkies.ObjectChooser.Models;

namespace UnderAfricanSkies.ObjectChooser.Services {

    /// <summary>
    /// Loads and saves the imaging schedule to a JSON file in the plugin's local data folder,
    /// so the plan (and the automatic-mode arm state) survive NINA restarts.
    /// </summary>
    public static class ScheduleStore {

        private static string PathFor() {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA", "Plugins", "UASObjectChooser");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "schedule.json");
        }

        public static ScheduleFile Load() {
            try {
                var p = PathFor();
                if (!File.Exists(p)) return new ScheduleFile();
                var json = File.ReadAllText(p);
                var doc = JsonSerializer.Deserialize<ScheduleFile>(json);
                return doc ?? new ScheduleFile();
            } catch {
                return new ScheduleFile();
            }
        }

        public static void Save(ScheduleFile doc) {
            try {
                var p = PathFor();
                var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(p, json);
            } catch { /* best effort; never let a save crash the UI */ }
        }
    }
}
