using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.Shell;

namespace ObjectFinder.Vsix
{
    /// <summary>Tiny file logger for the discovery work. Not for shipping.</summary>
    internal static class DiagLog
    {
        public static readonly string FilePath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ObjectFinder", "diagnostics.log");

        public static void Reset()
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, "ObjectFinder diagnostics " + DateTime.Now.ToString("s") + Environment.NewLine);
            }
            catch { }
        }

        public static void Write(string message)
        {
            try { File.AppendAllText(FilePath, message + Environment.NewLine); }
            catch { }
        }
    }

    /// <summary>
    /// Read-only discovery of how Object Explorer exposes the selected node and its live connection.
    /// Everything goes through reflection because these types are SSMS internals.
    /// </summary>
    internal static class ObjectExplorerDiagnostics
    {
        private const string ServiceTypeName =
            "Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer.IObjectExplorerService";
        private const string ServiceCacheTypeName =
            "Microsoft.SqlServer.Management.UI.VSIntegration.ServiceCache";

        // Properties whose names contain any of these are never written to the log.
        private static readonly string[] SecretMarkers =
            { "password", "pwd", "token", "secret", "connectionstring" };

        public static void Run()
        {
            DiagLog.Reset();
            try { RunCore(); }
            catch (Exception ex) { DiagLog.Write("FAILED: " + ex); }
            DiagLog.Write("done");
        }

        private static void RunCore()
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = asm.GetName().Name;
                if (name.IndexOf("ObjectExplorer", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("SqlWorkbench", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("ConnectionInfo", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    DiagLog.Write("assembly " + asm.GetName().FullName + " @ " + SafeLocation(asm));
                }
            }

            Type serviceType = FindType(ServiceTypeName);
            DiagLog.Write("IObjectExplorerService: " + (serviceType == null ? "NOT FOUND" : serviceType.AssemblyQualifiedName));
            if (serviceType == null) return;

            DiagLog.Write("members: " + string.Join(", ", serviceType.GetMembers().Select(m => m.Name).Distinct()));

            object service = Package.GetGlobalService(serviceType);
            DiagLog.Write("Package.GetGlobalService -> " + Describe(service));
            if (service == null) service = TryServiceCache(serviceType);
            if (service == null) { DiagLog.Write("no service instance found"); return; }

            MethodInfo getSelected = serviceType.GetMethod("GetSelectedNodes");
            if (getSelected == null) { DiagLog.Write("GetSelectedNodes NOT FOUND"); return; }

            object[] args = { 0, null };
            getSelected.Invoke(service, args);
            Array nodes = args[1] as Array;
            DiagLog.Write("selected nodes: " + (nodes == null ? 0 : nodes.Length));
            if (nodes == null || nodes.Length == 0)
            {
                DiagLog.Write("Select a node in Object Explorer first, then run again.");
                return;
            }

            for (int i = 0; i < nodes.Length; i++)
                DumpObject("node" + i, nodes.GetValue(i), 1);

            object connection = GetProperty(nodes.GetValue(0), "Connection");
            object serverConnection = connection == null ? null : GetProperty(connection, "ServerConnection");
            DiagLog.Write("ServerConnection: " + Describe(serverConnection));
            if (serverConnection != null) RunTestQuery(serverConnection);
        }

        private static void RunTestQuery(object serverConnection)
        {
            MethodInfo execute = serverConnection.GetType().GetMethod("ExecuteWithResults", new[] { typeof(string) });
            if (execute == null) { DiagLog.Write("ExecuteWithResults(string) NOT FOUND"); return; }

            DataSet result = execute.Invoke(serverConnection, new object[]
            {
                "SELECT DB_NAME() AS current_db, " +
                "(SELECT COUNT(*) FROM sys.objects WHERE is_ms_shipped = 0) AS object_count"
            }) as DataSet;

            if (result == null || result.Tables.Count == 0 || result.Tables[0].Rows.Count == 0)
            {
                DiagLog.Write("test query returned nothing");
                return;
            }

            DataRow row = result.Tables[0].Rows[0];
            DiagLog.Write("test query: current_db=" + row["current_db"] + ", object_count=" + row["object_count"]);
        }

        private static object TryServiceCache(Type serviceType)
        {
            Type cache = FindType(ServiceCacheTypeName);
            DiagLog.Write("ServiceCache: " + (cache == null ? "NOT FOUND" : cache.AssemblyQualifiedName));
            if (cache == null) return null;

            foreach (MethodInfo m in cache.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.GetParameters().Length != 0 || !serviceType.IsAssignableFrom(m.ReturnType)) continue;
                object result = m.Invoke(null, null);
                DiagLog.Write("ServiceCache." + m.Name + "() -> " + Describe(result));
                if (result != null) return result;
            }

            foreach (PropertyInfo p in cache.GetProperties(BindingFlags.Public | BindingFlags.Static))
            {
                if (p.GetIndexParameters().Length != 0 || !serviceType.IsAssignableFrom(p.PropertyType)) continue;
                object result = p.GetValue(null, null);
                DiagLog.Write("ServiceCache." + p.Name + " -> " + Describe(result));
                if (result != null) return result;
            }

            return null;
        }

        private static void DumpObject(string label, object obj, int depth)
        {
            if (obj == null) { DiagLog.Write(label + " = null"); return; }

            Type type = obj.GetType();
            DiagLog.Write(label + " : " + type.FullName + " (" + type.Assembly.GetName().Name + ")");

            foreach (PropertyInfo p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length != 0) continue;

                string lower = p.Name.ToLowerInvariant();
                if (SecretMarkers.Any(marker => lower.Contains(marker)))
                {
                    DiagLog.Write("  " + label + "." + p.Name + " = <withheld>");
                    continue;
                }

                object value;
                try { value = p.GetValue(obj, null); }
                catch (Exception ex) { DiagLog.Write("  " + label + "." + p.Name + " = <" + ex.GetType().Name + ">"); continue; }

                DiagLog.Write("  " + label + "." + p.Name + " = " + Format(value));

                if (depth > 0 && value != null && p.Name == "Connection")
                    DumpObject(label + ".Connection", value, depth - 1);
            }
        }

        private static string Format(object value)
        {
            if (value == null) return "null";
            string text = value as string;
            if (text != null) return "\"" + text + "\"";

            Type type = value.GetType();
            if (type.IsPrimitive || type.IsEnum || value is Guid || value is DateTime)
                return value.ToString();

            return "<" + type.FullName + ">";
        }

        private static string Describe(object value)
        {
            return value == null ? "null" : value.GetType().FullName;
        }

        private static object GetProperty(object obj, string name)
        {
            if (obj == null) return null;
            PropertyInfo p = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p == null) return null;
            try { return p.GetValue(obj, null); }
            catch (Exception ex) { DiagLog.Write("reading " + name + " failed: " + ex.GetType().Name); return null; }
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic) continue;
                try
                {
                    Type t = asm.GetType(fullName, false);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        private static string SafeLocation(Assembly asm)
        {
            try { return asm.IsDynamic ? "(dynamic)" : asm.Location; }
            catch { return "(unknown)"; }
        }
    }
}
