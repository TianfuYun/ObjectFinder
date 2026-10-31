using System;
using System.Collections.Generic;
using System.Linq;

namespace ObjectFinder.Core
{
    /// <summary>
    /// In-memory search over a database's object list. Build one per refresh, then call
    /// <see cref="Search"/> on every keystroke; it is a plain scan and fast enough for tens of thousands of objects.
    ///
    /// Query rules:
    ///   * Whitespace separates tokens; every token must match.
    ///   * Matching is case-insensitive.
    ///   * "schema.name" restricts to schemas starting with "schema"; "schema." lists that schema.
    ///   * A token ranks (best first): exact name, name prefix, word-boundary substring
    ///     (spTranslate / sp_translate for "translate"), any substring, schema substring,
    ///     and finally a fuzzy in-order subsequence ("sptrnsl" finds spTranslate).
    /// </summary>
    public sealed class ObjectSearcher
    {
        // Tier scores are spaced so a better tier always beats a worse one,
        // whatever the adjustments inside a tier (all adjustments stay under 100).
        private const int ExactScore = 1000;
        private const int PrefixScore = 800;
        private const int WordBoundaryScore = 600;
        private const int SubstringScore = 400;
        private const int SchemaScore = 300;
        private const int SchemaListScore = 500;
        private const int FuzzyMax = 250;

        private readonly DbObject[] _objects;

        public ObjectSearcher(IEnumerable<DbObject> objects)
        {
            if (objects == null) throw new ArgumentNullException(nameof(objects));
            _objects = objects.ToArray();
        }

        public int Count => _objects.Length;

        public IReadOnlyList<SearchResult> Search(string? query, int maxResults = 50)
        {
            if (maxResults <= 0 || string.IsNullOrWhiteSpace(query))
                return Array.Empty<SearchResult>();

            string[] tokens = query!.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
                return Array.Empty<SearchResult>();

            var results = new List<SearchResult>();
            foreach (DbObject obj in _objects)
            {
                int total = 0;
                bool matchesAll = true;
                foreach (string token in tokens)
                {
                    int score = ScoreToken(token, obj);
                    if (score <= 0)
                    {
                        matchesAll = false;
                        break;
                    }
                    total += score;
                }

                if (matchesAll)
                    results.Add(new SearchResult(obj, total));
            }

            return results
                .OrderByDescending(r => r.Score)
                .ThenBy(r => r.Item.Name.Length)
                .ThenBy(r => r.Item.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Item.Schema, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Item.Type)
                .Take(maxResults)
                .ToList();
        }

        private static int ScoreToken(string token, DbObject obj)
        {
            int dot = token.IndexOf('.');
            if (dot >= 0)
            {
                string schemaPart = token.Substring(0, dot);
                string namePart = token.Substring(dot + 1);

                if (schemaPart.Length > 0 &&
                    !obj.Schema.StartsWith(schemaPart, StringComparison.OrdinalIgnoreCase))
                    return 0;

                return namePart.Length == 0 ? SchemaListScore : ScoreName(namePart, obj.Name);
            }

            int nameScore = ScoreName(token, obj.Name);
            int schemaScore = obj.Schema.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0 ? SchemaScore : 0;
            return Math.Max(nameScore, schemaScore);
        }

        private static int ScoreName(string token, string name)
        {
            if (string.Equals(name, token, StringComparison.OrdinalIgnoreCase))
                return ExactScore;

            if (name.StartsWith(token, StringComparison.OrdinalIgnoreCase))
                return PrefixScore - Math.Min(name.Length - token.Length, 99);

            int first = name.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (first >= 0)
            {
                int boundary = FindBoundaryOccurrence(token, name, first);
                return boundary >= 0
                    ? WordBoundaryScore - Math.Min(boundary, 99)
                    : SubstringScore - Math.Min(first, 99);
            }

            return FuzzyScore(token, name);
        }

        private static int FindBoundaryOccurrence(string token, string name, int firstOccurrence)
        {
            int i = firstOccurrence;
            while (i >= 0)
            {
                if (IsBoundary(name, i))
                    return i;
                i = i + 1 < name.Length ? name.IndexOf(token, i + 1, StringComparison.OrdinalIgnoreCase) : -1;
            }
            return -1;
        }

        /// <summary>In-order subsequence match, rewarding runs and word starts and penalizing gaps. Returns 0 if no match.</summary>
        private static int FuzzyScore(string token, string name)
        {
            int score = 100;
            int tokenIndex = 0;
            int firstMatch = -1;
            int lastMatch = -2;

            for (int i = 0; i < name.Length && tokenIndex < token.Length; i++)
            {
                if (char.ToUpperInvariant(name[i]) != char.ToUpperInvariant(token[tokenIndex]))
                    continue;

                if (firstMatch < 0) firstMatch = i;
                if (i == lastMatch + 1) score += 10;
                if (IsBoundary(name, i)) score += 20;

                lastMatch = i;
                tokenIndex++;
            }

            if (tokenIndex < token.Length)
                return 0;

            int skipped = lastMatch - firstMatch + 1 - token.Length;
            score -= Math.Min(skipped, 90);
            return Math.Max(1, Math.Min(score, FuzzyMax));
        }

        /// <summary>True where a "word" starts: after a non-alphanumeric character, at a lower-to-upper camelCase step, or at a letter/digit switch.</summary>
        private static bool IsBoundary(string name, int index)
        {
            if (index == 0) return true;

            char previous = name[index - 1];
            char current = name[index];

            if (!char.IsLetterOrDigit(previous)) return true;
            if (char.IsUpper(current) && char.IsLower(previous)) return true;
            return char.IsDigit(current) != char.IsDigit(previous);
        }
    }
}
