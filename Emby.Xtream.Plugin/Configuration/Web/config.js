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

        view.querySelector('.btnAddMovieFolder').addEventListener('click', function () {
            addFolderEntry(view, 'movie', '', '', self.loadedVodCategories);
        });

        view.querySelector('.btnAddSeriesFolder').addEventListener('click', function () {
            addFolderEntry(view, 'series', '', '', self.loadedSeriesCategories);
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

        // Dashboard sync all button
        view.querySelector('.btnDashboardSyncAll').addEventListener('click', function () {
            dashboardSyncAll(self);
        });

        // Download sanitized log button
        view.querySelector('.btnDownloadLog').addEventListener('click', function () {
            window.open(ApiClient.getUrl('XtreamTuner/Logs') + '&api_key=' + ApiClient.accessToken(), '_blank');
        });

        // Dismiss update banner
        view.querySelector('.updateBannerDismiss').addEventListener('click', function () {
            view.querySelector('.updateBanner').style.display = 'none';
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

            // Sync settings
            view.querySelector('.chkSmartSkipExisting').checked = config.SmartSkipExisting !== false;
            view.querySelector('.txtSyncParallelism').value = config.SyncParallelism || 3;
            view.querySelector('.chkCleanupOrphans').checked = !!config.CleanupOrphans;

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

            // Sync settings
            config.SmartSkipExisting = view.querySelector('.chkSmartSkipExisting').checked;
            config.SyncParallelism = parseInt(view.querySelector('.txtSyncParallelism').value, 10) || 3;
            config.CleanupOrphans = view.querySelector('.chkCleanupOrphans').checked;

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

    // ---- Folder card management (for Multiple Folders mode) ----

    function addFolderEntry(view, type, name, checkedIdsStr, categories) {
        var listClass = type === 'movie' ? '.movieFoldersList' : '.seriesFoldersList';
        var list = view.querySelector(listClass);

        var card = document.createElement('div');
        card.className = 'folderCard';
        card.setAttribute('data-checked-ids', checkedIdsStr || '');
        card.style.cssText = 'border:1px solid rgba(255,255,255,0.15); border-radius:6px; padding:1em; margin-bottom:1em;';

        // Header: name input + remove button
        var header = document.createElement('div');
        header.style.cssText = 'display:flex; gap:0.5em; align-items:center; margin-bottom:0.5em;';

        var nameInput = document.createElement('input');
        nameInput.type = 'text';
        nameInput.className = 'folderCardName';
        nameInput.placeholder = 'Folder name';
        nameInput.value = name;
        nameInput.style.cssText = 'flex:1; padding:0.5em 0.8em; background:transparent; border:1px solid rgba(255,255,255,0.2); border-radius:4px; color:inherit; font-size:1em;';

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
        catContainer.style.cssText = 'max-height:300px; overflow-y:auto; border:1px solid rgba(255,255,255,0.1); border-radius:4px; padding:0.5em;';

        if (categories && categories.length > 0) {
            renderFolderCardCategories(catContainer, categories, checkedIdsStr);
        } else if (categories !== null && categories !== undefined) {
            catContainer.innerHTML = '<div style="color:#999; padding:0.5em;">No categories available from server. Click Refresh Categories to try again.</div>';
        } else {
            catContainer.innerHTML = '<div style="color:#999; padding:0.5em;">Loading categories...</div>';
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
            html += '<span>' + escapeHtml(cat.CategoryName) + ' <span style="color:#999;">(ID: ' + cat.CategoryId + ')</span></span>';
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
            catContainer.innerHTML = '<div style="color:#999; padding:0.5em;">No categories available from server.</div>';
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
        resultEl.innerHTML = '<span style="color:#999;">Testing connection...</span>';

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

    function testDispatcharrConnection(instance) {
        var view = instance.view;
        var resultEl = view.querySelector('.dispatcharrTestResult');
        resultEl.innerHTML = '<span style="color:#999;">Saving config &amp; testing connection...</span>';

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
                vodListEl.innerHTML = '<div style="color:#999;">Click "Refresh Categories" to load.</div>';
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
                seriesListEl.innerHTML = '<div style="color:#999;">Click "Refresh Categories" to load.</div>';
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
                listEl.innerHTML = '<div style="color:#999;">No categories found. Check your Xtream connection settings.</div>';
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
                listEl.innerHTML = '<div style="color:#999;">No VOD categories found. Check your Xtream connection settings.</div>';
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
        statusEl.style.color = '#999';

        var apiUrl = ApiClient.getUrl('XtreamTuner/Categories/Vod');

        ApiClient.getJSON(apiUrl).then(function (categories) {
            instance.loadedVodCategories = categories || [];

            if (!categories || categories.length === 0) {
                statusEl.textContent = 'No VOD categories found.';
                statusEl.style.color = '#cc0000';
                clearFolderCardCategories(view, 'movie');
                return;
            }

            statusEl.textContent = 'Loaded ' + categories.length + ' categories';
            statusEl.style.color = '#52B54B';
            populateFolderCheckboxes(view, 'movie', categories);
        }).catch(function () {
            statusEl.textContent = 'Failed to load categories. Save connection settings first.';
            statusEl.style.color = '#cc0000';
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
                listEl.innerHTML = '<div style="color:#999;">No series categories found. Check your Xtream connection settings.</div>';
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
        statusEl.style.color = '#999';

        var apiUrl = ApiClient.getUrl('XtreamTuner/Categories/Series');

        ApiClient.getJSON(apiUrl).then(function (categories) {
            instance.loadedSeriesCategories = categories || [];

            if (!categories || categories.length === 0) {
                statusEl.textContent = 'No series categories found.';
                statusEl.style.color = '#cc0000';
                clearFolderCardCategories(view, 'series');
                return;
            }

            statusEl.textContent = 'Loaded ' + categories.length + ' categories';
            statusEl.style.color = '#52B54B';
            populateFolderCheckboxes(view, 'series', categories);
        }).catch(function () {
            statusEl.textContent = 'Failed to load categories. Save connection settings first.';
            statusEl.style.color = '#cc0000';
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
                '<div style="background:rgba(255,255,255,0.15); border-radius:4px; height:20px; overflow:hidden;">' +
                    '<div style="background:#52B54B; height:100%; width:' + pct + '%; transition:width 0.3s ease; border-radius:4px;"></div>' +
                '</div>' +
                '<div style="color:#ccc; margin-top:0.4em; font-size:0.9em;">' +
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
        resultEl.innerHTML = '<span style="color:#999;">Starting movie sync...</span>';

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
        resultEl.innerHTML = '<span style="color:#999;">Starting series sync...</span>';

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
        resultEl.innerHTML = '<span style="color:#999;">Deleting ' + label + '...</span>';

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
        resultEl.innerHTML = '<span style="color:#999;">Refreshing cache...</span>';

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
        var apiUrl = ApiClient.getUrl('XtreamTuner/CheckUpdate');

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
                banner.style.background = 'rgba(82,181,75,0.15)';
                banner.style.borderColor = 'rgba(82,181,75,0.4)';
                view.querySelector('.updateBannerTitle').textContent = 'Update Available:';
                view.querySelector('.updateBannerText').textContent =
                    'v' + data.LatestVersion + ' is available (you have v' + data.CurrentVersion + ')';
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
        statusEl.innerHTML = '<span style="color:#999;">Downloading and installing update...</span>';

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
        statusEl.innerHTML = '<span style="color:#999;">Restarting Emby server...</span>';

        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('XtreamTuner/RestartEmby')
        }).then(function () {
            statusEl.innerHTML = '<span style="color:#999;">Waiting for server to come back...</span>';
            pollServerReady(view);
        }).catch(function () {
            // Server may have already restarted and dropped the connection
            statusEl.innerHTML = '<span style="color:#999;">Waiting for server to come back...</span>';
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
        var badgeClass = last.Success ? 'success' : 'failed';
        var badgeText = last.Success ? 'Success' : 'Failed';

        var duration = Math.round((new Date(last.EndTime) - new Date(last.StartTime)) / 1000);
        var durationText = duration >= 60
            ? Math.floor(duration / 60) + 'm ' + (duration % 60) + 's'
            : duration + 's';

        var timeAgo = formatTimeAgo(new Date(last.EndTime));

        container.innerHTML =
            '<span class="status-badge ' + badgeClass + '">' + badgeText + '</span>' +
            '<span style="margin-left:0.8em; opacity:0.6; font-size:0.9em;">' + timeAgo + ' (' + durationText + ')</span>';

        var statsHtml = '';
        if (last.WasMovieSync) {
            statsHtml +=
                '<div class="dashboard-stat"><div class="stat-value">' + last.MoviesTotal + '</div><div class="stat-label">Movies</div></div>' +
                '<div class="dashboard-stat"><div class="stat-value">' + last.MoviesSkipped + '</div><div class="stat-label">Skipped</div></div>' +
                '<div class="dashboard-stat"><div class="stat-value" style="color:' + (last.MoviesFailed > 0 ? '#cc0000' : '#52B54B') + ';">' + last.MoviesFailed + '</div><div class="stat-label">Failed</div></div>';
        }
        if (last.WasSeriesSync) {
            statsHtml +=
                '<div class="dashboard-stat"><div class="stat-value">' + last.SeriesTotal + '</div><div class="stat-label">Series</div></div>' +
                '<div class="dashboard-stat"><div class="stat-value">' + last.SeriesSkipped + '</div><div class="stat-label">Skipped</div></div>' +
                '<div class="dashboard-stat"><div class="stat-value" style="color:' + (last.SeriesFailed > 0 ? '#cc0000' : '#52B54B') + ';">' + last.SeriesFailed + '</div><div class="stat-label">Failed</div></div>';
        }

        if (statsHtml) {
            statsContainer.innerHTML = statsHtml;
            statsContainer.style.display = 'grid';
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
            container.innerHTML = '<div style="color:rgba(255,255,255,0.5);">No sync history yet</div>';
            return;
        }

        var html = '<table class="dashboard-history-table">';
        html += '<thead><tr><th>Time</th><th>Status</th><th>Duration</th><th>Movies</th><th>Series</th></tr></thead>';
        html += '<tbody>';

        for (var i = 0; i < data.History.length; i++) {
            var entry = data.History[i];
            var badgeClass = entry.Success ? 'success' : 'failed';
            var badgeText = entry.Success ? 'Success' : 'Failed';

            var duration = Math.round((new Date(entry.EndTime) - new Date(entry.StartTime)) / 1000);
            var durationText = duration >= 60
                ? Math.floor(duration / 60) + 'm ' + (duration % 60) + 's'
                : duration + 's';

            var timeStr = formatTimeAgo(new Date(entry.EndTime));

            var movieCol = entry.WasMovieSync
                ? entry.MoviesTotal + ' <span style="opacity:0.5;">(' + entry.MoviesSkipped + ' skip, ' + entry.MoviesFailed + ' fail)</span>'
                : '<span style="opacity:0.3;">\u2014</span>';

            var seriesCol = entry.WasSeriesSync
                ? entry.SeriesTotal + ' <span style="opacity:0.5;">(' + entry.SeriesSkipped + ' skip, ' + entry.SeriesFailed + ' fail)</span>'
                : '<span style="opacity:0.3;">\u2014</span>';

            html += '<tr>';
            html += '<td>' + timeStr + '</td>';
            html += '<td><span class="status-badge ' + badgeClass + '">' + badgeText + '</span></td>';
            html += '<td>' + durationText + '</td>';
            html += '<td>' + movieCol + '</td>';
            html += '<td>' + seriesCol + '</td>';
            html += '</tr>';
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
        resultEl.innerHTML = '<span style="color:#999;">Starting sync...</span>';

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
                    resultEl.innerHTML = '<span style="color:#999;">' + prefix + 'Starting series sync...</span>';

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
