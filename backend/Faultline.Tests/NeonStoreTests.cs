using System.Net;
using System.Text;
using Faultline.Core;

namespace Faultline.Tests;

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
