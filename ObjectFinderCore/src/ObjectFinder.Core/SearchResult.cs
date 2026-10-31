namespace ObjectFinder.Core
{
    public sealed class SearchResult
    {
        public SearchResult(DbObject item, int score)
        {
            Item = item;
            Score = score;
        }

        public DbObject Item { get; }

        /// <summary>Higher is better. Only meaningful relative to other results of the same query.</summary>
        public int Score { get; }

        public override string ToString() => Item.FullName + " (" + Score + ")";
    }
}
