using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

// Usage: h2test <urlFile> <ua> <from> <to> <h1|h2|auto|h3|h3plain>
//   for h3plain, from/to are ignored and NO Range header is sent.
AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.HTTP3Support", true);

var url = File.ReadAllText(args[0]).Trim();
var ua = args[1];
var from = long.Parse(args[2]);
var to = long.Parse(args[3]);
var mode = args[4];

var handler = new SocketsHttpHandler
{
    AutomaticDecompression = DecompressionMethods.None,
    ConnectTimeout = TimeSpan.FromSeconds(15)
};

using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
using var req = new HttpRequestMessage(HttpMethod.Get, url);
req.Headers.TryAddWithoutValidation("User-Agent", ua);

var plain = mode == "h3plain";
if (!plain)
{
    req.Headers.Range = new RangeHeaderValue(from, to);
}

switch (mode)
{
    case "h2":
        req.Version = HttpVersion.Version20;
        req.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        break;
    case "auto":
        req.Version = HttpVersion.Version20;
        req.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        break;
    case "h3":
    case "h3plain":
        req.Version = HttpVersion.Version30;
        req.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        break;
    // default h1: Version1.1
}

try
{
    using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
    var range = resp.Content.Headers.ContentRange;
    long read = 0;
    if (resp.IsSuccessStatusCode)
    {
        await using var s = await resp.Content.ReadAsStreamAsync();
        var buf = new byte[64 * 1024];
        int n;
        while ((n = await s.ReadAsync(buf)) > 0) read += n;
    }
    Console.WriteLine($"HTTP {resp.StatusCode} negotiated={resp.Version} read={read} contentRange={range}");
}
catch (Exception ex)
{
    Console.WriteLine($"EXC: {ex.GetType().Name}: {ex.Message}");
}
