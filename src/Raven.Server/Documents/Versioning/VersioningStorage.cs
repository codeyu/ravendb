using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Text;
using Raven.Abstractions.Data;
using Raven.Abstractions.Logging;
using Raven.Server.Json;
using Raven.Server.ServerWide.Context;
using Sparrow.Json;
using Voron;
using Voron.Data.Tables;

namespace Raven.Server.Documents.Versioning
{
    public unsafe class VersioningStorage : IDisposable
    {
        private readonly ILog Log = LogManager.GetLogger(typeof(VersioningStorage));

        private readonly DocumentDatabase _database;
        private readonly TableSchema _docsSchema = new TableSchema();

        private readonly VersioningConfiguration _versioningConfiguration;

        private const string VersioningRevisions = "VersioningRevisions";
        private const string VersioningRevisionsCount = "VersioningRevisionsCount";

        private readonly VersioningConfigurationCollection _emptyConfiguration = new VersioningConfigurationCollection();

        public VersioningStorage(DocumentDatabase database, VersioningConfiguration versioningConfiguration)
        {
            _database = database;
            _versioningConfiguration = versioningConfiguration;

            // The documents schema is as follows
            // 5 fields (lowered key, recored separator, etag, lazy string key, document)
            // format of lazy string key is detailed in GetLowerKeySliceAndStorageKey
            _docsSchema.DefineIndex("KeyAndEtag", new TableSchema.SchemaIndexDef
            {
                StartIndex = 0,
                Count = 3,
            });

            // TODO: Move code to bundle initialize event
            using (var tx = database.DocumentsStorage.Environment.WriteTransaction())
            {
                tx.CreateTree(VersioningRevisions);
                _docsSchema.Create(tx, VersioningRevisions);

                tx.CreateTree(VersioningRevisionsCount);

                tx.Commit();
            }
        }

        public static VersioningStorage LoadConfigurations(DocumentDatabase database)
        {
            DocumentsOperationContext context;
            using (database.DocumentsStorage.ContextPool.AllocateOperationContext(out context))
            {
                context.OpenReadTransaction();

                var configuration = database.DocumentsStorage.Get(context, Constants.Versioning.RavenVersioningConfiguration);
                if (configuration == null)
                    return null;

                var versioningConfiguration = JsonDeserialization.VersioningConfiguration(configuration.Data);
                return new VersioningStorage(database, versioningConfiguration);
            }
        }

        public void Dispose()
        {
        }

        private VersioningConfigurationCollection GetVersioningConfiguration(string collectionName)
        {
            VersioningConfigurationCollection configuration;
            if (_versioningConfiguration.Collections != null && _versioningConfiguration.Collections.TryGetValue(collectionName, out configuration))
            {
                return configuration;
            }

            if (_versioningConfiguration.Default != null)
            {
                return _versioningConfiguration.Default;
            }

            return _emptyConfiguration;
        }

        public void PutVersion(DocumentsOperationContext context, string collectionName, string key, long newEtagBigEndian,
            BlittableJsonReaderObject document, bool isSystemDocument)
        {
            if (isSystemDocument)
                return;

            var enableVersioning = false;
            BlittableJsonReaderObject metadata;
            if (document.TryGet(Constants.Metadata, out metadata))
            {
                if (metadata.TryGet(Constants.Versioning.RavenEnableVersioning, out enableVersioning))
                {
                    metadata.Modifications.Remove(Constants.Versioning.RavenEnableVersioning);
                }

                bool disableVersioning;
                if (metadata.TryGet(Constants.Versioning.RavenDisableVersioning, out disableVersioning))
                {
                    metadata.Modifications.Remove(Constants.Versioning.RavenDisableVersioning);
                    if (disableVersioning)
                        return;
                }
            }

            var configuration = GetVersioningConfiguration(collectionName);
            if (enableVersioning == false && configuration.Active == false)
                return;

            var table = new Table(_docsSchema, VersioningRevisions, context.Transaction.InnerTransaction);
            var revisionsCount = IncrementCountOfRevisions(context, key, 1);
            DeleteOldRevisions(context, table, key, configuration.MaxRevisions, revisionsCount);

            byte* lowerKey;
            int lowerSize;
            byte* keyPtr;
            int keySize;
            DocumentsStorage.GetLowerKeySliceAndStorageKey(context, key, out lowerKey, out lowerSize, out keyPtr, out keySize);

            byte recordSeperator = 30;

            var tbv = new TableValueBuilder
            {
                {lowerKey, lowerSize},
                {&recordSeperator, sizeof(char)},
                {(byte*)&newEtagBigEndian, sizeof(long)},
                {keyPtr, keySize},
                {document.BasePointer, document.Size}
            };

            table.Insert(tbv);
        }

        private void DeleteOldRevisions(DocumentsOperationContext context, Table table, string key, long? maxRevisions, long revisionsCount)
        {
            if (maxRevisions.HasValue == false || maxRevisions.Value == int.MaxValue)
                return;

            var numberOfRevisionsToDelete = revisionsCount - maxRevisions.Value;
            if (numberOfRevisionsToDelete <= 0)
                return;

            var prefixSlice = GetSliceFromKey(context, key);
            var deletedRevisionsCount = table.DeleteForwardFrom(_docsSchema.Indexes["KeyAndEtag"], prefixSlice, numberOfRevisionsToDelete);
            Debug.Assert(numberOfRevisionsToDelete == deletedRevisionsCount);
            IncrementCountOfRevisions(context, key, -deletedRevisionsCount);
        }

        private long IncrementCountOfRevisions(DocumentsOperationContext context, string key, long delta)
        {
            var numbers = context.Transaction.InnerTransaction.ReadTree(VersioningRevisionsCount);
            return numbers.Increment(key, delta);
        }

        private void DeleteCountOfRevisions(DocumentsOperationContext context, string key)
        {
            var numbers = context.Transaction.InnerTransaction.ReadTree(VersioningRevisionsCount);
            numbers.Delete(key);
        }

        public void Delete(DocumentsOperationContext context, string collectionName, string key, Document document, bool isSystemDocument)
        {
            if (isSystemDocument)
                return;

            var configuration = GetVersioningConfiguration(collectionName);
            if (configuration.Active == false)
                return;

            if (configuration.PurgeOnDelete == false)
                return;

            var table = new Table(_docsSchema, VersioningRevisions, context.Transaction.InnerTransaction);
            var prefixSlice = GetSliceFromKey(context, key);
            table.SeekForwardFrom(_docsSchema.Indexes["KeyAndEtag"], prefixSlice, startsWith: true);
            // todo: delete
        }

        public IEnumerable<Document> GetRevisions(DocumentsOperationContext context, string key, int start, int take)
        {
            var table = new Table(_docsSchema, VersioningRevisions, context.Transaction.InnerTransaction);

            var prefixSlice = GetSliceFromKey(context, key);
            // ReSharper disable once LoopCanBeConvertedToQuery
            foreach (var sr in table.SeekForwardFrom(_docsSchema.Indexes["KeyAndEtag"], prefixSlice, startsWith: true))
            {
                foreach (var tvr in sr.Results)
                {
                    if (start > 0)
                    {
                        start--;
                        continue;
                    }
                    if (take-- <= 0)
                        yield break;

                    var document = TableValueToDocument(context, tvr);
                    yield return document;
                }
                if (take <= 0)
                    yield break;
            }
        }

        public static Slice GetSliceFromKey(DocumentsOperationContext context, string key)
        {
            var byteCount = Encoding.UTF8.GetMaxByteCount(key.Length);
            if (byteCount > 255)
                throw new ArgumentException(
                    $"Key cannot exceed 255 bytes, but the key was {byteCount} bytes. The invalid key is '{key}'.",
                    nameof(key));

            int size;
            var buffer = context.GetNativeTempBuffer(
                byteCount
                + sizeof(char) * key.Length // for the lower calls
                + sizeof(char) * 2 // for the record separator
                , out size);

            fixed (char* pChars = key)
            {
                var destChars = (char*)buffer;
                for (var i = 0; i < key.Length; i++)
                {
                    destChars[i] = char.ToLowerInvariant(pChars[i]);
                }
                destChars[key.Length] = (char)30;

                var keyBytes = buffer + sizeof(char) + key.Length * sizeof(char);

                size = Encoding.UTF8.GetBytes(destChars, key.Length + 1, keyBytes, byteCount + 1);
                return new Slice(keyBytes, (ushort)size);
            }
        }

        private static Document TableValueToDocument(JsonOperationContext context, TableValueReader tvr)
        {
            var result = new Document
            {
                StorageId = tvr.Id
            };
            int size;
            // See format of the lazy string key in the GetLowerKeySliceAndStorageKey method
            var ptr = tvr.Read(3, out size);
            byte offset;
            size = BlittableJsonReaderBase.ReadVariableSizeInt(ptr, 0, out offset);
            result.Key = new LazyStringValue(null, ptr + offset, size, context);
            ptr = tvr.Read(2, out size);
            result.Etag = IPAddress.NetworkToHostOrder(*(long*)ptr);
            result.Data = new BlittableJsonReaderObject(tvr.Read(4, out size), size, context);

            return result;
        }
    }
}