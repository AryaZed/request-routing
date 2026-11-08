using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using RequestRouting.Application;
using RequestRouting.Domain.Requests;

namespace RequestRouting.IntegrationTests;

/// <summary>
/// Two separately hosted instances of the real application on one database, the deployment the task
/// describes. The sweeper is off unless a test asks for it, so it can never mask a dispatch bug.
/// </summary>
public sealed class TestApp : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly DispatchFailureCounter _dispatchFailures = new();
    private readonly SqlServerFixture _server;
    private readonly string _connectionString;
    private readonly WebApplicationFactory<Program> _factoryA;
    private readonly WebApplicationFactory<Program> _factoryB;
    private int _nextKey;

    private TestApp(SqlServerFixture server, string connectionString, TimeSpan sweepInterval)
    {
        _server = server;
        _connectionString = connectionString;
        _factoryA = CreateFactory(sweepInterval);
        _factoryB = CreateFactory(sweepInterval);
        InstanceA = _factoryA.CreateClient();
        InstanceB = _factoryB.CreateClient();
    }

    public HttpClient InstanceA { get; }
    public HttpClient InstanceB { get; }
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero));

    public static async Task<TestApp> StartAsync(
        SqlServerFixture server,
        bool readCommittedSnapshot = true,
        TimeSpan sweepInterval = default)
    {
        var connectionString = await server.CreateDatabaseAsync();
        var app = new TestApp(server, connectionString, sweepInterval);

        // This is an operator setting, not a migration, and correctness must not depend on it. Most tests use
        // the recommended ON; the concurrency tests also run with OFF, the SQL Server default.
        if (readCommittedSnapshot)
        {
            await app.ExecuteSqlAsync("ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE");
            SqlConnection.ClearAllPools();
        }

        return app;
    }

    /// <summary>Dispatch failures are logged rather than returned to clients, so tests must check for them.</summary>
    public int DispatchFailures => Volatile.Read(ref _dispatchFailures.Count);

    public string ConnectionString => _connectionString;

    public async Task<ExpertView> CreateExpertAsync(string name, int capacity, params string[] topics)
    {
        var response = await InstanceA.PostAsJsonAsync("/api/experts", new { name, capacity, topics }, Json);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ExpertView>(Json))!;
    }

    public async Task<ExpertView> GetExpertAsync(int id) =>
        (await InstanceA.GetFromJsonAsync<ExpertView>($"/api/experts/{id}", Json))!;

    public async Task<ExpertView> PatchExpertAsync(int id, object body)
    {
        var response = await InstanceA.PatchAsJsonAsync($"/api/experts/{id}", body, Json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ExpertView>(Json))!;
    }

    public Task<HttpResponseMessage> PostRequestAsync(
        string requestKey,
        string topic,
        RequestPriority priority = RequestPriority.Normal,
        HttpClient? via = null,
        string title = "title") =>
        (via ?? InstanceA).PostAsJsonAsync("/api/requests", new { requestKey, topic, title, priority }, Json);

    public async Task<RequestView> SubmitAsync(
        string topic,
        RequestPriority priority = RequestPriority.Normal,
        HttpClient? via = null)
    {
        var key = $"key-{Interlocked.Increment(ref _nextKey).ToString(CultureInfo.InvariantCulture)}";
        var response = await PostRequestAsync(key, topic, priority, via);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RequestView>(Json))!;
    }

    public async Task<RequestView> CloseAsync(long id, HttpClient? via = null)
    {
        var response = await (via ?? InstanceA).PostAsync($"/api/requests/{id}/close", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RequestView>(Json))!;
    }

    public async Task<RequestView> GetRequestAsync(long id) =>
        (await InstanceA.GetFromJsonAsync<RequestView>($"/api/requests/{id}", Json))!;

    public async Task<IReadOnlyList<RequestView>> GetPendingAsync() =>
        (await InstanceA.GetFromJsonAsync<List<RequestView>>("/api/requests/pending?take=200", Json))!;

    public static Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        response.Content.ReadFromJsonAsync<T>(Json)!;

    public async Task<int> ScalarAsync(string sql)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<long>> IdsAsync(string sql)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        var ids = new List<long>();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    public async Task ExecuteSqlAsync(string sql)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        InstanceA.Dispose();
        InstanceB.Dispose();
        await _factoryA.DisposeAsync();
        await _factoryB.DisposeAsync();
        await _server.DropDatabaseAsync(_connectionString);
    }

    private WebApplicationFactory<Program> CreateFactory(TimeSpan sweepInterval) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Testing");
            host.UseSetting("ConnectionStrings:Default", _connectionString);
            host.UseSetting("Database:MigrateOnStartup", "true");
            host.UseSetting("Queue:SweepInterval", sweepInterval.ToString());
            host.ConfigureTestServices(services =>
            {
                services.AddSingleton<TimeProvider>(Clock);
                services.AddSingleton<ILoggerProvider>(_dispatchFailures);
            });
        });

    private sealed class DispatchFailureCounter : ILoggerProvider, ILogger
    {
        public int Count;

        public ILogger CreateLogger(string categoryName) =>
            categoryName == typeof(Dispatcher).FullName ? this : NullLogger.Instance;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                Interlocked.Increment(ref Count);
            }
        }

        public void Dispose()
        {
        }
    }
}
