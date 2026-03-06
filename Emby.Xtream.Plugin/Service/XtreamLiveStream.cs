using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Logging;

namespace Emby.Xtream.Plugin.Service
{
    internal sealed class XtreamLiveStream : ILiveStream, IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;
        private HttpResponseMessage _response;
        private Stream _stream;
        private bool _disposed;
        private bool _needsReconnect;

        public XtreamLiveStream(MediaSourceInfo mediaSource, string tunerHostId, HttpClient httpClient, ILogger logger = null)
        {
            MediaSource = mediaSource;
            _httpClient = httpClient;
            _logger = logger;
            UniqueId = Guid.NewGuid().ToString("N");
            TunerHostId = tunerHostId;
            OriginalStreamId = mediaSource.Id;
            DateOpened = DateTimeOffset.UtcNow;
        }

        public int ConsumerCount { get; set; }
        public string OriginalStreamId { get; set; }
        public string TunerHostId { get; }
        public bool EnableStreamSharing => false;
        public MediaSourceInfo MediaSource { get; set; }
        public string UniqueId { get; }
        public DateTimeOffset DateOpened { get; }
        public bool SupportsCopyTo => true;

        // Open() is a no-op: Emby calls it to register the tuner host lifecycle
        // (RequiresOpening/RequiresClosing), but for browser HLS playback ffmpeg
        // opens its own connection directly. Connecting here would create a second
        // idle connection in Dispatcharr. The actual HTTP connection is deferred to
        // the first CopyToAsync() call via ConnectAsync().
        public Task Open(CancellationToken openCancellationToken)
        {
            _logger?.Info("[XtreamLiveStream] Open called (deferred connect)");
            return Task.CompletedTask;
        }

        private async Task ConnectAsync(CancellationToken cancellationToken)
        {
            var sw = Stopwatch.StartNew();
            _response = await _httpClient.GetAsync(
                MediaSource.Path,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            _logger?.Info("[stream-timing] Connect.HttpGet={0}ms status={1}", sw.ElapsedMilliseconds, (int)_response.StatusCode);
            sw.Restart();

            _response.EnsureSuccessStatusCode();
            _stream = await _response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            _logger?.Info("[stream-timing] Connect.StreamReady={0}ms", sw.ElapsedMilliseconds);
        }

        public Task Close()
        {
            Dispose();
            return Task.CompletedTask;
        }

        // Reopen the HTTP connection to the upstream source. Called when a prior CopyToAsync
        // was cancelled mid-read, which aborts the underlying SSL connection and leaves _stream
        // in a disposed state. RequiresOpening=true means the same XtreamLiveStream is reused
        // across the iOS/Apple TV player's probe connection (~500 ms) and its real playback
        // connection, so we must be able to reconnect after the probe disconnects.
        private async Task ReopenStreamAsync(CancellationToken cancellationToken)
        {
            _stream?.Dispose();
            _response?.Dispose();
            _stream = null;
            _response = null;
            _needsReconnect = false;

            _response = await _httpClient.GetAsync(
                MediaSource.Path,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            _response.EnsureSuccessStatusCode();
            _stream = await _response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            _logger?.Info("[stream-timing] Reconnected to upstream after client disconnect");
        }

        public async Task CopyToAsync(PipeWriter writer, CancellationToken cancellationToken)
        {
            if (_stream == null && !_needsReconnect)
                await ConnectAsync(cancellationToken).ConfigureAwait(false);

            if (_needsReconnect)
                await ReopenStreamAsync(cancellationToken).ConfigureAwait(false);

            var buffer = new byte[262144];
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    int bytesRead = await _stream.ReadAsync(
                        buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);

                    if (bytesRead == 0) break;

                    var writeBuffer = writer.GetMemory(bytesRead);
                    buffer.AsMemory(0, bytesRead).CopyTo(writeBuffer);
                    writer.Advance(bytesRead);

                    var flushResult = await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                    if (flushResult.IsCompleted) break;
                }
            }
            catch (OperationCanceledException)
            {
                // Client disconnected mid-read. Mark for reconnect so the next CopyToAsync
                // call (e.g. the player's real connection after a probe) opens a fresh
                // upstream connection instead of reading from the now-aborted SSL stream.
                _needsReconnect = true;
                throw;
            }
            finally
            {
                await writer.CompleteAsync().ConfigureAwait(false);
            }
        }

        public async Task CopyToAsync(
            Stream writer,
            DateTimeOffset? wallClockStartTime,
            Action<SegmentedStreamSegmentInfo> onSegmentWritten,
            CancellationToken cancellationToken)
        {
            if (_stream == null)
                await ConnectAsync(cancellationToken).ConfigureAwait(false);

            await _stream.CopyToAsync(writer, 262144, cancellationToken).ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _stream?.Dispose();
                _response?.Dispose();
                _httpClient?.Dispose();
                _disposed = true;
            }
        }
    }
}
