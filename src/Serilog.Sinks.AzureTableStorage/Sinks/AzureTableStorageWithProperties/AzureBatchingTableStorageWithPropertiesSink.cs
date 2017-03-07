﻿// Copyright 2014 Serilog Contributors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Table;
using Serilog.Events;
using Serilog.Sinks.PeriodicBatching;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Serilog.Sinks.AzureTableStorage.KeyGenerator;
using Serilog.Sinks.AzureTableStorage.Sinks.KeyGenerator;

namespace Serilog.Sinks.AzureTableStorage
{
    /// <summary>
    /// Writes log events as records to an Azure Table Storage table.
    /// </summary>
    public class AzureBatchingTableStorageWithPropertiesSink : PeriodicBatchingSink
    {
        private readonly int _waitTimeoutMilliseconds = Timeout.Infinite;
        private readonly IFormatProvider _formatProvider;
        private readonly CloudTable _table;
        private readonly string _additionalRowKeyPostfix;
        private readonly string[] _specificProperties;
        private readonly bool _onlySpecificProperties;
        private const int _maxAzureOperationsPerBatch = 100;
        private readonly IKeyGenerator _keyGenerator;

        /// <summary>
        /// Construct a sink that saves logs to the specified storage account.
        /// </summary>
        /// <param name="storageAccount">The Cloud Storage Account to use to insert the log entries to.</param>
        /// <param name="formatProvider">Supplies culture-specific formatting information, or null.</param>
        /// <param name="batchSizeLimit"></param>
        /// <param name="period"></param>
        /// <param name="storageTableName">Table name that log entries will be written to. Note: Optional, setting this may impact performance</param>
        /// <param name="additionalRowKeyPostfix">Additional postfix string that will be appended to row keys</param>
        /// <param name="keyGenerator">Generates the PartitionKey and the RowKey</param>
        /// <param name="specificProperties">Specific properties to be added to the Table Storage</param>
        /// <param name="onlySpecificProperties">Only configure the specific properties specified; otherwise, all properties provided 
        /// in the message templates will be created</param>
        /// <returns>Logger configuration, allowing configuration to continue.</returns>
        public AzureBatchingTableStorageWithPropertiesSink(CloudStorageAccount storageAccount,
            IFormatProvider formatProvider,
            int batchSizeLimit,
            TimeSpan period,
            string storageTableName = null,
            string additionalRowKeyPostfix = null,
            IKeyGenerator keyGenerator = null, 
            string[] specificProperties = null, 
            bool onlySpecificProperties = false)
            : base(batchSizeLimit, period)
        {
            var tableClient = storageAccount.CreateCloudTableClient();

            if (string.IsNullOrEmpty(storageTableName))
            {
                storageTableName = "LogEventEntity";
            }

            _table = tableClient.GetTableReference(storageTableName);
            _table.CreateIfNotExistsAsync().SyncContextSafeWait(_waitTimeoutMilliseconds);

            _formatProvider = formatProvider;
            _additionalRowKeyPostfix = additionalRowKeyPostfix;
            _specificProperties = specificProperties;
            _onlySpecificProperties = onlySpecificProperties;
            _keyGenerator = keyGenerator ?? new PropertiesKeyGenerator();
        }

        protected override async Task EmitBatchAsync(IEnumerable<LogEvent> events)
        {
            string lastPartitionKey = null;
            TableBatchOperation operation = null;
            var insertsPerOperation = 0;

            foreach (var logEvent in events)
            {
                var tableEntity = AzureTableStorageEntityFactory.CreateEntityWithProperties(logEvent, _formatProvider, _additionalRowKeyPostfix, _keyGenerator, _specificProperties, _onlySpecificProperties);

                // If partition changed, store the new and force an execution
                if (lastPartitionKey != tableEntity.PartitionKey)
                {
                    lastPartitionKey = tableEntity.PartitionKey;

                    // Force a new execution
                    insertsPerOperation = _maxAzureOperationsPerBatch;
                }

                // If reached max operations per batch, we need a new batch operation
                if (insertsPerOperation == _maxAzureOperationsPerBatch)
                {
                    // If there is an operation currently in use, execute it
                    if (operation != null)
                    {
                        await _table.ExecuteBatchAsync(operation);
                    }

                    // Create a new batch operation and zero count
                    operation = new TableBatchOperation();
                    insertsPerOperation = 0;
                }

                // Add current entry to the batch
                operation.Add(TableOperation.Insert(tableEntity));

                insertsPerOperation++;
            }

            // Execute last batch
            await _table.ExecuteBatchAsync(operation);
        }
    }
}
