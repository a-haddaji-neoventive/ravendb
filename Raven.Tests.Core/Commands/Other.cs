﻿// -----------------------------------------------------------------------
//  <copyright file="CoreTestServer.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------
using System.Threading.Tasks;
using Raven.Abstractions.Commands;
using Raven.Abstractions.Data;
using Raven.Json.Linq;
using Raven.Tests.Core.Utils.Entities;
using Xunit;

namespace Raven.Tests.Core.Commands
{
	public class Other : RavenCoreTestBase
	{
		[Fact]
		public async Task CanGetBuildNumber()
		{
			using (var store = GetDocumentStore())
			{
				var buildNumber = await store.AsyncDatabaseCommands.GetBuildNumberAsync();

				Assert.NotNull(buildNumber);
			}
		}

		[Fact]
		public async Task CanGetStatistics()
		{
			using (var store = GetDocumentStore())
			{
				var databaseStatistics = await store.AsyncDatabaseCommands.GetStatisticsAsync();

				Assert.NotNull(databaseStatistics);

				Assert.Equal(0, databaseStatistics.CountOfDocuments);
			}
		}

		[Fact]
		public async Task CanGetAListOfDatabasesAsync()
		{
			using (var store = GetDocumentStore())
			{
				var names = await store.AsyncDatabaseCommands.ForSystemDatabase().GetDatabaseNamesAsync(25);
				Assert.Contains(store.DefaultDatabase, names);
			}
		}

        [Fact]
        public void CanSwitchDatabases()
        {
            using (var store1 = GetDocumentStore("store1"))
            using (var store2 = GetDocumentStore("store2"))
            {
                store1.DatabaseCommands.Put(
                    "items/1",
                    null,
                    RavenJObject.FromObject(new
                    {
                        Name = "For store1"
                    }),
                    new RavenJObject());
                store2.DatabaseCommands.Put(
                    "items/2",
                    null,
                    RavenJObject.FromObject(new
                    {
                        Name = "For store2"
                    }),
                    new RavenJObject());

                var doc = store1.DatabaseCommands.ForDatabase("store2").Get("items/2");
                Assert.NotNull(doc);
                Assert.Equal("For store2", doc.DataAsJson.Value<string>("Name"));

                doc = store1.DatabaseCommands.ForDatabase("store1").Get("items/1");
                Assert.NotNull(doc);
                Assert.Equal("For store1", doc.DataAsJson.Value<string>("Name"));

                var docs = store1.DatabaseCommands.ForSystemDatabase().GetDocuments(0, 20);
                Assert.Equal(2, docs.Length);
                Assert.NotNull(docs[0].DataAsJson.Value<RavenJObject>("Settings"));
                Assert.NotNull(docs[1].DataAsJson.Value<RavenJObject>("Settings"));
            }
        }

        [Fact]
        public void CanGetUrlForDocument()
        {
            using (var store = GetDocumentStore())
            {
                store.DatabaseCommands.Put(
                    "items/1",
                    null,
                    RavenJObject.FromObject(new Company 
                    {
                        Name = "Name"
                    }),
                    new RavenJObject());
                Assert.Equal(store.Url + "/databases/CanGetUrlForDocument/docs/items/1", store.DatabaseCommands.UrlFor("items/1"));
            }
        }
	}
}
