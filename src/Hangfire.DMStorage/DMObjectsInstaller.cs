﻿using System;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Dapper;

using Hangfire.Logging;

namespace Hangfire.DMStorage
{
    public static class DMObjectsInstaller
    {
        private static readonly ILog Log = LogProvider.GetLogger(typeof(DMStorage));
        public static void Install(IDbConnection connection, string schemaName)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (string.IsNullOrWhiteSpace(schemaName))
            {
                var userMatch=Regex.Match(connection.ConnectionString, @"(?<=user\s*?id=).+?(?=;)",RegexOptions.IgnoreCase);
                schemaName = userMatch.Success ? userMatch.Value : "";
            }
            if (TablesExists(connection, schemaName))
            {
                Log.Info("DB tables already exist. Exit install");
                return;
            }
            Log.Info("Start installing Hangfire SQL objects...");
            var script = GetStringResource("Hangfire.DMStorage.Install.sql");
            script =!string.IsNullOrWhiteSpace(schemaName)?script.Replace("[SchemaName]",$"{schemaName}."): script.Replace("[SchemaName]","");
            var sqlCommands = script.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            sqlCommands.ToList().ForEach(s => connection.Execute(s));
            Log.Info("Hangfire SQL objects installed.");
        }

        private static bool TablesExists(IDbConnection connection, string schemaName)
        {
            string tableExistsQuery;

            if (!string.IsNullOrWhiteSpace(schemaName))
            {
                tableExistsQuery = $@"SELECT TABLE_NAME
FROM all_tables
WHERE OWNER = '{schemaName}' AND  TABLE_NAME IN('AggregatedCounter','Counter','DistributedLock','Hash','Job','JobParameter','JobQueue','JobState','List','Server','Set','State')
ORDER BY TABLE_NAME;";
            }
            else
            {
                tableExistsQuery = @"SELECT TABLE_NAME
FROM all_tables
WHERE TABLE_NAME IN('AggregatedCounter','Counter','DistributedLock','Hash','Job','JobParameter','JobQueue','JobState','List','Server','Set','State')
ORDER BY TABLE_NAME;";
            }
            return connection.ExecuteScalar<string>(tableExistsQuery) != null;
        }

        private static string GetStringResource(string resourceName)
        {
#if NET45
            var assembly = typeof(DMObjectsInstaller).Assembly;
#else
            var assembly = typeof(DMObjectsInstaller).GetTypeInfo().Assembly;
#endif

            using (var stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                {
                    throw new InvalidOperationException($"Requested resource `{resourceName}` was not found in the assembly `{assembly}`.");
                }

                using (var reader = new StreamReader(stream))
                {
                    return reader.ReadToEnd();
                }
            }
        }
    }
}
