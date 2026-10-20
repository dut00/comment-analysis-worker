using CommentAnalysis.Infrastructure.Persistence;
using Npgsql;

namespace CommentAnalysis.IntegrationTests.Persistence;

public class ConnectionStringDefaultsTests
{
    private const string Database = "Host=db.example.com;Database=comment_analysis;Username=worker;Password=secret;SSL Mode=VerifyFull";

    [Theory]
    [InlineData(Database, GssEncryptionMode.Disable)]
    [InlineData(Database + ";GSS Encryption Mode=Require", GssEncryptionMode.Require)]
    [InlineData(Database + ";GssEncryptionMode=Prefer", GssEncryptionMode.Prefer)]
    public void GssEncryption_IsDisabled_UnlessSet(string connectionString, GssEncryptionMode expected)
    {
        var result = new NpgsqlConnectionStringBuilder(DbContextOptionsBuilderExtensions.WithDefaults(connectionString, null));

        Assert.Equal(expected, result.GssEncryptionMode);
        Assert.Equal(SslMode.VerifyFull, result.SslMode);
        Assert.Equal("secret", result.Password);
    }

    [Fact]
    public void GssEncryption_IsLeftToNpgsql_WhenTheEnvironmentSetsIt() =>
        Assert.Equal(Database, DbContextOptionsBuilderExtensions.WithDefaults(Database, "require"));
}
