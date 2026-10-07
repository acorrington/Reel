define(['baseView', 'loading', 'emby-input', 'emby-button', 'emby-checkbox', 'emby-scroller'], function (BaseView, loading) {
    'use strict';

    // Reel plugin id — must match Plugin.Id in Plugin.cs
    var PLUGIN_ID = 'B910B3A1-A0EE-4C87-9695-AFD021DE678E';

    function loadPage(page, config) {

        page.querySelector('#EnableScheduledScan').checked = config.EnableScheduledScan !== false;
        page.querySelector('#MaxVideosPerRun').value = config.MaxVideosPerRun || 100;
        page.querySelector('#TargetFolder').value = config.TargetFolder || '';
        page.querySelector('#DurationTolerancePercent').value = config.DurationTolerancePercent != null ? config.DurationTolerancePercent : 20;
        page.querySelector('#ImvdbSkipsDurationGate').checked = config.ImvdbSkipsDurationGate !== false;
        page.querySelector('#ExcludeTitlePatterns').value = config.ExcludeTitlePatterns || '';
        page.querySelector('#RejectStaticImageVideos').checked = config.RejectStaticImageVideos !== false;
        page.querySelector('#StaticImageDiffThreshold').value = config.StaticImageDiffThreshold != null ? config.StaticImageDiffThreshold : 10;
        page.querySelector('#ImvdbApiKey').value = config.ImvdbApiKey || '';
        page.querySelector('#MaxSearchResults').value = config.MaxSearchResults || 5;
        page.querySelector('#MaxVideoHeight').value = config.MaxVideoHeight || 0;
        page.querySelector('#FfmpegPathOverride').value = config.FfmpegPathOverride || '';
        page.querySelector('#AndroidClientVersion').value = config.AndroidClientVersion || '20.10.3';
        page.querySelector('#IosClientVersion').value = config.IosClientVersion || '20.10.4';
        page.querySelector('#WebSearchClientVersion').value = config.WebSearchClientVersion || '2.20251006.01.00';
        page.querySelector('#PreferIosClient').checked = config.PreferIosClient === true;
        page.querySelector('#LastRunSummary').value = config.LastRunSummary || 'Never run';

        loading.hide();
    }

    function onSubmit(e) {

        e.preventDefault();

        var form = this;
        var targetFolder = form.querySelector('#TargetFolder').value.trim();

        if (!targetFolder) {
            Dashboard.alert('Target folder is required: point it at your Music Videos library folder.');
            return false;
        }

        loading.show();

        ApiClient.getPluginConfiguration(PLUGIN_ID).then(function (config) {

            config.EnableScheduledScan = form.querySelector('#EnableScheduledScan').checked;
            config.MaxVideosPerRun = parseInt(form.querySelector('#MaxVideosPerRun').value, 10) || 100;
            config.TargetFolder = targetFolder;
            config.DurationTolerancePercent = parseInt(form.querySelector('#DurationTolerancePercent').value, 10);
            if (isNaN(config.DurationTolerancePercent)) { config.DurationTolerancePercent = 20; }
            config.ImvdbSkipsDurationGate = form.querySelector('#ImvdbSkipsDurationGate').checked;
            config.ExcludeTitlePatterns = form.querySelector('#ExcludeTitlePatterns').value;
            config.RejectStaticImageVideos = form.querySelector('#RejectStaticImageVideos').checked;
            config.StaticImageDiffThreshold = parseFloat(form.querySelector('#StaticImageDiffThreshold').value);
            if (isNaN(config.StaticImageDiffThreshold) || config.StaticImageDiffThreshold <= 0) { config.StaticImageDiffThreshold = 10; }
            config.ImvdbApiKey = form.querySelector('#ImvdbApiKey').value.trim();
            config.MaxSearchResults = parseInt(form.querySelector('#MaxSearchResults').value, 10) || 5;
            config.MaxVideoHeight = parseInt(form.querySelector('#MaxVideoHeight').value, 10) || 0;
            config.FfmpegPathOverride = form.querySelector('#FfmpegPathOverride').value;
            config.AndroidClientVersion = form.querySelector('#AndroidClientVersion').value;
            config.IosClientVersion = form.querySelector('#IosClientVersion').value;
            config.WebSearchClientVersion = form.querySelector('#WebSearchClientVersion').value;
            config.PreferIosClient = form.querySelector('#PreferIosClient').checked;

            ApiClient.updatePluginConfiguration(PLUGIN_ID, config).then(Dashboard.processPluginConfigurationUpdateResult);
        });

        // Disable default form submission
        return false;
    }

    function getConfig() {

        return ApiClient.getPluginConfiguration(PLUGIN_ID);
    }

    function View(view, params) {
        BaseView.apply(this, arguments);

        view.querySelector('form').addEventListener('submit', onSubmit);
    }

    Object.assign(View.prototype, BaseView.prototype);

    View.prototype.onResume = function (options) {

        BaseView.prototype.onResume.apply(this, arguments);

        loading.show();

        var page = this.view;

        getConfig().then(function (response) {

            loadPage(page, response);
        });
    };

    return View;

});
