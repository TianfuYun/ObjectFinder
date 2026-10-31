using System.Linq;
using ObjectFinder.Core;
using Xunit;

namespace ObjectFinder.Core.Tests
{
    public class ObjectSearcherTests
    {
        private static DbObject Proc(string name, string schema = "dbo")
        {
            return new DbObject(schema, name, DbObjectType.StoredProcedure);
        }

        private static string[] Names(ObjectSearcher searcher, string query, int max = 50)
        {
            return searcher.Search(query, max).Select(r => r.Item.FullName).ToArray();
        }

        [Fact]
        public void ExactMatchRanksFirst()
        {
            var searcher = new ObjectSearcher(new[] { Proc("spTranslateAll"), Proc("spTranslate"), Proc("xspTranslate") });

            Assert.Equal(new[] { "dbo.spTranslate", "dbo.spTranslateAll", "dbo.xspTranslate" }, Names(searcher, "spTranslate"));
        }

        [Fact]
        public void PrefixBeatsWordBoundaryBeatsPlainSubstring()
        {
            var searcher = new ObjectSearcher(new[] { Proc("xtranslatex"), Proc("spTranslate"), Proc("TranslateText") });

            Assert.Equal(
                new[] { "dbo.TranslateText", "dbo.spTranslate", "dbo.xtranslatex" },
                Names(searcher, "translate"));
        }

        [Fact]
        public void UnderscoreCountsAsWordBoundary()
        {
            var searcher = new ObjectSearcher(new[] { Proc("xtranslatex"), Proc("sp_translate") });

            Assert.Equal(new[] { "dbo.sp_translate", "dbo.xtranslatex" }, Names(searcher, "translate"));
        }

        [Fact]
        public void MatchingIsCaseInsensitive()
        {
            var searcher = new ObjectSearcher(new[] { Proc("spTranslate") });

            Assert.Equal(new[] { "dbo.spTranslate" }, Names(searcher, "SPTRANSLATE"));
        }

        [Fact]
        public void SchemaQualifiedQueryRestrictsToThatSchema()
        {
            var searcher = new ObjectSearcher(new[] { Proc("Foo", "dbo"), Proc("Foo", "sales") });

            Assert.Equal(new[] { "sales.Foo" }, Names(searcher, "sales.foo"));
        }

        [Fact]
        public void SchemaWithTrailingDotListsTheSchema()
        {
            var searcher = new ObjectSearcher(new[] { Proc("A", "dbo"), Proc("B", "sales"), Proc("C", "sales") });

            Assert.Equal(new[] { "sales.B", "sales.C" }, Names(searcher, "sales."));
        }

        [Fact]
        public void TokenCanMatchSchemaOnly()
        {
            var searcher = new ObjectSearcher(new[] { Proc("Orders", "sales"), Proc("Orders", "dbo") });

            Assert.Equal(new[] { "sales.Orders" }, Names(searcher, "sales"));
        }

        [Fact]
        public void FuzzySubsequenceFindsCamelCaseAbbreviation()
        {
            var searcher = new ObjectSearcher(new[] { Proc("spTranslate"), Proc("spOther") });

            Assert.Equal(new[] { "dbo.spTranslate" }, Names(searcher, "sptrnsl"));
        }

        [Fact]
        public void FuzzyMatchRanksBelowSubstringMatch()
        {
            var searcher = new ObjectSearcher(new[] { Proc("spTranslate"), Proc("tr_ans_late_sp") });

            // "ranslat" is a plain substring of the first and only a scattered subsequence of the second.
            var names = Names(searcher, "ranslat");
            Assert.Equal("dbo.spTranslate", names[0]);
        }

        [Fact]
        public void AllTokensMustMatch()
        {
            var searcher = new ObjectSearcher(new[] { Proc("TranslateText"), Proc("TranslateOnly") });

            Assert.Equal(new[] { "dbo.TranslateText" }, Names(searcher, "trans text"));
        }

        [Fact]
        public void NoMatchReturnsEmpty()
        {
            var searcher = new ObjectSearcher(new[] { Proc("spTranslate") });

            Assert.Empty(searcher.Search("zzz"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void BlankQueryReturnsEmpty(string? query)
        {
            var searcher = new ObjectSearcher(new[] { Proc("spTranslate") });

            Assert.Empty(searcher.Search(query));
        }

        [Fact]
        public void MaxResultsIsHonoured()
        {
            var searcher = new ObjectSearcher(Enumerable.Range(0, 100).Select(i => Proc("sp" + i)));

            Assert.Equal(10, searcher.Search("sp", 10).Count);
            Assert.Empty(searcher.Search("sp", 0));
        }

        [Fact]
        public void TiesFavourShorterNames()
        {
            var searcher = new ObjectSearcher(new[] { Proc("abcdef"), Proc("abc"), Proc("abcd") });

            Assert.Equal(new[] { "dbo.abc", "dbo.abcd", "dbo.abcdef" }, Names(searcher, "ab"));
        }

        [Theory]
        [InlineData("U", DbObjectType.Table)]
        [InlineData("V", DbObjectType.View)]
        [InlineData("P", DbObjectType.StoredProcedure)]
        [InlineData("FN", DbObjectType.ScalarFunction)]
        [InlineData("IF", DbObjectType.TableFunction)]
        [InlineData("TF", DbObjectType.TableFunction)]
        [InlineData("SN", DbObjectType.Synonym)]
        [InlineData(" v ", DbObjectType.View)]
        [InlineData("XX", DbObjectType.Other)]
        [InlineData(null, DbObjectType.Other)]
        public void TypeCodesMapToObjectTypes(string? code, DbObjectType expected)
        {
            Assert.Equal(expected, DbObjectTypes.FromTypeCode(code));
        }
    }
}
