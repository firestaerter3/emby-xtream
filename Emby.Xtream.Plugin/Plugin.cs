using System;
using System.Collections.Generic;
using Emby.Xtream.Plugin.Service;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Emby.Xtream.Plugin
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        private static volatile Plugin _instance;
        private readonly IApplicationHost _applicationHost;
        private LiveTvService _liveTvService;
        private StrmSyncService _strmSyncService;

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogManager logManager, IApplicationHost applicationHost)
            : base(applicationPaths, xmlSerializer)
        {
            _instance = this;
            _applicationHost = applicationHost;
            _liveTvService = new LiveTvService(logManager.GetLogger("XtreamTuner.LiveTv"));
            _strmSyncService = new StrmSyncService(logManager.GetLogger("XtreamTuner.StrmSync"));
        }

        public override string Name => "Xtream Tuner";

        public override string Description =>
            "Xtream-compatible Live TV tuner with EPG, category filtering, and pre-populated media info.";

        public override Guid Id => Guid.Parse("b7e3c4a1-9f2d-4e8b-a5c6-d1f0e2b3c4a5");

        public static Plugin Instance => _instance ?? throw new InvalidOperationException("Plugin not initialized");

        public IApplicationHost ApplicationHost => _applicationHost;

        public LiveTvService LiveTvService => _liveTvService;

        public StrmSyncService StrmSyncService => _strmSyncService;

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "xtreamconfig",
                    EmbeddedResourcePath = "Emby.Xtream.Plugin.Configuration.Web.config.html",
                    IsMainConfigPage = true,
                },
                new PluginPageInfo
                {
                    Name = "xtreamconfigjs",
                    EmbeddedResourcePath = "Emby.Xtream.Plugin.Configuration.Web.config.js",
                },
            };
        }
    }
}
