﻿using System;
using System.Diagnostics;
using System.Threading;

using Raven.Abstractions.Logging;
using Raven.Client.Data.Indexes;
using Raven.Server.ServerWide.Context;
using Voron;

namespace Raven.Server.Documents.Indexes.Auto
{
    public class AutoMapIndex : Index<AutoMapIndexDefinition>
    {
        private AutoMapIndex(int indexId, AutoMapIndexDefinition definition)
            : base(indexId, IndexType.AutoMap, definition)
        {
        }

        public static AutoMapIndex CreateNew(int indexId, AutoMapIndexDefinition definition, DocumentDatabase documentDatabase)
        {
            var instance = new AutoMapIndex(indexId, definition);
            instance.Initialize(documentDatabase);

            return instance;
        }

        public static AutoMapIndex Open(int indexId, StorageEnvironment environment, DocumentDatabase documentDatabase)
        {
            var definition = AutoMapIndexDefinition.Load(environment);
            var instance = new AutoMapIndex(indexId, definition);
            instance.Initialize(environment, documentDatabase);

            return instance;
        }

        public override void DoIndexingWork(IndexingBatchStats stats, CancellationToken cancellationToken)
        {
            DocumentsOperationContext databaseContext;
            TransactionOperationContext indexContext;

            using (DocumentDatabase.DocumentsStorage.ContextPool.AllocateOperationContext(out databaseContext))
            using (_contextPool.AllocateOperationContext(out indexContext))
            using (var tx = indexContext.OpenWriteTransaction())
            {
                ExecuteCleanup(stats, cancellationToken, databaseContext, indexContext);
                ExecuteMap(stats, cancellationToken, databaseContext, indexContext);

                tx.Commit();
            }
        }

        private void ExecuteCleanup(IndexingBatchStats stats, CancellationToken token, DocumentsOperationContext databaseContext, TransactionOperationContext indexContext)
        {
            var pageSize = DocumentDatabase.Configuration.Indexing.MaxNumberOfTombstonesToFetch;

            foreach (var collection in Collections)
            {
                if (Log.IsDebugEnabled)
                    Log.Debug($"Executing cleanup for '{Name} ({IndexId})'. Collection: {collection}.");

                long lastMappedEtag;
                long lastTombstoneEtag;
                lastMappedEtag = ReadLastMappedEtag(indexContext.Transaction, collection);
                lastTombstoneEtag = ReadLastTombstoneEtag(indexContext.Transaction, collection);

                if (Log.IsDebugEnabled)
                    Log.Debug($"Executing cleanup for '{Name} ({IndexId})'. LastMappedEtag: {lastMappedEtag}. LastTombstoneEtag: {lastTombstoneEtag}.");

                var lastEtag = lastTombstoneEtag;
                var count = 0;

                var sw = Stopwatch.StartNew();
                using (var indexActions = IndexPersistence.OpenIndexWriter(indexContext.Transaction.InnerTransaction))
                {
                    using (databaseContext.OpenReadTransaction())
                    {
                        foreach (var tombstone in DocumentDatabase.DocumentsStorage.GetTombstonesAfter(databaseContext, collection, lastEtag + 1, 0, pageSize))
                        {
                            token.ThrowIfCancellationRequested();

                            if (Log.IsDebugEnabled)
                                Log.Debug($"Executing cleanup for '{Name} ({IndexId})'. Processing tombstone {tombstone.Key} ({tombstone.Etag}).");

                            count++;
                            lastEtag = tombstone.Etag;

                            if (tombstone.DeletedEtag > lastMappedEtag)
                                continue; // no-op, we have not yet indexed this document

                            indexActions.Delete(tombstone.Key);

                            if (sw.Elapsed > DocumentDatabase.Configuration.Indexing.TombstoneProcessingTimeout.AsTimeSpan)
                            {
                                break;
                            }
                        }
                    }
                }

                if (count == 0)
                    return;

                if (Log.IsDebugEnabled)
                    Log.Debug($"Executing cleanup for '{Name} ({IndexId})'. Processed {count} tombstones in '{collection}' collection in {sw.ElapsedMilliseconds:#,#;;0} ms.");

                if (lastEtag <= lastTombstoneEtag)
                    return;

                WriteLastTombstoneEtag(indexContext.Transaction, collection, lastEtag);

                _mre.Set(); // might be more
            }
        }


        private void ExecuteMap(IndexingBatchStats stats, CancellationToken cancellationToken, DocumentsOperationContext databaseContext, TransactionOperationContext indexContext)
        {
            var pageSize = DocumentDatabase.Configuration.Indexing.MaxNumberOfDocumentsToFetchForMap;

            foreach (var collection in Collections)
            {
                if (Log.IsDebugEnabled)
                    Log.Debug($"Executing map for '{Name} ({IndexId})'. Collection: {collection}.");

                long lastMappedEtag;
                lastMappedEtag = ReadLastMappedEtag(indexContext.Transaction, collection);

                if (Log.IsDebugEnabled)
                    Log.Debug($"Executing map for '{Name} ({IndexId})'. LastMappedEtag: {lastMappedEtag}.");

                var lastEtag = lastMappedEtag;
                var count = 0;

                var sw = Stopwatch.StartNew();
                using (var indexWriter = IndexPersistence.OpenIndexWriter(indexContext.Transaction.InnerTransaction))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    using (databaseContext.OpenReadTransaction())
                    {
                        foreach (var document in DocumentDatabase.DocumentsStorage.GetDocumentsAfter(databaseContext, collection, lastEtag + 1, 0, pageSize))
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            if (Log.IsDebugEnabled)
                                Log.Debug($"Executing map for '{Name} ({IndexId})'. Processing document: {document.Key}.");

                            stats.IndexingAttempts++;

                            count++;
                            lastEtag = document.Etag;

                            try
                            {
                                indexWriter.IndexDocument(document);
                                stats.IndexingSuccesses++;
                                DocumentDatabase.Metrics.IndexedPerSecond.Mark();
                            }
                            catch (Exception e)
                            {
                                stats.IndexingErrors++;
                                if(Log.IsWarnEnabled)
                                    Log.WarnException($"Failed to execute mapping function on '{document.Key}' for '{Name} ({IndexId}'.", e);

                                stats.AddMapError(document.Key, $"Failed to execute mapping function on {document.Key}. Message: {e.Message}");
                            }

                            if (sw.Elapsed > DocumentDatabase.Configuration.Indexing.DocumentProcessingTimeout.AsTimeSpan)
                            {
                                break;
                            }
                        }
                    }

                    if (count == 0)
                        return;

                    if (lastEtag <= lastMappedEtag)
                        return;

                    if (Log.IsDebugEnabled)
                        Log.Debug($"Executing map for '{Name} ({IndexId})'. Processed {count} documents in '{collection}' collection in {sw.ElapsedMilliseconds:#,#;;0} ms.");

                    WriteLastMappedEtag(indexContext.Transaction, collection, lastEtag);
                }

                _mre.Set(); // might be more
            }
        }
    }
}