using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.Extensions.DependencyInjection;
using Pairnets.Core;
using Pairnets.Core.Crypto;
using Pairnets.Server.Web;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.E2E;

public sealed partial class ApiTour
{
    /// <summary>
    /// The key mailbox of an encrypted nest (docs/ENCRYPTION.md). Turning encryption on can't be undone and needs a nest
    /// without files, so the tour's own nest only shows it is not encrypted; the rest runs on a second, new nest inside
    /// the test process whatever the target, like <see cref="LinkedNestAsync"/>. SealedNestTests covers the details.
    /// </summary>
    private async Task EncryptedNestAsync()
    {
        Step("The tour's nest is not encrypted and keeps its key mailbox to signed-in computers");
        await Refused(HttpMethod.Get, "api/keys");
        var info = await Read<KeysInfo>(Get(_laptopApi, "api/keys"));
        Assert.False(info.Sealed);

        Step("A new nest is encrypted: envelopes, sealing, and a second computer unlocked through the mailbox");
        await using var nest = await TestServer.StartAsync(configureBuilder: builder =>
            builder.Services.AddSingleton<IStartupFilter>(new AnsweredRoutesFilter(Record.ServerSaw)));
        using var http = new HttpClient(new RecordingHandler(Record, new SocketsHttpHandler { UseProxy = false })) { BaseAddress = nest.Url };
        http.DefaultRequestHeaders.Add(PairnetsHeaders.Token, nest.Token);
        http.DefaultRequestHeaders.Add(PairnetsHeaders.Features, PairnetsHeaders.SealedFeature);

        using var first = ComputerKeys.Create();
        var ring = KeyRing.Create(nest.Store.ServerId, first.AsTrusted("PC-1"));
        await Call(http, HttpMethod.Put, "api/keys/recovery", expect: HttpStatusCode.NoContent,
            tweak: r => r.Content = new ByteArrayContent(Envelope.SealForRecovery(ring, RecoveryKey.Create())));
        Assert.NotEmpty(await (await Get(http, "api/keys/recovery")).Content.ReadAsByteArrayAsync());
        await Call(http, HttpMethod.Put, $"api/keys/envelopes/{first.Id}", expect: HttpStatusCode.NoContent,
            tweak: r => r.Content = new ByteArrayContent(Envelope.SealForComputer(ring, first.AgreementPublicKey, first)));
        var envelope = await (await Get(http, $"api/keys/envelopes/{first.Id}")).Content.ReadAsByteArrayAsync();
        Assert.Equal(RingCheck.Ok, Envelope.OpenForComputer(envelope, first, ring).Check);

        await using var hub = await HubProbe.ConnectAsync("the encrypted nest's PC-1", nest.Url, nest.Token, "PC-1", Record, HttpTransportType.WebSockets);
        Assert.True((await Read<KeysInfo>(Post(http, "api/keys/seal"))).Sealed);
        await hub.WaitForAsync(SyncHub.NestSealedMethod);

        using var second = ComputerKeys.Create();
        using var fresh = new PairingNewComputer(second.AsTrusted("PC-2"));
        var started = await Read<KeyRequestView>(Call(http, HttpMethod.Post, "api/keys/requests", new KeyRequestStart("PC-2", fresh.Commitment),
            HttpStatusCode.Created));
        await hub.WaitForAsync(SyncHub.KeyRequestedMethod, args => (string?)args[0] == started.Id);
        var pending = Assert.Single(await Read<List<KeyRequestView>>(Get(http, "api/keys/requests")));
        using var old = new PairingOldComputer(pending.Commitment);
        await Post(http, $"api/keys/requests/{started.Id}/offer", new KeyRequestMessage(old.Offer));
        var view = await Read<KeyRequestView>(Get(http, $"api/keys/requests/{started.Id}"));
        await Post(http, $"api/keys/requests/{started.Id}/reveal", new KeyRequestMessage(fresh.Reveal(view.Offer!)));
        old.Accept((await Read<KeyRequestView>(Get(http, $"api/keys/requests/{started.Id}"))).Reveal!);
        Assert.Equal(fresh.Code, old.Code);
        await Post(http, $"api/keys/requests/{started.Id}/ring", new KeyRequestMessage(old.SealRing(ring.WithComputer(old.NewComputer!))));
        var done = await Read<KeyRequestView>(Get(http, $"api/keys/requests/{started.Id}"));
        Assert.Equal(ring.IdKey(), fresh.OpenRing(done.Ring!).IdKey());
        await Delete(http, $"api/keys/requests/{started.Id}");

        await Delete(http, $"api/keys/envelopes/{first.Id}");
        Assert.Empty((await Read<KeysInfo>(Get(http, "api/keys"))).Computers);
    }
}
