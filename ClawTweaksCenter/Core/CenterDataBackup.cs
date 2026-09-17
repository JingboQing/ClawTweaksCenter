using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Microsoft.Win32;

namespace ClawTweaksCenter.Core
{
    /// <summary>
    /// Center's OWN half of a backup: the library and Center settings, and the pictures the user
    /// chose. It rides inside the same ZIP the helper writes (Reset · Backup · Restore), under a
    /// prefix the helper never looks at, so one file holds the whole machine.
    ///
    /// ── WHY CENTER DOES THIS PART ITSELF ────────────────────────────────────────────────────────
    /// The helper writes the widget's and its own stores because those live behind package ACLs
    /// and a locked hive that only an elevated process can reach. Everything here is the opposite:
    /// %LOCALAPPDATA%\ClawTweaks\Center and HKCU\Software\ClawTweaks\Center are Center's, writable
    /// unelevated - and the ZIP sits in the user's Documents, so Center can append to it after the
    /// helper has closed it and read it before the helper touches it. No new pipe verb, no change
    /// to the helper's manifest or store list; a helper restore skips entries it does not know.
    ///
    /// ── WHAT IS IN IT ───────────────────────────────────────────────────────────────────────────
    ///   CENTER/data/...        every file under the Center data folder except the logs - favorites,
    ///                          play history, the Misc (own apps) list, the ROM cache, the art cache
    ///                          with the user's cover picks (artcache\overrides.json + the files it
    ///                          names), the downloaded wallpapers, the sound sets
    ///   CENTER/settings.json   every value under HKCU\Software\ClawTweaks\Center, except the ones
    ///                          that belong to the installer and the updater (see SkippedValues)
    ///
    /// NOT the user's own picture folder. It is theirs, it can be anywhere and any size, and the
    /// covers picked from it are copied into artcache when they are picked - that copy is what the
    /// library draws and what this backs up.
    ///
    /// ── OPT-IN, and why the default is off ──────────────────────────────────────────────────────
    /// The user has to tick it (user, 2026-09-16). A backup that silently grew from a few hundred
    /// kilobytes of profiles to fifty megabytes of covers would surprise the people it was not made
    /// for, and the automatic safety copies before a reset or a restore follow the same switch.
    /// </summary>
    internal static class CenterDataBackup
    {
        private const string ZipPrefix = "CENTER/";
        private const string DataPrefix = ZipPrefix + "data/";
        private const string SettingsEntry = ZipPrefix + "settings.json";

        private const string RegistryKeyPath = @"Software\ClawTweaks\Center";

        /// <summary>
        /// Values that describe THIS installation rather than the user's choices. Restoring them from
        /// a backup taken on a classic install would switch self-updates off on a Velopack one, and
        /// OnboardingPending is the installer's hand-off to the next start. Left alone in both
        /// directions: never written to the zip, never wiped by a reset.
        /// </summary>
        private static readonly HashSet<string> SkippedValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "VelopackUpdates", "VelopackSilentUpdates", "VelopackFeedOverride",
            "OnboardingPending", "DevModeRaisedBySetup",
        };

        /// <summary>
        /// A restore or a wipe is NOT done in the running Center. Covers on screen are bitmaps mapped
        /// from the art cache and a running library holds every store in memory, so deleting under
        /// them fails on the locked files and gets overwritten by the next save. The operation is
        /// written down here, Center restarts, and the fresh process performs it in OnStartup before
        /// a single store has been opened - see RunPendingAtStartup.
        ///
        /// Sits NEXT TO the data folder, not in it: the wipe would eat its own instruction.
        /// </summary>
        private static string PendingPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClawTweaks", "center-data-pending.txt");

        internal static void ScheduleRestore(string zipPath) => WritePending("restore\n" + zipPath);
        internal static void ScheduleWipe() => WritePending("wipe\n");

        private static void WritePending(string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PendingPath));
            File.WriteAllText(PendingPath, text);
        }

        /// <summary>Called first thing in App.OnStartup. Returns a one-line log of what it did, or
        /// null when nothing was pending. The instruction file is removed BEFORE the work, so a crash
        /// in the middle cannot replay it on every start.</summary>
        internal static string RunPendingAtStartup()
        {
            try
            {
                if (!File.Exists(PendingPath)) return null;
                var lines = File.ReadAllLines(PendingPath);
                File.Delete(PendingPath);
                if (lines.Length == 0) return null;

                string error;
                switch (lines[0].Trim())
                {
                    case "restore":
                        string zip = lines.Length > 1 ? lines[1].Trim() : "";
                        int n = RestoreFromZip(zip, out error);
                        return n < 0 ? "Center data restore at startup FAILED: " + error
                                     : $"Center data restored at startup: {n} file(s) from '{zip}'.";
                    case "wipe":
                        return Wipe(out error) ? "Center data wiped at startup."
                                               : "Center data wipe at startup FAILED: " + error;
                    default:
                        return "Center data: unknown pending instruction '" + lines[0] + "'.";
                }
            }
            catch (Exception ex)
            {
                return "Center data: pending instruction failed: " + ex.Message;
            }
        }

        internal static string DataDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClawTweaks", "Center");

        private static bool IsLog(string path) =>
            path.EndsWith(".log", StringComparison.OrdinalIgnoreCase);

        // ── Backup ─────────────────────────────────────────────────────────────────────────────

        /// <summary>Appends Center's data to a ZIP the helper has already written. Returns the number
        /// of files added, or -1 with an error message.</summary>
        internal static int AppendToZip(string zipPath, out string error)
        {
            error = null;
            try
            {
                if (string.IsNullOrEmpty(zipPath) || !File.Exists(zipPath)) { error = "Backup file not found."; return -1; }

                int added = 0;
                using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Update))
                {
                    // A second run over the same zip replaces rather than duplicates.
                    foreach (var stale in zip.Entries.Where(e => e.FullName.StartsWith(ZipPrefix, StringComparison.Ordinal)).ToList())
                        stale.Delete();

                    string root = DataDir;
                    if (Directory.Exists(root))
                    {
                        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                        {
                            if (IsLog(file)) continue;
                            string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                            try
                            {
                                zip.CreateEntryFromFile(file, DataPrefix + rel, CompressionLevel.Optimal);
                                added++;
                            }
                            catch (IOException ex)
                            {
                                // A file another part of Center is writing right now. One missing cache
                                // file is not worth failing the whole backup over; a missing store is.
                                InstallLog.Write($"CenterDataBackup: skipped '{rel}': {ex.Message}");
                            }
                        }
                    }

                    var entry = zip.CreateEntry(SettingsEntry, CompressionLevel.Optimal);
                    using (var s = entry.Open())
                    using (var w = new StreamWriter(s))
                        w.Write(ExportSettingsJson());
                    added++;
                }

                InstallLog.Write($"CenterDataBackup: added {added} Center file(s) to '{zipPath}'.");
                return added;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                InstallLog.Write("CenterDataBackup.AppendToZip failed: " + ex.Message);
                return -1;
            }
        }

        /// <summary>True when the ZIP carries a Center half - decides whether a restore has anything
        /// of ours to write back, and whether the safety copy before it must carry ours too.</summary>
        internal static bool HasCenterData(string zipPath)
        {
            try
            {
                using (var zip = ZipFile.OpenRead(zipPath))
                    return zip.GetEntry(SettingsEntry) != null;
            }
            catch { return false; }
        }

        // ── Restore ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Writes Center's half of a backup back: the data folder is cleared (logs excepted) and
        /// refilled, the registry values are replaced. Returns the number of files written, or -1.
        ///
        /// ⚠️ THE CALLER RESTARTS CENTER AFTERWARDS. Favorites, play history, the art index, the
        /// settings cache - every one of them is held in memory by a running Center and would write
        /// itself back over the restored files at the next change. A restart is the only reload that
        /// cannot miss one.
        /// </summary>
        internal static int RestoreFromZip(string zipPath, out string error)
        {
            error = null;
            try
            {
                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    var settings = zip.GetEntry(SettingsEntry);
                    if (settings == null) { error = "This backup has no Center data."; return -1; }

                    string root = DataDir;
                    WipeDataFiles(root);
                    Directory.CreateDirectory(root);

                    int written = 0;
                    foreach (var e in zip.Entries)
                    {
                        if (!e.FullName.StartsWith(DataPrefix, StringComparison.Ordinal)) continue;
                        if (e.FullName.EndsWith("/", StringComparison.Ordinal)) continue;

                        string rel = e.FullName.Substring(DataPrefix.Length);
                        // A zip written by anyone else could carry "..": keep every path inside root.
                        string dest = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
                        if (!dest.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;

                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        e.ExtractToFile(dest, overwrite: true);
                        written++;
                    }

                    string json;
                    using (var s = settings.Open())
                    using (var r = new StreamReader(s))
                        json = r.ReadToEnd();
                    ImportSettingsJson(json);
                    written++;

                    InstallLog.Write($"CenterDataBackup: restored {written} Center file(s) from '{zipPath}'.");
                    return written;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                InstallLog.Write("CenterDataBackup.RestoreFromZip failed: " + ex.Message);
                return -1;
            }
        }

        // ── Reset ──────────────────────────────────────────────────────────────────────────────

        /// <summary>Center's half of a full reset: data folder emptied (logs stay), settings
        /// removed - except the installer's and updater's own values. The caller restarts Center.</summary>
        internal static bool Wipe(out string error)
        {
            error = null;
            try
            {
                WipeDataFiles(DataDir);
                using (var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: true))
                {
                    if (key != null)
                        foreach (var name in key.GetValueNames())
                            if (!SkippedValues.Contains(name)) key.DeleteValue(name, throwOnMissingValue: false);
                }
                InstallLog.Write("CenterDataBackup: Center data and settings wiped.");
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                InstallLog.Write("CenterDataBackup.Wipe failed: " + ex.Message);
                return false;
            }
        }

        private static void WipeDataFiles(string root)
        {
            if (!Directory.Exists(root)) return;
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList())
            {
                if (IsLog(file)) continue;
                try { File.Delete(file); }
                catch (Exception ex) { InstallLog.Write($"CenterDataBackup: could not delete '{file}': {ex.Message}"); }
            }
            foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                                          .OrderByDescending(d => d.Length).ToList())
            {
                try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
                catch { /* a folder with a log in it simply stays */ }
            }
        }

        // ── Registry <-> JSON ──────────────────────────────────────────────────────────────────
        //
        // One object per value, with its kind, so a DWORD comes back as a DWORD: CenterSettings
        // reads booleans and integers as DWORDs and strings as strings, and a string "1" where a
        // DWORD 1 was expected reads as the default.

        private sealed class RegValue
        {
            public string Name { get; set; }
            public string Kind { get; set; }   // "dword" | "string" | "qword"
            public string Value { get; set; }
        }

        private static string ExportSettingsJson()
        {
            var list = new List<RegValue>();
            using (var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath))
            {
                if (key != null)
                    foreach (var name in key.GetValueNames())
                    {
                        if (SkippedValues.Contains(name)) continue;
                        object raw = key.GetValue(name);
                        switch (key.GetValueKind(name))
                        {
                            case RegistryValueKind.DWord:
                                list.Add(new RegValue { Name = name, Kind = "dword", Value = Convert.ToInt32(raw).ToString() });
                                break;
                            case RegistryValueKind.QWord:
                                list.Add(new RegValue { Name = name, Kind = "qword", Value = Convert.ToInt64(raw).ToString() });
                                break;
                            case RegistryValueKind.String:
                            case RegistryValueKind.ExpandString:
                                list.Add(new RegValue { Name = name, Kind = "string", Value = raw as string ?? "" });
                                break;
                            // Binary and multi-string are not something CenterSettings writes; skipped.
                        }
                    }
            }
            return JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
        }

        private static void ImportSettingsJson(string json)
        {
            var list = JsonSerializer.Deserialize<List<RegValue>>(json) ?? new List<RegValue>();
            using (var key = Registry.CurrentUser.CreateSubKey(RegistryKeyPath, writable: true))
            {
                // Replace, not merge: a setting that did not exist when the backup was taken must
                // not survive it, or "restore to Tuesday" leaves Wednesday's choices in place.
                foreach (var name in key.GetValueNames())
                    if (!SkippedValues.Contains(name)) key.DeleteValue(name, throwOnMissingValue: false);

                foreach (var v in list)
                {
                    if (string.IsNullOrEmpty(v.Name) || SkippedValues.Contains(v.Name)) continue;
                    switch (v.Kind)
                    {
                        case "dword": if (int.TryParse(v.Value, out int i)) key.SetValue(v.Name, i, RegistryValueKind.DWord); break;
                        case "qword": if (long.TryParse(v.Value, out long l)) key.SetValue(v.Name, l, RegistryValueKind.QWord); break;
                        case "string": key.SetValue(v.Name, v.Value ?? "", RegistryValueKind.String); break;
                    }
                }
            }
        }
    }
}
