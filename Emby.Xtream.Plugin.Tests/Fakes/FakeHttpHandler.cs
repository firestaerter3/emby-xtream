using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.Xtream.Plugin.Tests.Fakes
{
    /// <summary>
    /// Intercepts HttpClient calls and returns pre-registered responses.
    /// Register responses with RespondWith() before the code under test runs.
    /// Requests with no matching registration throw InvalidOperationException.
    /// </summary>
    public sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly List<(string UrlSubstring, Queue<(string Body, HttpStatusCode Status)> Responses)> _rules
            = new List<(string, Queue<(string, HttpStatusCode)>)>();

        /// <summary>Guards _rules, its queues, and ReceivedUrls.</summary>
        private readonly object _sync = new object();

        public List<string> ReceivedUrls { get; } = new List<string>();

        /// <summary>
        /// Optional gate. When set, every request waits on it before responding.
        /// </summary>
        /// <remarks>
        /// Responses are otherwise produced synchronously, so a caller runs to completion before it
        /// ever returns its Task. That makes it impossible to hold two calls genuinely in flight,
        /// which is exactly what a concurrency test needs. Complete this to let requests through.
        /// </remarks>
        public TaskCompletionSource<bool> Gate { get; set; }

        /// <summary>
        /// Optional per-URL gates. When a request URL contains a key from this dictionary,
        /// the request awaits that entry's TCS instead of the global <see cref="Gate"/>.
        /// Lets a test hold one specific request in flight while letting other matching
        /// requests proceed.
        /// </summary>
        public System.Collections.Generic.Dictionary<string, TaskCompletionSource<bool>> UrlGates { get; }
            = new System.Collections.Generic.Dictionary<string, TaskCompletionSource<bool>>();

        /// <summary>Register a single response for URLs containing <paramref name="urlSubstring"/>.</summary>
        public void RespondWith(string urlSubstring, string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            var q = new Queue<(string, HttpStatusCode)>();
            q.Enqueue((body, status));
            lock (_sync) { _rules.Add((urlSubstring, q)); }
        }

        /// <summary>Register multiple ordered responses for the same URL pattern.</summary>
        public void RespondWithSequence(string urlSubstring, IEnumerable<string> bodies, HttpStatusCode status = HttpStatusCode.OK)
        {
            var q = new Queue<(string, HttpStatusCode)>();
            foreach (var b in bodies)
                q.Enqueue((b, status));
            lock (_sync) { _rules.Add((urlSubstring, q)); }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;

            TaskCompletionSource<bool> perUrlGate = null;
            lock (_sync)
            {
                foreach (var kv in UrlGates)
                {
                    if (url.Contains(kv.Key))
                    {
                        perUrlGate = kv.Value;
                        break;
                    }
                }
            }

            if (perUrlGate != null)
            {
                await perUrlGate.Task.ConfigureAwait(false);
            }
            else
            {
                var gate = Gate;
                if (gate != null)
                {
                    await gate.Task.ConfigureAwait(false);
                }
            }

            // One lock over all shared state. Releasing the gate resumes several requests at once
            // on thread-pool threads, and Queue<T> is not thread-safe: concurrent dequeues can
            // hand back the wrong body or throw.
            lock (_sync)
            {
                ReceivedUrls.Add(url);

                foreach (var (urlSubstring, queue) in _rules)
                {
                    if (url.Contains(urlSubstring) && queue.Count > 0)
                    {
                        var (body, status) = queue.Dequeue();
                        return new HttpResponseMessage(status)
                        {
                            Content = new StringContent(body, Encoding.UTF8, "application/json")
                        };
                    }
                }
            }

            throw new InvalidOperationException(
                $"FakeHttpHandler: no registered response for URL: {url}\n" +
                $"Register one with handler.RespondWith(\"{url}\", json)");
        }

        protected override void Dispose(bool disposing) { }
    }
}
