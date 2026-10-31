namespace ObjectFinder.Core
{
    public enum DbObjectType
    {
        Other = 0,
        Table,
        View,
        StoredProcedure,
        ScalarFunction,
        TableFunction,
        Synonym,
    }

    public static class DbObjectTypes
    {
        /// <summary>
        /// The sys.objects.type codes the extension indexes. Use this to build the
        /// "type IN (...)" filter so the query and the mapping below cannot drift apart.
        /// </summary>
        public static readonly string[] IndexedTypeCodes =
        {
            "U",            // user table
            "V",            // view
            "P", "PC",      // stored procedure (T-SQL, CLR)
            "FN", "FS",     // scalar function (T-SQL, CLR)
            "IF", "TF", "FT", // table-valued function (inline, multi-statement, CLR)
            "SN",           // synonym
        };

        public static DbObjectType FromTypeCode(string? typeCode)
        {
            switch (typeCode == null ? "" : typeCode.Trim().ToUpperInvariant())
            {
                case "U": return DbObjectType.Table;
                case "V": return DbObjectType.View;
                case "P":
                case "PC": return DbObjectType.StoredProcedure;
                case "FN":
                case "FS": return DbObjectType.ScalarFunction;
                case "IF":
                case "TF":
                case "FT": return DbObjectType.TableFunction;
                case "SN": return DbObjectType.Synonym;
                default: return DbObjectType.Other;
            }
        }
    }
}
