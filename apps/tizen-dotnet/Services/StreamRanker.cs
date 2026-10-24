using System;
using System.Collections.Generic;
using System.Linq;
using StreamedTv.Models;

namespace StreamedTv.Services
{
    public sealed class StreamRanker
    {
        private readonly HashSet<string> _favoriteTeams;

        public StreamRanker(IEnumerable<string> favoriteTeams)
        {
            _favoriteTeams = new HashSet<string>(
                favoriteTeams ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyList<CatalogMatch> RankMatches(IEnumerable<CatalogMatch> matches)
        {
            return matches
                .OrderBy(MatchBucket)
                .ThenByDescending(MatchesFavoriteTeam)
                .ThenByDescending(m => m.HasPlayableSource)
                .ThenByDescending(m => m.Popular)
                .ThenBy(m => MatchStart(m))
                .ThenBy(m => m.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public IReadOnlyList<StreamOption> RankStreams(IEnumerable<StreamOption> streams)
        {
            return streams
                .OrderByDescending(s => s.Hd)
                .ThenBy(s => IsEnglish(s) ? 0 : 1)
                .ThenBy(s => s.StreamNo)
                .ThenBy(s => s.Source, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static int MatchBucket(CatalogMatch match)
        {
            if (string.Equals(match.Status, "live", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            if (string.Equals(match.Status, "recent", StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            return 2;
        }

        private bool MatchesFavoriteTeam(CatalogMatch match)
        {
            return ContainsFavorite(match.Teams?.Home?.Name) || ContainsFavorite(match.Teams?.Away?.Name) || ContainsFavorite(match.Title);
        }

        private bool ContainsFavorite(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            foreach (var team in _favoriteTeams)
            {
                if (value.IndexOf(team, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsEnglish(StreamOption stream)
        {
            return string.IsNullOrWhiteSpace(stream.Language) || stream.Language.IndexOf("english", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static DateTimeOffset MatchStart(CatalogMatch match)
        {
            if (match.Date > 0)
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(match.Date);
            }

            if (DateTimeOffset.TryParse(match.StartTime, out var parsed))
            {
                return parsed;
            }

            return DateTimeOffset.MaxValue;
        }
    }
}
