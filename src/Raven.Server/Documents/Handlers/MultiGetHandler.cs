﻿// -----------------------------------------------------------------------
//  <copyright file="MultiGetHandler.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------

using System.Threading.Tasks;

using Microsoft.AspNet.Http;

using Raven.Client.Data;
using Raven.Server.Routing;
using Raven.Server.Web;
using Sparrow.Json;
using Sparrow.Json.Parsing;

namespace Raven.Server.Documents.Handlers
{
    public class MultiGetHandler : DatabaseRequestHandler
    {
        [RavenAction("/databases/*/multi_get", "POST", "/databases/{databaseName:string}/multi_get?parallel=[yes|no] body{ requests:Raven.Abstractions.Data.GetRequest[] }")]
        public async Task PostMultiGet()
        {
            JsonOperationContext context;
            using (ContextPool.AllocateOperationContext(out context))
            {
                var requests = await context.ParseArrayToMemoryAsync(RequestBodyStream(), "multi_get", BlittableJsonDocumentBuilder.UsageMode.None);

                using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    writer.WriteStartArray();
                    var resultProperty = context.GetLazyStringForFieldWithCaching("Result");
                    var statusProperty = context.GetLazyStringForFieldWithCaching("Status");
                    var headersProperty = context.GetLazyStringForFieldWithCaching("Headers");

                    HttpContext.Response.StatusCode = 200;

                    for (int i = 0; i < requests.Length; i++)
                    {
                        var request = (BlittableJsonReaderObject) requests[i];

                        if (i != 0)
                            writer.WriteComma();
                        writer.WriteStartObject();

                        string method = "GET", url, query;
                        if (request.TryGet(nameof(GetRequest.Url), out url) == false ||
                            request.TryGet(nameof(GetRequest.Query), out query) == false)
                            continue;

                        RouteMatch localMatch;
                        var routeInformation = Server.Router.GetRoute(method, url, out localMatch);
                        if (routeInformation == null)
                        {
                            writer.WritePropertyName(statusProperty);
                            writer.WriteInteger(400);
                            writer.WritePropertyName(resultProperty);
                            context.Write(writer, new DynamicJsonValue
                            {
                                ["Error"] = $"There is no handler for path: {method} {url}{query}"
                            });
                            writer.WriteEndObject();
                            continue;
                        }

                        var requestHandler = routeInformation.GetRequestHandler();
                        writer.WritePropertyName(resultProperty);
                        writer.Flush();

                        HttpContext.Request.QueryString = new QueryString(query);
                        HttpContext.Response.Headers.Clear();
                        HttpContext.Request.Headers.Clear();
                        BlittableJsonReaderObject headers;
                        if (request.TryGet(nameof(GetRequest.Headers), out headers))
                        {
                            foreach (var header in headers.GetPropertyNames())
                            {
                                string value;
                                if (headers.TryGet(header, out value) == false)
                                    continue;

                                if (string.IsNullOrWhiteSpace(value))
                                    continue;

                                HttpContext.Request.Headers.Add(header, value);
                            }
                        }

                        await requestHandler(new RequestHandlerContext
                        {
                            Database = Database,
                            RavenServer = Server,
                            RouteMatch = localMatch,
                            HttpContext = HttpContext,
                            AllowResponseCompression = false
                        });

                        writer.WriteComma();
                        writer.WritePropertyName(statusProperty);
                        writer.WriteInteger(HttpContext.Response.StatusCode);
                        writer.WriteComma();

                        writer.WritePropertyName(headersProperty);
                        writer.WriteStartObject();
                        bool headerStart = true;
                        foreach (var header in HttpContext.Response.Headers)
                        {
                            foreach (var value in header.Value)
                            {
                                if (headerStart == false)
                                    writer.WriteComma();
                                headerStart = false;
                                writer.WritePropertyName(context.GetLazyStringForFieldWithCaching(header.Key));
                                writer.WriteString(context.GetLazyString(value));
                            }
                        }
                        writer.WriteEndObject();

                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
            }
        }
    }
}