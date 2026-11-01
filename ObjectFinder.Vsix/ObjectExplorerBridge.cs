using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.Shell;
using ObjectFinder.Core;

namespace ObjectFinder.Vsix
{
    /// <summary>What the user currently has selected in Object Explorer, plus the live connection behind it.</summary>
    internal sealed class ObjectExplorerSelection
    {
        public ObjectExplorerSelection(string serverName, string databaseName, object serverConnection)
        {
            ServerName = serverName;
            DatabaseName = databaseName;
            ServerConnection = serverConnection;
        }

        public string ServerName { get; }
        public string DatabaseName { get; }

        /// <summary>The already-authenticated Microsoft.SqlServer.Management.Common.ServerConnection owned by Object Explorer. Never dispose or close it.</summary>
        public object ServerConnection { get; }
    }

    /// <summary>
    /// The only place that touches SSMS internals. Everything is reflection, so a future SSMS change
    /// breaks here and nowhere else. Must be called on the UI thread.
    /// </summary>
    internal static class ObjectExplorerBridge
    {
        private const string ServiceTypeName =
            "Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer.IObjectExplorerService";

        // Object Explorer exposes each node's path as a URN such as
        //   Server[@Name='HOST']/Database[@Name='Sandbox']/Table[@Name='Orders' and @Schema='dbo']
        // Single quotes inside names are doubled.
        private static readonly Regex ServerPattern =
            new Regex(@"^Server\[@Name='((?:[^']|'')*)'\]", RegexOptions.CultureInvariant);

        private static readonly Regex DatabasePattern =
            new Regex(@"Database\[@Name='((?:[^']|'')*)'\]", RegexOptions.CultureInvariant);

        private static Type _serviceType;
        private static MethodInfo _getSelectedNodes;

        /// <exception cref="InvalidOperationException">With a message suitable for showing to the user.</exception>
        public static ObjectExplorerSelection GetSelection()
        {
            object service = GetService();

            object[] args = { 0, null };
            _getSelectedNodes.Invoke(service, args);

            Array nodes = args[1] as Array;
            if (nodes == null || nodes.Length == 0)
                throw new InvalidOperationException("Select a database in Object Explorer, then press F5.");

            object node = nodes.GetValue(0);
            string context = GetProperty(node, "Context") as string ?? "";

            Match database = DatabasePattern.Match(context);
            if (!database.Success)
                throw new InvalidOperationException(
                    "The selected node is not inside a database. Select a database or one of its objects, then press F5.");

            object connection = GetProperty(node, "Connection");
            object serverConnection = connection == null ? null : GetProperty(connection, "ServerConnection");
            if (serverConnection == null)
                throw new InvalidOperationException("Object Explorer did not provide a connection for the selected node.");

            Match server = ServerPattern.Match(context);
            return new ObjectExplorerSelection(
                server.Success ? Unescape(server.Groups[1].Value) : "",
                Unescape(database.Groups[1].Value),
                serverConnection);
        }

        /// <summary>Reads every indexed object in the selected database through Object Explorer's own connection.</summary>
        public static List<DbObject> LoadObjects(ObjectExplorerSelection selection)
        {
            string sql = BuildObjectQuery(selection.DatabaseName);

            MethodInfo execute = selection.ServerConnection.GetType()
                .GetMethod("ExecuteWithResults", new[] { typeof(string) });
            if (execute == null)
                throw new InvalidOperationException("This SSMS version's connection cannot run queries the way ObjectFinder expects.");

            DataSet result;
            try
            {
                result = execute.Invoke(selection.ServerConnection, new object[] { sql }) as DataSet;
            }
            catch (TargetInvocationException ex)
            {
                throw new InvalidOperationException(DeepestMessage(ex), ex);
            }

            var objects = new List<DbObject>();
            if (result == null || result.Tables.Count == 0)
                return objects;

            foreach (DataRow row in result.Tables[0].Rows)
            {
                string name = row["object_name"] as string;
                if (string.IsNullOrEmpty(name)) continue;

                objects.Add(new DbObject(
                    row["schema_name"] as string ?? "",
                    name,
                    DbObjectTypes.FromTypeCode(row["type_code"] as string)));
            }

            return objects;
        }

        /// <summary>
        /// Selects the object in the Object Explorer tree (expanding the folders on the way).
        /// context is the selection the object list was loaded from; it supplies the server and database names
        /// exactly as Object Explorer spells them.
        /// </summary>
        /// <exception cref="InvalidOperationException">With a message suitable for showing to the user.</exception>
        public static void Reveal(ObjectExplorerSelection context, DbObject target)
        {
            string urn = BuildUrn(context.ServerName, context.DatabaseName, target);
            object service = GetService();

            MethodInfo find = FindServiceMethod("FindNode", typeof(string));
            MethodInfo synchronize = FindServiceMethod("SynchronizeTree", null);

            object nodeInfo;
            try
            {
                nodeInfo = find.Invoke(service, new object[] { urn });
            }
            catch (TargetInvocationException ex)
            {
                throw new InvalidOperationException(DeepestMessage(ex), ex);
            }

            if (nodeInfo == null)
                throw new InvalidOperationException(
                    target.FullName + " was not found in Object Explorer. It may have been dropped or renamed; press F5 to reload.");

            try
            {
                synchronize.Invoke(service, new object[] { nodeInfo });
            }
            catch (TargetInvocationException ex)
            {
                throw new InvalidOperationException(DeepestMessage(ex), ex);
            }
        }

        public static void OpenScriptInNewQuery(ObjectExplorerSelection context, DbObject target, bool alter)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            Reveal(context, target);
            OpenGeneratedScriptDocument(context, target, alter);
        }

        private static void OpenGeneratedScriptDocument(ObjectExplorerSelection context, DbObject target, bool alter)
        {
            string script = BuildScriptWithSmo(context, target, alter);
            if (string.IsNullOrWhiteSpace(script))
                throw new InvalidOperationException("Scripting did not return any text.");

            string verb = alter ? "alter" : "create";
            string fileName = "ObjectFinder_" + verb + "_" + SanitizeFilePart(context.DatabaseName)
                + "_" + SanitizeFilePart(target.Schema) + "_" + SanitizeFilePart(target.Name)
                + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".sql";

            string path = Path.Combine(Path.GetTempPath(), fileName);
            File.WriteAllText(path, script, Encoding.UTF8);
            VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, path);
        }

        private static string BuildScriptWithSmo(ObjectExplorerSelection context, DbObject target, bool alter)
        {
            string createScript = BuildScriptWithSmoCore(context, target);
            if (string.IsNullOrWhiteSpace(createScript))
                return null;

            createScript = TrimToCreateStatement(createScript);
            if (string.IsNullOrWhiteSpace(createScript))
                return null;

            return alter ? ConvertCreateToAlter(createScript) : createScript;
        }

        private static string BuildScriptWithSmoCore(ObjectExplorerSelection context, DbObject target)
        {
            Type serverType = FindType("Microsoft.SqlServer.Management.Smo.Server");
            Type scripterType = FindType("Microsoft.SqlServer.Management.Smo.Scripter");
            Type scriptingOptionsType = FindType("Microsoft.SqlServer.Management.Smo.ScriptingOptions");
            Type urnType = FindType("Microsoft.SqlServer.Management.Sdk.Sfc.Urn");

            if (serverType == null || scripterType == null || scriptingOptionsType == null || urnType == null)
                throw new InvalidOperationException("SMO scripting types are not available in this SSMS environment.");

            object server = Activator.CreateInstance(serverType, context.ServerConnection);
            object scripter = Activator.CreateInstance(scripterType, server);
            object options = Activator.CreateInstance(scriptingOptionsType);

            SetProperty(options, "ScriptSchema", true);
            SetProperty(options, "IncludeHeaders", true);
            SetProperty(options, "SchemaQualify", true);
            SetProperty(options, "DriAll", true);
            SetProperty(options, "Indexes", true);
            SetProperty(options, "Triggers", true);
            SetProperty(options, "ScriptBatchTerminator", true);

            SetProperty(scripter, "Options", options);

            string urnText = BuildUrn(context.ServerName, context.DatabaseName, target);
            object urn = Activator.CreateInstance(urnType, urnText);
            Array urns = Array.CreateInstance(urnType, 1);
            urns.SetValue(urn, 0);

            object scriptResult = scripter.GetType().InvokeMember(
                "Script",
                BindingFlags.InvokeMethod,
                null,
                scripter,
                new object[] { urns });

            IEnumerable lines = scriptResult as IEnumerable;
            if (lines == null)
                return null;

            var text = new List<string>();
            foreach (object line in lines)
            {
                if (line != null)
                    text.Add(line.ToString());
            }

            return string.Join(Environment.NewLine, text);
        }

        private static string ConvertCreateToAlter(string createScript)
        {
            return Regex.Replace(
                createScript,
                @"\bCREATE\b",
                "ALTER",
                RegexOptions.IgnoreCase,
                TimeSpan.FromMilliseconds(200));
        }

        private static string TrimToCreateStatement(string script)
        {
            Match match = Regex.Match(
                script,
                @"\bCREATE\b",
                RegexOptions.IgnoreCase,
                TimeSpan.FromMilliseconds(200));

            if (!match.Success)
                return script;

            return script.Substring(match.Index).TrimStart();
        }

        private static string SanitizeFilePart(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "_";

            char[] invalid = Path.GetInvalidFileNameChars();
            var chars = text.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
            return new string(chars);
        }

        private static void SetProperty(object obj, string name, object value)
        {
            if (obj == null) return;

            Type type = obj.GetType();
            PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property != null && property.CanWrite)
            {
                try
                {
                    property.SetValue(obj, value, null);
                    return;
                }
                catch
                {
                }
            }

            try
            {
                type.InvokeMember(name, BindingFlags.SetProperty, null, obj, new[] { value });
            }
            catch
            {
            }
        }

        /// <summary>
        /// e.g. Server[@Name='HOST']/Database[@Name='Sandbox']/StoredProcedure[@Name='spTranslate' and @Schema='dbo']
        /// </summary>
        internal static string BuildUrn(string serverName, string databaseName, DbObject target)
        {
            string kind = UrnKind(target.Type);
            if (kind == null)
                throw new InvalidOperationException("Selecting " + target.Type + " objects in Object Explorer is not supported.");

            return "Server[@Name='" + Escape(serverName) + "']"
                 + "/Database[@Name='" + Escape(databaseName) + "']"
                 + "/" + kind + "[@Name='" + Escape(target.Name) + "' and @Schema='" + Escape(target.Schema) + "']";
        }

        private static string UrnKind(DbObjectType type)
        {
            switch (type)
            {
                case DbObjectType.Table: return "Table";
                case DbObjectType.View: return "View";
                case DbObjectType.StoredProcedure: return "StoredProcedure";
                case DbObjectType.ScalarFunction:
                case DbObjectType.TableFunction: return "UserDefinedFunction";
                case DbObjectType.Synonym: return "Synonym";
                default: return null;
            }
        }

        private static string Escape(string value)
        {
            return value.Replace("'", "''");
        }

        /// <summary>Finds a one-parameter method on the service interface; the error lists what is actually there.</summary>
        private static MethodInfo FindServiceMethod(string name, Type parameterType)
        {
            MethodInfo[] all = _serviceType.GetMethods().Where(m => m.Name == name).ToArray();

            MethodInfo match = all.FirstOrDefault(m =>
            {
                ParameterInfo[] parameters = m.GetParameters();
                return parameters.Length == 1 && (parameterType == null || parameters[0].ParameterType == parameterType);
            });

            if (match != null) return match;

            string found = all.Length == 0
                ? "no method with that name"
                : string.Join("; ", all.Select(m => m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name)) + ")"));
            throw new InvalidOperationException("Object Explorer's " + name + " does not have the expected shape in this SSMS version (" + found + ").");
        }

        /// <summary>
        /// The connection Object Explorer hands out is usually sitting in master, so the database is named
        /// explicitly (three-part names). This does not work on Azure SQL Database, which needs the
        /// connection opened against the target database instead.
        /// </summary>
        internal static string BuildObjectQuery(string databaseName)
        {
            string db = QuoteName(databaseName);
            string types = string.Join(", ", DbObjectTypes.IndexedTypeCodes.Select(code => "'" + code + "'"));

            return "SELECT s.name AS schema_name, o.name AS object_name, o.type AS type_code "
                 + "FROM " + db + ".sys.objects AS o "
                 + "JOIN " + db + ".sys.schemas AS s ON s.schema_id = o.schema_id "
                 + "WHERE o.is_ms_shipped = 0 AND o.type IN (" + types + ")";
        }

        internal static string QuoteName(string name)
        {
            return "[" + name.Replace("]", "]]") + "]";
        }

        internal static string Unescape(string urnValue)
        {
            return urnValue.Replace("''", "'");
        }

        private static object GetService()
        {
            if (_serviceType == null)
                _serviceType = FindType(ServiceTypeName);
            if (_serviceType == null)
                throw new InvalidOperationException("Object Explorer was not found. This SSMS version may not be supported.");

            if (_getSelectedNodes == null)
                _getSelectedNodes = _serviceType.GetMethod("GetSelectedNodes");
            if (_getSelectedNodes == null)
                throw new InvalidOperationException("Object Explorer does not expose its selection in this SSMS version.");

            object service = Package.GetGlobalService(_serviceType);
            if (service == null)
                throw new InvalidOperationException("Object Explorer is not available yet.");

            return service;
        }

        private static object GetProperty(object obj, string name)
        {
            if (obj == null) return null;

            Type type = obj.GetType();
            PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property != null)
            {
                try { return property.GetValue(obj, null); }
                catch (TargetInvocationException) { }
            }

            try
            {
                return type.InvokeMember(
                    name,
                    BindingFlags.GetProperty,
                    null,
                    obj,
                    new object[0]);
            }
            catch
            {
                return null;
            }
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic) continue;
                try
                {
                    Type type = assembly.GetType(fullName, false);
                    if (type != null) return type;
                }
                catch { }
            }
            return null;
        }

        private static string DeepestMessage(Exception ex)
        {
            Exception current = ex;
            while (current.InnerException != null)
                current = current.InnerException;
            return current.Message;
        }
    }
}
