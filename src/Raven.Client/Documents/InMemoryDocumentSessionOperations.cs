//-----------------------------------------------------------------------
// <copyright file="InMemoryDocumentSessionOperations.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using System.IO;
using System.Linq;
using Raven.Client.Document.Batches;
using System.Threading;
using System.Threading.Tasks;
using Raven.Abstractions.Extensions;
using Raven.Abstractions.Util;
using Raven.Abstractions.Data;
using Raven.Abstractions.Logging;
using Raven.Client.Data;
using Raven.Client.Document;
using Raven.Client.Documents.Options;
using Raven.Client.Exceptions;
using Raven.Client.Http;
using Raven.Client.Util;
using Raven.Imports.Newtonsoft.Json.Linq;
using Raven.Imports.Newtonsoft.Json.Utilities;
using Raven.Json.Linq;
using Sparrow.Json;
using Sparrow.Json.Parsing;

namespace Raven.Client.Documents
{
    /// <summary>
    /// Abstract implementation for in memory session operations
    /// </summary>
    public abstract class InMemoryDocumentSessionOperations : IDisposable
    {
        public readonly RequestExecuter RequestExecuter;
        private readonly IDisposable _releaseOperationContext;
        public readonly JsonOperationContext Context;

        private static readonly ILog log = LogManager.GetLogger(typeof(InMemoryDocumentSessionOperations));

        protected readonly List<ILazyOperation> pendingLazyOperations = new List<ILazyOperation>();
        protected readonly Dictionary<ILazyOperation, Action<object>> onEvaluateLazy = new Dictionary<ILazyOperation, Action<object>>();

        private static int _instancesCounter;
        private readonly int _hash = Interlocked.Increment(ref _instancesCounter);

        protected bool GenerateDocumentKeysOnStore = true;

        private BatchOptions saveChangesOptions;
        /// <summary>
        /// The session id 
        /// </summary>
        public Guid Id { get; private set; }

        /// <summary>
        /// The entities waiting to be deleted
        /// </summary>
        protected readonly HashSet<object> DeletedEntities = new HashSet<object>(ObjectReferenceEqualityComparer<object>.Default);

        /// <summary>
        /// Entities whose id we already know do not exists, because they are a missing include, or a missing load, etc.
        /// </summary>
        protected readonly HashSet<string> KnownMissingIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private Dictionary<string, object> externalState;

        public IDictionary<string, object> ExternalState => externalState ?? (externalState = new Dictionary<string, object>());

        /// <summary>
        /// Translate between a key and its associated entity
        /// </summary>
        internal readonly Dictionary<string, DocumentInfo> DocumentsById = new Dictionary<string, DocumentInfo>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Translate between a key and its associated entity
        /// </summary>
        internal readonly Dictionary<string, DocumentInfo> includedDocumentsByKey = new Dictionary<string, DocumentInfo>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// hold the data required to manage the data for RavenDB's Unit of Work
        /// </summary>
        protected internal readonly Dictionary<object, DocumentInfo> DocumentsByEntity = new Dictionary<object, DocumentInfo>(ObjectReferenceEqualityComparer<object>.Default);

        protected readonly string databaseName;
        private readonly DocumentStoreBase documentStore;

        public string DatabaseName => databaseName;

        /// <summary>
        /// all the listeners for this session
        /// </summary>
        protected readonly DocumentSessionListeners theListeners;

        /// <summary>
        /// all the listeners for this session
        /// </summary>
        public DocumentSessionListeners Listeners => theListeners;

        ///<summary>
        /// The document store associated with this session
        ///</summary>
        public IDocumentStore DocumentStore => documentStore;


        /// <summary>
        /// Gets the number of requests for this session
        /// </summary>
        /// <value></value>
        public int NumberOfRequests { get; private set; }

        /// <summary>
        /// Gets the number of entities held in memory to manage Unit of Work
        /// </summary>
        public int NumberOfEntitiesInUnitOfWork => DocumentsByEntity.Count;

        /// <summary>
        /// Initializes a new instance of the <see cref="InMemoryDocumentSessionOperations"/> class.
        /// </summary>
        protected InMemoryDocumentSessionOperations(
            string databaseName,
            DocumentStoreBase documentStore,
            DocumentSessionListeners listeners,
            RequestExecuter requestExecuter,
            Guid id)
        {
            Id = id;
            this.databaseName = databaseName;
            this.documentStore = documentStore;
            this.theListeners = listeners;
            RequestExecuter = requestExecuter;
            _releaseOperationContext = requestExecuter.ContextPool.AllocateOperationContext(out Context);
            UseOptimisticConcurrency = documentStore.Conventions.DefaultUseOptimisticConcurrency;
            MaxNumberOfRequestsPerSession = documentStore.Conventions.MaxNumberOfRequestsPerSession;
            GenerateEntityIdOnTheClient = new GenerateEntityIdOnTheClient(documentStore.Conventions, GenerateKey);
            EntityToBlittable = new EntityToBlittable(this);
        }

        /// <summary>
        /// Gets the store identifier for this session.
        /// The store identifier is the identifier for the particular RavenDB instance.
        /// </summary>
        /// <value>The store identifier.</value>
        public string StoreIdentifier => documentStore.Identifier + ";" + DatabaseName;

        /// <summary>
        /// Gets the conventions used by this session
        /// </summary>
        /// <value>The conventions.</value>
        /// <remarks>
        /// This instance is shared among all sessions, changes to the <see cref="DocumentConvention"/> should be done
        /// via the <see cref="IDocumentStore"/> instance, not on a single session.
        /// </remarks>
        public DocumentConvention Conventions => DocumentStore.Conventions;


        /// <summary>
        /// Gets or sets the max number of requests per session.
        /// If the <see cref="NumberOfRequests"/> rise above <see cref="MaxNumberOfRequestsPerSession"/>, an exception will be thrown.
        /// </summary>
        /// <value>The max number of requests per session.</value>
        public int MaxNumberOfRequestsPerSession { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the session should use optimistic concurrency.
        /// When set to <c>true</c>, a check is made so that a change made behind the session back would fail
        /// and raise <see cref="ConcurrencyException"/>.
        /// </summary>
        /// <value></value>
        public bool UseOptimisticConcurrency { get; set; }

        /// <summary>
        /// Gets the ETag for the specified entity.
        /// If the entity is transient, it will load the etag from the store
        /// and associate the current state of the entity with the etag from the server.
        /// </summary>
        /// <param name="instance">The instance.</param>
        /// <returns></returns>
        public long? GetEtagFor<T>(T instance)
        {
            return GetDocumentMetadata(instance).ETag;
        }

        //TODO - Metadata WIP
        private readonly List<object> _entitiesWithMetadataInstance = new List<object>();

        /// <summary>
        /// Gets the metadata for the specified entity.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="instance">The instance.</param>
        /// <returns></returns>
        public DynamicJsonValue GetMetadataFor<T>(T instance)
        {
            //TODO - Metadata WIP
            var documentInfo = GetDocumentMetadata(instance);
            var metadataAsBlittable = documentInfo.Metadata;
            var indexes = metadataAsBlittable.GetPropertiesByInsertionOrder();
            var metadata = new DynamicJsonValue(metadataAsBlittable);
            foreach (var index in indexes)
            {
                var prop = metadataAsBlittable.GetPropertyByIndex(index);
                metadata[prop.Item1] = prop.Item2;
            }
            _entitiesWithMetadataInstance.Add(documentInfo.Entity);
            documentInfo.MetadataInstance = metadata;
            return metadata;
        }

        private DocumentInfo GetDocumentMetadata<T>(T instance)
        {
            DocumentInfo value;
            if (DocumentsByEntity.TryGetValue(instance, out value) == false)
            {
                string id;
                if (GenerateEntityIdOnTheClient.TryGetIdFromInstance(instance, out id)
                    || (instance is IDynamicMetaObjectProvider &&
                        GenerateEntityIdOnTheClient.TryGetIdFromDynamic(instance, out id))
                )
                {
                    AssertNoNonUniqueInstance(instance, id);

                    var jsonDocument = GetJsonDocument(id);
                    value = GetDocumentMetadataValue(instance, id, jsonDocument);
                }
                else
                {
                    throw new InvalidOperationException("Could not find the document key for " + instance);
                }
            }
            return value;
        }

        /// <summary>
        /// Get the json document by key from the store
        /// </summary>
        protected abstract JsonDocument GetJsonDocument(string documentKey);

        protected DocumentInfo GetDocumentMetadataValue<T>(T instance, string id, JsonDocument jsonDocument)
        {
            throw new NotImplementedException();
            //EntitiesById[id] = instance;
            //return DocumentsAndMetadata[instance] = new DocumentInfo
            //{
            //    ETag = UseOptimisticConcurrency ? (long?) 0 : null,
            //    Id = id,
            //    OriginalMetadata = jsonDocument.Metadata,
            //    Metadata = (RavenJObject) jsonDocument.Metadata.CloneToken(),
            //    OriginalValue = new RavenJObject()
            //};
        }


        /// <summary>
        /// Returns whatever a document with the specified id is loaded in the 
        /// current session
        /// </summary>
        public bool IsLoaded(string id)
        {
            return IsLoadedOrDeleted(id);
        }

        internal bool IsLoadedOrDeleted(string id)
        {
            DocumentInfo documentInfo;
            return (DocumentsById.TryGetValue(id, out documentInfo) && (documentInfo.Document != null)) || IsDeleted(id) || includedDocumentsByKey.ContainsKey(id);
        }

        /// <summary>
        /// Returns whatever a document with the specified id is deleted 
        /// or known to be missing
        /// </summary>
        public bool IsDeleted(string id)
        {
            return KnownMissingIds.Contains(id);
        }

        /// <summary>
        /// Gets the document id.
        /// </summary>
        /// <param name="instance">The instance.</param>
        /// <returns></returns>
        public string GetDocumentId(object instance)
        {
            if (instance == null)
                return null;
            DocumentInfo value;
            if (DocumentsByEntity.TryGetValue(instance, out value) == false)
                return null;
            return value.Id;
        }

        public void IncrementRequestCount()
        {
            if (++NumberOfRequests > MaxNumberOfRequestsPerSession)
                throw new InvalidOperationException($@"The maximum number of requests ({MaxNumberOfRequestsPerSession}) allowed for this session has been reached.
Raven limits the number of remote calls that a session is allowed to make as an early warning system. Sessions are expected to be short lived, and 
Raven provides facilities like Load(string[] keys) to load multiple documents at once and batch saves (call SaveChanges() only once).
You can increase the limit by setting DocumentConvention.MaxNumberOfRequestsPerSession or MaxNumberOfRequestsPerSession, but it is
advisable that you'll look into reducing the number of remote calls first, since that will speed up your application significantly and result in a 
more responsive application.
");
        }
        
        /// <summary>
        /// Tracks the entity inside the unit of work
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="documentFound">The document found.</param>
        /// <returns></returns>
        public T TrackEntity<T>(DocumentInfo documentFound)
        {
            return (T)TrackEntity(typeof(T), documentFound);
        }

        /// <summary>
        /// Tracks the entity.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="key">The key.</param>
        /// <param name="document">The document.</param>
        /// <param name="metadata">The metadata.</param>'
        /// <param name="noTracking"></param>
        /// <returns></returns>
        public T TrackEntity<T>(string key, BlittableJsonReaderObject document, BlittableJsonReaderObject metadata, bool noTracking)
        {
            var entity = TrackEntity(typeof(T), key, document, metadata, noTracking);
            try
            {
                return (T)entity;
            }
            catch (InvalidCastException e)
            {
                var actual = typeof(T).Name;
                var expected = entity.GetType().Name;
                var message = string.Format("The query results type is '{0}' but you expected to get results of type '{1}'. " +
"If you want to return a projection, you should use .ProjectFromIndexFieldsInto<{1}>() (for Query) or .SelectFields<{1}>() (for DocumentQuery) before calling to .ToList().", expected, actual);
                throw new InvalidOperationException(message, e);
            }
        }
        
        /// <summary>
        /// Tracks the entity inside the unit of work
        /// </summary>
        /// <param name="entityType"></param>
        /// <param name="documentFound">The document found.</param>
        /// <returns></returns>
        public object TrackEntity(Type entityType, DocumentInfo documentFound)
        {
            bool documentDoesNotExist;
            if (documentFound.Metadata.TryGet(Constants.Headers.RavenDocumentDoesNotExists, out documentDoesNotExist))
            {
                if (documentDoesNotExist)
                    return null;
            }
           
            return TrackEntity(entityType, documentFound.Id, documentFound.Document, documentFound.Metadata, noTracking: false);
        }

        /// <summary>
        /// Tracks the entity.
        /// </summary>
        /// <param name="entityType">The entity type.</param>
        /// <param name="key">The key.</param>
        /// <param name="document">The document.</param>
        /// <param name="metadata">The metadata.</param>
        /// <param name="noTracking">Entity tracking is enabled if true, disabled otherwise.</param>
        /// <returns></returns>
        object TrackEntity(Type entityType, string key, BlittableJsonReaderObject document, BlittableJsonReaderObject metadata, bool noTracking)
        {
            if (string.IsNullOrEmpty(key))
            {
                // TODO, iftah, find out in which scenario the key would be empty and handle appropriately
                //return JsonObjectToClrInstancesWithoutTracking(entityType, document);
            }

            DocumentInfo docInfo;
            if (DocumentsById.TryGetValue(key, out docInfo))
            {
                // the local instance may have been changed, we adhere to the current Unit of Work
                // instance, and return that, ignoring anything new.
                if(docInfo.Entity != null)
                    return docInfo.Entity;
            }

            if (includedDocumentsByKey.TryGetValue(key, out docInfo) && docInfo.Entity != null)
            {
                if (noTracking == false)
                {
                    includedDocumentsByKey.Remove(key);
                    DocumentsById[key] = docInfo;
                    DocumentsByEntity[docInfo.Entity] = docInfo;
                }
                return docInfo.Entity;
            }

            var entity = ConvertToEntity(entityType, key, document);

            long etag;
            if (metadata.TryGet(Constants.Metadata.Etag, out etag) == false)
                throw new InvalidOperationException("Document must have an ETag");

            if (noTracking == false)
            {
                var newDocumentInfo = new DocumentInfo
                {
                    Id = key,
                    Document = document,
                    Metadata = metadata,
                    Entity = entity,
                    ETag = etag
                };

                DocumentsById[key] = newDocumentInfo;
                DocumentsByEntity[entity] = newDocumentInfo;
            }

            return entity;
        }

        /// <summary>
        /// Converts the json document to an entity.
        /// </summary>
        /// <param name="entityType"></param>
        /// <param name="id">The id.</param>
        /// <param name="documentFound">The document found.</param>
        /// <returns></returns>
        public object ConvertToEntity(Type entityType, string id, BlittableJsonReaderObject documentFound)
        {
            //TODO: consider removing this function entirely, leaving only the EntityToBlittalbe version
            return EntityToBlittable.ConvertToEntity(entityType, id, documentFound);
        }

        private void RegisterMissingProperties(object o, string key, object value)
        {
            Dictionary<string, JToken> dictionary;
            if (EntityToBlittable.MissingDictionary.TryGetValue(o, out dictionary) == false)
            {
                EntityToBlittable.MissingDictionary[o] = dictionary = new Dictionary<string, JToken>();
            }

            dictionary[key] = ConvertValueToJToken(value);
        }

        private JToken ConvertValueToJToken(object value)
        {
            var jToken = value as JToken;
            if (jToken != null)
                return jToken;

            try
            {
                // convert object value to JToken so it is compatible with dictionary
                // could happen because of primitive types, type name handling and references
                jToken = (value != null) ? JToken.FromObject(value) : JValue.CreateNull();
                return jToken;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("This is a bug. Value should be JToken.", ex);
            }
        }

        /// <summary>
        /// Gets the default value of the specified type.
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        public static object GetDefaultValue(Type type)
        {
            return type.IsValueType() ? Activator.CreateInstance(type) : null;
        }

        /// <summary>
        /// Marks the specified entity for deletion. The entity will be deleted when SaveChanges is called.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="entity">The entity.</param>
        public void Delete<T>(T entity)
        {
            if (ReferenceEquals(entity, null))
                throw new ArgumentNullException("entity");

            DocumentInfo value;
            if (DocumentsByEntity.TryGetValue(entity, out value) == false)
            {
                throw new InvalidOperationException(entity + " is not associated with the session, cannot delete unknown entity instance");
            }
            DeletedEntities.Add(entity);
            includedDocumentsByKey.Remove(value.Id);
            KnownMissingIds.Add(value.Id);
        }

        /// <summary>
        /// Marks the specified entity for deletion. The entity will be deleted when <see cref="IDocumentSession.SaveChanges"/> is called.
        /// WARNING: This method will not call beforeDelete listener!
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="id">The entity.</param>
        public void Delete<T>(ValueType id)
        {
            Delete(Conventions.FindFullDocumentKeyFromNonStringIdentifier(id, typeof(T), false));
        }

        /// <summary>
        /// Marks the specified entity for deletion. The entity will be deleted when <see cref="IDocumentSession.SaveChanges"/> is called.
        /// WARNING: This method will not call beforeDelete listener!
        /// </summary>
        /// <param name="id"></param>
        public void Delete(string id)
        {
            if (id == null) throw new ArgumentNullException("id");
            DocumentInfo documentInfo;
            long? etag = null;
            if (DocumentsById.TryGetValue(id, out documentInfo))
            {
                BlittableJsonReaderObject newObj = EntityToBlittable.ConvertEntityToBlittable(documentInfo.Entity, documentInfo);
                if (documentInfo.Entity != null && EntityChanged(newObj, documentInfo, null))
                {
                    throw new InvalidOperationException(
                        "Can't delete changed entity using identifier. Use Delete<T>(T entity) instead.");
                }
                if (documentInfo.Entity != null)
                {
                    DocumentsByEntity.Remove(documentInfo.Entity);
                }
                DocumentsById.Remove(id);
                etag = documentInfo.ETag;
            }
            KnownMissingIds.Add(id);
            etag = UseOptimisticConcurrency ? etag : null;
            Defer(new DynamicJsonValue()
            {
                [Constants.Command.Key] = id,
                [Constants.Command.Method] = "DELETE",
                [Constants.Command.Document] = null,
                [Constants.Command.Etag] = etag
            });
        }

        internal void EnsureNotReadVetoed(RavenJObject metadata)
        {
            var readVeto = metadata["Raven-Read-Veto"] as RavenJObject;
            if (readVeto == null)
                return;

            var s = readVeto.Value<string>("Reason");
            throw new ReadVetoException(
                "Document could not be read because of a read veto." + Environment.NewLine +
                "The read was vetoed by: " + readVeto.Value<string>("Trigger") + Environment.NewLine +
                "Veto reason: " + s
                );
        }

        /// <summary>
        /// Stores the specified entity in the session. The entity will be saved when SaveChanges is called.
        /// </summary>
        public void Store(object entity)
        {
            string id;
            var hasId = GenerateEntityIdOnTheClient.TryGetIdFromInstance(entity, out id);
            StoreInternal(entity, null, null, forceConcurrencyCheck: hasId == false);
        }

        /// <summary>
        /// Stores the specified entity in the session. The entity will be saved when SaveChanges is called.
        /// </summary>
        public void Store(object entity, long? etag)
        {
            StoreInternal(entity, etag, null, forceConcurrencyCheck: true);
        }

        /// <summary>
        /// Stores the specified entity in the session, explicitly specifying its Id. The entity will be saved when SaveChanges is called.
        /// </summary>
        public void Store(object entity, string id)
        {
            StoreInternal(entity, null, id, forceConcurrencyCheck: false);
        }

        /// <summary>
        /// Stores the specified entity in the session, explicitly specifying its Id. The entity will be saved when SaveChanges is called.
        /// </summary>
        public void Store(object entity, long? etag, string id)
        {
            StoreInternal(entity, etag, id, forceConcurrencyCheck: true);
        }

        private void StoreInternal(object entity, long? etag, string id, bool forceConcurrencyCheck)
        {
            if (null == entity)
                throw new ArgumentNullException("entity");

            DocumentInfo value;
            if (DocumentsByEntity.TryGetValue(entity, out value))
            {
                if (etag != null)
                    value.ETag = etag;
                value.ForceConcurrencyCheck = forceConcurrencyCheck;
                return;
            }

            if (id == null)
            {
                if (GenerateDocumentKeysOnStore)
                {
                    id = GenerateEntityIdOnTheClient.GenerateDocumentKeyForStorage(entity);
                }
                else
                {
                    RememberEntityForDocumentKeyGeneration(entity);
                }
            }
            else
            {
                // Store it back into the Id field so the client has access to to it                    
                GenerateEntityIdOnTheClient.TrySetIdentity(entity, id);
            }

            if (deferedCommands.Any(c => c["Key"].ToString() == id))
                throw new InvalidOperationException("Can't store document, there is a deferred command registered for this document in the session. Document id: " + id);

            if (DeletedEntities.Contains(entity))
                throw new InvalidOperationException("Can't store object, it was already deleted in this session.  Document id: " + id);

            // we make the check here even if we just generated the key
            // users can override the key generation behavior, and we need
            // to detect if they generate duplicates.
            AssertNoNonUniqueInstance(entity, id);

            var tag = documentStore.Conventions.GetDynamicTagName(entity);
            var metadata = new DynamicJsonValue();
            if (tag != null)
                metadata[Constants.Headers.RavenEntityName] = tag;
            if (id != null)
                KnownMissingIds.Remove(id);
            StoreEntityInUnitOfWork(id, entity, etag, metadata, forceConcurrencyCheck);
        }

        public Task StoreAsync(object entity, CancellationToken token = default(CancellationToken))
        {
            string id;
            var hasId = GenerateEntityIdOnTheClient.TryGetIdFromInstance(entity, out id);

            return StoreAsyncInternal(entity, null, null, forceConcurrencyCheck: hasId == false, token: token);
        }

        public Task StoreAsync(object entity, long? etag, CancellationToken token = default(CancellationToken))
        {
            return StoreAsyncInternal(entity, etag, null, forceConcurrencyCheck: true, token: token);
        }

        public Task StoreAsync(object entity, long? etag, string id, CancellationToken token = default(CancellationToken))
        {
            return StoreAsyncInternal(entity, etag, id, forceConcurrencyCheck: true, token: token);
        }

        public Task StoreAsync(object entity, string id, CancellationToken token = default(CancellationToken))
        {
            return StoreAsyncInternal(entity, null, id, forceConcurrencyCheck: false, token: token);
        }

        private async Task StoreAsyncInternal(object entity, long? etag, string id, bool forceConcurrencyCheck, CancellationToken token = default(CancellationToken))
        {
            if (null == entity)
                throw new ArgumentNullException("entity");

            if (id == null)
            {
                id = await GenerateDocumentKeyForStorageAsync(entity).WithCancellation(token).ConfigureAwait(false);
            }

            StoreInternal(entity, etag, id, forceConcurrencyCheck);
        }

        protected abstract string GenerateKey(object entity);

        protected virtual void RememberEntityForDocumentKeyGeneration(object entity)
        {
            throw new NotImplementedException("You cannot set GenerateDocumentKeysOnStore to false without implementing RememberEntityForDocumentKeyGeneration");
        }

        protected internal async Task<string> GenerateDocumentKeyForStorageAsync(object entity)
        {
            if (entity is IDynamicMetaObjectProvider)
            {
                string id;
                if (GenerateEntityIdOnTheClient.TryGetIdFromDynamic(entity, out id))
                    return id;

                var key = await GenerateKeyAsync(entity).ConfigureAwait(false);
                // If we generated a new id, store it back into the Id field so the client has access to to it                    
                if (key != null)
                    GenerateEntityIdOnTheClient.TrySetIdOnDynamic(entity, key);
                return key;
            }

            var result = await GetOrGenerateDocumentKeyAsync(entity).ConfigureAwait(false);
            GenerateEntityIdOnTheClient.TrySetIdentity(entity, result);
            return result;
        }

        protected abstract Task<string> GenerateKeyAsync(object entity);

        protected virtual void StoreEntityInUnitOfWork(string id, object entity, long? etag, DynamicJsonValue metadata, bool forceConcurrencyCheck)
        {
            DeletedEntities.Remove(entity);
            if (id != null)
                KnownMissingIds.Remove(id);

            var documentInfo = new DocumentInfo
            {
                Id = id,
                Metadata = Context.ReadObject(metadata, id),
                ETag = etag,
                ForceConcurrencyCheck = forceConcurrencyCheck,
                Entity = entity,
                IsNewDocument = true,
                Document = null
            };

            DocumentsByEntity.Add(entity, documentInfo);
            if (id != null)
                DocumentsById[id] = documentInfo;
        }

        protected virtual void AssertNoNonUniqueInstance(object entity, string id)
        {
            if (id == null || id.EndsWith("/") || !DocumentsById.ContainsKey(id) || ReferenceEquals(DocumentsById[id].Entity, entity))
                return;

            throw new NonUniqueObjectException("Attempted to associate a different object with id '" + id + "'.");
        }

        protected async Task<string> GetOrGenerateDocumentKeyAsync(object entity)
        {
            string id;
            GenerateEntityIdOnTheClient.TryGetIdFromInstance(entity, out id);

            Task<string> generator =
                id != null
                ? CompletedTask.With(id)
                : GenerateKeyAsync(entity);

            var result = await generator.ConfigureAwait(false);
            if (result != null && result.StartsWith("/"))
                throw new InvalidOperationException("Cannot use value '" + id + "' as a document id because it begins with a '/'");

            return result;
        }

        public SaveChangesData PrepareForSaveChanges()
        {
            var result = new SaveChangesData
            {
                Entities = new List<object>(),
                Commands = new List<DynamicJsonValue>(deferedCommands),
                DeferredCommandsCount = deferedCommands.Count,
                Options = saveChangesOptions
            };
            deferedCommands.Clear();

            //TODO - Metadata WIP
            UpdateMetadata();

            PrepareForEntitiesDeletion(result, null);
            PrepareForEntitiesPuts(result);

            return result;
        }

        private void UpdateMetadata()
        {
            foreach (var entity in _entitiesWithMetadataInstance)
            {
                DocumentInfo documentInfo;
                if (DocumentsByEntity.TryGetValue(entity, out documentInfo))
                {
                    if (documentInfo.Metadata.Modifications == null)
                    {
                        documentInfo.Metadata.Modifications = documentInfo.MetadataInstance;
                        break;
                    }
                    foreach (var prop in documentInfo.MetadataInstance.Properties)
                    {
                        documentInfo.Metadata.Modifications[prop.Item1] = prop.Item2;
                    }
                }
            }
            _entitiesWithMetadataInstance.Clear();
        }

        private void PrepareForEntitiesDeletion(SaveChangesData result, IDictionary<string, DocumentsChanges[]> changes)
        {
            DocumentInfo documentInfo = null;
            var keysToDelete = DeletedEntities.Where(deletedEntity => DocumentsByEntity.TryGetValue(deletedEntity, out documentInfo))
                .Select(deletedEntity => documentInfo.Id)
                .ToList();

            foreach (var key in keysToDelete)
            {
                if (changes != null)
                {
                    var docChanges = new List<DocumentsChanges>() { };
                    var change = new DocumentsChanges()
                    {
                        FieldNewValue = string.Empty,
                        FieldOldValue = string.Empty,
                        Change = DocumentsChanges.ChangeType.DocumentDeleted
                    };

                    docChanges.Add(change);
                    changes[key] = docChanges.ToArray();
                }
                else
                {
                    long? etag = null;
                    if (DocumentsById.TryGetValue(key, out documentInfo))
                    {
                        etag = documentInfo.ETag;
                        if (documentInfo.Entity != null)
                        {
                            foreach (var deleteListener in theListeners.DeleteListeners)
                            {
                                deleteListener.BeforeDelete(key, documentInfo.Entity, documentInfo.Metadata);
                            }

                            DocumentsByEntity.Remove(documentInfo.Entity);
                            result.Entities.Add(documentInfo.Entity);
                        }

                        DocumentsById.Remove(key);
                    }
                    etag = UseOptimisticConcurrency ? etag : null;
                    result.Commands.Add(new DynamicJsonValue()
                    {
                        [Constants.Command.Key] = key,
                        [Constants.Command.Method] = "DELETE",
                        [Constants.Command.Document] = null,
                        [Constants.Command.Etag] = etag
                    });
                    DeletedEntities.Clear();
                }
            }
        }

        private void PrepareForEntitiesPuts(SaveChangesData result)
        {
            foreach (var entity in DocumentsByEntity)
            {
                foreach (var documentStoreListener in theListeners.StoreListeners)
                {
                    documentStoreListener.BeforeStore(entity.Value.Id, entity.Key, entity.Value.Metadata,
                        entity.Value.Document);
                }

                BlittableJsonReaderObject document = null;
                document = EntityToBlittable.ConvertEntityToBlittable(entity.Key, entity.Value);

                if ((entity.Value.IgnoreChanges) || (!EntityChanged(document, entity.Value, null))) continue;

                entity.Value.IsNewDocument = false;
                result.Entities.Add(entity.Key);

                if (entity.Value.Entity != null)
                    DocumentsById.Remove(entity.Value.Id);

                entity.Value.Document = document;
                var etag = UseOptimisticConcurrency || entity.Value.ForceConcurrencyCheck
                          ? (long?)(entity.Value.ETag ?? 0)
                          : null;

                result.Commands.Add(new DynamicJsonValue()
                {
                    ["Key"] = entity.Value.Id,
                    ["Method"] = "PUT",
                    ["Document"] = document,
                    ["Etag"] = etag
                });
            }
        }

        public void MarkReadOnly(object entity)
        {
            GetMetadataFor(entity)[Constants.Headers.RavenReadOnly] = true;
        }

        protected bool EntityChanged(BlittableJsonReaderObject newObj, DocumentInfo documentInfo, IDictionary<string, DocumentsChanges[]> changes)
        {
            // prevent saves of a modified read only entity
            bool readOnly;
            documentInfo.Metadata.TryGet(Constants.Headers.RavenReadOnly, out readOnly);
            BlittableJsonReaderObject newMetadata;
            var newReadOnly = false;
            if (newObj.TryGet(Constants.Metadata.Key, out newMetadata))
                newMetadata.TryGet(Constants.Headers.RavenReadOnly, out newReadOnly);

            if ((readOnly) && (newReadOnly))
                return false;

            var docChanges = new List<DocumentsChanges>() { };

            if (!documentInfo.IsNewDocument && documentInfo.Document != null)
                return CompareBlittable(documentInfo.Id, documentInfo.Document, newObj, changes, docChanges);

            if (changes == null)
                return true;

            NewChange(null, null, null, docChanges, DocumentsChanges.ChangeType.DocumentAdded);
            changes[documentInfo.Id] = docChanges.ToArray();
            return true;
        }

        private const BlittableJsonToken TypesMask =
                BlittableJsonToken.Boolean |
                BlittableJsonToken.Float |
                BlittableJsonToken.Integer |
                BlittableJsonToken.Null |
                BlittableJsonToken.StartArray |
                BlittableJsonToken.StartObject |
                BlittableJsonToken.String |
                BlittableJsonToken.CompressedString;

        private bool CompareBlittable(string id, BlittableJsonReaderObject originalBlittable,
            BlittableJsonReaderObject newBlittable, IDictionary<string, DocumentsChanges[]> changes,
            List<DocumentsChanges> docChanges)
        {
            var newBlittableProps = newBlittable.GetPropertyNames();
            var oldBlittableProps = originalBlittable.GetPropertyNames();
            var newFields = newBlittableProps.Except(oldBlittableProps);
            var removedFields = oldBlittableProps.Except(newBlittableProps);

            var propertiesIds = newBlittable.GetPropertiesByInsertionOrder();

            foreach (var field in removedFields)
            {
                if (changes == null)
                    return true;
                NewChange(field, null, null, docChanges, DocumentsChanges.ChangeType.RemovedField);
            }

            foreach (var propId in propertiesIds)
            {
                var newPropInfo = newBlittable.GetPropertyByIndex(propId);

                //TODO - mybe?
                if (newPropInfo.Item1.Equals(Constants.Headers.RavenLastModified))
                    continue;

                if (newFields.Contains(newPropInfo.Item1))
                {
                    if (changes == null)
                        return true;
                    NewChange(newPropInfo.Item1, newPropInfo.Item2, null, docChanges, DocumentsChanges.ChangeType.NewField);
                    continue;
                }


                var oldPropId = originalBlittable.GetPropertyIndex(newPropInfo.Item1);
                var oldPropInfo = originalBlittable.GetPropertyByIndex(oldPropId);

                //TODO - problem when was null , mybe just remove it
                /*if (newPropInfo.Item3 != oldPropInfo.Item3)
                {
                    if (changes == null)
                        return true;

                    NewFieldTypeChange(newPropInfo.Item3, oldPropInfo.Item3, docChanges, DocumentsChanges.ChangeType.FieldTypeChanged);
                    continue;
                }*/
                switch ((newPropInfo.Item3 & TypesMask))
                {
                    case BlittableJsonToken.Integer:
                    case BlittableJsonToken.Boolean:
                    case BlittableJsonToken.Float:
                    case BlittableJsonToken.CompressedString:
                    case BlittableJsonToken.String:
                        if (newPropInfo.Item2.Equals(oldPropInfo.Item2))
                            break;

                        if (changes == null)
                            return true;
                        NewChange(newPropInfo.Item1, newPropInfo.Item2, oldPropInfo.Item2, docChanges,
                            DocumentsChanges.ChangeType.FieldChanged);
                        break;
                    case BlittableJsonToken.Null:
                        break;
                    case BlittableJsonToken.StartArray:
                        var newArray = newPropInfo.Item2 as BlittableJsonReaderArray;
                        var oldArray = oldPropInfo.Item2 as BlittableJsonReaderArray;

                        if ((newArray == null) || (oldArray == null))
                            throw new InvalidDataException("Invalid blittable");

                        if (!(newArray.Except(oldArray).Any()))
                            break;

                        if (changes == null)
                            return true;
                        NewChange(newPropInfo.Item1, newPropInfo.Item2, oldPropInfo.Item2, docChanges,
                            DocumentsChanges.ChangeType.FieldChanged);
                        break;
                    case BlittableJsonToken.StartObject:
                        {
                            var changed = CompareBlittable(id, oldPropInfo.Item2 as BlittableJsonReaderObject,
                                newPropInfo.Item2 as BlittableJsonReaderObject, changes, docChanges);
                            if (changes == null)
                                return changed;
                            break;
                        }
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            if ((changes == null) || (docChanges.Count <= 0)) return false;

            changes[id] = docChanges.ToArray();
            return true;
        }

        private static void NewChange(string name, object newValue, object oldValue, List<DocumentsChanges> docChanges, DocumentsChanges.ChangeType change)
        {
            docChanges.Add(new DocumentsChanges()
            {
                FieldName = name,
                FieldNewValue = newValue,
                FieldOldValue = oldValue,
                Change = change
            });
        }

        private static void NewFieldTypeChange(BlittableJsonToken newValue, BlittableJsonToken oldValue, List<DocumentsChanges> docChanges, DocumentsChanges.ChangeType change)
        {
            docChanges.Add(new DocumentsChanges()
            {
                FieldNewType = newValue,
                FieldOldType = oldValue,
                Change = change
            });
        }

        public IDictionary<string, DocumentsChanges[]> WhatChanged()
        {
            var changes = new Dictionary<string, DocumentsChanges[]>();
            //TODO - Metadata WIP
            UpdateMetadata();

            PrepareForEntitiesDeletion(null, changes);
            GetAllEntitiesChanges(changes);
            return changes;
        }

        /// <summary>
        /// Gets a value indicating whether any of the entities tracked by the session has changes.
        /// </summary>
        /// <value></value>
        public bool HasChanges
        {
            get
            {
                foreach (var entity in DocumentsByEntity)
                {
                    var document = EntityToBlittable.ConvertEntityToBlittable(entity.Key, entity.Value);
                    //TODO = FIX and remove
                    entity.Value.Metadata.Modifications = null;
                    if (EntityChanged(document, entity.Value, null))
                    {
                        return true;
                    }
                }
                return DeletedEntities.Count > 0;
            }
        }

        /// <summary>
        /// Determines whether the specified entity has changed.
        /// </summary>
        /// <param name="entity">The entity.</param>
        /// <returns>
        /// 	<c>true</c> if the specified entity has changed; otherwise, <c>false</c>.
        /// </returns>
        public bool HasChanged(object entity)
        {
            DocumentInfo documentInfo;
            if (DocumentsByEntity.TryGetValue(entity, out documentInfo) == false)
                return false;
            var document = EntityToBlittable.ConvertEntityToBlittable(entity, documentInfo);
            //TODO = FIX and remove
            documentInfo.Metadata.Modifications = null;
            return EntityChanged(document, documentInfo, null); ;
        }

        public void WaitForReplicationAfterSaveChanges(TimeSpan? timeout = null, bool throwOnTimeout = true, int replicas = 1, bool majority = false)
        {
            var realTimeout = timeout ?? TimeSpan.FromSeconds(15);
            if (saveChangesOptions == null)
                saveChangesOptions = new BatchOptions();
            saveChangesOptions.WaitForReplicas = true;
            saveChangesOptions.Majority = majority;
            saveChangesOptions.NumberOfReplicasToWaitFor = replicas;
            saveChangesOptions.WaitForReplicasTimout = realTimeout;
            saveChangesOptions.ThrowOnTimeoutInWaitForReplicas = throwOnTimeout;
        }

        public void WaitForIndexesAfterSaveChanges(TimeSpan? timeout = null, bool throwOnTimeout = false, string[] indexes = null)
        {
            var realTimeout = timeout ?? TimeSpan.FromSeconds(15);
            if (saveChangesOptions == null)
                saveChangesOptions = new BatchOptions();
            saveChangesOptions.WaitForIndexes = true;
            saveChangesOptions.WaitForIndexesTimeout = realTimeout;
            saveChangesOptions.ThrowOnTimeoutInWaitForIndexes = throwOnTimeout;
            saveChangesOptions.WaitForSpecificIndexes = indexes;
        }

        private void GetAllEntitiesChanges(IDictionary<string, DocumentsChanges[]> changes)
        {
            foreach (var pair in DocumentsById)
            {
                var newObj = EntityToBlittable.ConvertEntityToBlittable(pair.Value.Entity, pair.Value);
                EntityChanged(newObj, pair.Value, changes);
                //TODO = FIX and remove
                pair.Value.Metadata.Modifications = null;
            }
        }

        /// <summary>
        /// Mark the entity as one that should be ignore for change tracking purposes,
        /// it still takes part in the session, but is ignored for SaveChanges.
        /// </summary>
        public void IgnoreChangesFor(object entity)
        {
            GetDocumentMetadata(entity).IgnoreChanges = true;
        }

        /// <summary>
        /// Evicts the specified entity from the session.
        /// Remove the entity from the delete queue and stops tracking changes for this entity.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="entity">The entity.</param>
        public void Evict<T>(T entity)
        {
            DocumentInfo value;
            if (DocumentsByEntity.TryGetValue(entity, out value))
            {
                DocumentsByEntity.Remove(entity);
                DocumentsById.Remove(value.Id);
            }

            DeletedEntities.Remove(entity);
        }

        /// <summary>
        /// Clears this instance.
        /// Remove all entities from the delete queue and stops tracking changes for all entities.
        /// </summary>
        public void Clear()
        {
            DocumentsByEntity.Clear();
            DeletedEntities.Clear();
            DocumentsById.Clear();
            KnownMissingIds.Clear();
        }

        private readonly List<DynamicJsonValue> deferedCommands = new List<DynamicJsonValue>();
        protected string _databaseName;
        public GenerateEntityIdOnTheClient GenerateEntityIdOnTheClient { get; private set; }
        public EntityToBlittable EntityToBlittable { get; private set; }

        /// <summary>
        /// Defer commands to be executed on SaveChanges()
        /// </summary>
        /// <param name="commands">The commands to be executed</param>
        public virtual void Defer(params DynamicJsonValue[] commands)
        {
            // Should we remove Defer?
            // and Patch would send Put and Delete and Patch separatly, like { Delete: [], Put: [], Patch: []}
            deferedCommands.AddRange(commands);
        }

        /// <summary>
        /// Version this entity when it is saved.  Use when Versioning bundle configured to ExcludeUnlessExplicit.
        /// </summary>
        /// <param name="entity">The entity.</param>
        public void ExplicitlyVersion(object entity)
        {
            //TODO - Check (Metadata WIP)
            GetMetadataFor(entity)[Constants.Versioning.RavenEnableVersioning] = true;
            /*var metadata = GetDocumentMetadata(entity).Metadata;
            if (metadata.Modifications == null)
                metadata.Modifications = new DynamicJsonValue();
            metadata.Modifications[Constants.Versioning.RavenEnableVersioning] = true;*/
        }

        private void Dispose(bool isDisposing)
        {
            if (isDisposing)
                GC.SuppressFinalize(this);
            _releaseOperationContext.Dispose();
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public virtual void Dispose()
        {
            Dispose(true);
        }

        ~InMemoryDocumentSessionOperations()
        {
            Dispose(false);

#if DEBUG
            Debug.WriteLine("Disposing a session for finalizer! It should be disposed by calling session.Dispose()!");
#endif
        }

        /// <summary>
        /// Information held about an entity by the session
        /// </summary>
        public class DocumentInfo
        {
            /// <summary>
            /// Gets or sets the id.
            /// </summary>
            /// <value>The id.</value>
            public string Id { get; set; }

            /// <summary>
            /// Gets or sets the ETag.
            /// </summary>
            /// <value>The ETag.</value>
            public long? ETag { get; set; }

            /// <summary>
            /// A concurrency check will be forced on this entity 
            /// even if UseOptimisticConcurrency is set to false
            /// </summary>
            public bool ForceConcurrencyCheck { get; set; }

            /// <summary>
            /// If set to true, the session will ignore this document
            /// when SaveChanges() is called, and won't perform and change tracking
            /// </summary>
            public bool IgnoreChanges { get; set; }

            public BlittableJsonReaderObject Metadata { get; set; }

            public BlittableJsonReaderObject Document { get; set; }

            public DynamicJsonValue MetadataInstance { get; set; }

            public object Entity { get; set; }

            public bool IsNewDocument { get; set; }

            public static DocumentInfo GetNewDocumentInfo(BlittableJsonReaderObject document)
            {
                BlittableJsonReaderObject metadata;
                string id;
                long etag;

                if (document.TryGet(Constants.Metadata.Key, out metadata) == false)
                    throw new InvalidOperationException("Document must have a metadata");
                if (metadata.TryGet(Constants.Metadata.Id, out id) == false)
                    throw new InvalidOperationException("Document must have an id");
                if (metadata.TryGet(Constants.Metadata.Etag, out etag) == false)
                    throw new InvalidOperationException("Document must have an ETag");

                var newDocumentInfo = new DocumentInfo
                {
                    Id = id,
                    Document = document,
                    Metadata = metadata,
                    Entity = null,
                    ETag = etag
                };
                return newDocumentInfo;
            }
        }

        /// <summary>
        /// Data for a batch command to the server
        /// </summary>
        public class SaveChangesData
        {
            public SaveChangesData()
            {
                Commands = new List<DynamicJsonValue>();
                Entities = new List<object>();
            }

            /// <summary>
            /// Gets or sets the commands.
            /// </summary>
            /// <value>The commands.</value>
            public List<DynamicJsonValue> Commands { get; set; }

            public BatchOptions Options { get; set; }

            public int DeferredCommandsCount { get; set; }

            /// <summary>
            /// Gets or sets the entities.
            /// </summary>
            /// <value>The entities.</value>
            public List<object> Entities { get; set; }

        }

        public void RegisterMissing(string id)
        {
            KnownMissingIds.Add(id);
        }
        public void UnregisterMissing(string id)
        {
            KnownMissingIds.Remove(id);
        }

        public void RegisterMissingIncludes(BlittableJsonReaderArray results, ICollection<string> includes)
        {
            if (includes == null || includes.Any() == false)
                return;

            foreach (BlittableJsonReaderObject result in results)
            {
                foreach (var include in includes)
                {
                    IncludesUtil.Include(result, include, id =>
                    {
                        if (id == null)
                            return false;
                        if (IsLoaded(id) == false)
                        {
                            RegisterMissing(id);
                            return false;
                        }
                        return true;
                    });
                }
            }
        }

        public override int GetHashCode()
        {
            return _hash;
        }

        public override bool Equals(object obj)
        {
            return ReferenceEquals(obj, this);
        }

        internal void HandleInternalMetadata(RavenJObject result)
        {
            // Implant a property with "id" value ... if not exists
            var metadata = result.Value<RavenJObject>("@metadata");
            if (metadata == null || string.IsNullOrEmpty(metadata.Value<string>("@id")))
            {
                // if the item has metadata, then nested items will not have it, so we can skip recursing down
                foreach (var nested in result.Select(property => property.Value))
                {
                    var jObject = nested as RavenJObject;
                    if (jObject != null)
                        HandleInternalMetadata(jObject);
                    var jArray = nested as RavenJArray;
                    if (jArray == null)
                        continue;
                    foreach (var item in jArray.OfType<RavenJObject>())
                    {
                        HandleInternalMetadata(item);
                    }
                }
                return;
            }

            var entityName = metadata.Value<string>(Constants.Headers.RavenEntityName);

            var idPropName = Conventions.FindIdentityPropertyNameFromEntityName(entityName);
            if (result.ContainsKey(idPropName))
                return;

            result[idPropName] = new RavenJValue(metadata.Value<string>("@id"));
        }

        public string CreateDynamicIndexName<T>()
        {
            var indexName = "dynamic";
            if (typeof(T).IsEntityType())
            {
                indexName += "/" + Conventions.GetTypeTagName(typeof(T));
            }
            return indexName;
        }


        public bool CheckIfIdAlreadyIncluded(string[] ids, KeyValuePair<string, Type>[] includes)
        {
            return CheckIfIdAlreadyIncluded(ids, includes.Select(x => x.Key));
        }


        public bool CheckIfIdAlreadyIncluded(string[] ids, IEnumerable<string> includes)
        {
            foreach (var id in ids)
            {
                if (KnownMissingIds.Contains(id))
                    continue;

                DocumentInfo documentInfo;

                // Check if document was already loaded, the check if we've received it through include
                if (DocumentsById.TryGetValue(id, out documentInfo) == false && includedDocumentsByKey.TryGetValue(id, out documentInfo) == false)
                    return false;

                if (documentInfo.Entity == null)
                    return false;
                var rawData = DocumentsById[id];

                if (includes == null)
                    continue;

                foreach (var include in includes)
                {
                    var hasAll = true;
                    IncludesUtil.Include(rawData.Document, include, s =>
                    {
                        hasAll &= IsLoaded(s);
                        return true;
                    });
                    if (hasAll == false)
                        return false;
                }
            }
            return true;
        }


        protected static T GetOperationResult<T>(object result)
        {
            if (result == null)
                return default(T);

            if (result is T)
                return (T)result;

            var results = result as T[];
            if (results != null && results.Length > 0)
                return results[0];

            throw new InvalidCastException($"Unable to cast {result.GetType().Name} to {typeof(T).Name}");
        }
    }
}