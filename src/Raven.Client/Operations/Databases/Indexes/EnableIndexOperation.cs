﻿using System;
using System.Net.Http;
using Raven.Client.Commands;
using Raven.Client.Document;
using Raven.Client.Http;
using Sparrow.Json;

namespace Raven.Client.Operations.Databases.Indexes
{
    public class EnableIndexOperation : IAdminOperation
    {
        private readonly string _indexName;

        public EnableIndexOperation(string indexName)
        {
            if (indexName == null)
                throw new ArgumentNullException(nameof(indexName));

            _indexName = indexName;
        }

        public RavenCommand<object> GetCommand(DocumentConventions conventions, JsonOperationContext context)
        {
            return new EnableIndexCommand(_indexName);
        }

        private class EnableIndexCommand : RavenCommand<object>
        {
            private readonly string _indexName;

            public EnableIndexCommand(string indexName)
            {
                if (indexName == null)
                    throw new ArgumentNullException(nameof(indexName));

                _indexName = indexName;
            }

            public override HttpRequestMessage CreateRequest(ServerNode node, out string url)
            {
                url = $"{node.Url}/databases/{node.Database}/admin/indexes/enable?name={Uri.EscapeUriString(_indexName)}";

                return new HttpRequestMessage
                {
                    Method = HttpMethod.Post
                };
            }

            public override void SetResponse(BlittableJsonReaderObject response, bool fromCache)
            {
            }

            public override bool IsReadRequest => false;
        }
    }
}