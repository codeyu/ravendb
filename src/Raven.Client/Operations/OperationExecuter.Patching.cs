﻿using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Raven.Client.Commands;
using Raven.Client.Operations.Databases.Documents;
using Raven.Client.Util;
using Sparrow.Json;

namespace Raven.Client.Operations
{
    public partial class OperationExecuter
    {
        public PatchStatus Send(PatchOperation operation)
        {
            return AsyncHelpers.RunSync(() => SendAsync(operation));
        }

        public async Task<PatchStatus> SendAsync(PatchOperation operation, CancellationToken token = default(CancellationToken))
        {
            JsonOperationContext context;
            using (GetContext(out context))
            {
                var command = operation.GetCommand(_store.Conventions, context);

                await _requestExecuter.ExecuteAsync(command, context, token).ConfigureAwait(false);

                if (command.StatusCode == HttpStatusCode.NotModified)
                    return PatchStatus.NotModified;

                if (command.StatusCode == HttpStatusCode.NotFound)
                    return PatchStatus.DocumentDoesNotExist;

                return command.Result.Status;
            }
        }

        public PatchOperation.Result<TEntity> Send<TEntity>(PatchOperation operation)
        {
            return AsyncHelpers.RunSync(() => SendAsync<TEntity>(operation));
        }

        public async Task<PatchOperation.Result<TEntity>> SendAsync<TEntity>(PatchOperation operation, CancellationToken token = default(CancellationToken))
        {
            JsonOperationContext context;
            using (GetContext(out context))
            {
                var command = operation.GetCommand(_store.Conventions, context);

                await _requestExecuter.ExecuteAsync(command, context, token).ConfigureAwait(false);

                var result = new PatchOperation.Result<TEntity>();

                if (command.StatusCode == HttpStatusCode.NotModified)
                {
                    result.Status = PatchStatus.NotModified;
                    return result;
                }

                if (command.StatusCode == HttpStatusCode.NotFound)
                {
                    result.Status = PatchStatus.DocumentDoesNotExist;
                    return result;
                }

                result.Status = command.Result.Status;
                result.Document = (TEntity)_store.Conventions.DeserializeEntityFromBlittable(typeof(TEntity), command.Result.ModifiedDocument);
                return result;
            }
        }
    }
}