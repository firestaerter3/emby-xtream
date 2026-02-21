using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
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

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogManager logManager, IApplicationHost applicationHost)
            : base(applicationPaths, xmlSerializer)
        {
            _instance = this;
            _applicationHost = applicationHost;
            _applicationPaths = applicationPaths;
            _liveTvService = new LiveTvService(logManager.GetLogger("XtreamTuner.LiveTv"));
            _strmSyncService = new StrmSyncService(logManager.GetLogger("XtreamTuner.StrmSync"));
        }

        public override string Name => "Xtream Tuner";

        public override string Description =>
            "Xtream-compatible Live TV tuner with EPG, category filtering, and pre-populated media info.";

        public override Guid Id => Guid.Parse("b7e3c4a1-9f2d-4e8b-a5c6-d1f0e2b3c4a5");

        public static Plugin Instance => _instance ?? throw new InvalidOperationException("Plugin not initialized");

        public IApplicationHost ApplicationHost => _applicationHost;

        public new IApplicationPaths ApplicationPaths => _applicationPaths;

        public LiveTvService LiveTvService => _liveTvService;

        public StrmSyncService StrmSyncService => _strmSyncService;

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
                    Name = GetJsPageName(),
                    EmbeddedResourcePath = "Emby.Xtream.Plugin.Configuration.Web.config.js",
                },
            };
        }

        /// <summary>
        /// Returns a stable page name for config.html. Must never change between versions —
        /// if it did, the Emby SPA would navigate to a stale URL after a banner install and
        /// show "error processing request" because the old page name no longer exists in the
        /// new DLL. JS cache-busting is handled separately via GetJsPageName().
        /// </summary>
        private static string GetHtmlPageName()
        {
            return "xtreamconfig";
        }

        /// <summary>
        /// Returns a stable JS page name derived from an 8-char MD5 hash of the embedded
        /// config.js content. The build script stamps the same hash into config.html's
        /// data-controller, so the browser always loads a fresh JS URL after each build.
        /// </summary>
        private static string GetJsPageName()
        {
            return GetEmbeddedResourcePageName("Emby.Xtream.Plugin.Configuration.Web.config.js", "xtreamconfigjs");
        }

        private static string GetEmbeddedResourcePageName(string resourcePath, string fallback)
        {
            try
            {
                using (var stream = typeof(Plugin).Assembly.GetManifestResourceStream(resourcePath))
                {
                    if (stream == null) return fallback;
                    using (var md5 = MD5.Create())
                    {
                        var hash = md5.ComputeHash(stream);
                        var slug = BitConverter.ToString(hash).Replace("-", string.Empty)
                                               .Substring(0, 8).ToLowerInvariant();
                        return fallback + slug;
                    }
                }
            }
            catch
            {
                return fallback;
            }
        }
    }
}
