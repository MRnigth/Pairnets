using Pairnets.Core;
using Pairnets.Server.Services;
using Pairnets.Server.Storage;

namespace Pairnets.Server.Web;

/// <summary>
/// Friend invites (1.0.61.2), the apps' side: what this computer may do on the nest (any computer), and the people the
/// nest is shared with plus those waiting to be let in (the owner's computers only, see <see cref="AccessGate"/>).
/// Inviting, letting in and changing a share happen on the account page; the service tells the nest through
/// <see cref="RelayEndpoints"/>.
/// </summary>
public static class FriendEndpoints
{
    /// <summary><c>GET /api/access</c>: "owner", "edit" or "view", the shared folders (null: the whole nest), and whose share it is.</summary>
    public sealed record AccessView(string Access, IReadOnlyList<string>? Folders, string? Person, string? NestName);

    public sealed record PersonDevice(string Id, string Name, string? System, DateTimeOffset? LastSeen);

    /// <summary>One friend: their account id at the service, the account page's line about them, what they may do, their computers.</summary>
    public sealed record PersonView(string? PersonId, string? Person, string Access, IReadOnlyList<string>? Folders, IReadOnlyList<PersonDevice> Devices);

    /// <summary><c>GET /api/people</c>: the friends with computers on the nest, and the people waiting for the owner's OK.</summary>
    public sealed record PeopleView(IReadOnlyList<PersonView> People, IReadOnlyList<FriendRequest> Requests);

    public static void Map(WebApplication app)
    {
        // How the apps learn what they may do here (on connect and every few minutes). Always the records' latest.
        app.MapGet("/api/access", (HttpContext ctx, AuthStore auth) =>
        {
            var me = DeviceIdentity.Of(ctx);
            var device = me is { Kind: DeviceAuthKind.DeviceKey, Id: { } id } ? auth.GetDevice(id) : null;
            var access = device?.AccessOrOwner ?? DeviceAccess.Full;
            return Json(new AccessView(access.Level, access.Folders, device?.Person, auth.NestName));
        });

        // The People section under the owner's Computers page.
        app.MapGet("/api/people", (HttpContext ctx, AuthStore auth, DeviceRegistry devices, FriendRequests requests) =>
        {
            if (DeviceIdentity.Of(ctx)?.AccessOrOwner.IsOwner != true)
                return Results.Json(new AccessGate.Refusal(AccessGate.OwnerOnly, "Only the nest's owner can do this.", AccessGate.OwnerOnly),
                    PairnetsJson.Options, statusCode: StatusCodes.Status403Forbidden);
            var lastSeen = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            foreach (var seen in devices.List())
            {
                if (seen.Id is { } seenId)
                    lastSeen.TryAdd(seenId, seen.LastSeen);
            }
            var people = auth.ListDevices()
                .Where(d => !d.AccessOrOwner.IsOwner)
                .GroupBy(d => d.PersonId)
                .Select(person =>
                {
                    // All of one person's computers share one access; the newest one's line is the freshest.
                    var newest = person.Last();
                    return new PersonView(person.Key, newest.Person, newest.AccessOrOwner.Level, newest.AccessOrOwner.Folders,
                        person.Select(d => new PersonDevice(d.Id, d.Name, d.System, lastSeen.TryGetValue(d.Id, out var at) ? at : null)).ToList());
                })
                .ToList();
            return Json(new PeopleView(people, requests.List()));
        });
    }

    private static IResult Json<T>(T value) => Results.Json(value, PairnetsJson.Options);
}
