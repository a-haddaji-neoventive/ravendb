﻿using System;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Tests.Core.Utils.Entities;
using Xunit;

namespace SlowTests.Server.Documents.ETL.Raven
{
    public class RavenDB_11157_Raven : EtlTestBase
    {
        [Fact]
        public void Should_load_all_counters_when_no_script_is_defined()
        {
            using (var src = GetDocumentStore())
            using (var dest = GetDocumentStore())
            {
                AddEtl(src, dest, "Users", script: null);

                var etlDone = WaitForEtl(src, (n, s) => s.LoadSuccesses > 0);

                using (var session = src.OpenSession())
                {
                    session.Store(new User()
                    {
                        Name = "Joe Doe"
                    }, "users/1");

                    session.Advanced.Counters.Increment("users/1", "likes");

                    session.SaveChanges();
                }

                etlDone.Wait(TimeSpan.FromMinutes(1));

                using (var session = dest.OpenSession())
                {
                    var user = session.Load<User>("users/1");

                    Assert.NotNull(user);
                    Assert.Equal("Joe Doe", user.Name);

                    var counter = session.Advanced.Counters.Get("users/1", "likes");

                    Assert.NotNull(counter);
                    Assert.Equal(1, counter.Value);
                }

                etlDone.Reset();

                using (var session = src.OpenSession())
                {
                    session.Delete("users/1");

                    session.SaveChanges();
                }

                etlDone.Wait(TimeSpan.FromMinutes(1));

                using (var session = dest.OpenSession())
                {
                    var user = session.Load<User>("users/1");

                    Assert.Null(user);

                    var counter = session.Advanced.Counters.Get("users/1", "likes");

                    Assert.Null(counter);
                }
            }
        }

        [Fact]
        public void Should_not_send_counters_metadata_when_using_script()
        {
            using (var src = GetDocumentStore())
            using (var dest = GetDocumentStore())
            {
                AddEtl(src, dest, "Users", script: @"this.Name = 'James Doe';
                                       loadToUsers(this);");

                var etlDone = WaitForEtl(src, (n, s) => s.LoadSuccesses > 0);

                using (var session = src.OpenSession())
                {
                    session.Store(new User()
                    {
                        Name = "Joe Doe"
                    }, "users/1");

                    session.Advanced.Counters.Increment("users/1", "likes");

                    session.SaveChanges();
                }

                etlDone.Wait(TimeSpan.FromMinutes(1));

                using (var session = dest.OpenSession())
                {
                    var user = session.Load<User>("users/1");

                    Assert.NotNull(user);
                    Assert.Equal("James Doe", user.Name);

                    var metatata = session.Advanced.GetMetadataFor(user);

                    Assert.False(metatata.ContainsKey(Constants.Documents.Metadata.Counters));
                }
            }
        }


        [Fact]
        public void Should_handle_counters()
        {
            using (var src = GetDocumentStore())
            using (var dest = GetDocumentStore())
            {
                AddEtl(src, dest, "Users", script:
                    @"
var counters = this['@metadata']['@counters'];

this.Name = 'James';

// case 1 : doc id will be preserved

var doc = loadToUsers(this);

for (var i = 0; i < counters.length; i++) {
    doc.addCounter(counters[i] + '-etl', loadCounter(counters[i]));
}

// case 2 : doc id will be generated on the destination side

var person = loadToPeople({ Name: this.Name + ' ' + this.LastName });

person.addCounter('down-etl', loadCounter('down'));
"
);
                var etlDone = WaitForEtl(src, (n, s) => s.LoadSuccesses > 0);

                using (var session = src.OpenSession())
                {
                    session.Store(new User()
                    {
                        Name = "Joe",
                        LastName = "Doe",
                    }, "users/1");

                    session.Advanced.Counters.Increment("users/1", "up", 20);
                    session.Advanced.Counters.Increment("users/1", "down", 10);

                    session.SaveChanges();
                }

                etlDone.Wait(TimeSpan.FromMinutes(1));

                AssertCounters(dest, new[]
                {
                    ("users/1", "up-etl", 20L, false),
                    ("users/1", "down-etl", 10, false),
                    ("users/1/people/", "down-etl", 10, true)
                });

                string personId;

                using (var session = dest.OpenSession())
                {
                    personId = session.Advanced.LoadStartingWith<Person>("users/1/people/")[0].Id;
                }

                etlDone.Reset();

                using (var session = src.OpenSession())
                {
                    session.Advanced.Counters.Delete("users/1", "up");
                    session.SaveChanges();
                }

                etlDone.Wait(TimeSpan.FromMinutes(1));

                using (var session = dest.OpenSession())
                {
                    var user = session.Load<User>("users/1");

                    var metatata = session.Advanced.GetMetadataFor(user);

                    Assert.True(metatata.ContainsKey(Constants.Documents.Metadata.Counters));

                    var counter = session.Advanced.Counters.Get("users/1", "up-etl");

                    Assert.Null(counter); // this counter was removed
                }

                AssertCounters(dest, new[]
                {
                    ("users/1", "down-etl", 10L, false),
                    ("users/1/people/", "down-etl", 10, true)
                });

                etlDone.Reset();

                using (var session = src.OpenSession())
                {
                    session.Delete("users/1");

                    session.SaveChanges();
                }

                etlDone.Wait(TimeSpan.FromMinutes(1));

                using (var session = dest.OpenSession())
                {
                    Assert.Null(session.Load<User>("users/1"));

                    Assert.Null(session.Advanced.Counters.Get("users/1", "up-etl"));
                    Assert.Null(session.Advanced.Counters.Get("users/1", "up-etl"));

                    Assert.Empty(session.Advanced.LoadStartingWith<Person>("users/1/people/"));

                    Assert.Null(session.Advanced.Counters.Get(personId, "down-etl"));
                }
            }
        }

        private void AssertCounters(IDocumentStore store, params (string DocId, string CounterName, long CounterValue, bool LoadUsingStartingWith)[] items)
        {
            using (var session = store.OpenSession())
            {
                foreach (var item in items)
                {
                    var doc = item.LoadUsingStartingWith ? session.Advanced.LoadStartingWith<User>(item.DocId)[0] : session.Load<User>(item.DocId);
                    Assert.NotNull(doc);

                    var metatata = session.Advanced.GetMetadataFor(doc);

                    Assert.True(metatata.ContainsKey(Constants.Documents.Metadata.Counters));
                    
                    var value = session.Advanced.Counters.Get(doc.Id, item.CounterName);

                    Assert.NotNull(value);
                    Assert.Equal(item.CounterValue, value);
                }
            }
        }
        
        [Fact]
        public void Can_use_get_counters()
        {
            using (var src = GetDocumentStore())
            using (var dest = GetDocumentStore())
            {
                AddEtl(src, dest, "Users", script:
                    @"
var counters = getCounters();

for (var i = 0; i < counters.length; i++) {
    this.LastName = this.LastName + counters[i];
}

var doc = loadToUsers(this);

for (var i = 0; i < counters.length; i++) {
    doc.addCounter(loadCounter(counters[i]));
}
"
);
                var etlDone = WaitForEtl(src, (n, s) => s.LoadSuccesses > 0);

                using (var session = src.OpenSession())
                {
                    session.Store(new User()
                    {
                        Name = "Joe",
                        LastName = "",
                    }, "users/1");

                    session.Advanced.Counters.Increment("users/1", "up");
                    session.Advanced.Counters.Increment("users/1", "down", -1);

                    session.SaveChanges();
                }

                etlDone.Wait(TimeSpan.FromMinutes(1));

                AssertCounters(dest, new[]
                {
                    ("users/1", "up", 1L, false),
                    ("users/1", "down", -1, false),
                });

                using (var session = dest.OpenSession())
                {
                    var user = session.Load<User>("users/1");

                    Assert.Equal("downup", user.LastName);
                }
            }
        }

        [Fact]
        public void Should_error_if_attachment_doesnt_exist()
        {
            using (var src = GetDocumentStore())
            using (var dest = GetDocumentStore())
            {
                AddEtl(src, dest, "Users", script:
                    @"

var doc = loadToUsers(this);
doc.addCounter('likes', loadCounter('likes'));
"
                );
                var etlDone = WaitForEtl(src, (n, s) => s.LoadSuccesses > 0);

                using (var session = src.OpenSession())
                {
                    session.Store(new User()
                    {
                        Name = "Joe"
                    }, "users/1");

                    session.Store(new User()
                    {
                        Name = "Doe"
                    }, "users/2");

                    session.Store(new User()
                    {
                        Name = "Foo"
                    }, "users/3");

                    session.Advanced.Counters.Increment("users/1", "up");
                    session.Advanced.Counters.Increment("users/2", "likes");

                    session.SaveChanges();
                }

                etlDone.Wait(TimeSpan.FromMinutes(1));

                AssertCounters(dest, new[]
                {
                    ("users/2", "likes", 1L, false)
                });

                using (var session = dest.OpenSession())
                {
                    Assert.Null(session.Load<User>("users/1"));
                    Assert.Null(session.Load<User>("users/3"));
                }
            }
        }

        [Fact]
        public void Can_use_has_counter()
        {
            using (var src = GetDocumentStore())
            using (var dest = GetDocumentStore())
            {
                AddEtl(src, dest, "Users", script:
                    @"

var doc = loadToUsers(this);

if (hasCounter('up')) {
  doc.addCounter(loadCounter('up'));
}

if (hasCounter('down')) {
  doc.addCounter(loadCounter('down'));
}
"
                );
                var etlDone = WaitForEtl(src, (n, s) => s.LoadSuccesses > 0);

                using (var session = src.OpenSession())
                {
                    session.Store(new User()
                    {
                        Name = "Joe"
                    }, "users/1");

                    session.Advanced.Counters.Increment("users/1", "down", -1);

                    session.Store(new User()
                    {
                        Name = "Joe"
                    }, "users/2");

                    session.SaveChanges();
                }

                etlDone.Wait(TimeSpan.FromMinutes(1));

                AssertCounters(dest, new[]
                {
                    ("users/1", "down", -1L, false)
                });

                using (var session = dest.OpenSession())
                {
                    Assert.NotNull(session.Load<User>("users/1"));
                    Assert.NotNull(session.Load<User>("users/2"));
                }
            }
        }
    }
}
