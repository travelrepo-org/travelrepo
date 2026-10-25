using System.Net;
using System.Net.Http.Headers;
using LibGit2Sharp;

namespace TravelRepo.Git.LibGit2;

/// <summary>
/// Git's smart HTTP protocol over <see cref="HttpClient"/>, registered for <c>http</c> and <c>https</c>. libgit2 then
/// needs no TLS library of its own: Android uses its own TLS and certificate store, desktops use .NET's, and system
/// proxies apply as they do for other requests.
/// </summary>
internal sealed class ManagedHttpTransport : RpcSmartSubtransport
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = true, AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromMinutes(2) };
    private static int registered;
    /// <summary>libgit2 reports a failed transport as "no error", so the reason is kept here, per thread, for the backend.</summary>
    [ThreadStatic] private static string? lastFailure;

    /// <summary>The reason the last request on this thread failed, if any; reading it clears it.</summary>
    public static string? TakeFailure() { var failure = lastFailure; lastFailure = null; return failure; }

    /// <summary>Registers the transport once per process. Later calls do nothing.</summary>
    public static void Register()
    {
        if (Interlocked.Exchange(ref registered, 1) == 1) return;
        GlobalSettings.RegisterSmartSubtransport<ManagedHttpTransport>("http");
        GlobalSettings.RegisterSmartSubtransport<ManagedHttpTransport>("https");
    }

    protected override SmartSubtransportStream Action(string url, GitSmartSubtransportAction action) => action switch
    {
        GitSmartSubtransportAction.UploadPackList => new Exchange(this, url + "/info/refs?service=git-upload-pack", null),
        GitSmartSubtransportAction.UploadPack => new Exchange(this, url + "/git-upload-pack", "application/x-git-upload-pack-request"),
        GitSmartSubtransportAction.ReceivePackList => new Exchange(this, url + "/info/refs?service=git-receive-pack", null),
        GitSmartSubtransportAction.ReceivePack => new Exchange(this, url + "/git-receive-pack", "application/x-git-receive-pack-request"),
        _ => throw new LibGit2SharpException("Unsupported Git transport action " + action)
    };

    /// <summary>One request: libgit2 writes the body (for POST), then reads the response.</summary>
    private sealed class Exchange(ManagedHttpTransport owner, string endpoint, string? postType) : SmartSubtransportStream(owner)
    {
        private readonly MemoryStream body = new();
        private Stream? response;
        private AuthenticationHeaderValue? authorization;

        public override int Write(Stream dataStream, long length)
        {
            var buffer = new byte[81920]; var left = length;
            while (left > 0) { var read = dataStream.Read(buffer, 0, (int)Math.Min(buffer.Length, left)); if (read <= 0) break; body.Write(buffer, 0, read); left -= read; }
            return 0;
        }

        public override int Read(Stream dataStream, long length, out long bytesRead)
        {
            response ??= Send();
            var buffer = new byte[Math.Min(length, 81920)];
            var read = response.Read(buffer, 0, buffer.Length);
            dataStream.Write(buffer, 0, read); bytesRead = read;
            return 0;
        }

        private Stream Send()
        {
            for (var attempt = 0; ; attempt++)
            {
                using var request = new HttpRequestMessage(postType is null ? HttpMethod.Get : HttpMethod.Post, endpoint);
                request.Headers.UserAgent.ParseAdd("git/2.0 (TravelRepo libgit2)");
                if (authorization is not null) request.Headers.Authorization = authorization;
                if (postType is not null)
                {
                    request.Content = new ByteArrayContent(body.ToArray()); request.Content.Headers.ContentType = new MediaTypeHeaderValue(postType);
                    request.Headers.Accept.ParseAdd(postType.Replace("-request", "-result", StringComparison.Ordinal));
                }
                HttpResponseMessage result;
                // Android's handler has no synchronous Send; libgit2 calls this on a background thread, so waiting is safe.
                try { result = Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult(); }
                catch (Exception ex) { lastFailure = "Failed to connect: " + ex.Message; throw; }
                // Ask libgit2 for the sign-in once; it comes from the credentials provider of the fetch or push.
                if (result.StatusCode == HttpStatusCode.Unauthorized && attempt == 0 && SignIn()) { result.Dispose(); continue; }
                if (!result.IsSuccessStatusCode)
                {
                    var status = (int)result.StatusCode; result.Dispose();
                    lastFailure = status is 401 or 403 ? "authentication failed: request failed with status code: " + status
                        : status == 404 ? "repository not found: request failed with status code: 404" : "unexpected HTTP status code: " + status;
                    throw new LibGit2SharpException(lastFailure);
                }
                return result.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
            }
        }

        private bool SignIn()
        {
            if (SmartTransport.AcquireCredentials(out var credentials, null, typeof(UsernamePasswordCredentials)) != 0 || credentials is not UsernamePasswordCredentials password) return false;
            authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(password.Username + ":" + password.Password)));
            return true;
        }

        protected override void Free()
        {
            response?.Dispose(); body.Dispose();
            base.Free();
        }
    }
}
