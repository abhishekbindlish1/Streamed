using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StreamedTv.Models;

namespace StreamedTv.Services
{
    /// <summary>
    /// Polls the backend's <c>/v1/live/cricket</c> endpoint and exposes the latest snapshot
    /// to UI components synchronously. Owns a <see cref="System.Threading.Timer"/> that ticks
    /// every <see cref="DefaultPollInterval"/>; views consult <see cref="TryGetForMatch"/> to
    /// enrich a tile's pill text with the current live score.
    ///
    /// Designed to fail open: if the backend is unreachable or RAPIDAPI_KEY isn't wired
    /// (yields 503), <see cref="TryGetForMatch"/> just returns false and the UI shows the
    /// non-live pill.
    /// </summary>
    public sealed class LiveScoreService : IDisposable
    {
        public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(60);

        private readonly SportsDataService _sports;
        private readonly TimeSpan _interval;
        private Timer _timer;
        private CancellationTokenSource _cts;
        private readonly object _lock = new object();
        private List<LiveCricketMatch> _snapshot = new List<LiveCricketMatch>();
        private DateTimeOffset _lastSuccess = DateTimeOffset.MinValue;
        private string _lastError;

        public event EventHandler SnapshotUpdated;

        public LiveScoreService(SportsDataService sports, TimeSpan? interval = null)
        {
            _sports = sports ?? throw new ArgumentNullException(nameof(sports));
            _interval = interval ?? DefaultPollInterval;
        }

        public DateTimeOffset LastSuccess { get { lock (_lock) return _lastSuccess; } }
        public string LastError { get { lock (_lock) return _lastError; } }
        public int MatchCount { get { lock (_lock) return _snapshot.Count; } }

        /// <summary>Snapshot of all currently-tracked matches. Returns a defensive copy.</summary>
        public IReadOnlyList<LiveCricketMatch> Snapshot
        {
            get
            {
                lock (_lock)
                {
                    return _snapshot.ToList();
                }
            }
        }

        /// <summary>
        /// Look up the live snapshot for a streamed.pk match. Match by team-name fuzzy
        /// comparison since RapidAPI uses Cricbuzz IDs which don't align with streamed.pk.
        /// Returns true + the snapshot if both teams from <paramref name="match"/> appear
        /// (in either order) in any live entry's Team1/Team2 names.
        ///
        /// Robustness layers (each catches a class of mismatch the previous can't):
        ///   1. Substring fuzzy match on normalized names — handles capitalisation, punctuation,
        ///      whitespace, and "Mumbai" vs "Mumbai Indians" partial-name truncation.
        ///   2. Acronym expansion via <see cref="CricketAcronyms"/> — handles "CSK" vs "Chennai
        ///      Super Kings" where there's no substring overlap. Tries both raw and expanded.
        ///   3. Date sanity gate — only accepts a candidate when its start time is within
        ///      ±6 hours of the streamed.pk match's date. Filters false positives like
        ///      "India" matching both India vs Aus today AND India A vs SA-A tomorrow.
        /// </summary>
        public bool TryGetForMatch(CatalogMatch match, out LiveCricketMatch live)
        {
            live = null;
            if (match == null) return false;
            HashSet<string> homeAliases = ExpandAliases(match.Teams?.Home?.Name);
            HashSet<string> awayAliases = ExpandAliases(match.Teams?.Away?.Name);
            if (homeAliases.Count == 0 && awayAliases.Count == 0) return false;

            List<LiveCricketMatch> snapshot;
            lock (_lock)
            {
                snapshot = _snapshot;
            }
            foreach (LiveCricketMatch lm in snapshot)
            {
                HashSet<string> n1 = ExpandAliases(lm.Team1?.Name, lm.Team1?.ShortName);
                HashSet<string> n2 = ExpandAliases(lm.Team2?.Name, lm.Team2?.ShortName);
                bool aMatch = AnyOverlap(n1, homeAliases) && AnyOverlap(n2, awayAliases);
                bool bMatch = AnyOverlap(n1, awayAliases) && AnyOverlap(n2, homeAliases);
                if (!aMatch && !bMatch) continue;

                if (!IsDateClose(match, lm)) continue;

                live = lm;
                return true;
            }
            return false;
        }

        public void Start()
        {
            if (_timer != null) return;
            _cts = new CancellationTokenSource();
            // Fire immediately + every interval. The TimerCallback runs on a thread-pool thread;
            // we marshal nothing — UI consumers read the snapshot lazily on render.
            _timer = new Timer(OnTick, null, TimeSpan.Zero, _interval);
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            _timer?.Dispose();
            _timer = null;
            _cts?.Dispose();
            _cts = null;
        }

        /// <summary>Suspend polling without disposing the snapshot. Used while the WebView
        /// player is on-screen — keeping a 60s background HTTP request firing during playback
        /// can starve the WebView's HTTP stack on Tizen's shared network plane and contributes
        /// to the player's "audio loaded, video plane unstable" symptom. Snapshot stays
        /// readable so any tile rendered AFTER pause still shows the last-known score.
        /// Re-enable via <see cref="Resume"/> when the player closes.</summary>
        public void Pause()
        {
            try { _timer?.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
        }

        public void Resume()
        {
            try { _timer?.Change(TimeSpan.Zero, _interval); } catch { }
        }

        public void Dispose() { Stop(); }

        private async void OnTick(object _)
        {
            CancellationTokenSource cts = _cts;
            if (cts == null || cts.IsCancellationRequested) return;
            try
            {
                LiveCricketResponse resp = await _sports.GetLiveCricketAsync(cts.Token);
                if (resp == null)
                {
                    lock (_lock) { _lastError = _sports.LastError ?? "no response"; }
                    return;
                }
                lock (_lock)
                {
                    _snapshot = resp.Matches ?? new List<LiveCricketMatch>();
                    _lastSuccess = DateTimeOffset.UtcNow;
                    _lastError = null;
                }
                SnapshotUpdated?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                lock (_lock) { _lastError = ex.GetType().Name + ": " + ex.Message; }
            }
        }

        // --- Name matching helpers -----------------------------------------

        // Lowercase + strip punctuation/whitespace so "Mumbai Indians" matches
        // "MUMBAI INDIANS" matches "mumbai-indians".
        private static string NormalizeName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            System.Text.StringBuilder sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s.Trim().ToLowerInvariant())
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        // Two normalized names "match" if either is a substring of the other.
        // Cheap and forgiving — handles "Mumbai" vs "Mumbai Indians" and any
        // capitalisation drift.
        private static bool NamesMatch(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            if (a == b) return true;
            return a.Contains(b) || b.Contains(a);
        }

        // Cricket-team acronym expansion table. Keys are normalized acronyms
        // (lowercase, no punct); values are the canonical team-name expansions.
        // Both directions matter at runtime: the acronym IS one of the aliases,
        // and so are all expansions.
        //
        // Sources: alias table in backend/internal/data/aliases.go (kept in
        // approximate sync), plus Pakistan Super League franchises.
        private static readonly Dictionary<string, string[]> CricketAcronyms = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            // IPL
            { "csk",  new[] { "chennai super kings", "chennai" } },
            { "mi",   new[] { "mumbai indians", "mumbai" } },
            { "rcb",  new[] { "royal challengers bangalore", "royal challengers bengaluru", "bangalore", "bengaluru" } },
            { "kkr",  new[] { "kolkata knight riders", "kolkata" } },
            { "dc",   new[] { "delhi capitals", "delhi" } },
            { "srh",  new[] { "sunrisers hyderabad", "hyderabad" } },
            { "pbks", new[] { "punjab kings", "punjab" } },
            { "rr",   new[] { "rajasthan royals", "rajasthan" } },
            { "gt",   new[] { "gujarat titans", "gujarat" } },
            { "lsg",  new[] { "lucknow super giants", "lucknow" } },
            // International
            { "ind",  new[] { "india", "team india" } },
            { "aus",  new[] { "australia" } },
            { "eng",  new[] { "england" } },
            { "pak",  new[] { "pakistan" } },
            { "sa",   new[] { "south africa", "proteas" } },
            { "nz",   new[] { "new zealand", "blackcaps" } },
            { "sl",   new[] { "sri lanka" } },
            { "wi",   new[] { "west indies" } },
            { "ban",  new[] { "bangladesh" } },
            { "afg",  new[] { "afghanistan" } },
            { "ire",  new[] { "ireland" } },
            { "zim",  new[] { "zimbabwe" } },
            // PSL
            { "isl",  new[] { "islamabad united" } },
            { "kar",  new[] { "karachi kings" } },
            { "lhr",  new[] { "lahore qalandars" } },
            { "mul",  new[] { "multan sultans" } },
            { "pes",  new[] { "peshawar zalmi" } },
            { "que",  new[] { "quetta gladiators" } },
        };

        // Reverse map for the long-form → acronym direction; lazy-built once.
        private static readonly Lazy<Dictionary<string, string>> ExpansionToAcronym =
            new Lazy<Dictionary<string, string>>(BuildExpansionToAcronym);

        private static Dictionary<string, string> BuildExpansionToAcronym()
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string[]> kv in CricketAcronyms)
            {
                foreach (string expansion in kv.Value)
                {
                    string norm = NormalizeName(expansion);
                    if (!string.IsNullOrEmpty(norm)) map[norm] = NormalizeName(kv.Key);
                }
            }
            return map;
        }

        // Build the set of normalized aliases for one team identity. Inputs may
        // be the long name, an acronym/short code, or both — all are normalized
        // and expanded against the acronym table so substring-match has the
        // widest possible surface.
        private static HashSet<string> ExpandAliases(params string[] rawNames)
        {
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (rawNames == null) return set;
            foreach (string raw in rawNames)
            {
                string norm = NormalizeName(raw);
                if (string.IsNullOrEmpty(norm)) continue;
                set.Add(norm);
                // If this IS an acronym → add every expansion as an alias.
                string[] expansions;
                if (CricketAcronyms.TryGetValue(norm, out expansions))
                {
                    foreach (string e in expansions)
                    {
                        string en = NormalizeName(e);
                        if (!string.IsNullOrEmpty(en)) set.Add(en);
                    }
                }
                // If this is an expansion → add the acronym as an alias.
                string acro;
                if (ExpansionToAcronym.Value.TryGetValue(norm, out acro))
                {
                    set.Add(acro);
                    // Also add the OTHER expansions for the same acronym
                    // (e.g. "Mumbai" → acronym "mi" → also adds "mumbai indians")
                    if (CricketAcronyms.TryGetValue(acro, out expansions))
                    {
                        foreach (string e in expansions)
                        {
                            string en = NormalizeName(e);
                            if (!string.IsNullOrEmpty(en)) set.Add(en);
                        }
                    }
                }
            }
            return set;
        }

        // True if any alias from <paramref name="haystack"/> substring-matches any from
        // <paramref name="needle"/>. Order-independent.
        private static bool AnyOverlap(HashSet<string> haystack, HashSet<string> needle)
        {
            if (haystack.Count == 0 || needle.Count == 0) return false;
            foreach (string h in haystack)
            {
                foreach (string n in needle)
                {
                    if (NamesMatch(h, n)) return true;
                }
            }
            return false;
        }

        // Sanity-check that the streamed.pk match and the live candidate are
        // about the same fixture — same teams playing on different days
        // shouldn't false-match. ±6 h window covers timezone drift +
        // toss/start delays without admitting "same teams next week".
        //
        // If either side is missing a parsable timestamp we DO admit the
        // match — the team-name overlap is already strong evidence and
        // demanding both timestamps would over-filter.
        private const double DateMatchToleranceHours = 6.0;
        private static bool IsDateClose(CatalogMatch match, LiveCricketMatch live)
        {
            DateTimeOffset matchTime;
            try
            {
                if (match.Date <= 0) return true;
                matchTime = DateTimeOffset.FromUnixTimeMilliseconds(match.Date);
            }
            catch
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(live?.StartTime)) return true;
            DateTimeOffset liveTime;
            if (!DateTimeOffset.TryParse(live.StartTime, out liveTime)) return true;

            double deltaHours = Math.Abs((matchTime - liveTime).TotalHours);
            return deltaHours <= DateMatchToleranceHours;
        }
    }
}
