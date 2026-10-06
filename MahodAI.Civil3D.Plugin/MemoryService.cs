using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MahodAI.Civil3D.Plugin
{
    /// <summary>
    /// שירות זיכרון ולמידה - שומר Q&A מוצלחים והעדפות משתמש
    /// </summary>
    public sealed class MemoryService
    {
        private readonly string _path;
        private readonly ReaderWriterLockSlim _rwLock = new();
        private List<Entry> _entries = new List<Entry>();
        private Dictionary<string, string> _prefs = new Dictionary<string, string>();

        public MemoryService()
        {
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var folder = Path.Combine(appDataPath, Constants.AppConstants.AppDataFolder);
            Directory.CreateDirectory(folder);
            _path = Path.Combine(folder, Constants.AppConstants.MemoryFileName);
        }

        /// <summary>
        /// טוען את הזיכרון מהדיסק
        /// </summary>
        public async Task LoadAsync()
        {
            try
            {
                if (!File.Exists(_path))
                {
                    await SaveAsync();
                    return;
                }

                var json = await File.ReadAllTextAsync(_path);
                var data = JsonSerializer.Deserialize<Store>(json) ?? new Store();
                _rwLock.EnterWriteLock();
                try
                {
                    _entries = data.Entries ?? new List<Entry>();
                    _prefs = data.Prefs ?? new Dictionary<string, string>();
                }
                finally
                {
                    _rwLock.ExitWriteLock();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Memory] שגיאה בטעינה: {ex.Message}");
                _rwLock.EnterWriteLock();
                try
                {
                    _entries = new List<Entry>();
                    _prefs = new Dictionary<string, string>();
                }
                finally
                {
                    _rwLock.ExitWriteLock();
                }
            }
        }

        /// <summary>
        /// שומר את הזיכרון לדיסק
        /// </summary>
        public async Task SaveAsync()
        {
            try
            {
                var data = new Store
                {
                    Entries = _entries,
                    Prefs = _prefs
                };
                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(_path, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Memory] שגיאה בשמירה: {ex.Message}");
            }
        }

        /// <summary>
        /// זוכר זוג שאלה-תשובה
        /// </summary>
        public void RememberQA(string question, string html)
        {
            if (string.IsNullOrWhiteSpace(question) || string.IsNullOrWhiteSpace(html))
                return;

            _rwLock.EnterWriteLock();
            try
            {
                _entries.Add(new Entry
                {
                    Q = question.Trim(),
                    Html = html,
                    At = DateTime.UtcNow
                });

                // מגביל ל-200 ערכים אחרונים כדי שלא יתנפח הקובץ
                if (_entries.Count > 200)
                {
                    _entries = _entries.OrderByDescending(e => e.At).Take(200).ToList();
                }
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// מחפש תשובה דומה בזיכרון (דמיון Jaccard)
        /// </summary>
        public string? FindSimilar(string question, double minScore = 0.8)
        {
            if (string.IsNullOrWhiteSpace(question))
                return null;

            _rwLock.EnterReadLock();
            try
            {
                if (_entries.Count == 0)
                    return null;

                var scores = _entries
                    .Select(e => new { Score = Jaccard(question, e.Q), Entry = e })
                    .OrderByDescending(x => x.Score)
                    .ToList();

                var best = scores.FirstOrDefault();
                if (best != null && best.Score >= minScore)
                {
                    System.Diagnostics.Debug.WriteLine($"[Memory] נמצאה התאמה: {best.Score:F2} - {best.Entry.Q}");
                    return best.Entry.Html;
                }

                return null;
            }
            finally
            {
                _rwLock.ExitReadLock();
            }
        }

        /// <summary>
        /// שומר העדפת משתמש
        /// </summary>
        public void SetPref(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(key))
                return;
            _rwLock.EnterWriteLock();
            try
            {
                _prefs[key.Trim()] = value?.Trim() ?? string.Empty;
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// מחזיר העדפת משתמש
        /// </summary>
        public string? GetPref(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return null;
            _rwLock.EnterReadLock();
            try
            {
                return _prefs.TryGetValue(key.Trim(), out var value) ? value : null;
            }
            finally
            {
                _rwLock.ExitReadLock();
            }
        }

        /// <summary>
        /// מנקה את כל הזיכרון
        /// </summary>
        public void ClearAll()
        {
            _rwLock.EnterWriteLock();
            try
            {
                _entries.Clear();
                _prefs.Clear();
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// מחזיר את כל הערכים (לצורך ניפוי באגים)
        /// </summary>
        public IEnumerable<Entry> All()
        {
            _rwLock.EnterReadLock();
            try
            {
                return _entries.ToList();
            }
            finally
            {
                _rwLock.ExitReadLock();
            }
        }

        /// <summary>
        /// מחזיר את כל ההעדפות (לצורך ניפוי באגים)
        /// </summary>
        public Dictionary<string, string> AllPrefs()
        {
            _rwLock.EnterReadLock();
            try
            {
                return new Dictionary<string, string>(_prefs);
            }
            finally
            {
                _rwLock.ExitReadLock();
            }
        }

        /// <summary>
        /// חישוב דמיון Jaccard בין שתי מחרוזות
        /// </summary>
        internal static double Jaccard(string a, string b)
        {
            var separators = new[] { ' ', '\t', '\n', '\r', '.', ',', ';', '?', '!', '-', '/', ':', '(', ')' };
            
            var setA = a.Split(separators, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.ToLowerInvariant())
                .ToHashSet();

            var setB = b.Split(separators, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.ToLowerInvariant())
                .ToHashSet();

            var intersection = setA.Intersect(setB).Count();
            var union = setA.Union(setB).Count();

            return union == 0 ? 0 : (double)intersection / union;
        }

        /// <summary>
        /// ערך בזיכרון - שאלה + תשובה HTML + תאריך
        /// </summary>
        public sealed class Entry
        {
            public string Q { get; set; } = string.Empty;
            public string Html { get; set; } = string.Empty;
            public DateTime At { get; set; }
        }

        /// <summary>
        /// מבנה לשמירה/טעינה מ-JSON
        /// </summary>
        public sealed class Store
        {
            public List<Entry> Entries { get; set; } = new();
            public Dictionary<string, string> Prefs { get; set; } = new();
        }
    }
}
