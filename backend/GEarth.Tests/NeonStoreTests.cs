using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using GEarth.Core;

namespace GEarth.Tests;

public sealed class NeonStoreTests
{
    [Fact]
    public async Task TransportRetriesPreserveTheSameQuery()
    {
        var handler = new RetryHandler();
        using var store = new NeonStore("postgresql://test:test@ep-test.us-east-2.aws.neon.tech/neondb", handler);

        var runs = await store.List(CancellationToken.None);

        Assert.Equal(2, handler.Attempts);
        Assert.Equal(["saved_run"], runs);
        Assert.Equal(handler.Bodies[0], handler.Bodies[1]);
    }

    [Fact]
    public async Task SchemaPreservesOrderedCheckpointsAndAddsNullableAuditHeaders()
    {
        var handler = new SchemaHandler();
        using var store = new NeonStore("postgresql://test:test@ep-test.us-east-2.aws.neon.tech/neondb", handler);
        await store.Initialize(CancellationToken.None);
        var schema = string.Join("\n", handler.Queries);
        Assert.Contains("state json NOT NULL", schema);
        Assert.DoesNotContain("state jsonb NOT NULL", schema);
        Assert.Contains("ALTER COLUMN state TYPE json", schema);
        Assert.Contains("ADD COLUMN IF NOT EXISTS audit_sequence bigint", schema);
        Assert.Contains("ADD COLUMN IF NOT EXISTS run_status text", schema);
        Assert.Contains("ADD COLUMN IF NOT EXISTS analytics_enabled boolean", schema);
        Assert.Contains("CREATE TABLE IF NOT EXISTS gearth.checkpoints", schema);
        Assert.Contains("lsn pg_lsn NOT NULL", schema);
    }

    private sealed class SchemaHandler : HttpMessageHandler
    {
        public List<string> Queries { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            Queries.Add(body["query"]!.GetValue<string>());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"rows\":[]}", Encoding.UTF8, "application/json")
            };
        }
    }

    [Fact]
    public async Task UnmigratedDatabaseCannotSilentlyStartWithEmptyStorage()
    {
        var handler = new ExistingSchemaHandler();
        using var store = new NeonStore("postgresql://test:test@ep-test.us-east-2.aws.neon.tech/neondb", handler);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.Initialize(CancellationToken.None));
        Assert.Contains("must be migrated", error.Message);
        Assert.Equal(1, handler.Attempts);
    }

    private sealed class ExistingSchemaHandler : HttpMessageHandler
    {
        public int Attempts { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Attempts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"rows\":[[\"previous_project\"]]}", Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class RetryHandler : HttpMessageHandler
    {
        public int Attempts { get; private set; }
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            Attempts++;

            if (Attempts == 1)
                throw new HttpRequestException("Test connection drop");

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"rows\":[[\"saved_run\"]]}", Encoding.UTF8, "application/json")
            };
        }
    }
}
