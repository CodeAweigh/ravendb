//-----------------------------------------------------------------------
// <copyright file="Expiration.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------
using System;
using System.IO;
using System.Reflection;
using Raven.Client.Document;
using Raven.Server;
using Raven.Tests.Common.Util;

namespace Raven.Tests.Bundles.CompressionAndEncryption
{
    public class CompressionAndEncryption : IDisposable
    {
        protected readonly string path;
        protected readonly DocumentStore documentStore;
        private RavenDbServer ravenDbServer;
        private bool closed = false;
        private Raven.Database.Config.AppSettingsBasedConfiguration settings;

        public CompressionAndEncryption()
        {
            path = Path.GetDirectoryName(Assembly.GetAssembly(typeof(Versioning.Versioning)).CodeBase);
            path = Path.Combine(path, "TestDb").Substring(6);
            Raven.Database.Extensions.IOExtensions.DeleteDirectory(path);
            settings = new Raven.Database.Config.AppSettingsBasedConfiguration
            {
                RunInUnreliableYetFastModeThatIsNotSuitableForProduction = true,
                Core =
                {
                    DataDirectory = path,
                    Port = 8079,
                    _ActiveBundlesString = "Compression;Encryption"
                },
                Encryption =
                {
                    EncryptionKey = "3w17MIVIBLSWZpzH0YarqRlR2+yHiv1Zq3TCWXLEMI8="
                }
            };
            ConfigureServer(settings);
            settings.PostInit();
            ravenDbServer = new RavenDbServer(settings)
            {
                UseEmbeddedHttpServer = true
            };
            ravenDbServer.Initialize();
            documentStore = new DocumentStore
            {
                Url = "http://localhost:8079"
            };
            documentStore.Initialize();
        }

        protected virtual void ConfigureServer(Raven.Database.Config.AppSettingsBasedConfiguration appSettingsBasedConfiguration)
        {
        }

        protected void AssertPlainTextIsNotSavedInDatabase(params string[] plaintext)
        {
            Close();
            EncryptionTestUtil.AssertPlainTextIsNotSavedInAnyFileInPath(plaintext, path, s => true);
        }

        protected void RecycleServer()
        {
            ravenDbServer.Dispose();
            ravenDbServer =  new RavenDbServer(settings)
            {
                UseEmbeddedHttpServer = true
            };
            ravenDbServer.Initialize();
        }

        protected void Close()
        {
            if (closed)
                return;

            documentStore.Dispose();
            ravenDbServer.Dispose();
            closed = true;
        }

        public void Dispose()
        {
            Close();
            Raven.Database.Extensions.IOExtensions.DeleteDirectory(path);
        }
    }
}