using BeatSaverSharp;
using BeatSaverSharp.Http;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PlaylistManager.Utilities
{
    public sealed class PlaylistHttpException : Exception
    {
        public int StatusCode { get; }
        public bool IsTransient { get; }
        public PlaylistHttpException(string message, int statusCode, bool transient, Exception inner = null) : base(message, inner)
        {
            StatusCode = statusCode;
            IsTransient = transient;
        }
    }

    public static class BeatSaverClientFactory
    {
        // BeatSaverSharp 3.4.5 fixes its transport in a private field. Replace that
        // transport once, retaining its models, client association and pagination.
        private static readonly FieldInfo Transport = typeof(BeatSaver).GetField("_httpService", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(BeatSaver).FullName, "_httpService");

        public static BeatSaver Create(BeatSaverOptions options)
        {
            var client = new BeatSaver(options);
            var previous = Transport.GetValue(client) as IDisposable;
            var transport = new PlaylistHttpService(options.BeatSaverAPI, options.ApplicationName + "/" + options.Version);
            try { Transport.SetValue(client, transport); }
            catch { transport.Dispose(); client.Dispose(); throw; }
            previous?.Dispose();
            return client;
        }
    }

    internal sealed class PlaylistHttpService : IHttpService, IDisposable
    {
        private readonly HttpClient client;
        private readonly Uri baseUri;
        private readonly CancellationTokenSource lifetime = new();

        public PlaylistHttpService(Uri baseUri, string userAgent)
        {
            this.baseUri = baseUri;
            client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate });
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
        }

        public Task<IHttpResponse> GetAsync(string url, CancellationToken token = default, IProgress<double> progress = null)
            => SendAsync(HttpMethod.Get, url, null, token, progress);

        public Task<IHttpResponse> PostAsync(string url, object content, CancellationToken token = default)
            => SendAsync(HttpMethod.Post, url, content, token, null);

        private async Task<IHttpResponse> SendAsync(HttpMethod method, string url, object content, CancellationToken token, IProgress<double> progress)
        {
            var uri = Uri.TryCreate(url, UriKind.Absolute, out var absolute) &&
                (absolute.Scheme == Uri.UriSchemeHttps || absolute.Scheme == Uri.UriSchemeHttp) ? absolute : new Uri(baseUri, url);
            var attempts = method == HttpMethod.Get ? 3 : 1;
            using var linkedLifetime = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                linkedLifetime.Token.ThrowIfCancellationRequested();
                using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(linkedLifetime.Token);
                requestCancellation.CancelAfter(TimeSpan.FromSeconds(uri.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? 90 : 30));
                TimeSpan retryDelay = TimeSpan.FromMilliseconds(300 * attempt);
                try
                {
                    using var request = new HttpRequestMessage(method, uri);
                    if (content != null) request.Content = new StringContent(JsonConvert.SerializeObject(content), Encoding.UTF8, "application/json");
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestCancellation.Token).ConfigureAwait(false);
                    using var abortResponse = requestCancellation.Token.Register(response.Dispose);
                    var status = (int)response.StatusCode;
                    if (!response.IsSuccessStatusCode)
                    {
                        var transient = status == 408 || status == 429 || status >= 500;
                        var after = response.Headers.RetryAfter;
                        var delay = after?.Delta ?? (after?.Date - DateTimeOffset.UtcNow);
                        if (delay.HasValue) retryDelay = TimeSpan.FromSeconds(Math.Max(0, Math.Min(5, delay.Value.TotalSeconds)));
                        throw new PlaylistHttpException("HTTP " + status + " while requesting " + uri, status, transient);
                    }

                    using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    using var bytes = new MemoryStream();
                    var buffer = new byte[64 * 1024];
                    var length = response.Content.Headers.ContentLength;
                    int read;
                    while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, requestCancellation.Token).ConfigureAwait(false)) > 0)
                    {
                        bytes.Write(buffer, 0, read);
                        if (length > 0) progress?.Report(Math.Min(0.99, bytes.Length / (double)length.Value));
                    }
                    requestCancellation.Token.ThrowIfCancellationRequested();
                    progress?.Report(1);
                    return new BufferedResponse(status, bytes.ToArray());
                }
                catch (OperationCanceledException) when (linkedLifetime.IsCancellationRequested) { throw; }
                catch (Exception exception) when (linkedLifetime.IsCancellationRequested)
                {
                    throw new OperationCanceledException("Request cancelled: " + uri, exception, linkedLifetime.Token);
                }
                catch (OperationCanceledException exception)
                {
                    if (attempt == attempts) throw new TimeoutException("Request timed out: " + uri, exception);
                }
                catch (Exception exception) when (requestCancellation.IsCancellationRequested)
                {
                    if (attempt == attempts) throw new TimeoutException("Response timed out: " + uri, exception);
                }
                catch (PlaylistHttpException exception) when (exception.IsTransient && attempt < attempts) { }
                catch (HttpRequestException exception)
                {
                    if (attempt == attempts) throw new PlaylistHttpException("Network request failed: " + uri, 0, true, exception);
                }
                catch (IOException exception)
                {
                    if (attempt == attempts) throw new PlaylistHttpException("Network response failed: " + uri, 0, true, exception);
                }
                await Task.Delay(retryDelay, linkedLifetime.Token).ConfigureAwait(false);
            }
            throw new InvalidOperationException("Request exhausted without a result: " + uri);
        }

        public void Dispose()
        {
            lifetime.Cancel();
            client.Dispose();
        }

        private sealed class BufferedResponse : IHttpResponse
        {
            private readonly byte[] bytes;
            public int Code { get; }
            public bool Successful => true;
            public BufferedResponse(int code, byte[] bytes) { Code = code; this.bytes = bytes; }
            public Task<string> ReadAsStringAsync() => Task.FromResult(Encoding.UTF8.GetString(bytes));
            public Task<byte[]> ReadAsByteArrayAsync() => Task.FromResult(bytes);
            Task<T> IHttpResponse.ReadAsObjectAsync<T>()
            {
                var value = JsonConvert.DeserializeObject<T>(Encoding.UTF8.GetString(bytes));
                if (value is null) throw new InvalidDataException("The service returned a null JSON response");
                return Task.FromResult(value);
            }
            public Task PopulateObjectAsync(object target)
            {
                JsonConvert.PopulateObject(Encoding.UTF8.GetString(bytes), target);
                return Task.CompletedTask;
            }
        }
    }
}
