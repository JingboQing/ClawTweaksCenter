using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClawTweaksCenter.Core
{
    /// <summary>
    /// Center's own notification list: the place where "there is a driver update", "Windows has
    /// something pending" and "a new widget build is out" collect, instead of each one having to
    /// catch the user on the one screen that happens to show it.
    ///
    /// ── THE KEY IS WHAT MAKES THIS USABLE ───────────────────────────────────────────────────────
    /// Every entry carries a <see cref="Notification.Key"/> and the same key is never added twice.
    /// Without that rule a weekly check writes "Intel Arc 32.0.101.8826" again every week, the
    /// counter climbs, and a counter nobody can ever bring to zero is a counter nobody reads. The
    /// key therefore names the THING and its VERSION - "driver:Intel Arc:32.0.101.8826" - so the
    /// next version does produce a new entry and the same one never does.
    ///
    /// ⚠️ Only FINDINGS land here. "Checked, nothing new" is not a notification (user, 2026-09-13):
    /// a weekly all-clear is the fastest way to make people stop opening the list.
    ///
    /// A JSON file rather than <see cref="CenterSettings"/>: these are records with a timestamp and
    /// a read mark, and that class is explicitly for a handful of single-value preferences.
    ///
    /// Every operation is best-effort. A notification list that cannot be written must never stop
    /// Center from running - the worst case is that a message is shown twice, which is a great deal
    /// better than a crash on the way into the library.
    /// </summary>
    public static class Notifications
    {
        /// <summary>Read entries are dropped after this long. They are a record of what was already
        /// dealt with, and an unbounded one only grows. Assumption, not a decision the user made -
        /// the retention question was left open on 2026-09-13.</summary>
        private static readonly TimeSpan KeepRead = TimeSpan.FromDays(30);

        /// <summary>Hard ceiling, so a bug in a key cannot grow the file without limit. Oldest go
        /// first, and unread survive a trim that would drop them for age alone.</summary>
        private const int MaxEntries = 200;

        private static readonly object Gate = new object();
        private static List<Notification> _items;

        /// <summary>Raised after any change, so an open screen can redraw its counter. Marshalling to
        /// the UI thread is the subscriber's job - a background check raises this from a worker.</summary>
        public static event Action Changed;

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClawTweaks", "Center", "notifications.json");

        // ── Reading ─────────────────────────────────────────────────────────────────────────────

        /// <summary>Newest first.</summary>
        public static List<Notification> All()
        {
            lock (Gate)
            {
                Load();
                return _items.OrderByDescending(n => n.CreatedUtc).ToList();
            }
        }

        public static int UnreadCount()
        {
            lock (Gate)
            {
                Load();
                return _items.Count(n => n.ReadUtc == null);
            }
        }

        // ── Writing ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Adds one, unless its key is already known. Returns true only when something was actually
        /// added - the caller uses that to decide whether anything is worth telling the user about.
        /// </summary>
        public static bool Add(string key, string kind, string title, string detail)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;
            bool added = false;
            lock (Gate)
            {
                Load();
                if (!_items.Any(n => string.Equals(n.Key, key, StringComparison.OrdinalIgnoreCase)))
                {
                    _items.Add(new Notification
                    {
                        Key = key,
                        Kind = kind,
                        Title = title,
                        Detail = detail,
                        CreatedUtc = DateTime.UtcNow,
                    });
                    added = true;
                    Trim();
                    Save();
                }
            }
            if (added) Raise();
            return added;
        }

        public static void MarkAllRead()
        {
            bool changed = false;
            lock (Gate)
            {
                Load();
                foreach (var n in _items.Where(n => n.ReadUtc == null))
                {
                    n.ReadUtc = DateTime.UtcNow;
                    changed = true;
                }
                if (changed) Save();
            }
            if (changed) Raise();
        }

        public static void MarkRead(string key)
        {
            bool changed = false;
            lock (Gate)
            {
                Load();
                var n = _items.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
                if (n != null && n.ReadUtc == null) { n.ReadUtc = DateTime.UtcNow; changed = true; Save(); }
            }
            if (changed) Raise();
        }

        /// <summary>Wipes the list. Only for the maintenance/reset path - nothing on the notification
        /// screen itself deletes, because "mark as read" is the gesture people mean there.</summary>
        public static void Clear()
        {
            lock (Gate)
            {
                Load();
                if (_items.Count == 0) return;
                _items.Clear();
                Save();
            }
            Raise();
        }

        // ── Storage ─────────────────────────────────────────────────────────────────────────────

        private static void Load()
        {
            if (_items != null) return;
            _items = new List<Notification>();
            try
            {
                string path = FilePath;
                if (!File.Exists(path)) return;
                var read = JsonSerializer.Deserialize<List<Notification>>(File.ReadAllText(path));
                if (read != null) _items = read;
                Trim();
            }
            catch (Exception ex)
            {
                // A corrupt file starts empty rather than taking the app down. It is a list of
                // reminders, not something worth failing a launch over.
                InstallLog.Write("Notifications: could not read the store, starting empty - " + ex.Message);
                _items = new List<Notification>();
            }
        }

        private static void Trim()
        {
            var cutoff = DateTime.UtcNow - KeepRead;
            _items.RemoveAll(n => n.ReadUtc != null && n.ReadUtc < cutoff);
            if (_items.Count <= MaxEntries) return;

            // Over the ceiling: drop the oldest READ ones first, and only then the oldest of all.
            // Losing an unread message is losing the thing this list exists for.
            var byAge = _items.OrderBy(n => n.CreatedUtc).ToList();
            foreach (var n in byAge)
            {
                if (_items.Count <= MaxEntries) break;
                if (n.ReadUtc != null) _items.Remove(n);
            }
            while (_items.Count > MaxEntries)
                _items.Remove(_items.OrderBy(n => n.CreatedUtc).First());
        }

        private static void Save()
        {
            try
            {
                string path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonSerializer.Serialize(_items,
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                InstallLog.Write("Notifications: could not write the store - " + ex.Message);
            }
        }

        private static void Raise()
        {
            try { Changed?.Invoke(); }
            catch (Exception ex) { InstallLog.Write("Notifications: a Changed handler threw - " + ex.Message); }
        }
    }

    public sealed class Notification
    {
        /// <summary>Identity, and the whole duplicate guard. Shape: "driver:&lt;name&gt;:&lt;version&gt;",
        /// "windows:&lt;kb or title&gt;", "widget:&lt;version&gt;".</summary>
        public string Key { get; set; } = "";

        /// <summary>"driver" | "windows" | "widget". Drives the glyph, nothing else.</summary>
        public string Kind { get; set; } = "";

        public string Title { get; set; } = "";
        public string Detail { get; set; } = "";
        public DateTime CreatedUtc { get; set; }

        /// <summary>Null while unread. A timestamp rather than a bool so the retention sweep has
        /// something to measure against.</summary>
        public DateTime? ReadUtc { get; set; }

        [JsonIgnore]
        public bool IsUnread => ReadUtc == null;
    }
}
