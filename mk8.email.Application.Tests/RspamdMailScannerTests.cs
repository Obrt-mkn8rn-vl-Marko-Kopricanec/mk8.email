using System.Net;
using System.Text;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Configuration;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class RspamdMailScannerTests
{
    [TestMethod]
    public async Task AuthenticatedScanReturnsValidatedDkimHeaderAndMetadata()
    {
        var handler = new RecordingHandler(
            """
            {
              "action":"no action",
              "score":0.5,
              "required_score":15.0,
              "symbols":{"DKIM_SIGNED":{"score":0.0}},
              "dkim-signature":"v=1; a=rsa-sha256; d=mk8n.com;\n\tb=test"
            }
            """);
        using var client = new HttpClient(handler);
        using var scanner = new RspamdMailScanner(CreateEnvironment(), client);
        var queueId = Guid.CreateVersion7();

        var result = await scanner.ScanAsync(new MailScanRequest(
            queueId,
            "admin@mk8n.com",
            ["recipient@example.net"],
            "From: admin@mk8n.com\r\n\r\nbody\r\n",
            "192.0.2.10",
            "client.example",
            "admin@mk8n.com"));

        Assert.AreEqual("no action", result.Action);
        StringAssert.StartsWith(result.AddedHeaders, "DKIM-Signature: v=1;");
        StringAssert.Contains(result.AddedHeaders, "\r\n\tb=test\r\n");
        Assert.AreEqual(queueId.ToString("N"), handler.Headers["Queue-Id"].Single());
        Assert.AreEqual("admin@mk8n.com", handler.Headers["User"].Single());
        Assert.AreEqual("recipient@example.net", handler.Headers["Rcpt"].Single());
        StringAssert.Contains(handler.Body!, "From: admin@mk8n.com");
    }

    [TestMethod]
    public async Task ClamFailureRequestsRetryWithoutAddedHeaders()
    {
        var handler = new RecordingHandler(
            """
            {
              "action":"soft reject",
              "score":0.0,
              "required_score":15.0,
              "symbols":{"CLAM_VIRUS_FAIL":{"score":0.0}}
            }
            """);
        using var client = new HttpClient(handler);
        using var scanner = new RspamdMailScanner(CreateEnvironment(), client);

        var result = await scanner.ScanAsync(InboundRequest());

        Assert.IsTrue(result.IsTemporaryFailure);
        Assert.IsFalse(result.IsMalware);
        Assert.AreEqual(string.Empty, result.AddedHeaders);
    }

    [TestMethod]
    public async Task VirusSymbolMarksMessageAsMalware()
    {
        var handler = new RecordingHandler(
            """
            {
              "action":"reject",
              "score":20.0,
              "required_score":15.0,
              "symbols":{"CLAM_VIRUS":{"score":0.0}}
            }
            """);
        using var client = new HttpClient(handler);
        using var scanner = new RspamdMailScanner(CreateEnvironment(), client);

        var result = await scanner.ScanAsync(InboundRequest());

        Assert.IsTrue(result.IsMalware);
        Assert.IsFalse(result.IsTemporaryFailure);
        StringAssert.Contains(result.AddedHeaders, "X-Spam-Status: Yes");
    }

    [TestMethod]
    public async Task InvalidFoldedDkimHeaderStopsProcessing()
    {
        var handler = new RecordingHandler(
            """
            {
              "action":"no action",
              "score":0.0,
              "required_score":15.0,
              "symbols":{},
              "dkim-signature":"v=1; a=rsa-sha256;\nInjected: value"
            }
            """);
        using var client = new HttpClient(handler);
        using var scanner = new RspamdMailScanner(CreateEnvironment(), client);
        var request = InboundRequest() with { AuthenticatedUser = "admin@mk8n.com" };

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => scanner.ScanAsync(request));
    }

    private static MailScanRequest InboundRequest() => new(
        Guid.CreateVersion7(),
        "sender@example.net",
        ["admin@mk8n.com"],
        "From: sender@example.net\r\n\r\nbody\r\n",
        "192.0.2.10",
        "sender.example.net",
        null);

    private static EnvironmentConfig CreateEnvironment() => new()
    {
        Smtp = new SmtpConfig { Hostname = "email.mk8n.com" },
        Filtering = new FilteringConfig
        {
            RspamdEndpoint = "http://127.0.0.1:11333/checkv2",
            TimeoutSeconds = 5,
        },
    };

    private sealed class RecordingHandler(string responseJson) : HttpMessageHandler
    {
        public Dictionary<string, string[]> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            foreach (var header in request.Headers)
                Headers.Add(header.Key, header.Value.ToArray());
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }
}
