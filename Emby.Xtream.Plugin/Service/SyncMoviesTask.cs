using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace Emby.Xtream.Plugin.Service
{
    public class SyncMoviesTask : IScheduledTask
    {
        private readonly ILogger _logger;

        public SyncMoviesTask(ILogManager logManager)
            => _logger = logManager.GetLogger("XtreamTuner.SyncMoviesTask");

        public string Name        => "Xtream Tuner \u2013 Sync Movies";
        public string Description => "Sync VOD movie STRM files from the Xtream server.";
        public string Category    => "Xtream Tuner";
        public string Key         => "XtreamTunerSyncMovies";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // One-time default (first install). Plugin UI overrides this via Emby's trigger API.
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerDaily,
                TimeOfDayTicks = TimeSpan.FromHours(3).Ticks  // 03:00
            };
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var config = Plugin.Instance.Configuration;
            if (!config.SyncMovies)
            {
                _logger.Info("Movie sync disabled — skipping.");
                return;
            }
            var svc = Plugin.Instance.StrmSyncService;
            if (svc.MovieProgress.IsRunning)
            {
                _logger.Info("Movie sync already running — skipping scheduled run.");
                return;
            }
            progress.Report(0);
            await svc.SyncMoviesAsync(cancellationToken).ConfigureAwait(false);
            progress.Report(100);
        }
    }
}
