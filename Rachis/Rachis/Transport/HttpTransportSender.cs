﻿// -----------------------------------------------------------------------
//  <copyright file="HttpTransportSender.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using NLog;
using Rachis.Messages;
using Raven.Abstractions.Connection;
using Raven.Abstractions.OAuth;
using Raven.Imports.Newtonsoft.Json;

namespace Rachis.Transport
{
	/// <summary>
	/// All requests are fire & forget, with the reply coming in (if at all)
	/// from the resulting thread.
	/// </summary>
	public class HttpTransportSender  : IDisposable
	{
		private readonly HttpTransportBus _bus;

		private readonly ConcurrentDictionary<string, SecuredAuthenticator> _securedAuthenticatorCache = new ConcurrentDictionary<string, SecuredAuthenticator>();

		private readonly ConcurrentDictionary<string, ConcurrentQueue<HttpClient>> _httpClientsCache = new ConcurrentDictionary<string, ConcurrentQueue<HttpClient>>();
		private readonly Logger _log;
		public HttpTransportSender(string name, HttpTransportBus bus)
		{
			_bus = bus;
			_log = LogManager.GetLogger(GetType().Name + "." + name);
		}


		public void Stream(NodeConnectionInfo dest, InstallSnapshotRequest req, Action<Stream> streamWriter)
		{
			LogStatus("install snapshot to " + dest, async () =>
			{
				var requestUri =
					string.Format("raft/installSnapshot?term={0}&=lastIncludedIndex={1}&lastIncludedTerm={2}&from={3}&topology={4}&clusterTopologyId={5}",
						req.Term, req.LastIncludedIndex, req.LastIncludedTerm, req.From, Uri.EscapeDataString(JsonConvert.SerializeObject(req.Topology)), req.ClusterTopologyId);
				using (var request = CreateRequest(dest, requestUri, "POST"))
				{
					var httpResponseMessage = await request.WriteAsync(() => new SnapshotContent(streamWriter));
					var reply = await httpResponseMessage.Content.ReadAsStringAsync();
					if (httpResponseMessage.IsSuccessStatusCode == false && httpResponseMessage.StatusCode != HttpStatusCode.NotAcceptable)
					{
						_log.Warn("Error installing snapshot to {0}. Status: {1}\r\n{2}", dest.Name, httpResponseMessage.StatusCode, reply);
						return;
					}
					var installSnapshotResponse = JsonConvert.DeserializeObject<InstallSnapshotResponse>(reply);
					SendToSelf(installSnapshotResponse);
				}
			});
		}

		public class SnapshotContent : HttpContent
		{
			private readonly Action<Stream> _streamWriter;

			public SnapshotContent(Action<Stream> streamWriter)
			{
				_streamWriter = streamWriter;
			}

			protected override Task SerializeToStreamAsync(Stream stream, TransportContext context)
			{
				_streamWriter(stream);

				return Task.FromResult(1);
			}

			protected override bool TryComputeLength(out long length)
			{
				length = -1;
				return false;
			}
		}

		private HttpRaftRequest CreateRequest(NodeConnectionInfo node, string url, string method)
		{
			var request = new HttpRaftRequest(node, url, method, info =>
			{
				HttpClient client;
				var dispose = (IDisposable) GetConnection(info, out client);
				return Tuple.Create(dispose, client);
			})
			{
				UnauthorizedResponseAsyncHandler = HandleUnauthorizedResponseAsync, 
				ForbiddenResponseAsyncHandler = HandleForbiddenResponseAsync
			};
			GetAuthenticator(node).ConfigureRequest(this, new WebRequestEventArgs
			{
				Client = request.httpClient,
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

		public void Send(NodeConnectionInfo dest, AppendEntriesRequest req)
		{
			LogStatus("append entries to " + dest, async () =>
			{
				var requestUri = string.Format("raft/appendEntries?term={0}&leaderCommit={1}&prevLogTerm={2}&prevLogIndex={3}&entriesCount={4}&from={5}&clusterTopologyId={6}",
					req.Term, req.LeaderCommit, req.PrevLogTerm, req.PrevLogIndex, req.EntriesCount, req.From, req.ClusterTopologyId);
				using (var request = CreateRequest(dest, requestUri, "POST"))
				{
					var httpResponseMessage = await request.WriteAsync(() => new EntriesContent(req.Entries));

					var reply = await httpResponseMessage.Content.ReadAsStringAsync();
					if (httpResponseMessage.IsSuccessStatusCode == false && httpResponseMessage.StatusCode != HttpStatusCode.NotAcceptable)
					{
						_log.Warn("Error appending entries to {0}. Status: {1}\r\n{2}", dest.Name, httpResponseMessage.StatusCode, reply);
						return;
					}
					var appendEntriesResponse = JsonConvert.DeserializeObject<AppendEntriesResponse>(reply);
					SendToSelf(appendEntriesResponse);	
				}
			});
		}

		private class EntriesContent : HttpContent
		{
			private readonly LogEntry[] _entries;

			public EntriesContent(LogEntry[] entries)
			{
				_entries = entries;
			}

			protected override Task SerializeToStreamAsync(Stream stream, TransportContext context)
			{
				foreach (var logEntry in _entries)
				{
					Write7BitEncodedInt64(stream, logEntry.Index);
					Write7BitEncodedInt64(stream, logEntry.Term);
					stream.WriteByte(logEntry.IsTopologyChange == true ? (byte)1 : (byte)0);
					Write7BitEncodedInt64(stream, logEntry.Data.Length);
					stream.Write(logEntry.Data, 0, logEntry.Data.Length);
				}
				return Task.FromResult(1);
			}

			private void Write7BitEncodedInt64(Stream stream, long value)
			{
				var v = (ulong)value;
				while (v >= 128)
				{
					stream.WriteByte((byte)(v | 128));
					v >>= 7;
				}
				stream.WriteByte((byte)(v));
			}

			protected override bool TryComputeLength(out long length)
			{
				length = -1;
				return false;
			}
		}

		public void Send(NodeConnectionInfo dest, CanInstallSnapshotRequest req)
		{
			LogStatus("can install snapshot to " + dest, async () =>
			{
				var requestUri = string.Format("raft/canInstallSnapshot?term={0}&=index{1}&from={2}&clusterTopologyId={3}", req.Term, req.Index,
					req.From, req.ClusterTopologyId);
				using (var request = CreateRequest(dest, requestUri, "GET"))
				{
					var httpResponseMessage = await request.ExecuteAsync();
					var reply = await httpResponseMessage.Content.ReadAsStringAsync();
					if (httpResponseMessage.IsSuccessStatusCode == false && httpResponseMessage.StatusCode != HttpStatusCode.NotAcceptable)
					{
						_log.Warn("Error checking if can install snapshot to {0}. Status: {1}\r\n{2}", dest.Name, httpResponseMessage.StatusCode, reply);
						return;
					}
					var canInstallSnapshotResponse = JsonConvert.DeserializeObject<CanInstallSnapshotResponse>(reply);
					SendToSelf(canInstallSnapshotResponse);
				}
			});
		}

		public void Send(NodeConnectionInfo dest, RequestVoteRequest req)
		{
			LogStatus("request vote from " + dest, async () =>
			{
				var requestUri = string.Format("raft/requestVote?term={0}&lastLogIndex={1}&lastLogTerm={2}&trialOnly={3}&forcedElection={4}&from={5}&clusterTopologyId={6}", 
					req.Term, req.LastLogIndex, req.LastLogTerm, req.TrialOnly, req.ForcedElection, req.From, req.ClusterTopologyId);
				using (var request = CreateRequest(dest, requestUri, "GET"))
				{
					var httpResponseMessage = await request.ExecuteAsync();
					var reply = await httpResponseMessage.Content.ReadAsStringAsync();
					if (httpResponseMessage.IsSuccessStatusCode == false && httpResponseMessage.StatusCode != HttpStatusCode.NotAcceptable)
					{
						_log.Warn("Error requesting vote from {0}. Status: {1}\r\n{2}", dest.Name, httpResponseMessage.StatusCode, reply);
						return;
					}
					var requestVoteResponse = JsonConvert.DeserializeObject<RequestVoteResponse>(reply);
					SendToSelf(requestVoteResponse);
				}
			});
		}

		private void SendToSelf(object o)
		{
			_bus.Publish(o, source: null);
		}

		public void Send(NodeConnectionInfo dest, TimeoutNowRequest req)
		{
			LogStatus("timeout to " + dest, async () =>
			{
				var requestUri = string.Format("raft/timeoutNow?term={0}&from={1}&clusterTopologyId={2}", req.Term, req.From, req.ClusterTopologyId);
				using (var request = CreateRequest(dest, requestUri, "GET"))
				{
					var message = await request.ExecuteAsync();
					var reply = await message.Content.ReadAsStringAsync();
					if (message.IsSuccessStatusCode == false)
					{
						_log.Warn("Error appending entries to {0}. Status: {1}\r\n{2}", dest.Name, message.StatusCode, message, reply);
						return;
					}
					SendToSelf(new NothingToDo());
				}
			});
		}

		public void Send(NodeConnectionInfo dest, DisconnectedFromCluster req)
		{
			LogStatus("disconnect " + dest, async () =>
			{
				var requestUri = string.Format("raft/disconnectFromCluster?term={0}&from={1}&clusterTopologyId={2}", req.Term, req.From, req.ClusterTopologyId);
				using (var request = CreateRequest(dest, requestUri, "GET"))
				{
					var message = await request.ExecuteAsync();
					var reply = await message.Content.ReadAsStringAsync();
					if (message.IsSuccessStatusCode == false)
					{
						_log.Warn("Error sending disconnecton notification to {0}. Status: {1}\r\n{2}", dest.Name, message.StatusCode, message, reply);
						return;
					}
					SendToSelf(new NothingToDo());
				}
			});
		}

		private ConcurrentDictionary<Task, object> _runningOps = new ConcurrentDictionary<Task, object>();

		private void LogStatus(string details, Func<Task> operation)
		{
			var op = operation();
			_runningOps.TryAdd(op, op);
			op
				.ContinueWith(task =>
				{
					object value;
					_runningOps.TryRemove(op, out value);
					if (task.Exception != null)
					{
						_log.Warn("Failed to send " + details + " " + InnerMostMessage(task.Exception), task.Exception);
						return;
					}
					_log.Info("Sent {0}", details);
				});
		}

		private string InnerMostMessage(Exception exception)
		{
			if (exception.InnerException == null)
				return exception.Message;
			return InnerMostMessage(exception.InnerException);
		}


		public void Dispose()
		{
			foreach (var q in _httpClientsCache.Select(x=>x.Value))
			{
				HttpClient result;
				while (q.TryDequeue(out result))
				{
					result.Dispose();
				}
			}
			_httpClientsCache.Clear();

			_securedAuthenticatorCache.Clear();

			var array = _runningOps.Keys.ToArray();
			_runningOps.Clear();
			try
			{
				Task.WaitAll(array);
			}
			catch (OperationCanceledException)
			{
				// nothing to do here
			}
			catch (AggregateException e)
			{
				if (e.InnerException is OperationCanceledException == false)
					throw;
				// nothing to do here
			}
		}


		internal SecuredAuthenticator GetAuthenticator(NodeConnectionInfo info)
		{
			return _securedAuthenticatorCache.GetOrAdd(info.Name, _ => new SecuredAuthenticator());
		}

		internal ReturnToQueue GetConnection(NodeConnectionInfo info, out HttpClient result)
		{
			var connectionQueue = _httpClientsCache.GetOrAdd(info.Name, _ => new ConcurrentQueue<HttpClient>());

			if (connectionQueue.TryDequeue(out result) == false)
			{
				result = new HttpClient
				{
					BaseAddress = info.Uri
				};
			}

			return new ReturnToQueue(result, connectionQueue);
		}

		internal struct ReturnToQueue : IDisposable
		{
			private readonly HttpClient client;
			private readonly ConcurrentQueue<HttpClient> queue;

			public ReturnToQueue(HttpClient client, ConcurrentQueue<HttpClient> queue)
			{
				this.client = client;
				this.queue = queue;
			}

			public void Dispose()
			{
				queue.Enqueue(client);
			}
		}

	
	}
}