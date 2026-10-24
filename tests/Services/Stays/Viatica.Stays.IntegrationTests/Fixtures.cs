using Viatica.Testing;

[assembly: AssemblyFixture(typeof(PostgresFixture))]
[assembly: AssemblyFixture(typeof(KafkaFixture))]
[assembly: AssemblyFixture(typeof(ValkeyFixture))]

// One test class at a time. Every class starts a whole service with its own pool of connections to one Postgres
// container, whose default limit is 100; run together the classes exhaust it ("too many clients"), and the failure is
// the container's, not the service's.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]
