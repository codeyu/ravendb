﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Raven.Abstractions.Data;
using Raven.Abstractions.Indexing;
using Raven.Abstractions.Util;
using Raven.Client.Data.Indexes;
using Raven.Client.Indexing;
using Raven.Client.Smuggler;
using Raven.Server.Config.Settings;
using Raven.Server.Documents;
using Raven.Server.Documents.Indexes;
using Raven.Server.ServerWide;
using Raven.Server.ServerWide.Context;
using Raven.Server.Smuggler.Documents.Data;
using Sparrow.Json;
using Sparrow.Json.Parsing;

namespace Raven.Server.Smuggler.Documents
{
    public class DatabaseDestination : ISmugglerDestination
    {
        private readonly DocumentDatabase _database;
        private long _buildVersion;

        public DatabaseDestination(DocumentDatabase database)
        {
            _database = database;
        }

        public IDisposable Initialize(DatabaseSmugglerOptions options, SmugglerResult result, long buildVersion)
        {
            _buildVersion = buildVersion;
            return null;
        }

        public IDocumentActions Documents()
        {
            return new DatabaseDocumentActions(_database, _buildVersion, isRevision: false);
        }

        public IDocumentActions RevisionDocuments()
        {
            return new DatabaseDocumentActions(_database, _buildVersion, isRevision: true);
        }

        public IIdentityActions Identities()
        {
            return new DatabaseIdentityActions(_database);
        }

        public IIndexActions Indexes()
        {
            return new DatabaseIndexActions(_database);
        }

        public ITransformerActions Transformers()
        {
            return new DatabaseTransformerActions(_database);
        }

        private class DatabaseTransformerActions : ITransformerActions
        {
            private readonly DocumentDatabase _database;

            public DatabaseTransformerActions(DocumentDatabase database)
            {
                _database = database;
            }

            public void WriteTransformer(TransformerDefinition transformerDefinition)
            {
                _database.TransformerStore.CreateTransformer(transformerDefinition);
            }

            public void Dispose()
            {
            }
        }

        private class DatabaseIndexActions : IIndexActions
        {
            private readonly DocumentDatabase _database;

            public DatabaseIndexActions(DocumentDatabase database)
            {
                _database = database;
            }

            public void WriteIndex(IndexDefinitionBase indexDefinition, IndexType indexType)
            {
                _database.IndexStore.CreateIndex(indexDefinition);
            }

            public void WriteIndex(IndexDefinition indexDefinition)
            {
                _database.IndexStore.CreateIndex(indexDefinition);
            }

            public void Dispose()
            {
            }
        }

        private class DatabaseDocumentActions : IDocumentActions
        {
            private readonly DocumentDatabase _database;
            private readonly long _buildVersion;
            private readonly bool _isRevision;
            private MergedBatchPutCommand _command;
            private MergedBatchPutCommand _prevCommand;
            private Task _prevCommandTask;

            private readonly Size _enqueueThreshold = new Size(16, SizeUnit.Megabytes);

            public DatabaseDocumentActions(DocumentDatabase database, long buildVersion, bool isRevision)
            {
                _database = database;
                _buildVersion = buildVersion;
                _isRevision = isRevision;
                _command = new MergedBatchPutCommand(database, buildVersion)
                {
                    IsRevision = isRevision
                };
            }

            public void WriteDocument(Document document)
            {
                ModifyDocumentIfNecessary(document);

                _command.Add(document);

                HandleBatchOfDocumentsIfNecessary();
            }

            public JsonOperationContext GetContextForNewDocument()
            {
                _command.Context.CachedProperties.NewDocument();
                return _command.Context;
            }

            public void Dispose()
            {
                FinishBatchOfDocuments();
            }

            private void ModifyDocumentIfNecessary(Document document)
            {
                if (_buildVersion == 40 || _buildVersion >= 40000)
                    return;

                // apply all the metadata conversions here
            }

            private void HandleBatchOfDocumentsIfNecessary()
            {
                if (_command.TotalSize < _enqueueThreshold)
                    return;

                if (_prevCommand != null)
                {
                    using (_prevCommand)
                        AsyncHelpers.RunSync(() => _prevCommandTask);
                }

                _prevCommandTask = _database.TxMerger.Enqueue(_command);
                _prevCommand = _command;
                _command = new MergedBatchPutCommand(_database, _buildVersion)
                {
                    IsRevision = _isRevision
                };
            }

            private void FinishBatchOfDocuments()
            {
                if (_prevCommand != null)
                {
                    using (_prevCommand)
                        AsyncHelpers.RunSync(() => _prevCommandTask);

                    _prevCommand = null;
                }

                if (_command.Documents.Count > 0)
                {
                    using (_command)
                        AsyncHelpers.RunSync(() => _database.TxMerger.Enqueue(_command));
                }

                _command = null;
            }
        }

        private class DatabaseIdentityActions : IIdentityActions
        {
            private readonly DocumentDatabase _database;
            private readonly DocumentsOperationContext _context;
            private readonly Dictionary<string, long> _identities;
            private readonly IDisposable _returnContext;

            public DatabaseIdentityActions(DocumentDatabase database)
            {
                _database = database;
                _returnContext = _database.DocumentsStorage.ContextPool.AllocateOperationContext(out _context);
                _identities = new Dictionary<string, long>();
            }

            public void WriteIdentity(string key, long value)
            {
                _identities[key] = value;
            }

            public void Dispose()
            {
                try
                {
                    if (_identities.Count == 0)
                        return;

                    using (var tx = _context.OpenWriteTransaction())
                    {
                        _database.DocumentsStorage.UpdateIdentities(_context, _identities);

                        tx.Commit();
                    }
                }
                finally
                {
                    _returnContext?.Dispose();
                }
            }
        }

        private class MergedBatchPutCommand : TransactionOperationsMerger.MergedTransactionCommand, IDisposable
        {
            public bool IsRevision;

            private readonly DocumentDatabase _database;
            private readonly long _buildVersion;

            public Size TotalSize = new Size(0, SizeUnit.Bytes);

            public readonly List<Document> Documents = new List<Document>();
            private IDisposable _resetContext;
            private bool _isDisposed;
            private readonly DocumentsOperationContext _context;

            public MergedBatchPutCommand(DocumentDatabase database, long buildVersion)
            {
                _database = database;
                _buildVersion = buildVersion;
                _resetContext = _database.DocumentsStorage.ContextPool.AllocateOperationContext(out _context);
            }

            public JsonOperationContext Context => _context;

            public override void Execute(DocumentsOperationContext context, RavenTransaction tx)
            {
                foreach (var document in Documents)
                {
                    var key = document.Key;

                    BlittableJsonReaderObject metadata;
                    if (document.Data.TryGet(Constants.Metadata.Key, out metadata) == false)
                        throw new InvalidOperationException("A document must have a metadata");

                    if (metadata.Modifications == null)
                        metadata.Modifications = new DynamicJsonValue(metadata);

                    metadata.Modifications.Remove(Constants.Metadata.Id);
                    metadata.Modifications.Remove(Constants.Metadata.Etag);

                    if (IsRevision)
                    {
                        long etag;
                        if (metadata.TryGet(Constants.Metadata.Etag, out etag) == false)
                            throw new InvalidOperationException("Document's metadata must include the document's key.");

                        _database.BundleLoader.VersioningStorage.PutDirect(context, key, etag, document.Data);
                    }
                    else if (_buildVersion < 40000 && key.Contains("/revisions/"))
                    {
                        long etag;
                        if (metadata.TryGet(Constants.Metadata.Etag, out etag) == false)
                            throw new InvalidOperationException("Document's metadata must include the document's key.");

                        var endIndex = key.IndexOf("/revisions/", StringComparison.OrdinalIgnoreCase);
                        var newKey = key.Substring(0, endIndex);

                        _database.BundleLoader.VersioningStorage.PutDirect(context, newKey, etag, document.Data);
                    }
                    else
                    {
                        _database.DocumentsStorage.Put(context, key, null, document.Data);
                    }
                }
            }

            public void Dispose()
            {
                if (_isDisposed)
                    return;

                _isDisposed = true;
                foreach (var doc in Documents)
                    doc.Data.Dispose();

                Documents.Clear();
                _resetContext?.Dispose();
                _resetContext = null;
            }

            public void Add(Document document)
            {
                Documents.Add(document);
                TotalSize.Add(document.Data.Size, SizeUnit.Bytes);
            }
        }
    }
}