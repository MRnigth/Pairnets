using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Pairnets.Core;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.E2E;

public sealed partial class ApiTour
{
    /// <summary>
    /// WebDAV links (feature/webdav, docs/WEBDAV.md): the owner's computer makes a link, its password opens /dav (Finder's and
    /// Explorer's requests, one of each) and nothing else, and removing the link shuts it out at once. WebDavTests covers every case.
    /// </summary>
    private async Task WebDavLinksAsync()
    {
        Step("A WebDAV link: the owner's computer makes one, and only the owner's computers can");
        await Refused(HttpMethod.Get, "api/webdav/links");
        var before = await Read<JsonElement>(Get(_laptopApi, "api/webdav/links"));
        Assert.False(before.GetProperty("sealed").GetBoolean());
        var made = await Read<JsonElement>(Post(_laptopApi, "api/webdav/links", new { label = $"Tour {_tag}", access = "edit" }, HttpStatusCode.Created));
        var id = made.GetProperty("id").GetString()!;
        var password = made.GetProperty("password").GetString()!;
        _secrets.Add(password);
        _secrets.Add(password.Replace("-", string.Empty, StringComparison.Ordinal));
        Assert.Contains(id, (await Read<JsonElement>(Get(_laptopApi, "api/webdav/links"))).GetProperty("links").EnumerateArray().Select(l => l.GetProperty("id").GetString()));

        Step("Its password opens the nest's files the way Finder and Explorer ask for them, and nothing else");
        var dav = new HttpClient(new RecordingHandler(Record, Localhost.Handler())) { BaseAddress = _target.ApiUrl, Timeout = TimeSpan.FromSeconds(60) };
        dav.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("tour:" + password)));
        _clients.Add(dav);
        var file = $"dav/tour-{_tag}.txt";
        var folder = $"dav/tour-{_tag}-folder";
        async Task<HttpResponseMessage> Dav(string method, string url, HttpStatusCode expect, string? body = null, params (string Name, string Value)[] headers) =>
            await Call(dav, new HttpMethod(method), url, expect: expect, tweak: r =>
            {
                if (body is not null)
                    r.Content = new StringContent(body);
                foreach (var (name, value) in headers)
                    r.Headers.TryAddWithoutValidation(name, value);
            });

        var options = await Dav("OPTIONS", file, HttpStatusCode.OK);
        Assert.Equal("1, 2", options.Headers.GetValues("DAV").Single());
        await Dav("PUT", file, HttpStatusCode.Created, "put through WebDAV");
        Assert.Equal("put through WebDAV", await (await Dav("GET", file, HttpStatusCode.OK)).Content.ReadAsStringAsync());
        await Dav("HEAD", file, HttpStatusCode.OK);
        Assert.Contains($"tour-{_tag}.txt", await (await Dav("PROPFIND", file, (HttpStatusCode)207, null, ("Depth", "0"))).Content.ReadAsStringAsync());
        await Dav("PROPPATCH", file, (HttpStatusCode)207, """<?xml version="1.0"?><propertyupdate xmlns="DAV:"/>""");
        var locked = await Dav("LOCK", file, HttpStatusCode.OK, """<?xml version="1.0"?><lockinfo xmlns="DAV:"><lockscope><exclusive/></lockscope><locktype><write/></locktype></lockinfo>""");
        await Dav("UNLOCK", file, HttpStatusCode.NoContent, null, ("Lock-Token", locked.Headers.GetValues("Lock-Token").Single()));
        await Dav("MKCOL", folder, HttpStatusCode.Created);
        await Dav("MOVE", file, HttpStatusCode.Created, null, ("Destination", $"/dav/tour-{_tag}-moved.txt"));
        Assert.Equal("put through WebDAV", await (await Get(_laptopApi, $"api/file?path={Uri.EscapeDataString($"tour-{_tag}-moved.txt")}")).Content.ReadAsStringAsync());
        await Dav("DELETE", $"dav/tour-{_tag}-moved.txt", HttpStatusCode.NoContent);
        await Dav("DELETE", folder, HttpStatusCode.NoContent);
        using (var asKey = new HttpRequestMessage(HttpMethod.Get, "api/manifest"))
        {
            asKey.Headers.Authorization = dav.DefaultRequestHeaders.Authorization;
            Assert.Equal(HttpStatusCode.Unauthorized, (await dav.SendAsync(asKey)).StatusCode); // the password opens /dav only
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await Api(_laptop.Key).SendAsync(new HttpRequestMessage(new HttpMethod("PROPFIND"), "dav/"))).StatusCode); // a computer's key opens no /dav

        Step("Removing the link shuts its password out at once");
        await Delete(_laptopApi, $"api/webdav/links/{Uri.EscapeDataString(id)}");
        await Dav("PROPFIND", "dav/", HttpStatusCode.Unauthorized, null, ("Depth", "0"));
        await Delete(_laptopApi, $"api/webdav/links/{Uri.EscapeDataString(id)}", HttpStatusCode.NotFound);
    }
}
