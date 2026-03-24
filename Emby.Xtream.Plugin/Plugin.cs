using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using Emby.Xtream.Plugin.Service;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Emby.Xtream.Plugin
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage
    {
        private static volatile Plugin _instance;
        private readonly IApplicationHost _applicationHost;
        private readonly IApplicationPaths _applicationPaths;
        private LiveTvService _liveTvService;
        private StrmSyncService _strmSyncService;
        private GracenoteDbService _gracenoteDbService;

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogManager logManager, IApplicationHost applicationHost)
            : base(applicationPaths, xmlSerializer)
        {
            _instance = this;
            _applicationHost = applicationHost;
            _applicationPaths = applicationPaths;
            _liveTvService = new LiveTvService(logManager.GetLogger("XtreamTuner.LiveTv"));
            _strmSyncService = new StrmSyncService(logManager.GetLogger("XtreamTuner.StrmSync"));
            _gracenoteDbService = new GracenoteDbService(logManager.GetLogger("XtreamTuner.GracenoteDb"));
        }

        public override string Name => "Xtream Tuner";

        public override string Description =>
            "Xtream-compatible Live TV tuner with EPG, category filtering, and pre-populated media info.";

        public override Guid Id => Guid.Parse("b7e3c4a1-9f2d-4e8b-a5c6-d1f0e2b3c4a5");

        public static Plugin Instance => _instance ?? throw new InvalidOperationException("Plugin not initialized");

        /// <summary>Returns the current instance, or null if the plugin has not been initialised (e.g. during unit tests).</summary>
        internal static Plugin InstanceOrNull => _instance;

        public IApplicationHost ApplicationHost => _applicationHost;

        public new IApplicationPaths ApplicationPaths => _applicationPaths;

        /// <summary>
        /// Creates an HttpClient configured with the plugin's User-Agent setting.
        /// </summary>
        public static HttpClient CreateHttpClient(int timeoutSeconds = 10)
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
            var ua = _instance?.Configuration?.HttpUserAgent;
            if (!string.IsNullOrEmpty(ua))
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ua);
            return client;
        }

        public LiveTvService LiveTvService => _liveTvService;

        public StrmSyncService StrmSyncService => _strmSyncService;

        public GracenoteDbService GracenoteDbService => _gracenoteDbService;

        public Stream GetThumbImage()
        {
            return GetType().Assembly.GetManifestResourceStream("Emby.Xtream.Plugin.thumb.png");
        }

        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = GetHtmlPageName(),
                    EmbeddedResourcePath = "Emby.Xtream.Plugin.Configuration.Web.config.html",
                    IsMainConfigPage = true,
                    EnableInMainMenu = true,
                    MenuIcon = "live_tv",
                },
                new PluginPageInfo
                {
                    // Alias: Emby's Admin Plugins page derives the settings URL from
                    // Plugin.Name with spaces stripped → "XtreamTuner". Registering that
                    // name here ensures the Plugins management page links work as well.
                    Name = "XtreamTuner",
                    EmbeddedResourcePath = "Emby.Xtream.Plugin.Configuration.Web.config.html",
                },
                new PluginPageInfo
                {
                    Name = GetJsPageName(),
                    EmbeddedResourcePath = "Emby.Xtream.Plugin.Configuration.Web.config.js",
                },
            };
        }

        /// <summary>
        /// Returns a stable page name for config.html. Must never change between versions —
        /// if it did, the Emby SPA would navigate to a stale URL after a banner install and
        /// show "error processing request" because the old page name no longer exists in the
        /// new DLL. Emby appends ?v=&lt;ServerVersion&gt; for cache-busting.
        /// </summary>
        private static string GetHtmlPageName()
        {
            return "xtreamconfig";
        }

        /// <summary>
        /// Returns a stable JS page name. Emby appends ?v=&lt;ServerVersion&gt; automatically,
        /// which provides sufficient cache-busting across plugin updates.
        /// </summary>
        private static string GetJsPageName()
        {
            return "xtreamconfigjs";
        }
    }
}
