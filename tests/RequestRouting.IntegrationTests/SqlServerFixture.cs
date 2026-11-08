using Microsoft.Data.SqlClient;
using RequestRouting.IntegrationTests;
using Testcontainers.MsSql;
using Xunit.Sdk;
using Xunit.v3;

[assembly: AssemblyFixture(typeof(SqlServerFixture))]
[assembly: Parallelization(Mode = ParallelMode.None)]

namespace RequestRouting.IntegrationTests;

/// <summary>
/// Uses the server named by ROUTING_TEST_SQLSERVER, or starts a container when it is not set.
/// These tests are about real locking behaviour, so an in-memory substitute would prove nothing.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private MsSqlContainer? _container;
    private string _serverConnectionString = "";

    public async ValueTask InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable("ROUTING_TEST_SQLSERVER");

        if (!string.IsNullOrWhiteSpace(configured))
        {
            _serverConnectionString = configured;
            return;
        }

        _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
        await _container.StartAsync();
        _serverConnectionString = _container.GetConnectionString();
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>Each test gets its own database, so the queue of one can never affect another.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"RequestRoutingTest_{Guid.NewGuid():N}";
        await ExecuteOnMasterAsync($"CREATE DATABASE [{name}]");

        return new SqlConnectionStringBuilder(_serverConnectionString) { InitialCatalog = name }.ConnectionString;
    }

    public async Task DropDatabaseAsync(string connectionString)
    {
        var name = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        SqlConnection.ClearAllPools();
        await ExecuteOnMasterAsync($"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]");
    }

    private async Task ExecuteOnMasterAsync(string sql)
    {
        var builder = new SqlConnectionStringBuilder(_serverConnectionString) { InitialCatalog = "master" };
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
