define(['baseView', 'loading', 'emby-input', 'emby-button', 'emby-checkbox', 'emby-scroller'], function (BaseView, loading) {
    'use strict';

    var PLUGIN_ID = '910C9CE1-C355-48FA-93D5-411EE319D392';

    function loadPage(page, config) {

        page.querySelector('#EnableAutoDownload').checked = config.EnableAutoDownload !== false;
        page.querySelector('#TriggerDelaySeconds').value = config.TriggerDelaySeconds != null ? config.TriggerDelaySeconds : 30;
        page.querySelector('#MaxVideoHeight').value = config.MaxVideoHeight || 0;
        page.querySelector('#EnableYouTubeSearchFallback').checked = config.EnableYouTubeSearchFallback !== false;
        page.querySelector('#MaxSearchResults').value = config.MaxSearchResults || 3;
        page.querySelector('#FfmpegPathOverride').value = config.FfmpegPathOverride || '';
        page.querySelector('#AndroidClientVersion').value = config.AndroidClientVersion || '20.10.3';
        page.querySelector('#IosClientVersion').value = config.IosClientVersion || '20.10.4';
        page.querySelector('#PreferIosClient').checked = config.PreferIosClient === true;
        page.querySelector('#LastRunSummary').value = config.LastRunSummary || 'Never run';

        loading.hide();
    }

    function onSubmit(e) {

        e.preventDefault();

        loading.show();

        var form = this;

        ApiClient.getPluginConfiguration(PLUGIN_ID).then(function (config) {

            config.EnableAutoDownload = form.querySelector('#EnableAutoDownload').checked;
            config.TriggerDelaySeconds = parseInt(form.querySelector('#TriggerDelaySeconds').value, 10) || 0;
            config.MaxVideoHeight = parseInt(form.querySelector('#MaxVideoHeight').value, 10) || 0;
            config.EnableYouTubeSearchFallback = form.querySelector('#EnableYouTubeSearchFallback').checked;
            config.MaxSearchResults = parseInt(form.querySelector('#MaxSearchResults').value, 10) || 3;
            config.FfmpegPathOverride = form.querySelector('#FfmpegPathOverride').value;
            config.AndroidClientVersion = form.querySelector('#AndroidClientVersion').value;
            config.IosClientVersion = form.querySelector('#IosClientVersion').value;
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
