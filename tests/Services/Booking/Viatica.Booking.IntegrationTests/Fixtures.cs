using Viatica.Testing;

[assembly: AssemblyFixture(typeof(PostgresFixture))]
[assembly: AssemblyFixture(typeof(KafkaFixture))]

// One test class at a time. Every class starts a whole service and four stand-ins, each with a pool of connections to
// one Postgres container whose default limit is 100; run together the classes exhaust it.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]
