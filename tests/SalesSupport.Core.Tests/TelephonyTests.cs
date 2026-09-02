using Microsoft.AspNetCore.SignalR;
using SalesSupport.Backend;
using SalesSupport.Backend.Telephony;
using SalesSupport.Core.Model;

namespace SalesSupport.Core.Tests;

/// <summary>D32: one call contract; the Telavox edge and the provider-neutral pipeline behind it.</summary>
public class TelephonyTests
{
    [Theory]
    [InlineData("08-123 45 67", "+4681234567")]
    [InlineData("+46 8 123 45 67", "+4681234567")]
    [InlineData("+46 (0)8 123 45 67", "+4681234567")]
    [InlineData("0046 8 123 45 67", "+4681234567")]
    [InlineData("4681234567", "+4681234567")]
    [InlineData("0701234567", "+46701234567")]
    [InlineData("+1 415 555 0100", "+14155550100")]
    public void Phone_numbers_normalize_to_one_key(string raw, string expected) =>
        Assert.Equal(expected, PhoneNumbers.Normalize(raw));

    [Theory]
    [InlineData("anonymous")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("123")]
    public void Hidden_callers_and_extensions_normalize_to_null(string raw) =>
        Assert.Null(PhoneNumbers.Normalize(raw));

    [Fact]
    public void Customer_index_resolves_across_number_formats_and_reloads_on_change()
    {
        var path = Path.Combine(Path.GetTempPath(), $"customers-{Guid.NewGuid():N}.jsonl");
        try
        {
            var index = new CustomerIndex(path);
            Assert.Equal(0, index.Count);
            Assert.Null(index.Resolve("+4681234567"));

            File.WriteAllLines(path,
            [
                """{"phone":"08-123 45 67","company":"Nordfrys AB","crm_id":"C-1001","notes":"Kyllager"}""",
                """{"phone":"+46 70 123 45 67","company":"Vaxholm Marin"}""",
                "not json at all",
            ]);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);

            var hit = index.Resolve("+46 8 123 45 67");
            Assert.NotNull(hit);
            Assert.Equal("Nordfrys AB", hit!.Company);
            Assert.Equal("C-1001", hit.CrmId);
            Assert.Equal("Vaxholm Marin", index.Resolve("0701234567")!.Company);
            Assert.Null(index.Resolve("0899999999"));

            File.AppendAllLines(path, ["""{"phone":"08-99 99 99 99","company":"Ny Kund AB"}"""]);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            Assert.Equal("Ny Kund AB", index.Resolve("0899999999")!.Company);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Telavox_adapter_reads_caller_and_rep_from_webhook_parameters()
    {
        var query = new Dictionary<string, string> { ["caller"] = "+46 8 123 45 67", ["rep"] = "andreas" };
        var signal = TelavoxAdapter.Parse(key => query.GetValueOrDefault(key));

        Assert.Equal("+46 8 123 45 67", signal.RawNumber);
        Assert.Equal("andreas", signal.RepKey);
        Assert.Equal("telavox", signal.Provider);

        var bare = TelavoxAdapter.Parse(_ => null);
        Assert.Null(bare.RawNumber);
        Assert.Null(bare.RepKey);
    }

    [Fact]
    public async Task Signal_resolves_customer_and_targets_the_rep_group()
    {
        var path = Path.Combine(Path.GetTempPath(), $"customers-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(path, """{"phone":"08-123 45 67","company":"Nordfrys AB","crm_id":"C-1001","notes":"Kyllager"}""");
        try
        {
            var hub = new FakeHub();
            var service = new CallSignalService(new CustomerIndex(path), hub, new BackendOptions());

            await service.HandleAsync(new IncomingCallSignal("+46812345 67", "Andreas", "telavox", DateTime.UtcNow));

            var (target, method, args) = Assert.Single(hub.Sent);
            Assert.Equal("group:rep:andreas", target);
            Assert.Equal("IncomingCall", method);
            var notice = Assert.IsType<IncomingCallNotice>(args[0]);
            Assert.Equal("+4681234567", notice.Number);
            Assert.Equal("Nordfrys AB", notice.Customer!.Company);
            Assert.Equal("Kyllager", notice.Customer.Notes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Signal_without_rep_broadcasts_and_hidden_number_stays_null()
    {
        var hub = new FakeHub();
        var service = new CallSignalService(new CustomerIndex(Path.Combine(Path.GetTempPath(), "missing.jsonl")), hub, new BackendOptions());

        await service.HandleAsync(new IncomingCallSignal("anonymous", null, "telavox", DateTime.UtcNow));

        var (target, _, args) = Assert.Single(hub.Sent);
        Assert.Equal("all", target);
        var notice = Assert.IsType<IncomingCallNotice>(args[0]);
        Assert.Null(notice.Number);
        Assert.Null(notice.Customer);
    }

    [Fact]
    public void Webhook_secret_gates_the_endpoint_when_configured()
    {
        var open = new CallSignalService(new CustomerIndex("nope.jsonl"), new FakeHub(), new BackendOptions());
        Assert.True(open.IsAuthorized(null));

        var guarded = new CallSignalService(new CustomerIndex("nope.jsonl"), new FakeHub(),
            new BackendOptions { TelephonyWebhookSecret = "s3cret" });
        Assert.False(guarded.IsAuthorized(null));
        Assert.False(guarded.IsAuthorized("wrong"));
        Assert.True(guarded.IsAuthorized("s3cret"));
    }

    [Fact]
    public void Webhook_url_template_carries_rep_and_telavox_placeholder()
    {
        var url = TelephonyWire.TelavoxWebhookUrl("http://localhost:5155/", "andreas");
        Assert.Equal("http://localhost:5155/api/telephony/telavox/ring?rep=andreas&caller={system.caller}", url);
    }

    private sealed class FakeHub : IHubContext<CallHub>
    {
        public List<(string Target, string Method, object?[] Args)> Sent { get; } = [];
        public IHubClients Clients => new FakeClients(Sent);
        public IGroupManager Groups => throw new NotSupportedException();
    }

    private sealed class FakeClients(List<(string, string, object?[])> sink) : IHubClients
    {
        private IClientProxy Proxy(string target) => new FakeProxy(target, sink);
        public IClientProxy All => Proxy("all");
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy("all-except");
        public IClientProxy Client(string connectionId) => Proxy("client:" + connectionId);
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy("clients");
        public IClientProxy Group(string groupName) => Proxy("group:" + groupName);
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Proxy("group-except:" + groupName);
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => Proxy("groups");
        public IClientProxy User(string userId) => Proxy("user:" + userId);
        public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy("users");
    }

    private sealed class FakeProxy(string target, List<(string, string, object?[])> sink) : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            sink.Add((target, method, args));
            return Task.CompletedTask;
        }
    }
}
