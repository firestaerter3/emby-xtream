define(['baseView', 'loading', 'emby-input', 'emby-select', 'emby-checkbox', 'emby-button'],
function (BaseView, loading) {
    'use strict';

    var pluginId = 'b7e3c4a1-9f2d-4e8b-a5c6-d1f0e2b3c4a5';

    function View(view, params) {
        BaseView.apply(this, arguments);

        this.loadedCategories = [];
        this.selectedCategoryIds = [];
        this.loadedVodCategories = [];
        this.selectedVodCategoryIds = [];
        this.loadedSeriesCategories = [];
        this.selectedSeriesCategoryIds = [];

        var self = this;

        view.querySelector('.xtreamConfigForm').addEventListener('submit', function (e) {
            e.preventDefault();
            saveConfig(self);
        });

        view.querySelector('.chkEnableNameCleaning').addEventListener('change', function () {
            updateNameCleaningVisibility(view);
        });

        view.querySelector('.chkEnableTmdbFolderNaming').addEventListener('change', function () {
            updateTmdbVisibility(view);
        });

        view.querySelector('.chkEnableDispatcharr').addEventListener('change', function () {
            updateDispatcharrVisibility(view);
        });

        view.querySelector('.chkEnableEpg').addEventListener('change', function () {
            updateEpgVisibility(view);
        });

        view.querySelector('.chkEnableCatchup').addEventListener('change', function () {
            updateCatchupVisibility(view);
        });

        view.querySelector('.chkSyncMovies').addEventListener('change', function () {
            updateVodMovieVisibility(view);
        });

        view.querySelector('.chkSyncSeries').addEventListener('change', function () {
            updateSeriesVisibility(view);
        });

        view.querySelector('.selMovieFolderMode').addEventListener('change', function () {
            updateFoldersVisibility(view, 'movie');
        });

        view.querySelector('.selSeriesFolderMode').addEventListener('change', function () {
            updateFoldersVisibility(view, 'series');
        });

        view.querySelector('.chkAutoSyncEnabled').addEventListener('change', function () {
            updateAutoSyncVisibility(view);
        });

        view.querySelector('.selAutoSyncMode').addEventListener('change', function () {
            updateAutoSyncVisibility(view);
        });

        view.querySelector('.btnAddMovieFolder').addEventListener('click', function () {
            addFolderEntry(view, 'movie', '', '', self.loadedVodCategories);
        });

        view.querySelector('.btnAddSeriesFolder').addEventListener('click', function () {
            addFolderEntry(view, 'series', '', '', self.loadedSeriesCategories);
        });

        view.querySelector('.txtStrmLibraryPath').addEventListener('blur', function () {
            validateStrmPath(view);
        });

        view.querySelector('.btnBrowseStrmPath').addEventListener('click', function () {
            openBrowser(view);
        });

        view.querySelector('.btnCloseBrowser').addEventListener('click', function () {
            closeBrowser(view);
        });

        view.querySelector('.btnBrowserCancel').addEventListener('click', function () {
            closeBrowser(view);
        });

        view.querySelector('.btnBrowserOk').addEventListener('click', function () {
            var path = (view.querySelector('.txtBrowserCurrentPath').value || '').trim();
            if (path) {
                view.querySelector('.txtStrmLibraryPath').value = path;
                validateStrmPath(view);
            }
            closeBrowser(view);
        });

        view.querySelector('.txtBrowserCurrentPath').addEventListener('keydown', function (e) {
            if (e.key === 'Enter') { e.preventDefault(); browserNavigate(view, this.value.trim() || null); }
        });

        view.querySelector('.strmBrowserModal').addEventListener('click', function (e) {
            if (e.target === this) closeBrowser(view);
        });

        view.querySelector('.btnTestConnection').addEventListener('click', function () {
            testXtreamConnection(view);
        });

        view.querySelector('.btnTestDispatcharr').addEventListener('click', function () {
            testDispatcharrConnection(self);
        });

        view.querySelector('.btnLoadCategories').addEventListener('click', function () {
            loadCategories(self);
        });

        view.querySelector('.btnSelectAllCategories').addEventListener('click', function () {
            toggleAllCategories(view, true);
        });

        view.querySelector('.btnDeselectAllCategories').addEventListener('click', function () {
            toggleAllCategories(view, false);
        });

        view.querySelector('.btnRefreshCache').addEventListener('click', function () {
            refreshCache(view);
        });

        // VOD category buttons (single mode)
        view.querySelector('.btnLoadVodCategories').addEventListener('click', function () {
            loadVodCategories(self);
        });

        view.querySelector('.btnSelectAllVodCategories').addEventListener('click', function () {
            toggleAllVodCategories(view, true);
        });

        view.querySelector('.btnDeselectAllVodCategories').addEventListener('click', function () {
            toggleAllVodCategories(view, false);
        });

        // VOD category buttons (multi mode)
        view.querySelector('.btnLoadVodCategoriesMulti').addEventListener('click', function () {
            loadVodCategoriesMulti(self);
        });

        // Series category buttons (single mode)
        view.querySelector('.btnLoadSeriesCategories').addEventListener('click', function () {
            loadSeriesCategories(self);
        });

        view.querySelector('.btnSelectAllSeriesCategories').addEventListener('click', function () {
            toggleAllSeriesCategories(view, true);
        });

        view.querySelector('.btnDeselectAllSeriesCategories').addEventListener('click', function () {
            toggleAllSeriesCategories(view, false);
        });

        // Series category buttons (multi mode)
        view.querySelector('.btnLoadSeriesCategoriesMulti').addEventListener('click', function () {
            loadSeriesCategoriesMulti(self);
        });

        // Sync buttons
        view.querySelector('.btnSyncMovies').addEventListener('click', function () {
            syncMovies(view);
        });

        view.querySelector('.btnSyncSeries').addEventListener('click', function () {
            syncSeries(view);
        });

        // Delete content buttons
        view.querySelector('.btnDeleteMovies').addEventListener('click', function () {
            deleteContent(view, 'Movies');
        });

        view.querySelector('.btnDeleteSeries').addEventListener('click', function () {
            deleteContent(view, 'Series');
        });

        view.querySelector('.chkCleanupOrphans').addEventListener('change', function () {
            view.querySelector('.orphanThresholdContainer').style.display = this.checked ? '' : 'none';
        });

        // Dashboard sync all button
        view.querySelector('.btnDashboardSyncAll').addEventListener('click', function () {
            dashboardSyncAll(self);
        });

        // Download sanitized log button
        view.querySelector('.btnDownloadLog').addEventListener('click', function () {
            window.open(ApiClient.getUrl('XtreamTuner/Logs') + '?api_key=' + ApiClient.accessToken(), '_blank');
        });

        // Dismiss update banner
        view.querySelector('.updateBannerDismiss').addEventListener('click', function () {
            view.querySelector('.updateBanner').style.display = 'none';
        });

        // Beta channel toggle — save immediately then re-check for updates
        view.querySelector('.chkUseBetaChannel').addEventListener('change', function () {
            saveConfig(self, function () { checkForUpdate(view); });
        });

        // Install update button
        view.querySelector('.btnInstallUpdate').addEventListener('click', function () {
            installUpdate(view);
        });

        // Restart Emby button
        view.querySelector('.btnRestartEmby').addEventListener('click', function () {
            restartEmby(view);
        });

        // Danger zone toggles (event delegation on form)
        view.querySelector('.xtreamConfigForm').addEventListener('click', function (e) {
            var header = e.target.closest('.danger-zone-header');
            if (!header) return;
            var zone = header.parentNode;
            zone.classList.toggle('open');
            var arrow = header.querySelector('.danger-zone-arrow');
            if (arrow) arrow.textContent = zone.classList.contains('open') ? '\u25BC' : '\u25B6';
        });

        // Category search filters
        setupCategorySearch(view, '.vodCategorySearch', '.vodCategoriesList');
        setupCategorySearch(view, '.seriesCategorySearch', '.seriesCategoriesList');
        setupCategorySearch(view, '.liveCategorySearch', '.categoriesList');

        // Tab buttons
        var tabBtns = view.querySelectorAll('.tabBtn');
        for (var i = 0; i < tabBtns.length; i++) {
            tabBtns[i].addEventListener('click', function () {
                var tab = this.getAttribute('data-tab');
                switchTab(view, tab);
                if (tab === 'dashboard') {
                    loadDashboard(view);
                }
                // Auto-load categories on tab switch if not already loaded
                if (tab === 'movies' && self.loadedVodCategories.length === 0) {
                    loadVodCategories(self);
                }
                if (tab === 'series' && self.loadedSeriesCategories.length === 0) {
                    loadSeriesCategories(self);
                }
                if (tab === 'liveTv' && self.loadedCategories.length === 0) {
                    loadCategories(self);
                }
            });
        }
        switchTab(view, 'dashboard');
    }

    Object.assign(View.prototype, BaseView.prototype);

    View.prototype.onResume = function (options) {
        BaseView.prototype.onResume.apply(this, arguments);
        loadConfig(this);
        loadDashboard(this.view);
    };

    View.prototype.onPause = function () {};

    function loadConfig(instance) {
        loading.show();
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            var view = instance.view;

            view.querySelector('.txtBaseUrl').value = config.BaseUrl || '';
            view.querySelector('.txtUsername').value = config.Username || '';
            view.querySelector('.txtPassword').value = config.Password || '';

            view.querySelector('.chkEnableLiveTv').checked = config.EnableLiveTv !== false;
            view.querySelector('.selOutputFormat').value = config.LiveTvOutputFormat || 'ts';
            view.querySelector('.chkIncludeAdult').checked = !!config.IncludeAdultChannels;

            view.querySelector('.chkEnableEpg').checked = config.EnableEpg !== false;
            view.querySelector('.txtEpgCacheMinutes').value = config.EpgCacheMinutes || 30;
            view.querySelector('.txtEpgDaysToFetch').value = config.EpgDaysToFetch || 2;
            view.querySelector('.txtM3UCacheMinutes').value = config.M3UCacheMinutes || 15;

            view.querySelector('.chkEnableCatchup').checked = !!config.EnableCatchup;
            view.querySelector('.txtCatchupDays').value = config.CatchupDays || 7;

            instance.selectedCategoryIds = config.SelectedLiveCategoryIds || [];

            // Unified name cleaning (drives both content + channel cleaning)
            var nameCleaningEnabled = !!config.EnableContentNameCleaning || !!config.EnableChannelNameCleaning;
            view.querySelector('.chkEnableNameCleaning').checked = nameCleaningEnabled;
            var removeTerms = config.ContentRemoveTerms || '';
            if (!removeTerms && config.ChannelRemoveTerms) {
                removeTerms = config.ChannelRemoveTerms.split(',').map(function (t) { return t.trim(); }).filter(function (t) { return t; }).join('\n');
            }
            view.querySelector('.txtRemoveTerms').value = removeTerms;

            view.querySelector('.chkEnableDispatcharr').checked = !!config.EnableDispatcharr;
            view.querySelector('.txtDispatcharrUrl').value = config.DispatcharrUrl || '';
            view.querySelector('.txtDispatcharrUser').value = config.DispatcharrUser || '';
            view.querySelector('.txtDispatcharrPass').value = config.DispatcharrPass || '';
            view.querySelector('.chkDispatcharrFallback').checked = config.DispatcharrFallbackToXtream !== false;

            // Pre-parse cached categories so folder cards render correctly from the start
            var cachedVodCats = null;
            if (config.CachedVodCategories) {
                try { cachedVodCats = JSON.parse(config.CachedVodCategories); } catch (e) {}
            }
            var cachedSeriesCats = null;
            if (config.CachedSeriesCategories) {
                try { cachedSeriesCats = JSON.parse(config.CachedSeriesCategories); } catch (e) {}
            }

            // VOD Movies
            view.querySelector('.chkSyncMovies').checked = !!config.SyncMovies;
            var movieMode = config.MovieFolderMode || 'single';
            if (movieMode === 'multiple') movieMode = 'custom';
            view.querySelector('.selMovieFolderMode').value = movieMode;
            loadFolderEntries(view, 'movie', config.MovieFolderMappings || '', cachedVodCats);
            instance.selectedVodCategoryIds = config.SelectedVodCategoryIds || [];

            // Series
            view.querySelector('.chkSyncSeries').checked = !!config.SyncSeries;
            var seriesMode = config.SeriesFolderMode || 'single';
            if (seriesMode === 'multiple') seriesMode = 'custom';
            view.querySelector('.selSeriesFolderMode').value = seriesMode;
            loadFolderEntries(view, 'series', config.SeriesFolderMappings || '', cachedSeriesCats);
            instance.selectedSeriesCategoryIds = config.SelectedSeriesCategoryIds || [];

            // Update channel
            view.querySelector('.chkUseBetaChannel').checked = !!config.UseBetaChannel;

            // Sync settings
            view.querySelector('.txtStrmLibraryPath').value = config.StrmLibraryPath || '/config/xtream';
            validateStrmPath(view);
            view.querySelector('.chkSmartSkipExisting').checked = config.SmartSkipExisting !== false;
            view.querySelector('.txtSyncParallelism').value = config.SyncParallelism || 3;
            view.querySelector('.chkCleanupOrphans').checked = !!config.CleanupOrphans;
            view.querySelector('.txtOrphanSafetyThreshold').value = Math.round((config.OrphanSafetyThreshold || 0.20) * 100);
            view.querySelector('.orphanThresholdContainer').style.display = config.CleanupOrphans ? '' : 'none';
            view.querySelector('.chkEnableNfoFiles').checked = !!config.EnableNfoFiles;

            // Auto-sync schedule
            view.querySelector('.chkAutoSyncEnabled').checked = !!config.AutoSyncEnabled;
            view.querySelector('.selAutoSyncMode').value = config.AutoSyncMode || 'interval';
            view.querySelector('.txtAutoSyncIntervalHours').value = config.AutoSyncIntervalHours || 24;
            view.querySelector('.txtAutoSyncDailyTime').value = config.AutoSyncDailyTime || '03:00';
            updateAutoSyncVisibility(view);

            // Metadata ID naming (unified)
            var metadataIdEnabled = !!config.EnableTmdbFolderNaming || !!config.EnableSeriesIdFolderNaming;
            view.querySelector('.chkEnableTmdbFolderNaming').checked = metadataIdEnabled;
            var fallbackEnabled = !!config.EnableTmdbFallbackLookup || !!config.EnableSeriesMetadataLookup;
            view.querySelector('.chkEnableTmdbFallbackLookup').checked = fallbackEnabled;
            view.querySelector('.txtTvdbFolderIdOverrides').value = config.TvdbFolderIdOverrides || '';

            updateTmdbVisibility(view);
            updateNameCleaningVisibility(view);
            updateDispatcharrVisibility(view);
            updateEpgVisibility(view);
            updateCatchupVisibility(view);
            updateVodMovieVisibility(view);
            updateSeriesVisibility(view);
            updateFoldersVisibility(view, 'movie');
            updateFoldersVisibility(view, 'series');

            loading.hide();

            // Load cached categories from config (instant, no API call)
            loadCachedCategories(instance, config);
        }).catch(function () {
            loading.hide();
            console.error('Xtream: failed to load plugin configuration');
        });
    }

    function saveConfig(instance, callback) {
        loading.show();
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            var view = instance.view;

            config.BaseUrl = view.querySelector('.txtBaseUrl').value.replace(/\/+$/, '');
            config.Username = view.querySelector('.txtUsername').value;
            config.Password = view.querySelector('.txtPassword').value;

            config.EnableLiveTv = view.querySelector('.chkEnableLiveTv').checked;
            config.LiveTvOutputFormat = view.querySelector('.selOutputFormat').value;
            config.IncludeAdultChannels = view.querySelector('.chkIncludeAdult').checked;

            config.EnableEpg = view.querySelector('.chkEnableEpg').checked;
            config.EpgCacheMinutes = parseInt(view.querySelector('.txtEpgCacheMinutes').value, 10) || 30;
            config.EpgDaysToFetch = parseInt(view.querySelector('.txtEpgDaysToFetch').value, 10) || 2;
            config.M3UCacheMinutes = parseInt(view.querySelector('.txtM3UCacheMinutes').value, 10) || 15;

            config.EnableCatchup = view.querySelector('.chkEnableCatchup').checked;
            config.CatchupDays = parseInt(view.querySelector('.txtCatchupDays').value, 10) || 7;

            config.SelectedLiveCategoryIds = getSelectedCategoryIds(instance);

            // Unified name cleaning → both backend properties
            var nameCleaningOn = view.querySelector('.chkEnableNameCleaning').checked;
            var removeTermsVal = view.querySelector('.txtRemoveTerms').value;
            config.EnableContentNameCleaning = nameCleaningOn;
            config.EnableChannelNameCleaning = nameCleaningOn;
            config.ContentRemoveTerms = removeTermsVal;
            config.ChannelRemoveTerms = removeTermsVal.split('\n').map(function (t) { return t.trim(); }).filter(function (t) { return t; }).join(',');

            config.EnableDispatcharr = view.querySelector('.chkEnableDispatcharr').checked;
            config.DispatcharrUrl = view.querySelector('.txtDispatcharrUrl').value.replace(/\/+$/, '');
            config.DispatcharrUser = view.querySelector('.txtDispatcharrUser').value;
            config.DispatcharrPass = view.querySelector('.txtDispatcharrPass').value;
            config.DispatcharrFallbackToXtream = view.querySelector('.chkDispatcharrFallback').checked;

            // VOD Movies
            config.SyncMovies = view.querySelector('.chkSyncMovies').checked;
            config.MovieFolderMode = view.querySelector('.selMovieFolderMode').value;
            config.MovieFolderMappings = serializeFolderEntries(view, 'movie');
            config.SelectedVodCategoryIds = getSelectedVodCategoryIds(instance);

            // Series
            config.SyncSeries = view.querySelector('.chkSyncSeries').checked;
            config.SeriesFolderMode = view.querySelector('.selSeriesFolderMode').value;
            config.SeriesFolderMappings = serializeFolderEntries(view, 'series');
            config.SelectedSeriesCategoryIds = getSelectedSeriesCategoryIds(instance);

            // Update channel
            config.UseBetaChannel = view.querySelector('.chkUseBetaChannel').checked;

            // Sync settings
            config.StrmLibraryPath = view.querySelector('.txtStrmLibraryPath').value.replace(/\/+$/, '') || '/config/xtream';
            config.SmartSkipExisting = view.querySelector('.chkSmartSkipExisting').checked;
            config.SyncParallelism = parseInt(view.querySelector('.txtSyncParallelism').value, 10) || 3;
            config.CleanupOrphans = view.querySelector('.chkCleanupOrphans').checked;
            config.OrphanSafetyThreshold = (parseInt(view.querySelector('.txtOrphanSafetyThreshold').value, 10) || 0) / 100;
            config.EnableNfoFiles = view.querySelector('.chkEnableNfoFiles').checked;

            // Auto-sync schedule
            config.AutoSyncEnabled = view.querySelector('.chkAutoSyncEnabled').checked;
            config.AutoSyncMode = view.querySelector('.selAutoSyncMode').value;
            config.AutoSyncIntervalHours = parseInt(view.querySelector('.txtAutoSyncIntervalHours').value, 10) || 24;
            config.AutoSyncDailyTime = view.querySelector('.txtAutoSyncDailyTime').value || '03:00';

            // Metadata ID naming (unified → both backend properties)
            var metadataIdOn = view.querySelector('.chkEnableTmdbFolderNaming').checked;
            config.EnableTmdbFolderNaming = metadataIdOn;
            config.EnableSeriesIdFolderNaming = metadataIdOn;
            var fallbackOn = view.querySelector('.chkEnableTmdbFallbackLookup').checked;
            config.EnableTmdbFallbackLookup = fallbackOn;
            config.EnableSeriesMetadataLookup = fallbackOn;
            config.TvdbFolderIdOverrides = view.querySelector('.txtTvdbFolderIdOverrides').value;

            ApiClient.updatePluginConfiguration(pluginId, config).then(function () {
                Dashboard.processPluginConfigurationUpdateResult();
                applyScheduleToTasks(view, config, ApiClient);
                if (typeof callback === 'function') callback();
            });
        }).catch(function () {
            loading.hide();
            Dashboard.alert('Failed to save configuration.');
        });
    }

    function switchTab(view, tabName) {
        var panels = view.querySelectorAll('.tabPanel');
        for (var i = 0; i < panels.length; i++) {
            panels[i].style.display = 'none';
        }

        var btns = view.querySelectorAll('.tabBtn');
        for (var i = 0; i < btns.length; i++) {
            btns[i].style.opacity = '0.7';
            btns[i].style.borderBottomColor = 'transparent';
        }

        var panelMap = { dashboard: '.tabDashboard', generic: '.tabGeneric', movies: '.tabMovies', series: '.tabSeries', liveTv: '.tabLiveTv' };
        var btnMap = { dashboard: '.tabBtnDashboard', generic: '.tabBtnGeneric', movies: '.tabBtnMovies', series: '.tabBtnSeries', liveTv: '.tabBtnLiveTv' };

        var panel = view.querySelector(panelMap[tabName]);
        if (panel) panel.style.display = 'block';

        var btn = view.querySelector(btnMap[tabName]);
        if (btn) {
            btn.style.opacity = '1';
            btn.style.borderBottomColor = '#52B54B';
        }
    }

    function updateTmdbVisibility(view) {
        var enabled = view.querySelector('.chkEnableTmdbFolderNaming').checked;
        var settings = view.querySelector('.tmdbSettings');
        var inputs = settings.querySelectorAll('input, textarea');
        for (var i = 0; i < inputs.length; i++) {
            inputs[i].disabled = !enabled;
        }
        settings.style.opacity = enabled ? '1' : '0.5';
    }

    function updateDispatcharrVisibility(view) {
        var enabled = view.querySelector('.chkEnableDispatcharr').checked;
        var settings = view.querySelector('.dispatcharrSettings');
        var inputs = settings.querySelectorAll('input');
        for (var i = 0; i < inputs.length; i++) {
            inputs[i].disabled = !enabled;
        }
        settings.style.opacity = enabled ? '1' : '0.5';
        view.querySelector('.btnTestDispatcharr').disabled = !enabled;
    }

    function updateEpgVisibility(view) {
        var enabled = view.querySelector('.chkEnableEpg').checked;
        var settings = view.querySelector('.epgSettings');
        var inputs = settings.querySelectorAll('input');
        for (var i = 0; i < inputs.length; i++) {
            inputs[i].disabled = !enabled;
        }
        settings.style.opacity = enabled ? '1' : '0.5';
    }

    function updateCatchupVisibility(view) {
        var enabled = view.querySelector('.chkEnableCatchup').checked;
        var settings = view.querySelector('.catchupSettings');
        var inputs = settings.querySelectorAll('input');
        for (var i = 0; i < inputs.length; i++) {
            inputs[i].disabled = !enabled;
        }
        settings.style.opacity = enabled ? '1' : '0.5';
    }

    function updateNameCleaningVisibility(view) {
        var enabled = view.querySelector('.chkEnableNameCleaning').checked;
        var settings = view.querySelector('.nameCleaningSettings');
        var inputs = settings.querySelectorAll('input, textarea');
        for (var i = 0; i < inputs.length; i++) {
            inputs[i].disabled = !enabled;
        }
        settings.style.opacity = enabled ? '1' : '0.5';
    }

    function updateVodMovieVisibility(view) {
        var enabled = view.querySelector('.chkSyncMovies').checked;
        var settings = view.querySelector('.vodMovieSettings');
        var inputs = settings.querySelectorAll('input, select, textarea');
        for (var i = 0; i < inputs.length; i++) {
            inputs[i].disabled = !enabled;
        }
        var buttons = settings.querySelectorAll('button');
        for (var i = 0; i < buttons.length; i++) {
            buttons[i].disabled = !enabled;
        }
        settings.style.opacity = enabled ? '1' : '0.5';
    }

    function updateSeriesVisibility(view) {
        var enabled = view.querySelector('.chkSyncSeries').checked;
        var settings = view.querySelector('.seriesSettings');
        var inputs = settings.querySelectorAll('input, select, textarea');
        for (var i = 0; i < inputs.length; i++) {
            inputs[i].disabled = !enabled;
        }
        var buttons = settings.querySelectorAll('button');
        for (var i = 0; i < buttons.length; i++) {
            buttons[i].disabled = !enabled;
        }
        settings.style.opacity = enabled ? '1' : '0.5';
    }

    function updateFoldersVisibility(view, type) {
        var selClass = type === 'movie' ? '.selMovieFolderMode' : '.selSeriesFolderMode';
        var singleClass = type === 'movie' ? '.movieSingleContainer' : '.seriesSingleContainer';
        var multiClass = type === 'movie' ? '.movieFoldersContainer' : '.seriesFoldersContainer';
        var mode = view.querySelector(selClass).value;
        view.querySelector(singleClass).style.display = mode === 'single' ? 'block' : 'none';
        view.querySelector(multiClass).style.display = mode === 'custom' ? 'block' : 'none';
    }

    function updateAutoSyncVisibility(v) {
        var enabled = v.querySelector('.chkAutoSyncEnabled').checked;
        v.querySelector('.autoSyncSettings').style.display = enabled ? '' : 'none';
        var mode = v.querySelector('.selAutoSyncMode').value;
        v.querySelector('.autoSyncIntervalContainer').style.display = mode === 'interval' ? '' : 'none';
        v.querySelector('.autoSyncDailyContainer').style.display    = mode === 'daily'    ? '' : 'none';
    }

    function buildTriggers(config) {
        if (!config.AutoSyncEnabled) return [];
        if (config.AutoSyncMode === 'daily') {
            var parts = (config.AutoSyncDailyTime || '03:00').split(':');
            var ticks = (parseInt(parts[0], 10) * 3600 + parseInt(parts[1] || '0', 10) * 60) * 10000000;
            return [{ Type: 'DailyTrigger', TimeOfDayTicks: ticks }];
        }
        // interval
        var hours = Math.max(1, config.AutoSyncIntervalHours || 24);
        return [{ Type: 'IntervalTrigger', IntervalTicks: hours * 36000000000 }];
    }

    function applyScheduleToTasks(view, config, apiClient) {
        apiClient.ajax({ url: apiClient.getUrl('ScheduledTasks'), type: 'GET' })
            .then(function (tasks) {
                var xtreamTasks = tasks.filter(function (t) {
                    return t.Category === 'Xtream Tuner';
                });
                var triggers = buildTriggers(config);
                xtreamTasks.forEach(function (task) {
                    apiClient.ajax({
                        url: apiClient.getUrl('ScheduledTasks/' + task.Id + '/Triggers'),
                        type: 'POST',
                        contentType: 'application/json',
                        data: JSON.stringify(triggers)
                    });
                });
            });
    }

    // ---- Folder card management (for Multiple Folders mode) ----

    function addFolderEntry(view, type, name, checkedIdsStr, categories) {
        var listClass = type === 'movie' ? '.movieFoldersList' : '.seriesFoldersList';
        var list = view.querySelector(listClass);

        var card = document.createElement('div');
        card.className = 'folderCard';
        card.setAttribute('data-checked-ids', checkedIdsStr || '');
        card.style.cssText = 'border:1px solid rgba(128,128,128,0.2); border-radius:6px; padding:1em; margin-bottom:1em;';

        // Header: name input + remove button
        var header = document.createElement('div');
        header.style.cssText = 'display:flex; gap:0.5em; align-items:center; margin-bottom:0.5em;';

        var nameInput = document.createElement('input');
        nameInput.type = 'text';
        nameInput.className = 'folderCardName';
        nameInput.placeholder = 'Folder name';
        nameInput.value = name;
        nameInput.style.cssText = 'flex:1; padding:0.5em 0.8em; background:transparent; border:1px solid rgba(128,128,128,0.25); border-radius:4px; color:inherit; font-size:1em;';

        var removeBtn = document.createElement('button');
        removeBtn.type = 'button';
        removeBtn.textContent = 'Remove';
        removeBtn.style.cssText = 'background:#c0392b; color:white; border:none; border-radius:4px; padding:0.5em 1em; cursor:pointer; font-size:0.9em;';
        removeBtn.addEventListener('click', function () {
            card.parentNode.removeChild(card);
        });

        header.appendChild(nameInput);
        header.appendChild(removeBtn);
        card.appendChild(header);

        // Category checkboxes container
        var catContainer = document.createElement('div');
        catContainer.className = 'folderCardCategories';
        catContainer.style.cssText = 'max-height:300px; overflow-y:auto; border:1px solid rgba(128,128,128,0.15); border-radius:4px; padding:0.5em;';

        if (categories && categories.length > 0) {
            renderFolderCardCategories(catContainer, categories, checkedIdsStr);
        } else if (categories !== null && categories !== undefined) {
            catContainer.innerHTML = '<div style="opacity:0.5; padding:0.5em;">No categories available from server. Click Refresh Categories to try again.</div>';
        } else {
            catContainer.innerHTML = '<div style="opacity:0.5; padding:0.5em;">Loading categories...</div>';
        }

        card.appendChild(catContainer);
        list.appendChild(card);
    }

    function renderFolderCardCategories(container, categories, checkedIdsStr) {
        var checkedIds = [];
        if (checkedIdsStr) {
            var parts = checkedIdsStr.split(',');
            for (var i = 0; i < parts.length; i++) {
                var n = parseInt(parts[i].trim(), 10);
                if (!isNaN(n)) checkedIds.push(n);
            }
        }

        var html = '';
        for (var i = 0; i < categories.length; i++) {
            var cat = categories[i];
            var checked = checkedIds.indexOf(cat.CategoryId) >= 0 ? ' checked' : '';
            html += '<div class="checkboxContainer" style="margin:0.3em 0; padding:0.2em 0.5em;">';
            html += '<label style="display:flex; align-items:center; cursor:pointer;">';
            html += '<input type="checkbox" class="folderCategoryCheckbox" data-category-id="' + cat.CategoryId + '"' + checked + ' style="margin-right:0.5em;" />';
            html += '<span>' + escapeHtml(cat.CategoryName) + ' <span style="opacity:0.5;">(ID: ' + cat.CategoryId + ')</span></span>';
            html += '</label>';
            html += '</div>';
        }
        container.innerHTML = html;
    }

    function clearFolderCardCategories(view, type) {
        var listClass = type === 'movie' ? '.movieFoldersList' : '.seriesFoldersList';
        var cards = view.querySelectorAll(listClass + ' .folderCard');
        for (var i = 0; i < cards.length; i++) {
            var catContainer = cards[i].querySelector('.folderCardCategories');
            catContainer.innerHTML = '<div style="opacity:0.5; padding:0.5em;">No categories available from server.</div>';
        }
    }

    function populateFolderCheckboxes(view, type, categories) {
        var listClass = type === 'movie' ? '.movieFoldersList' : '.seriesFoldersList';
        var cards = view.querySelectorAll(listClass + ' .folderCard');
        for (var i = 0; i < cards.length; i++) {
            var card = cards[i];
            var checkedIdsStr = card.getAttribute('data-checked-ids') || '';
            var catContainer = card.querySelector('.folderCardCategories');
            renderFolderCardCategories(catContainer, categories, checkedIdsStr);
        }
    }

    function loadFolderEntries(view, type, mappingsText, categories) {
        var listClass = type === 'movie' ? '.movieFoldersList' : '.seriesFoldersList';
        view.querySelector(listClass).innerHTML = '';

        if (!mappingsText) return;

        var lines = mappingsText.split('\n');
        for (var i = 0; i < lines.length; i++) {
            var line = lines[i].trim();
            if (!line) continue;
            var eqIdx = line.indexOf('=');
            if (eqIdx < 0) continue;
            var name = line.substring(0, eqIdx).trim();
            var ids = line.substring(eqIdx + 1).trim();
            addFolderEntry(view, type, name, ids, categories);
        }
    }

    function serializeFolderEntries(view, type) {
        var listClass = type === 'movie' ? '.movieFoldersList' : '.seriesFoldersList';
        var cards = view.querySelectorAll(listClass + ' .folderCard');
        var lines = [];
        for (var i = 0; i < cards.length; i++) {
            var name = cards[i].querySelector('.folderCardName').value.trim();
            if (!name) continue;

            // Check if checkboxes have been rendered (categories loaded)
            var allCheckboxes = cards[i].querySelectorAll('.folderCategoryCheckbox');
            var ids = [];
            if (allCheckboxes.length > 0) {
                // Categories loaded - read from checked checkboxes
                var checkedBoxes = cards[i].querySelectorAll('.folderCategoryCheckbox:checked');
                for (var j = 0; j < checkedBoxes.length; j++) {
                    ids.push(checkedBoxes[j].getAttribute('data-category-id'));
                }
            } else {
                // Categories not loaded yet - fall back to stored data attribute
                var storedIds = cards[i].getAttribute('data-checked-ids') || '';
                if (storedIds) {
                    var parts = storedIds.split(',');
                    for (var j = 0; j < parts.length; j++) {
                        var s = parts[j].trim();
                        if (s) ids.push(s);
                    }
                }
            }

            if (ids.length > 0) {
                lines.push(name + '=' + ids.join(','));
            }
        }
        return lines.join('\n');
    }

    function testXtreamConnection(view) {
        var resultEl = view.querySelector('.connectionTestResult');
        resultEl.innerHTML = '<span style="opacity:0.5;">Testing connection...</span>';

        var url = view.querySelector('.txtBaseUrl').value.replace(/\/+$/, '');
        var user = view.querySelector('.txtUsername').value;
        var pass = view.querySelector('.txtPassword').value;

        if (!url || !user || !pass) {
            resultEl.innerHTML = '<span style="color:#cc0000;">Please enter server URL, username, and password.</span>';
            return;
        }

        var testUrl = url + '/player_api.php?username=' + encodeURIComponent(user) + '&password=' + encodeURIComponent(pass);
        var xhr = new XMLHttpRequest();
        xhr.open('GET', testUrl, true);
        xhr.timeout = 10000;

        xhr.onload = function () {
            if (xhr.status >= 200 && xhr.status < 300) {
                try {
                    var resp = JSON.parse(xhr.responseText);
                    if (resp.user_info) {
                        var status = resp.user_info.status || 'unknown';
                        var msg = 'Connection successful! Status: ' + status;
                        if (resp.user_info.active_cons !== undefined) {
                            msg += ', Active connections: ' + resp.user_info.active_cons;
                        }
                        if (resp.user_info.max_connections !== undefined) {
                            msg += '/' + resp.user_info.max_connections;
                        }
                        resultEl.innerHTML = '<span style="color:#52B54B;">' + msg + '</span>';
                    } else {
                        resultEl.innerHTML = '<span style="color:#52B54B;">Connection successful!</span>';
                    }
                } catch (e) {
                    resultEl.innerHTML = '<span style="color:#52B54B;">Connection successful (non-JSON response).</span>';
                }
            } else {
                resultEl.innerHTML = '<span style="color:#cc0000;">Connection failed (HTTP ' + xhr.status + ').</span>';
            }
        };

        xhr.onerror = function () {
            resultEl.innerHTML = '<span style="color:#cc0000;">Connection failed. Check URL and ensure server is reachable.</span>';
        };

        xhr.ontimeout = function () {
            resultEl.innerHTML = '<span style="color:#cc0000;">Connection timed out.</span>';
        };

        xhr.send();
    }

    // ---- Folder browser ----

    function openBrowser(view) {
        var modal = view.querySelector('.strmBrowserModal');
        modal.style.display = 'flex';
        var startPath = (view.querySelector('.txtStrmLibraryPath').value || '').trim() || null;
        browserNavigate(view, startPath);
    }

    function closeBrowser(view) {
        view.querySelector('.strmBrowserModal').style.display = 'none';
    }

    function browserNavigate(view, path) {
        var modal = view.querySelector('.strmBrowserModal');
        var listEl = modal.querySelector('.browserList');
        listEl.innerHTML = '<div style="padding:1.2em 1.5em; opacity:0.5;">Loading...</div>';
        modal.querySelector('.txtBrowserCurrentPath').value = path || '';

        var url = ApiClient.getUrl('XtreamTuner/BrowsePath');
        if (path) url += '?path=' + encodeURIComponent(path);

        ApiClient.ajax({ type: 'GET', url: url, dataType: 'json' })
            .then(function (result) { browserRenderList(view, result); })
            .catch(function () {
                listEl.innerHTML = '<div style="padding:1.2em 1.5em; color:#cc0000;">Failed to load directory.</div>';
            });
    }

    function browserRenderList(view, result) {
        var modal = view.querySelector('.strmBrowserModal');
        var listEl = modal.querySelector('.browserList');
        listEl.innerHTML = '';

        modal.querySelector('.txtBrowserCurrentPath').value = result.CurrentPath || '';

        var isRoot = !result.CurrentPath;

        if (!isRoot) {
            var upRow = createBrowserRow('\u2191', '..', function () {
                browserNavigate(view, result.ParentPath || null);
            });
            listEl.appendChild(upRow);
        }

        if (result.Directories && result.Directories.length > 0) {
            result.Directories.forEach(function (dir) {
                var parts = dir.replace(/\\/g, '/').split('/').filter(function (p) { return p.length > 0; });
                var name = parts.length > 0 ? parts[parts.length - 1] : dir;
                var row = createBrowserRow('\uD83D\uDCC1', name, function () {
                    browserNavigate(view, dir);
                });
                listEl.appendChild(row);
            });
        } else if (isRoot) {
            listEl.innerHTML = '<div style="padding:1.2em 1.5em; opacity:0.5;">No accessible folders found.</div>';
        } else {
            var emptyEl = document.createElement('div');
            emptyEl.style.cssText = 'padding:1.2em 1.5em; opacity:0.5;';
            emptyEl.textContent = 'No subdirectories.';
            listEl.appendChild(emptyEl);
        }
    }

    function createBrowserRow(icon, label, onClick) {
        var row = document.createElement('div');
        row.style.cssText = 'display:flex; align-items:center; gap:0.8em; padding:0.6em 1.5em; cursor:pointer; border-bottom:1px solid rgba(128,128,128,0.07); user-select:none;';
        var iconEl = document.createElement('span');
        iconEl.textContent = icon;
        iconEl.style.cssText = 'flex-shrink:0; width:1.2em; text-align:center; opacity:0.6;';
        var labelEl = document.createElement('span');
        labelEl.textContent = label;
        labelEl.style.cssText = 'font-size:0.9em; font-family:monospace;';
        row.appendChild(iconEl);
        row.appendChild(labelEl);
        row.addEventListener('mouseenter', function () { this.style.background = 'rgba(128,128,128,0.1)'; });
        row.addEventListener('mouseleave', function () { this.style.background = ''; });
        row.addEventListener('click', onClick);
        return row;
    }

    function validateStrmPath(view) {
        var path = (view.querySelector('.txtStrmLibraryPath').value || '').trim();
        var resultEl = view.querySelector('.strmPathValidationResult');
        if (!path) {
            resultEl.innerHTML = '';
            return;
        }
        resultEl.innerHTML = '<span style="opacity:0.5;">Checking path...</span>';
        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('XtreamTuner/ValidateStrmPath'),
            contentType: 'application/json',
            data: JSON.stringify({ Path: path }),
            dataType: 'json'
        }).then(function (result) {
            var color = result.Success ? '#52B54B' : '#cc0000';
            var icon = result.Success ? '✓' : '✗';
            resultEl.innerHTML = '<span style="color:' + color + ';">' + icon + ' ' + escapeHtml(result.Message) + '</span>';
        }).catch(function () {
            resultEl.innerHTML = '<span style="color:#cc0000;">✗ Validation request failed.</span>';
        });
    }

    function testDispatcharrConnection(instance) {
        var view = instance.view;
        var resultEl = view.querySelector('.dispatcharrTestResult');
        resultEl.innerHTML = '<span style="opacity:0.5;">Saving config &amp; testing connection...</span>';

        // Save config first so the server reads the latest Dispatcharr credentials
        saveConfig(instance, function () {
            ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl('XtreamTuner/TestDispatcharr'),
                dataType: 'json'
            }).then(function (result) {
                var color = result.Success ? '#52B54B' : '#cc0000';
                resultEl.innerHTML = '<span style="color:' + color + ';">' + escapeHtml(result.Message) + '</span>';
            }).catch(function () {
                resultEl.innerHTML = '<span style="color:#cc0000;">Test request failed. Check server logs.</span>';
            });
        });
    }

    // ---- Cached category loading (instant from config) ----

    function loadCachedCategories(instance, config) {
        var view = instance.view;

        // VOD categories
        var vodLoaded = false;
        if (config.CachedVodCategories) {
            try {
                var vodCats = JSON.parse(config.CachedVodCategories);
                if (vodCats && vodCats.length > 0) {
                    vodLoaded = true;
                    instance.loadedVodCategories = vodCats;
                    renderCategoryList(view, '.vodCategoriesList', vodCats, 'vodCategoryCheckbox', instance.selectedVodCategoryIds);
                    view.querySelector('.btnSelectAllVodCategories').disabled = false;
                    view.querySelector('.btnDeselectAllVodCategories').disabled = false;
                    var statusEl = view.querySelector('.vodCategoriesStatus');
                    if (statusEl) statusEl.textContent = vodCats.length + ' categories';
                    populateFolderCheckboxes(view, 'movie', vodCats);
                }
            } catch (e) { /* ignore parse errors */ }
        }
        if (!vodLoaded) {
            clearFolderCardCategories(view, 'movie');
            var vodListEl = view.querySelector('.vodCategoriesList');
            if (vodListEl && !vodListEl.innerHTML.trim()) {
                vodListEl.innerHTML = '<div style="opacity:0.5;">Click "Refresh Categories" to load.</div>';
            }
        }

        // Series categories
        var seriesLoaded = false;
        if (config.CachedSeriesCategories) {
            try {
                var seriesCats = JSON.parse(config.CachedSeriesCategories);
                if (seriesCats && seriesCats.length > 0) {
                    seriesLoaded = true;
                    instance.loadedSeriesCategories = seriesCats;
                    renderCategoryList(view, '.seriesCategoriesList', seriesCats, 'seriesCategoryCheckbox', instance.selectedSeriesCategoryIds);
                    view.querySelector('.btnSelectAllSeriesCategories').disabled = false;
                    view.querySelector('.btnDeselectAllSeriesCategories').disabled = false;
                    var statusEl = view.querySelector('.seriesCategoriesStatus');
                    if (statusEl) statusEl.textContent = seriesCats.length + ' categories';
                    populateFolderCheckboxes(view, 'series', seriesCats);
                }
            } catch (e) { /* ignore parse errors */ }
        }
        if (!seriesLoaded) {
            clearFolderCardCategories(view, 'series');
            var seriesListEl = view.querySelector('.seriesCategoriesList');
            if (seriesListEl && !seriesListEl.innerHTML.trim()) {
                seriesListEl.innerHTML = '<div style="opacity:0.5;">Click "Refresh Categories" to load.</div>';
            }
        }

        // Live TV categories
        if (config.CachedLiveCategories) {
            try {
                var liveCats = JSON.parse(config.CachedLiveCategories);
                if (liveCats && liveCats.length > 0) {
                    instance.loadedCategories = liveCats;
                    renderCategoryList(view, '.categoriesList', liveCats, 'categoryCheckbox', instance.selectedCategoryIds);
                    view.querySelector('.btnSelectAllCategories').disabled = false;
                    view.querySelector('.btnDeselectAllCategories').disabled = false;
                }
            } catch (e) { /* ignore parse errors */ }
        }
    }

    function renderCategoryList(view, listSelector, categories, checkboxClass, selectedIds) {
        var listEl = view.querySelector(listSelector);
        if (!listEl) return;
        var html = '';
        for (var i = 0; i < categories.length; i++) {
            var cat = categories[i];
            var checked = selectedIds.indexOf(cat.CategoryId) >= 0 ? ' checked' : '';
            html += '<div class="checkboxContainer" style="margin:0.15em 0;">';
            html += '<label style="display:flex; align-items:center; cursor:pointer;">';
            html += '<input type="checkbox" class="' + checkboxClass + '" data-category-id="' + cat.CategoryId + '"' + checked + ' style="margin-right:0.5em;" />';
            html += '<span>' + escapeHtml(cat.CategoryName) + '</span>';
            html += '</label>';
            html += '</div>';
        }
        listEl.innerHTML = html;
    }

    // ---- Live TV Categories ----

    function loadCategories(instance) {
        var view = instance.view;
        var listEl = view.querySelector('.categoriesList');
        var loadingEl = view.querySelector('.categoriesLoading');

        loadingEl.style.display = 'block';
        listEl.innerHTML = '';

        var apiUrl = ApiClient.getUrl('XtreamTuner/Categories/Live');

        ApiClient.getJSON(apiUrl).then(function (categories) {
            loadingEl.style.display = 'none';
            instance.loadedCategories = categories;

            if (!categories || categories.length === 0) {
                listEl.innerHTML = '<div style="opacity:0.5;">No categories found. Check your Xtream connection settings.</div>';
                return;
            }

            var html = '';
            for (var i = 0; i < categories.length; i++) {
                var cat = categories[i];
                var checked = instance.selectedCategoryIds.indexOf(cat.CategoryId) >= 0 ? ' checked' : '';
                html += '<div class="checkboxContainer" style="margin:0.15em 0;">';
                html += '<label style="display:flex; align-items:center; cursor:pointer;">';
                html += '<input type="checkbox" class="categoryCheckbox" data-category-id="' + cat.CategoryId + '"' + checked + ' style="margin-right:0.5em;" />';
                html += '<span>' + escapeHtml(cat.CategoryName) + '</span>';
                html += '</label>';
                html += '</div>';
            }
            listEl.innerHTML = html;

            view.querySelector('.btnSelectAllCategories').disabled = false;
            view.querySelector('.btnDeselectAllCategories').disabled = false;
        }).catch(function () {
            loadingEl.style.display = 'none';
            listEl.innerHTML = '<div style="color:#cc0000;">Failed to load categories. Save your connection settings first, then try again.</div>';
        });
    }

    function toggleAllCategories(view, checked) {
        var checkboxes = view.querySelectorAll('.categoryCheckbox');
        for (var i = 0; i < checkboxes.length; i++) {
            checkboxes[i].checked = checked;
        }
    }

    function getSelectedCategoryIds(instance) {
        var view = instance.view;
        var checkboxes = view.querySelectorAll('.categoryCheckbox');
        var ids = [];
        for (var i = 0; i < checkboxes.length; i++) {
            if (checkboxes[i].checked) {
                ids.push(parseInt(checkboxes[i].getAttribute('data-category-id'), 10));
            }
        }
        if (checkboxes.length === 0) {
            return instance.selectedCategoryIds;
        }
        return ids;
    }

    // ---- VOD Categories (single mode) ----

    function loadVodCategories(instance) {
        var view = instance.view;
        var listEl = view.querySelector('.vodCategoriesList');
        var loadingEl = view.querySelector('.vodCategoriesLoading');
        var statusEl = view.querySelector('.vodCategoriesStatus');

        loadingEl.style.display = 'block';
        listEl.innerHTML = '';

        var apiUrl = ApiClient.getUrl('XtreamTuner/Categories/Vod');

        ApiClient.getJSON(apiUrl).then(function (categories) {
            loadingEl.style.display = 'none';
            instance.loadedVodCategories = categories;

            if (!categories || categories.length === 0) {
                listEl.innerHTML = '<div style="opacity:0.5;">No VOD categories found. Check your Xtream connection settings.</div>';
                return;
            }

            statusEl.textContent = 'Loaded ' + categories.length + ' categories';

            var html = '';
            for (var i = 0; i < categories.length; i++) {
                var cat = categories[i];
                var checked = instance.selectedVodCategoryIds.indexOf(cat.CategoryId) >= 0 ? ' checked' : '';
                html += '<div class="checkboxContainer" style="margin:0.15em 0;">';
                html += '<label style="display:flex; align-items:center; cursor:pointer;">';
                html += '<input type="checkbox" class="vodCategoryCheckbox" data-category-id="' + cat.CategoryId + '"' + checked + ' style="margin-right:0.5em;" />';
                html += '<span>' + escapeHtml(cat.CategoryName) + '</span>';
                html += '</label>';
                html += '</div>';
            }
            listEl.innerHTML = html;

            view.querySelector('.btnSelectAllVodCategories').disabled = false;
            view.querySelector('.btnDeselectAllVodCategories').disabled = false;
        }).catch(function () {
            loadingEl.style.display = 'none';
            listEl.innerHTML = '<div style="color:#cc0000;">Failed to load VOD categories. Save your connection settings first, then try again.</div>';
        });
    }

    function toggleAllVodCategories(view, checked) {
        var checkboxes = view.querySelectorAll('.vodCategoryCheckbox');
        for (var i = 0; i < checkboxes.length; i++) {
            checkboxes[i].checked = checked;
        }
    }

    // ---- VOD Categories (multi/folder mode) ----

    function loadVodCategoriesMulti(instance) {
        var view = instance.view;
        var statusEl = view.querySelector('.vodCategoriesMultiStatus');
        statusEl.textContent = 'Loading...';
        statusEl.style.opacity = '0.5';

        var apiUrl = ApiClient.getUrl('XtreamTuner/Categories/Vod');

        ApiClient.getJSON(apiUrl).then(function (categories) {
            instance.loadedVodCategories = categories || [];

            if (!categories || categories.length === 0) {
                statusEl.textContent = 'No VOD categories found.';
                statusEl.style.color = '#cc0000'; statusEl.style.opacity = '1';
                clearFolderCardCategories(view, 'movie');
                return;
            }

            statusEl.textContent = 'Loaded ' + categories.length + ' categories';
            statusEl.style.color = '#52B54B'; statusEl.style.opacity = '1';
            populateFolderCheckboxes(view, 'movie', categories);
        }).catch(function () {
            statusEl.textContent = 'Failed to load categories. Save connection settings first.';
            statusEl.style.color = '#cc0000'; statusEl.style.opacity = '1';
        });
    }

    function getSelectedVodCategoryIds(instance) {
        var view = instance.view;
        var mode = view.querySelector('.selMovieFolderMode').value;

        if (mode === 'custom') {
            // Union of all checked IDs across all folder cards
            var allCheckboxes = view.querySelectorAll('.movieFoldersList .folderCategoryCheckbox');
            if (allCheckboxes.length === 0) {
                return instance.selectedVodCategoryIds;
            }
            var ids = [];
            var seen = {};
            for (var i = 0; i < allCheckboxes.length; i++) {
                if (allCheckboxes[i].checked) {
                    var id = parseInt(allCheckboxes[i].getAttribute('data-category-id'), 10);
                    if (!seen[id]) {
                        ids.push(id);
                        seen[id] = true;
                    }
                }
            }
            return ids;
        }

        // Single mode: flat checkboxes
        var checkboxes = view.querySelectorAll('.vodCategoryCheckbox');
        var ids = [];
        for (var i = 0; i < checkboxes.length; i++) {
            if (checkboxes[i].checked) {
                ids.push(parseInt(checkboxes[i].getAttribute('data-category-id'), 10));
            }
        }
        if (checkboxes.length === 0) {
            return instance.selectedVodCategoryIds;
        }
        return ids;
    }

    // ---- Series Categories (single mode) ----

    function loadSeriesCategories(instance) {
        var view = instance.view;
        var listEl = view.querySelector('.seriesCategoriesList');
        var loadingEl = view.querySelector('.seriesCategoriesLoading');
        var statusEl = view.querySelector('.seriesCategoriesStatus');

        loadingEl.style.display = 'block';
        listEl.innerHTML = '';

        var apiUrl = ApiClient.getUrl('XtreamTuner/Categories/Series');

        ApiClient.getJSON(apiUrl).then(function (categories) {
            loadingEl.style.display = 'none';
            instance.loadedSeriesCategories = categories;

            if (!categories || categories.length === 0) {
                listEl.innerHTML = '<div style="opacity:0.5;">No series categories found. Check your Xtream connection settings.</div>';
                return;
            }

            statusEl.textContent = 'Loaded ' + categories.length + ' categories';

            var html = '';
            for (var i = 0; i < categories.length; i++) {
                var cat = categories[i];
                var checked = instance.selectedSeriesCategoryIds.indexOf(cat.CategoryId) >= 0 ? ' checked' : '';
                html += '<div class="checkboxContainer" style="margin:0.15em 0;">';
                html += '<label style="display:flex; align-items:center; cursor:pointer;">';
                html += '<input type="checkbox" class="seriesCategoryCheckbox" data-category-id="' + cat.CategoryId + '"' + checked + ' style="margin-right:0.5em;" />';
                html += '<span>' + escapeHtml(cat.CategoryName) + '</span>';
                html += '</label>';
                html += '</div>';
            }
            listEl.innerHTML = html;

            view.querySelector('.btnSelectAllSeriesCategories').disabled = false;
            view.querySelector('.btnDeselectAllSeriesCategories').disabled = false;
        }).catch(function () {
            loadingEl.style.display = 'none';
            listEl.innerHTML = '<div style="color:#cc0000;">Failed to load series categories. Save your connection settings first, then try again.</div>';
        });
    }

    function toggleAllSeriesCategories(view, checked) {
        var checkboxes = view.querySelectorAll('.seriesCategoryCheckbox');
        for (var i = 0; i < checkboxes.length; i++) {
            checkboxes[i].checked = checked;
        }
    }

    // ---- Series Categories (multi/folder mode) ----

    function loadSeriesCategoriesMulti(instance) {
        var view = instance.view;
        var statusEl = view.querySelector('.seriesCategoriesMultiStatus');
        statusEl.textContent = 'Loading...';
        statusEl.style.opacity = '0.5';

        var apiUrl = ApiClient.getUrl('XtreamTuner/Categories/Series');

        ApiClient.getJSON(apiUrl).then(function (categories) {
            instance.loadedSeriesCategories = categories || [];

            if (!categories || categories.length === 0) {
                statusEl.textContent = 'No series categories found.';
                statusEl.style.color = '#cc0000'; statusEl.style.opacity = '1';
                clearFolderCardCategories(view, 'series');
                return;
            }

            statusEl.textContent = 'Loaded ' + categories.length + ' categories';
            statusEl.style.color = '#52B54B'; statusEl.style.opacity = '1';
            populateFolderCheckboxes(view, 'series', categories);
        }).catch(function () {
            statusEl.textContent = 'Failed to load categories. Save connection settings first.';
            statusEl.style.color = '#cc0000'; statusEl.style.opacity = '1';
        });
    }

    function getSelectedSeriesCategoryIds(instance) {
        var view = instance.view;
        var mode = view.querySelector('.selSeriesFolderMode').value;

        if (mode === 'custom') {
            var allCheckboxes = view.querySelectorAll('.seriesFoldersList .folderCategoryCheckbox');
            if (allCheckboxes.length === 0) {
                return instance.selectedSeriesCategoryIds;
            }
            var ids = [];
            var seen = {};
            for (var i = 0; i < allCheckboxes.length; i++) {
                if (allCheckboxes[i].checked) {
                    var id = parseInt(allCheckboxes[i].getAttribute('data-category-id'), 10);
                    if (!seen[id]) {
                        ids.push(id);
                        seen[id] = true;
                    }
                }
            }
            return ids;
        }

        var checkboxes = view.querySelectorAll('.seriesCategoryCheckbox');
        var ids = [];
        for (var i = 0; i < checkboxes.length; i++) {
            if (checkboxes[i].checked) {
                ids.push(parseInt(checkboxes[i].getAttribute('data-category-id'), 10));
            }
        }
        if (checkboxes.length === 0) {
            return instance.selectedSeriesCategoryIds;
        }
        return ids;
    }

    // ---- Sync operations ----

    function renderProgressBar(resultEl, progress) {
        var total = progress.Total || 0;
        var completed = progress.Completed || 0;
        var skipped = progress.Skipped || 0;
        var failed = progress.Failed || 0;
        var phase = progress.Phase || 'Working';
        var pct = total > 0 ? Math.round((completed / total) * 100) : 0;

        resultEl.innerHTML =
            '<div style="margin:0.5em 0;">' +
                '<div style="background:rgba(128,128,128,0.2); border-radius:4px; height:20px; overflow:hidden;">' +
                    '<div style="background:#52B54B; height:100%; width:' + pct + '%; transition:width 0.3s ease; border-radius:4px;"></div>' +
                '</div>' +
                '<div style="opacity:0.7; margin-top:0.4em; font-size:0.9em;">' +
                    escapeHtml(phase) + ' \u2014 ' + completed + ' / ' + total +
                    ' (' + skipped + ' skipped, ' + failed + ' failed) \u2014 ' + pct + '%' +
                '</div>' +
            '</div>';
    }

    function pollSyncProgress(view, type) {
        var resultClass = type === 'Movies' ? '.syncMoviesResult' : '.syncSeriesResult';
        var resultEl = view.querySelector(resultClass);
        var apiUrl = ApiClient.getUrl('XtreamTuner/Sync/Status');

        var intervalId = setInterval(function () {
            ApiClient.getJSON(apiUrl).then(function (status) {
                var progress = status[type];
                if (!progress) return;
                if (progress.IsRunning) {
                    renderProgressBar(resultEl, progress);
                }
            }).catch(function () {
                // Ignore poll errors; the POST completion will handle cleanup
            });
        }, 500);

        return intervalId;
    }

    function syncMovies(view) {
        var resultEl = view.querySelector('.syncMoviesResult');
        var btn = view.querySelector('.btnSyncMovies');
        btn.disabled = true;
        resultEl.innerHTML = '<span style="opacity:0.5;">Starting movie sync...</span>';

        var pollId = pollSyncProgress(view, 'Movies');
        var apiUrl = ApiClient.getUrl('XtreamTuner/Sync/Movies');

        ApiClient.ajax({
            type: 'POST',
            url: apiUrl,
            dataType: 'json'
        }).then(function (result) {
            clearInterval(pollId);
            btn.disabled = false;
            if (result.Success) {
                resultEl.innerHTML = '<span style="color:#52B54B;">' + escapeHtml(result.Message) +
                    ' (Total: ' + result.Total + ', Skipped: ' + result.Skipped + ', Failed: ' + result.Failed + ')</span>';
            } else {
                resultEl.innerHTML = '<span style="color:#cc0000;">' + escapeHtml(result.Message) + '</span>';
            }
        }).catch(function () {
            clearInterval(pollId);
            btn.disabled = false;
            resultEl.innerHTML = '<span style="color:#cc0000;">Movie sync request failed. Check server logs for details.</span>';
        });
    }

    function syncSeries(view) {
        var resultEl = view.querySelector('.syncSeriesResult');
        var btn = view.querySelector('.btnSyncSeries');
        btn.disabled = true;
        resultEl.innerHTML = '<span style="opacity:0.5;">Starting series sync...</span>';

        var pollId = pollSyncProgress(view, 'Series');
        var apiUrl = ApiClient.getUrl('XtreamTuner/Sync/Series');

        ApiClient.ajax({
            type: 'POST',
            url: apiUrl,
            dataType: 'json'
        }).then(function (result) {
            clearInterval(pollId);
            btn.disabled = false;
            if (result.Success) {
                resultEl.innerHTML = '<span style="color:#52B54B;">' + escapeHtml(result.Message) +
                    ' (Total: ' + result.Total + ', Skipped: ' + result.Skipped + ', Failed: ' + result.Failed + ')</span>';
            } else {
                resultEl.innerHTML = '<span style="color:#cc0000;">' + escapeHtml(result.Message) + '</span>';
            }
        }).catch(function () {
            clearInterval(pollId);
            btn.disabled = false;
            resultEl.innerHTML = '<span style="color:#cc0000;">Series sync request failed. Check server logs for details.</span>';
        });
    }

    function deleteContent(view, type) {
        var label = type === 'Movies' ? 'movies' : 'series';
        var resultClass = type === 'Movies' ? '.deleteMoviesResult' : '.deleteSeriesResult';
        var btnClass = type === 'Movies' ? '.btnDeleteMovies' : '.btnDeleteSeries';
        var resultEl = view.querySelector(resultClass);
        var btn = view.querySelector(btnClass);

        if (!confirm('Are you sure you want to delete ALL ' + label + '? This cannot be undone.')) {
            return;
        }

        btn.disabled = true;
        resultEl.innerHTML = '<span style="opacity:0.5;">Deleting ' + label + '...</span>';

        var apiUrl = ApiClient.getUrl('XtreamTuner/Content/' + type);

        ApiClient.ajax({
            type: 'DELETE',
            url: apiUrl,
            dataType: 'json'
        }).then(function (result) {
            btn.disabled = false;
            if (result.Success) {
                resultEl.innerHTML = '<span style="color:#52B54B;">' + escapeHtml(result.Message) + '</span>';
            } else {
                resultEl.innerHTML = '<span style="color:#cc0000;">' + escapeHtml(result.Message) + '</span>';
            }
        }).catch(function () {
            btn.disabled = false;
            resultEl.innerHTML = '<span style="color:#cc0000;">Delete request failed. Check server logs.</span>';
        });
    }

    function refreshCache(view) {
        var resultEl = view.querySelector('.refreshCacheResult');
        resultEl.innerHTML = '<span style="opacity:0.5;">Refreshing cache...</span>';

        var apiUrl = ApiClient.getUrl('XtreamTuner/RefreshCache');

        ApiClient.ajax({
            type: 'POST',
            url: apiUrl
        }).then(function () {
            resultEl.innerHTML = '<span style="color:#52B54B;">Cache refreshed successfully!</span>';
        }).catch(function () {
            resultEl.innerHTML = '<span style="color:#cc0000;">Failed to refresh cache.</span>';
        });
    }

    // ---- Dashboard ----

    var dashboardPollId = null;

    function loadDashboard(view) {
        var apiUrl = ApiClient.getUrl('XtreamTuner/Dashboard');

        ApiClient.getJSON(apiUrl).then(function (data) {
            renderDashboardStatus(view, data);
            renderLibraryStats(view, data);
            renderDashboardHistory(view, data);

            if (data.IsRunning) {
                startDashboardProgressPolling(view);
            } else {
                stopDashboardProgressPolling();
                view.querySelector('.dashboardLiveProgress').style.display = 'none';
            }
        }).catch(function () {
            // Dashboard load failed silently
        });

        checkForUpdate(view);
    }

    function checkForUpdate(view) {
        var betaEl = view.querySelector('.chkUseBetaChannel');
        var betaParam = betaEl ? ('?beta=' + (betaEl.checked ? 'true' : 'false')) : '';
        var apiUrl = ApiClient.getUrl('XtreamTuner/CheckUpdate') + betaParam;

        ApiClient.getJSON(apiUrl).then(function (data) {
            var banner = view.querySelector('.updateBanner');
            if (!banner) return;

            // Always show current version on Dashboard
            var versionEl = view.querySelector('.pluginVersion');
            if (versionEl && data.CurrentVersion) {
                if (data.UpdateInstalled) {
                    versionEl.innerHTML = 'v' + escapeHtml(data.CurrentVersion) +
                        ' <span style="color:#e67e22;">\u2192 v' + escapeHtml(data.LatestVersion) + ' (restart needed)</span>';
                } else if (data.UpdateAvailable) {
                    versionEl.innerHTML = 'v' + escapeHtml(data.CurrentVersion) +
                        ' <span style="color:#e67e22;">— update available</span>';
                } else {
                    versionEl.innerHTML = 'v' + escapeHtml(data.CurrentVersion) +
                        ' <span style="color:#52B54B;">— latest</span>';
                }
            }

            if (data.UpdateInstalled) {
                // Update already installed, show restart banner
                banner.style.background = 'rgba(230,126,34,0.15)';
                banner.style.borderColor = 'rgba(230,126,34,0.4)';
                view.querySelector('.updateBannerTitle').textContent = 'Update Installed:';
                view.querySelector('.updateBannerText').textContent =
                    'v' + data.LatestVersion + ' has been installed. Restart Emby to apply.';
                view.querySelector('.btnInstallUpdate').style.display = 'none';
                view.querySelector('.btnRestartEmby').style.display = '';
                var link = view.querySelector('.updateBannerLink');
                if (data.ReleaseUrl) {
                    link.href = data.ReleaseUrl;
                    link.style.display = '';
                } else {
                    link.style.display = 'none';
                }
                view.querySelector('.updateStatus').style.display = 'none';
                banner.style.display = 'block';
            } else if (data.UpdateAvailable) {
                var isBeta = !!data.IsPreRelease;
                banner.style.background = isBeta ? 'rgba(230,152,34,0.15)' : 'rgba(82,181,75,0.15)';
                banner.style.borderColor = isBeta ? 'rgba(230,152,34,0.4)' : 'rgba(82,181,75,0.4)';
                view.querySelector('.updateBannerTitle').textContent = 'Update Available:';
                var betaLabel = isBeta ? ' (beta)' : '';
                view.querySelector('.updateBannerText').textContent =
                    'v' + data.LatestVersion + betaLabel + ' is available (you have v' + data.CurrentVersion + ')';
                view.querySelector('.btnInstallUpdate').style.display = '';
                view.querySelector('.btnInstallUpdate').disabled = false;
                view.querySelector('.btnRestartEmby').style.display = 'none';
                var link = view.querySelector('.updateBannerLink');
                if (data.ReleaseUrl) {
                    link.href = data.ReleaseUrl;
                    link.style.display = '';
                } else {
                    link.style.display = 'none';
                }
                // Hide install button if no download URL
                if (!data.DownloadUrl) {
                    view.querySelector('.btnInstallUpdate').style.display = 'none';
                }
                view.querySelector('.updateStatus').style.display = 'none';
                banner.style.display = 'block';
            } else {
                banner.style.display = 'none';
            }
        }).catch(function () {
            // Update check failed silently
        });
    }

    function installUpdate(view) {
        var btn = view.querySelector('.btnInstallUpdate');
        var statusEl = view.querySelector('.updateStatus');
        btn.disabled = true;
        btn.textContent = 'Installing...';
        statusEl.style.display = 'block';
        statusEl.innerHTML = '<span style="opacity:0.5;">Downloading and installing update...</span>';

        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('XtreamTuner/InstallUpdate'),
            dataType: 'json'
        }).then(function (result) {
            if (result.Success) {
                statusEl.innerHTML = '<span style="color:#52B54B;">' + escapeHtml(result.Message) + '</span>';
                // Switch banner to restart state
                var banner = view.querySelector('.updateBanner');
                banner.style.background = 'rgba(230,126,34,0.15)';
                banner.style.borderColor = 'rgba(230,126,34,0.4)';
                view.querySelector('.updateBannerTitle').textContent = 'Update Installed:';
                btn.style.display = 'none';
                view.querySelector('.btnRestartEmby').style.display = '';
            } else {
                statusEl.innerHTML = '<span style="color:#cc0000;">' + escapeHtml(result.Message) + '</span>';
                btn.disabled = false;
                btn.textContent = 'Update Now';
            }
        }).catch(function () {
            statusEl.innerHTML = '<span style="color:#cc0000;">Install request failed. Check server logs.</span>';
            btn.disabled = false;
            btn.textContent = 'Update Now';
        });
    }

    function restartEmby(view) {
        if (!confirm('Are you sure you want to restart Emby? All active streams will be interrupted.')) {
            return;
        }

        var btn = view.querySelector('.btnRestartEmby');
        var statusEl = view.querySelector('.updateStatus');
        btn.disabled = true;
        btn.textContent = 'Restarting...';
        statusEl.style.display = 'block';
        statusEl.innerHTML = '<span style="opacity:0.5;">Restarting Emby server...</span>';

        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('XtreamTuner/RestartEmby')
        }).then(function () {
            statusEl.innerHTML = '<span style="opacity:0.5;">Waiting for server to come back...</span>';
            pollServerReady(view);
        }).catch(function () {
            // Server may have already restarted and dropped the connection
            statusEl.innerHTML = '<span style="opacity:0.5;">Waiting for server to come back...</span>';
            pollServerReady(view);
        });
    }

    function pollServerReady(view) {
        var statusEl = view.querySelector('.updateStatus');
        var attempts = 0;
        var maxAttempts = 60; // 60 * 2s = 2 minutes

        var pollId = setInterval(function () {
            attempts++;
            if (attempts > maxAttempts) {
                clearInterval(pollId);
                statusEl.innerHTML = '<span style="color:#cc0000;">Server did not come back within 2 minutes. Try reloading manually.</span>';
                return;
            }

            var xhr = new XMLHttpRequest();
            xhr.open('GET', ApiClient.getUrl('System/Info/Public'), true);
            xhr.timeout = 3000;
            xhr.onload = function () {
                if (xhr.status >= 200 && xhr.status < 300) {
                    clearInterval(pollId);
                    statusEl.innerHTML = '<span style="color:#52B54B;">Server is back! Reloading...</span>';
                    setTimeout(function () { window.location.reload(); }, 1000);
                }
            };
            xhr.onerror = function () { };
            xhr.ontimeout = function () { };
            xhr.send();
        }, 2000);
    }

    function renderDashboardStatus(view, data) {
        var container = view.querySelector('.dashboardStatusContent');
        var statsContainer = view.querySelector('.dashboardStatusStats');

        if (!data.LastSync) {
            container.innerHTML = '<span class="status-badge idle">No syncs yet</span>';
            statsContainer.style.display = 'none';
            return;
        }

        var last = data.LastSync;

        // Look for a companion scan (movies if last was series-only, or vice versa).
        // Movies always run before series in a combined sync, so it's in History[1+].
        var companion = null;
        if (data.History && data.History.length > 1) {
            for (var i = 1; i < data.History.length; i++) {
                var entry = data.History[i];
                if (last.WasSeriesSync && !last.WasMovieSync && entry.WasMovieSync) {
                    companion = entry;
                    break;
                }
                if (last.WasMovieSync && !last.WasSeriesSync && entry.WasSeriesSync) {
                    companion = entry;
                    break;
                }
            }
        }

        var overallSuccess = last.Success && (!companion || companion.Success);
        var badgeClass = overallSuccess ? 'success' : 'failed';
        var badgeText = overallSuccess ? 'Success' : 'Failed';

        var duration = Math.round((new Date(last.EndTime) - new Date(last.StartTime)) / 1000);
        var durationText = duration >= 60
            ? Math.floor(duration / 60) + 'm ' + (duration % 60) + 's'
            : duration + 's';

        var timeAgo = formatTimeAgo(new Date(last.EndTime));

        container.innerHTML =
            '<span class="status-badge ' + badgeClass + '">' + badgeText + '</span>' +
            '<span style="margin-left:0.8em; opacity:0.6; font-size:0.9em;">' + timeAgo + ' (' + durationText + ')</span>';

        var movieEntry = last.WasMovieSync ? last : (companion && companion.WasMovieSync ? companion : null);
        var seriesEntry = last.WasSeriesSync ? last : (companion && companion.WasSeriesSync ? companion : null);

        function statTile(value, label, color) {
            return '<div class="dashboard-stat"><div class="stat-value" style="color:' + (color || '#52B54B') + ';">' + value + '</div><div class="stat-label">' + label + '</div></div>';
        }
        function rowLabel(text) {
            return '<div style="font-size:0.75em; font-weight:600; opacity:0.45; text-transform:uppercase; letter-spacing:0.06em; margin-bottom:0.35em;">' + text + '</div>';
        }

        var statsHtml = '';
        if (movieEntry) {
            var movieTiles =
                statTile(movieEntry.MoviesTotal, 'Total') +
                (movieEntry.MoviesAdded > 0 ? statTile('+' + movieEntry.MoviesAdded, 'New') : '') +
                statTile(movieEntry.MoviesSkipped, 'Skipped') +
                statTile(movieEntry.MoviesFailed, 'Failed', movieEntry.MoviesFailed > 0 ? '#cc0000' : '#52B54B') +
                (movieEntry.MoviesDeleted > 0 ? statTile(movieEntry.MoviesDeleted, 'Deleted', '#e74c3c') : '');
            statsHtml += '<div style="margin-bottom:0.7em;">' + rowLabel('Movies') + '<div style="display:grid;grid-auto-flow:column;grid-auto-columns:1fr;gap:0.5em;">' + movieTiles + '</div></div>';
        }
        if (seriesEntry) {
            var seriesTiles =
                statTile(seriesEntry.SeriesTotal, 'Total') +
                (seriesEntry.SeriesAdded > 0 ? statTile('+' + seriesEntry.SeriesAdded, 'New') : '') +
                statTile(seriesEntry.SeriesSkipped, 'Skipped') +
                statTile(seriesEntry.SeriesFailed, 'Failed', seriesEntry.SeriesFailed > 0 ? '#cc0000' : '#52B54B') +
                (seriesEntry.SeriesDeleted > 0 ? statTile(seriesEntry.SeriesDeleted, 'Deleted', '#e74c3c') : '');
            statsHtml += '<div>' + rowLabel('Episodes') + '<div style="display:grid;grid-auto-flow:column;grid-auto-columns:1fr;gap:0.5em;">' + seriesTiles + '</div></div>';
        }

        if (statsHtml) {
            statsContainer.innerHTML = statsHtml;
            statsContainer.style.display = 'block';
        } else {
            statsContainer.style.display = 'none';
        }
    }

    function renderLibraryStats(view, data) {
        var container = view.querySelector('.dashboardLibraryContent');
        var stats = data.LibraryStats || {};

        container.innerHTML =
            '<div class="library-stat-bar">' +
                '<div class="stat-count">' + (stats.MovieFolders || 0) + '</div>' +
                '<div class="stat-type">Movie folders</div>' +
            '</div>' +
            '<div class="library-stat-bar">' +
                '<div class="stat-count">' + (stats.SeriesFolders || 0) + '</div>' +
                '<div class="stat-type">Series folders</div>' +
            '</div>';
    }

    function renderDashboardHistory(view, data) {
        var container = view.querySelector('.dashboardHistoryContent');

        if (!data.History || data.History.length === 0) {
            container.innerHTML = '<div style="opacity:0.5;">No sync history yet</div>';
            return;
        }

        var html = '<table class="dashboard-history-table">';
        html += '<thead><tr><th>Time</th><th>Status</th><th>Duration</th><th>Movies</th><th>Episodes</th></tr></thead>';
        html += '<tbody>';

        function historyMovieCol(e) {
            return e.MoviesTotal +
                ' <span style="opacity:0.5;">(' +
                '<span style="color:#52B54B; opacity:1;">+' + (e.MoviesAdded || 0) + '</span> ' +
                '<span style="color:#e74c3c; opacity:1;">-' + (e.MoviesDeleted || 0) + '</span>, ' +
                e.MoviesSkipped + ' skip, ' +
                e.MoviesFailed + ' fail' +
                ')</span>';
        }
        function historySeriesCol(e) {
            return e.SeriesTotal +
                ' <span style="opacity:0.5;">(' +
                '<span style="color:#52B54B; opacity:1;">+' + (e.SeriesAdded || 0) + '</span> ' +
                '<span style="color:#e74c3c; opacity:1;">-' + (e.SeriesDeleted || 0) + '</span>, ' +
                e.SeriesSkipped + ' skip, ' +
                e.SeriesFailed + ' fail' +
                ')</span>';
        }

        var dash = '<span style="opacity:0.3;">\u2014</span>';

        var i = 0;
        while (i < data.History.length) {
            var entry = data.History[i];
            var next = i + 1 < data.History.length ? data.History[i + 1] : null;

            // Pair a series-only entry with the following movie-only entry (or vice versa)
            var movieEntry = null, seriesEntry = null, consumed = 1;
            if (next && entry.WasSeriesSync && !entry.WasMovieSync && next.WasMovieSync && !next.WasSeriesSync) {
                seriesEntry = entry; movieEntry = next; consumed = 2;
            } else if (next && entry.WasMovieSync && !entry.WasSeriesSync && next.WasSeriesSync && !next.WasMovieSync) {
                movieEntry = entry; seriesEntry = next; consumed = 2;
            } else {
                movieEntry = entry.WasMovieSync ? entry : null;
                seriesEntry = entry.WasSeriesSync ? entry : null;
            }

            // Use the most recent entry for time/status; sum durations if paired
            var primary = entry;
            var success = entry.Success;
            if (consumed === 2) {
                success = (movieEntry ? movieEntry.Success : true) && (seriesEntry ? seriesEntry.Success : true);
            }
            var badgeClass = success ? 'success' : 'failed';
            var badgeText = success ? 'Success' : 'Failed';

            var dur = Math.round((new Date(primary.EndTime) - new Date(primary.StartTime)) / 1000);
            if (consumed === 2 && next) {
                dur += Math.round((new Date(next.EndTime) - new Date(next.StartTime)) / 1000);
            }
            var durationText = dur >= 60 ? Math.floor(dur / 60) + 'm ' + (dur % 60) + 's' : dur + 's';
            var timeStr = formatTimeAgo(new Date(primary.EndTime));

            var movieCol = movieEntry ? historyMovieCol(movieEntry) : dash;
            var seriesCol = seriesEntry ? historySeriesCol(seriesEntry) : dash;

            html += '<tr>';
            html += '<td>' + timeStr + '</td>';
            html += '<td><span class="status-badge ' + badgeClass + '">' + badgeText + '</span></td>';
            html += '<td>' + durationText + '</td>';
            html += '<td>' + movieCol + '</td>';
            html += '<td>' + seriesCol + '</td>';
            html += '</tr>';

            i += consumed;
        }

        html += '</tbody></table>';
        container.innerHTML = html;
    }

    function startDashboardProgressPolling(view) {
        stopDashboardProgressPolling();
        var progressCard = view.querySelector('.dashboardLiveProgress');
        progressCard.style.display = 'block';

        dashboardPollId = setInterval(function () {
            var apiUrl = ApiClient.getUrl('XtreamTuner/Sync/Status');
            ApiClient.getJSON(apiUrl).then(function (status) {
                var movieProg = status.Movies;
                var seriesProg = status.Series;
                var isRunning = (movieProg && movieProg.IsRunning) || (seriesProg && seriesProg.IsRunning);

                if (!isRunning) {
                    stopDashboardProgressPolling();
                    progressCard.style.display = 'none';
                    loadDashboard(view);
                    return;
                }

                var active = (movieProg && movieProg.IsRunning) ? movieProg : seriesProg;
                var total = active.Total || 0;
                var completed = active.Completed || 0;
                var pct = total > 0 ? Math.round((completed / total) * 100) : 0;

                view.querySelector('.dashboardProgressPhase').textContent = active.Phase || 'Working...';
                view.querySelector('.dashboardProgressBarFill').style.width = pct + '%';
                view.querySelector('.dashboardProgressDetail').textContent =
                    completed + ' / ' + total + ' (' + active.Skipped + ' skipped, ' + active.Failed + ' failed) \u2014 ' + pct + '%';
            }).catch(function () { });
        }, 500);
    }

    function stopDashboardProgressPolling() {
        if (dashboardPollId) {
            clearInterval(dashboardPollId);
            dashboardPollId = null;
        }
    }

    function dashboardSyncAll(instance) {
        var view = instance.view;
        var btn = view.querySelector('.btnDashboardSyncAll');
        var resultEl = view.querySelector('.dashboardSyncAllResult');
        btn.disabled = true;
        resultEl.innerHTML = '<span style="opacity:0.5;">Starting sync...</span>';

        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            var doMovies = config.SyncMovies;
            var doSeries = config.SyncSeries;

            if (!doMovies && !doSeries) {
                btn.disabled = false;
                resultEl.innerHTML = '<span style="color:#cc0000;">Nothing to sync. Enable Movies or Series sync in Settings first.</span>';
                return;
            }

            startDashboardProgressPolling(view);

            var movieUrl = ApiClient.getUrl('XtreamTuner/Sync/Movies');
            var seriesUrl = ApiClient.getUrl('XtreamTuner/Sync/Series');
            var movieMsg = '';

            var moviePromise = doMovies
                ? ApiClient.ajax({ type: 'POST', url: movieUrl, dataType: 'json' })
                : Promise.resolve(null);

            moviePromise.then(function (movieResult) {
                if (movieResult) {
                    movieMsg = movieResult.Success
                        ? 'Movies: ' + movieResult.Total + ' total, ' + movieResult.Skipped + ' skipped, ' + movieResult.Failed + ' failed'
                        : 'Movies failed: ' + movieResult.Message;
                }

                if (doSeries) {
                    var prefix = movieMsg ? escapeHtml(movieMsg) + ' \u2014 ' : '';
                    resultEl.innerHTML = '<span style="opacity:0.5;">' + prefix + 'Starting series sync...</span>';

                    return ApiClient.ajax({ type: 'POST', url: seriesUrl, dataType: 'json' }).then(function (seriesResult) {
                        stopDashboardProgressPolling();
                        view.querySelector('.dashboardLiveProgress').style.display = 'none';
                        btn.disabled = false;

                        var seriesMsg = seriesResult.Success
                            ? 'Series: ' + seriesResult.Total + ' total, ' + seriesResult.Skipped + ' skipped, ' + seriesResult.Failed + ' failed'
                            : 'Series failed: ' + seriesResult.Message;

                        var parts = [];
                        if (movieMsg) parts.push(movieMsg);
                        parts.push(seriesMsg);
                        var overallSuccess = (!movieResult || movieResult.Success) && seriesResult.Success;
                        var color = overallSuccess ? '#52B54B' : '#cc0000';
                        resultEl.innerHTML = '<span style="color:' + color + ';">' + escapeHtml(parts.join(' | ')) + '</span>';
                        loadDashboard(view);
                    });
                } else {
                    stopDashboardProgressPolling();
                    view.querySelector('.dashboardLiveProgress').style.display = 'none';
                    btn.disabled = false;
                    var color = movieResult && movieResult.Success ? '#52B54B' : '#cc0000';
                    resultEl.innerHTML = '<span style="color:' + color + ';">' + escapeHtml(movieMsg) + '</span>';
                    loadDashboard(view);
                }
            }).catch(function () {
                stopDashboardProgressPolling();
                view.querySelector('.dashboardLiveProgress').style.display = 'none';
                btn.disabled = false;
                resultEl.innerHTML = '<span style="color:#cc0000;">Sync request failed. Check server logs.</span>';
                loadDashboard(view);
            });
        }).catch(function () {
            btn.disabled = false;
            resultEl.innerHTML = '<span style="color:#cc0000;">Failed to load config.</span>';
        });
    }

    function formatTimeAgo(date) {
        var now = new Date();
        var diff = Math.round((now - date) / 1000);
        if (diff < 60) return 'just now';
        if (diff < 3600) return Math.floor(diff / 60) + 'm ago';
        if (diff < 86400) return Math.floor(diff / 3600) + 'h ago';
        return date.toLocaleDateString() + ' ' + date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    }

    function setupCategorySearch(view, inputSelector, listSelector) {
        var input = view.querySelector(inputSelector);
        if (!input) return;
        input.addEventListener('input', function () {
            var filter = input.value.toLowerCase();
            var items = view.querySelectorAll(listSelector + ' .checkboxContainer');
            for (var i = 0; i < items.length; i++) {
                var text = items[i].textContent.toLowerCase();
                items[i].style.display = text.indexOf(filter) >= 0 ? '' : 'none';
            }
        });
    }

    function escapeHtml(text) {
        var div = document.createElement('div');
        div.appendChild(document.createTextNode(text));
        return div.innerHTML;
    }

    return View;
});
