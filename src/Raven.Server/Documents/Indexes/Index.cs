﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Raven.Abstractions.Data;
using Raven.Server.Documents.Indexes.Persistance.Lucene;
using Raven.Server.Json;
using Raven.Server.ServerWide;

using Voron;
using Voron.Impl;

namespace Raven.Server.Documents.Indexes
{
    public abstract class Index<TIndexDefinition> : Index
        where TIndexDefinition : IndexDefinitionBase
    {
        public new TIndexDefinition Definition => (TIndexDefinition)base.Definition;

        protected Index(int indexId, IndexType type, TIndexDefinition definition)
            : base(indexId, type, definition)
        {
        }
    }

    public abstract class Index
    {
        private static readonly Slice TypeSlice = "Type";

        private static readonly Slice LastMappedEtagSlice = "LastMappedEtag";

        private static readonly Slice LastReducedEtagSlice = "LastReducedEtag";

        protected readonly LuceneIndexPersistance IndexPersistence;

        private readonly object _locker = new object();

        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        private DocumentsStorage _documentsStorage;

        private Task _indexingTask;

        private bool _initialized;

        private UnmanagedBuffersPool _unmanagedBuffersPool;

        private StorageEnvironment _environment;

        private ContextPool _contextPool;

        private bool _disposed;

        protected Index(int indexId, IndexType type, IndexDefinitionBase definition)
        {
            if (indexId <= 0)
                throw new ArgumentException("IndexId must be greater than zero.", nameof(indexId));

            IndexId = indexId;
            Type = type;
            Definition = definition;
            IndexPersistence = new LuceneIndexPersistance();
        }

        public static Index Open(int indexId, string path, DocumentsStorage documentsStorage)
        {
            var options = StorageEnvironmentOptions.ForPath(path);
            try
            {
                options.SchemaVersion = 1;

                var environment = new StorageEnvironment(options);
                using (var tx = environment.ReadTransaction())
                {
                    var statsTree = tx.ReadTree("Stats");
                    var result = statsTree.Read(TypeSlice);
                    if (result == null)
                        throw new InvalidOperationException();

                    var type = (IndexType)result.Reader.ReadLittleEndianInt32();

                    switch (type)
                    {
                        case IndexType.Auto:
                            return AutoIndex.Open(indexId, environment, documentsStorage);
                        default:
                            throw new NotImplementedException();
                    }
                }
            }
            catch (Exception)
            {
                options.Dispose();
                throw;
            }
        }

        public int IndexId { get; }

        public IndexType Type { get; }

        public IndexDefinitionBase Definition { get; }

        public string PublicName => Definition.Name;

        public bool ShouldRun { get; private set; } = true;

        protected void Initialize(DocumentsStorage documentsStorage)
        {
            if (_initialized)
                throw new InvalidOperationException();

            lock (_locker)
            {
                if (_initialized)
                    throw new InvalidOperationException();

                var options = documentsStorage.Configuration.Core.RunInMemory
                    ? StorageEnvironmentOptions.CreateMemoryOnly()
                    : StorageEnvironmentOptions.ForPath(Path.Combine(_documentsStorage.Configuration.Core.IndexStoragePath, IndexId.ToString()));

                options.SchemaVersion = 1;

                try
                {
                    Initialize(new StorageEnvironment(options), documentsStorage);
                }
                catch (Exception)
                {
                    options.Dispose();
                    throw;
                }
            }
        }

        protected unsafe void Initialize(StorageEnvironment environment, DocumentsStorage documentsStorage)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(Index));

            if (_initialized)
                throw new InvalidOperationException();

            lock (_locker)
            {
                if (_initialized) throw new InvalidOperationException();

                try
                {
                    Debug.Assert(Definition != null);

                    _environment = environment;
                    _documentsStorage = documentsStorage;
                    _unmanagedBuffersPool = new UnmanagedBuffersPool($"Indexes//{IndexId}");
                    _contextPool = new ContextPool(_unmanagedBuffersPool, _environment);

                    using (var tx = _environment.WriteTransaction())
                    {
                        var typeInt = (int)Type;

                        var statsTree = tx.CreateTree("Stats");
                        statsTree.Add(TypeSlice, new Slice((byte*)&typeInt, sizeof(int)));

                        tx.Commit();
                    }

                    _initialized = true;
                }
                catch (Exception)
                {
                    Dispose();
                    throw;
                }
            }
        }

        public void Execute(CancellationToken cancellationToken)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(Index));

            if (_initialized == false)
                throw new InvalidOperationException();

            if (_indexingTask != null)
                throw new InvalidOperationException();

            lock (_locker)
            {
                if (_indexingTask != null)
                    throw new InvalidOperationException();

                _indexingTask = Task.Factory.StartNew(() => ExecuteIndexing(cancellationToken), TaskCreationOptions.LongRunning);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(Index));

            lock (_locker)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(Index));

                _disposed = true;

                _cancellationTokenSource.Cancel();

                _indexingTask?.Wait();
                _indexingTask = null;

                _environment?.Dispose();
                _environment = null;

                _unmanagedBuffersPool?.Dispose();
                _unmanagedBuffersPool = null;

                _contextPool?.Dispose();
                _contextPool = null;
            }
        }

        protected string[] Collections
        {
            get
            {
                return Definition.Collections;
            }
        }

        protected abstract bool IsStale(RavenOperationContext databaseContext, RavenOperationContext indexContext, out long lastEtag);

        protected abstract Lucene.Net.Documents.Document ConvertDocument(string collection, Document document);

        public long GetLastMappedEtag()
        {
            RavenOperationContext context;
            using (_contextPool.AllocateOperationContext(out context))
            {
                using (var tx = context.Environment.ReadTransaction())
                {
                    return ReadLastMappedEtag(tx);
                }
            }
        }

        protected long ReadLastMappedEtag(Transaction tx)
        {
            return ReadLastEtag(tx, LastMappedEtagSlice);
        }

        protected long ReadLastReducedEtag(Transaction tx)
        {
            return ReadLastEtag(tx, LastReducedEtagSlice);
        }

        private static long ReadLastEtag(Transaction tx, Slice key)
        {
            var statsTree = tx.CreateTree("Stats");
            var readResult = statsTree.Read(key);
            long lastEtag = 0;
            if (readResult != null)
                lastEtag = readResult.Reader.ReadLittleEndianInt64();

            return lastEtag;
        }

        private void WriteLastMappedEtag(Transaction tx, long etag)
        {
            WriteLastEtag(tx, LastMappedEtagSlice, etag);
        }

        private void WriteLastReducedEtag(Transaction tx, long etag)
        {
            WriteLastEtag(tx, LastReducedEtagSlice, etag);
        }

        private static unsafe void WriteLastEtag(Transaction tx, Slice key, long etag)
        {
            var statsTree = tx.CreateTree("Stats");
            statsTree.Add(key, new Slice((byte*)&etag, sizeof(long)));
        }

        private void ExecuteIndexing(CancellationToken cancellationToken)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellationTokenSource.Token))
            {
                while (ShouldRun)
                {
                    bool foundWork;
                    try
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        foundWork = ExecuteMap(cts.Token);
                    }
                    catch (OutOfMemoryException oome)
                    {
                        foundWork = true;
                        // TODO
                    }
                    catch (AggregateException ae)
                    {
                        foundWork = true;
                        // TODO
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception e)
                    {
                        foundWork = true;
                        // TODO
                    }

                    if (foundWork == false && ShouldRun)
                    {
                        // cleanup tasks here
                    }
                }
            }
        }

        private bool ExecuteMap(CancellationToken cancellationToken)
        {
            RavenOperationContext databaseContext;
            RavenOperationContext indexContext;
            using (_documentsStorage.ContextPool.AllocateOperationContext(out databaseContext))
            using (_contextPool.AllocateOperationContext(out indexContext))
            {
                long lastEtag;
                if (IsStale(databaseContext, indexContext, out lastEtag) == false)
                    return false;

                var foundWork = false;
                foreach (var collection in Collections)
                {
                    var start = 0;
                    const int PageSize = 1024 * 10;

                    while (true)
                    {
                        var count = 0;
                        var indexDocuments = new List<Lucene.Net.Documents.Document>();
                        using (var tx = databaseContext.Environment.ReadTransaction())
                        {
                            databaseContext.Transaction = tx;

                            foreach (var document in _documentsStorage.GetDocumentsAfter(databaseContext, collection, lastEtag, start, PageSize))
                            {
                                cancellationToken.ThrowIfCancellationRequested();

                                indexDocuments.Add(ConvertDocument(collection, document));
                                count++;

                                Debug.Assert(document.Etag > lastEtag);

                                lastEtag = document.Etag;
                            }
                        }

                        foundWork = foundWork || indexDocuments.Count > 0;

                        using (var tx = indexContext.Environment.WriteTransaction())
                        {
                            indexContext.Transaction = tx;

                            IndexPersistence.Write(indexContext, indexDocuments, cancellationToken);
                            WriteLastMappedEtag(tx, lastEtag);

                            tx.Commit();
                        }

                        if (count < PageSize) break;

                        start += PageSize;
                    }
                }

                return foundWork;
            }
        }

        private void ExecuteReduce()
        {
        }

        public QueryResult Query(IndexQuery query)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(Index));

            throw new NotImplementedException();
        }
    }
}