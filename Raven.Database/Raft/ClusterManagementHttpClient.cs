﻿// -----------------------------------------------------------------------
//  <copyright file="ClusterManagementHttpClient.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

using Rachis;
using Rachis.Transport;
using Rachis.Utils;

using Raven.Abstractions.Connection;
using Raven.Abstractions.Data;
using Raven.Abstractions.Exceptions;
using Raven.Abstractions.Extensions;
using Raven.Abstractions.OAuth;
using Raven.Database.Raft.Commands;
using Raven.Database.Raft.Dto;
using Raven.Database.Raft.Util;
using Raven.Imports.Newtonsoft.Json;
using Raven.Json.Linq;

namespace Raven.Database.Raft
{
	public class ClusterManagementHttpClient
	{
		private readonly RaftEngine raftEngine;

		private NodeConnectionInfo SelfConnection
		{
			get
			{
				return raftEngine.Options.SelfConnection;
			}
		}

		private readonly HttpClient httpClient;

		private readonly ConcurrentDictionary<string, SecuredAuthenticator> _securedAuthenticatorCache = new ConcurrentDictionary<string, SecuredAuthenticator>();

		public ClusterManagementHttpClient(RaftEngine raftEngine)
		{
			this.raftEngine = raftEngine;
			httpClient = new HttpClient();
		}

		private HttpRaftRequest CreateRequest(NodeConnectionInfo node, string url, string method)
		{
			var request = new HttpRaftRequest(node, url, method, info => new Tuple<IDisposable, HttpClient>(null, httpClient))
			{
				UnauthorizedResponseAsyncHandler = HandleUnauthorizedResponseAsync,
				ForbiddenResponseAsyncHandler = HandleForbiddenResponseAsync
			};
			GetAuthenticator(node).ConfigureRequest(this, new WebRequestEventArgs
			{
				Client = httpClient,
				Credentials = new OperationCredentials(node.ApiKey, null) //TODO: fix me
			});
			return request;
		}

		internal async Task<Action<HttpClient>> HandleUnauthorizedResponseAsync(HttpResponseMessage unauthorizedResponse, NodeConnectionInfo nodeConnectionInfo)
		{
			var oauthSource = unauthorizedResponse.Headers.GetFirstValue("OAuth-Source");


			if (string.IsNullOrEmpty(oauthSource))
				oauthSource = nodeConnectionInfo.Uri.AbsoluteUri + "/OAuth/API-Key";

			return await GetAuthenticator(nodeConnectionInfo).DoOAuthRequestAsync(nodeConnectionInfo.Uri.AbsoluteUri, oauthSource, nodeConnectionInfo.ApiKey);
		}

		internal async Task<Action<HttpClient>> HandleForbiddenResponseAsync(HttpResponseMessage forbiddenResponse, NodeConnectionInfo nodeConnection)
		{
			throw new NotImplementedException();
		}

		internal SecuredAuthenticator GetAuthenticator(NodeConnectionInfo info)
		{
			return _securedAuthenticatorCache.GetOrAdd(info.Name, _ => new SecuredAuthenticator());
		}

		public async Task SendJoinServerAsync(NodeConnectionInfo nodeConnectionInfo)
		{
			try
			{
				await raftEngine.AddToClusterAsync(nodeConnectionInfo);
				return;
			}
			catch (NotLeadingException)
			{
			}
			await SendJoinServerInternalAsync(raftEngine.GetLeaderNode(), nodeConnectionInfo);
		}

		public async Task<CanJoinResult> SendJoinServerInternalAsync(NodeConnectionInfo leaderNode, NodeConnectionInfo newNode)
		{
			var url = leaderNode.Uri.AbsoluteUri + "admin/cluster/join";

			using (var request = CreateRequest(leaderNode, url, "POST"))
			{
				var response = await request.WriteAsync(() => new JsonContent(RavenJToken.FromObject(newNode))).ConfigureAwait(false);

				if (response.IsSuccessStatusCode)
					return CanJoinResult.CanJoin;

				switch (response.StatusCode)
				{
					case HttpStatusCode.NotModified:
						return CanJoinResult.AlreadyJoined;
					case HttpStatusCode.NotAcceptable:
						return CanJoinResult.InAnotherCluster;
					default:
						throw await CreateErrorResponseExceptionAsync(response);
				}
			}
			
		}

		public Task SendClusterConfigurationAsync(ClusterConfiguration configuration)
		{
			try
			{
				var command = ClusterConfigurationUpdateCommand.Create(configuration);
				raftEngine.AppendCommand(command);
				return command.Completion.Task;
			}
			catch (NotLeadingException)
			{
				return SendClusterConfigurationInternalAsync(raftEngine.GetLeaderNode(), configuration);
			}
		}

		public Task SendDatabaseUpdateAsync(string databaseName, DatabaseDocument document)
		{
			try
			{
				var command = DatabaseUpdateCommand.Create(databaseName, document);
				raftEngine.AppendCommand(command);
				return command.Completion.Task;
			}
			catch (NotLeadingException)
			{
				return SendDatabaseUpdateInternalAsync(raftEngine.GetLeaderNode(), databaseName, document);
			}
		}

		public Task SendDatabaseDeleteAsync(string databaseName, bool hardDelete)
		{
			try
			{
				var command = DatabaseDeletedCommand.Create(databaseName, hardDelete);
				raftEngine.AppendCommand(command);
				return command.Completion.Task;
			}
			catch (NotLeadingException)
			{
				return SendDatabaseDeleteInternalAsync(raftEngine.GetLeaderNode(), databaseName, hardDelete);
			}
		}

		private async Task SendDatabaseDeleteInternalAsync(NodeConnectionInfo node, string databaseName, bool hardDelete)
		{
			var url = node.Uri.AbsoluteUri + "admin/cluster/commands/cluster/database/" + Uri.EscapeDataString(databaseName) + "?hardDelete=" + hardDelete;
			using (var request = CreateRequest(node, url, "DELETE"))
			{
				var response = await request.ExecuteAsync().ConfigureAwait(false);
				if (response.IsSuccessStatusCode)
					return;

				throw await CreateErrorResponseExceptionAsync(response);
			}
		}

		private Task SendClusterConfigurationInternalAsync(NodeConnectionInfo leaderNode, ClusterConfiguration configuration)
		{
			return PutAsync(leaderNode, "admin/cluster/commands/cluster/configuration", configuration);
		}

		private Task SendDatabaseUpdateInternalAsync(NodeConnectionInfo leaderNode, string databaseName, DatabaseDocument document)
		{
			return PutAsync(leaderNode, "admin/cluster/commands/cluster/database/" + Uri.EscapeDataString(databaseName), document);
		}

		private async Task PutAsync(NodeConnectionInfo node, string action, object content)
		{
			var url = node.Uri.AbsoluteUri + action;
			using (var request = CreateRequest(node, url, "PUT"))
			{
				var response = await request.WriteAsync(() => new JsonContent(RavenJObject.FromObject(content))).ConfigureAwait(false);
				if (response.IsSuccessStatusCode)
					return;

				throw await CreateErrorResponseExceptionAsync(response);
			}
		}

		public async Task<CanJoinResult> SendCanJoinAsync(NodeConnectionInfo nodeConnectionInfo)
		{
			var url = nodeConnectionInfo.Uri.AbsoluteUri + "admin/cluster/canJoin?topologyId=" + raftEngine.CurrentTopology.TopologyId;

			using (var request = CreateRequest(nodeConnectionInfo, url, "GET"))
			{
				var response = await request.ExecuteAsync().ConfigureAwait(false);

				if (response.IsSuccessStatusCode)
					return CanJoinResult.CanJoin;

				switch (response.StatusCode)
				{
					case HttpStatusCode.NotModified:
						return CanJoinResult.AlreadyJoined;
					case HttpStatusCode.NotAcceptable:
						return CanJoinResult.InAnotherCluster;
					default:
						throw await CreateErrorResponseExceptionAsync(response);
				}
			}
		}

		public async Task SendLeaveAsync(NodeConnectionInfo node)
		{
			try
			{
				if (raftEngine.GetLeaderNode() == node)
				{
					await raftEngine.StepDownAsync().ConfigureAwait(false);
					raftEngine.WaitForLeader();
				}
				else
				{
					await raftEngine.RemoveFromClusterAsync(node);
				}
			}
			catch (NotLeadingException)
			{
			}

			await SendLeaveClusterInternalAsync(raftEngine.GetLeaderNode(), node);
		}

		public async Task SendLeaveClusterInternalAsync(NodeConnectionInfo leaderNode, NodeConnectionInfo leavingNode)
		{
			var url = leavingNode.Uri.AbsoluteUri + "admin/cluster/leave?name=" + leavingNode.Name;
			using (var request = CreateRequest(leavingNode, url, "GET"))
			{
				var response = await request.ExecuteAsync().ConfigureAwait(false);
				if (response.IsSuccessStatusCode)
					return;

				throw await CreateErrorResponseExceptionAsync(response);
			}
		}

		private static async Task<ErrorResponseException> CreateErrorResponseExceptionAsync(HttpResponseMessage response)
		{
			using (var sr = new StreamReader(await response.GetResponseStreamWithHttpDecompression().ConfigureAwait(false)))
			{
				var readToEnd = sr.ReadToEnd();

				if (string.IsNullOrWhiteSpace(readToEnd))
					throw ErrorResponseException.FromResponseMessage(response);

				RavenJObject ravenJObject;
				try
				{
					ravenJObject = RavenJObject.Parse(readToEnd);
				}
				catch (Exception e)
				{
					throw new ErrorResponseException(response, readToEnd, e);
				}

				if (response.StatusCode == HttpStatusCode.BadRequest && ravenJObject.ContainsKey("Message"))
				{
					throw new BadRequestException(ravenJObject.Value<string>("Message"), ErrorResponseException.FromResponseMessage(response));
				}

				if (ravenJObject.ContainsKey("Error"))
				{
					var sb = new StringBuilder();
					foreach (var prop in ravenJObject)
					{
						if (prop.Key == "Error")
							continue;

						sb.Append(prop.Key).Append(": ").AppendLine(prop.Value.ToString(Formatting.Indented));
					}

					if (sb.Length > 0)
						sb.AppendLine();
					sb.Append(ravenJObject.Value<string>("Error"));

					throw new ErrorResponseException(response, sb.ToString(), readToEnd);
				}

				throw new ErrorResponseException(response, readToEnd);
			}
		}

		public async Task<Guid> GetDatabaseId(NodeConnectionInfo nodeConnectionInfo)
		{
			var response = await httpClient.GetAsync(nodeConnectionInfo.Uri + "/stats").ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
				throw new InvalidOperationException("Unable to fetch database statictics for: " + nodeConnectionInfo.Uri);

			using (var responseStream = await response.GetResponseStreamWithHttpDecompression().ConfigureAwait(false))
			{
				var json = RavenJToken.TryLoad(responseStream);
				var stats = json.JsonDeserialization<DatabaseStatistics>();
				return stats.DatabaseId;
			}
		}
	}

	public enum CanJoinResult
	{
		CanJoin,

		AlreadyJoined,

		InAnotherCluster
	}
}