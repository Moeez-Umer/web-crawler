namespace Crawler.Worker.Tests.Integration;

// Marks all tests in the "Integration" collection as non-parallel with each other.
// Tests within the class still run sequentially sharing the PostgresFixture schema.
[CollectionDefinition("Integration")]
public sealed class IntegrationCollection : ICollectionFixture<PostgresFixture> { }

