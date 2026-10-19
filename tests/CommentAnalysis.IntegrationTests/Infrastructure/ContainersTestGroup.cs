namespace CommentAnalysis.IntegrationTests.Infrastructure;

/// <summary>
/// Test classes in this group share one PostgreSQL and one RabbitMQ container, started once.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ContainersTestGroup : ICollectionFixture<PostgresFixture>, ICollectionFixture<RabbitMqFixture>
{
    public const string Name = "Containers";
}
