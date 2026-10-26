using Pairnets.Core;
using Pairnets.Server.Services;

namespace Pairnets.Server.Web;

/// <summary>
/// "Downloads on the nest" (1.0.61.8): the owner's computers hand the nest a link and it downloads the file by itself
/// (<see cref="NestDownloader"/>). Only the owner: <see cref="AccessGate"/> answers a friend's computer 403 <c>owner_only</c>.
/// <list type="bullet">
/// <item><c>GET /api/downloads</c>: the list (<see cref="NestDownloadList"/>).</item>
/// <item><c>POST /api/downloads</c> {url, folder}: adds one; 400 with a <see cref="NestDownloadErrors"/> code when refused.</item>
/// <item><c>DELETE /api/downloads/{id}</c>: stops one (its half-done file goes) or takes a finished one off the list.</item>
/// <item><c>POST /api/downloads/{id}/retry</c>: "Try again" on a stopped one.</item>
/// <item><c>GET /api/downloads/{id}/file</c>: a held download of an encrypted nest (<see cref="NestDownload.Plain"/>).</item>
/// </list>
/// </summary>
public static class DownloadEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/downloads", (NestDownloader downloads) => Json(downloads.List()));

        app.MapPost("/api/downloads", (NestDownloadRequest? body, NestDownloader downloads) =>
        {
            var (download, error) = downloads.Add(body?.Url, body?.Folder);
            return download is not null
                ? Json(download)
                : Error(error == NestDownloadErrors.Full ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest, error!);
        });

        app.MapDelete("/api/downloads/{id}", (string id, NestDownloader downloads) =>
            downloads.Remove(id) ? Results.NoContent() : NoSuch());

        app.MapPost("/api/downloads/{id}/retry", (string id, NestDownloader downloads) =>
            downloads.Retry(id) ? Results.NoContent() : NoSuch());

        app.MapGet("/api/downloads/{id}/file", (string id, NestDownloader downloads) =>
            downloads.HeldFile(id) is { } file
                ? Results.File(file, "application/octet-stream", Path.GetFileName(file), enableRangeProcessing: true)
                : NoSuch());
    }

    private static IResult Json<T>(T value) => Results.Json(value, PairnetsJson.Options);

    private static IResult Error(int status, string code) =>
        Results.Json(new ErrorBody(code, NestDownloadErrors.Text(code)), PairnetsJson.Options, statusCode: status);

    private static IResult NoSuch() =>
        Results.Json(new ErrorBody(ErrorCodes.NotFound, "No such download."), PairnetsJson.Options, statusCode: StatusCodes.Status404NotFound);
}
