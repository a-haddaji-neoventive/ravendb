﻿using System.Threading.Tasks;
using JetBrains.Annotations;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Http;
using Raven.Server.Json;
using Raven.Server.Web;
using Sparrow.Json;

namespace Raven.Server.Documents.Handlers.Processors;

internal abstract class AbstractIndexHandlerProcessorForGetErrors<TRequestHandler, TOperationContext> : AbstractHandlerReadProcessor<IndexErrors[], TRequestHandler, TOperationContext>
    where TRequestHandler : RequestHandler
    where TOperationContext : JsonOperationContext
{
    protected AbstractIndexHandlerProcessorForGetErrors([NotNull] TRequestHandler requestHandler, [NotNull] JsonContextPoolBase<TOperationContext> contextPool) 
        : base(requestHandler, contextPool)
    {
    }

    protected override RavenCommand<IndexErrors[]> CreateCommandForNode(string nodeTag) => new GetIndexErrorsOperation.GetIndexErrorsCommand(GetIndexNames(), nodeTag);

    protected string[] GetIndexNames()
    {
        return RequestHandler.GetStringValuesQueryString("name", required: false);
    }

    protected override async ValueTask WriteResultAsync(IndexErrors[] result)
    {
        using (ContextPool.AllocateOperationContext(out JsonOperationContext context))
        await using (var writer = new AsyncBlittableJsonTextWriter(context, RequestHandler.ResponseBodyStream()))
            writer.WriteIndexErrors(context, result);
    }
}
