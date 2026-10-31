using System;

namespace ObjectFinder.Core
{
    /// <summary>One searchable database object: enough to find it and to build its Object Explorer URN later.</summary>
    public sealed class DbObject
    {
        public DbObject(string schema, string name, DbObjectType type)
        {
            if (schema == null) throw new ArgumentNullException(nameof(schema));
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("Name is required.", nameof(name));

            Schema = schema;
            Name = name;
            Type = type;
        }

        public string Schema { get; }
        public string Name { get; }
        public DbObjectType Type { get; }

        public string FullName => Schema.Length == 0 ? Name : Schema + "." + Name;

        public override string ToString() => FullName;
    }
}
