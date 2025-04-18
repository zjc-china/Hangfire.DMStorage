﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;

using Dapper;

using Hangfire.Annotations;
using Hangfire.Logging;
using Hangfire.DMStorage.JobQueue;
using Hangfire.DMStorage.Monitoring;
using Hangfire.Server;
using Hangfire.Storage;
using Hangfire;
using Dm;

namespace Hangfire.DMStorage
{
    public class DMStorage : JobStorage
    {
        private static readonly ILog Logger = LogProvider.GetLogger(typeof(DMStorage));

        private string _string;
        private readonly string _connectionString;
        private readonly Func<IDbConnection> _connectionFactory;
        private readonly DMStorageOptions _options;

/*
        private readonly Dictionary<string, bool> _features =
           new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
           {
                { JobStorageFeatures.JobQueueProperty, true }
           };*/

        public virtual PersistentJobQueueProviderCollection QueueProviders { get; private set; }

        public DMStorage(string connectionString)
            : this(connectionString, new DMStorageOptions())
        {
        }

        public DMStorage(string connectionString, DMStorageOptions options)
        {
            if (connectionString == null)
            {
                throw new ArgumentNullException(nameof(connectionString));
            }

            if (IsConnectionString(connectionString))
            {
                _connectionString = connectionString;
            }
            else
            {
                throw new ArgumentException($"Could not find connection string with name '{connectionString}' in application config file");
            }

            _options = options ?? throw new ArgumentNullException(nameof(options));
            PrepareSchemaIfNecessary(options);

            InitializeQueueProviders();
        }       
     
        public DMStorage(Func<IDbConnection> connectionFactory, DMStorageOptions options)
        {
            _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

            _options = options ?? throw new ArgumentNullException(nameof(options));
            PrepareSchemaIfNecessary(options);

            InitializeQueueProviders();
        }

        private void PrepareSchemaIfNecessary(DMStorageOptions options)
        {
            if (options.PrepareSchemaIfNecessary)
            {
                using (var connection = CreateAndOpenConnection())
                {
                    DMObjectsInstaller.Install(connection, options.SchemaName);
                }
            }
        }

        private void InitializeQueueProviders()
        {
            QueueProviders = new PersistentJobQueueProviderCollection(new DMJobQueueProvider(this, _options));
        }

#pragma warning disable 618
        public override IEnumerable<IServerComponent> GetComponents()
#pragma warning restore 618
        {
            yield return new ExpirationManager(this, _options.JobExpirationCheckInterval);
            yield return new CountersAggregator(this, _options.CountersAggregateInterval);
        }

        public override void WriteOptionsToLog(ILog logger)
        {
            logger.Info("Using the following options for DM Database job storage:");
            logger.InfoFormat("    Queue poll interval: {0}.", _options.QueuePollInterval);
        }

        public override string ToString()
        {
            if (!string.IsNullOrWhiteSpace(_string))
            {
                return _string;
            }

            var connectionString = _connectionString;

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                using (var connection = CreateAndOpenConnection())
                {
                    connectionString = connection.ConnectionString;
                }
            }

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                _string = "DM";
                return _string;
            }

            try
            {
                var parts = connectionString.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Split(new[] { '=' }, StringSplitOptions.RemoveEmptyEntries))
                    .Select(x => new { Key = x[0].Trim(), Value = x.Length > 1 ? x[1].Trim() : "" })
                    .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);

                var builder = new StringBuilder();

                foreach (var alias in new[] { "Data Source", "Server", "Address", "Addr", "Network Address" })
                {
                    if (parts.ContainsKey(alias))
                    {
                        builder.Append(parts[alias]);
                        break;
                    }
                }

                _string = $"DM: {builder}";
                return _string;
            }
            catch (Exception ex)
            {
                Logger.ErrorException(ex.Message, ex);
                _string = "<Connection string can not be parsed>";
                return _string;
            }
        }

        public override IMonitoringApi GetMonitoringApi()
        {
            return new DMMonitoringApi(this, _options.DashboardJobListLimit);
        }

        public override IStorageConnection GetConnection()
        {
            return new DMStorageConnection(this);
        }

        private static bool IsConnectionString(string nameOrConnectionString)
        {
            return nameOrConnectionString.Contains(";");
        }

        internal void UseTransaction([InstantHandle] Action<IDbConnection> action)
        {
            UseTransaction(connection =>
            {
                action(connection);
                return true;
            }, null);
        }

        internal T UseTransaction<T>([InstantHandle] Func<IDbConnection, T> func, IsolationLevel? isolationLevel)
        {
            return UseConnection(connection =>
            {
                using (var transaction = connection.BeginTransaction(isolationLevel ?? _options.TransactionIsolationLevel ?? IsolationLevel.ReadCommitted))
                {
                    var result = func(connection);
                    transaction.Commit();

                    return result;
                }
            });
        }

        internal void UseConnection([InstantHandle] Action<IDbConnection> action)
        {
            UseConnection(connection =>
            {
                action(connection);
                return true;
            });
        }

        internal T UseConnection<T>([InstantHandle] Func<IDbConnection, T> func)
        {
            IDbConnection connection = null;

            try
            {
                connection = CreateAndOpenConnection();
                return func(connection);
            }
            finally
            {
                ReleaseConnection(connection);
            }
        }

        internal IDbConnection CreateAndOpenConnection()
        {
            var connection = _connectionFactory != null ? _connectionFactory() : new DmConnection(_connectionString);

            if (connection.State == ConnectionState.Closed)
            {
                connection.Open();

                if (!string.IsNullOrWhiteSpace(_options.SchemaName))
                {
                    connection.Execute($"ALTER SESSION SET CURRENT_SCHEMA={_options.SchemaName}");
                }
            }

            return connection;
        }

        internal void ReleaseConnection(IDbConnection connection)
        {
            connection?.Dispose();
        }
       
        /*
        public override bool HasFeature(string featureId)
        {
            if (featureId == null) throw new ArgumentNullException(nameof(featureId));

            return _features.TryGetValue(featureId, out var isSupported)
                ? isSupported
                : base.HasFeature(featureId);
        }*/

    }
}
