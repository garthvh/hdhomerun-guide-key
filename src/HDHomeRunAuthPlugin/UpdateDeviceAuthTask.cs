using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace HDHomeRunAuthPlugin;

public class UpdateDeviceAuthTask : IScheduledTask
{
    private const string LiveTvConfigKey = "livetv";
    private const string XmlTvBaseUrl = "https://api.hdhomerun.com/api/xmltv";
    private const string RefreshGuideTaskKey = "RefreshGuide";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfigurationManager _configManager;
    private readonly ITaskManager _taskManager;
    private readonly ILogger<UpdateDeviceAuthTask> _logger;

    public UpdateDeviceAuthTask(
        IHttpClientFactory httpClientFactory,
        IConfigurationManager configManager,
        ITaskManager taskManager,
        ILogger<UpdateDeviceAuthTask> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configManager = configManager;
        _taskManager = taskManager;
        _logger = logger;
    }

    public string Name => "Sync HDHomeRun Guide Auth Key";

    public string Key => "HDHomeRunUpdateDeviceAuth";

    public string Description => "Checks configured HDHomeRun tuners for their current guide DeviceAuth key and updates the XMLTV listings provider if it changed.";

    public string Category => "Live TV";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(0);

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var tunerUrls = GetTunerUrls(config);

        if (tunerUrls.Count == 0)
        {
            _logger.LogInformation(
                "No HDHomeRun tuner hosts configured (add one under Live TV > Tuner Devices, or set a manual URL in this plugin's settings). Nothing to do.");
            progress.Report(100);
            return;
        }

        progress.Report(10);

        var deviceAuthByDeviceId = await DiscoverDeviceAuthKeysAsync(tunerUrls, cancellationToken).ConfigureAwait(false);

        progress.Report(50);

        var distinctAuthKeys = deviceAuthByDeviceId.Values.Distinct().ToList();

        if (distinctAuthKeys.Count == 0)
        {
            _logger.LogWarning("Could not retrieve a DeviceAuth key from any configured tuner.");
            progress.Report(100);
            return;
        }

        if (distinctAuthKeys.Count > 1)
        {
            _logger.LogWarning(
                "Found {Count} HDHomeRun devices with different guide auth keys; skipping automatic update to avoid updating the wrong listings provider.",
                distinctAuthKeys.Count);
            progress.Report(100);
            return;
        }

        UpdateListingsProvider(distinctAuthKeys[0]);

        progress.Report(100);
    }

    private List<string> GetTunerUrls(PluginConfiguration config)
    {
        var urls = new List<string>();

        if (config.UseConfiguredTunerHosts)
        {
            var liveTvOptions = _configManager.GetConfiguration<LiveTvOptions>(LiveTvConfigKey);
            urls.AddRange(
                liveTvOptions.TunerHosts
                    .Where(t => string.Equals(t.Type, "hdhomerun", StringComparison.OrdinalIgnoreCase))
                    .Select(t => t.Url)
                    .Where(u => !string.IsNullOrWhiteSpace(u)));
        }

        if (!string.IsNullOrWhiteSpace(config.ManualTunerUrls))
        {
            urls.AddRange(
                config.ManualTunerUrls
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<Dictionary<string, string>> DiscoverDeviceAuthKeysAsync(
        IEnumerable<string> tunerUrls,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var client = _httpClientFactory.CreateClient();

        foreach (var rawUrl in tunerUrls)
        {
            var baseUrl = rawUrl.Contains("://", StringComparison.Ordinal) ? rawUrl : $"http://{rawUrl}";
            var discoverUrl = $"{baseUrl.TrimEnd('/')}/discover.json";

            try
            {
                var response = await client.GetAsync(discoverUrl, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Tuner at {Url} returned status {Status}.", baseUrl, response.StatusCode);
                    continue;
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("DeviceAuth", out var authElement) || authElement.GetString() is not { Length: > 0 } deviceAuth)
                {
                    _logger.LogWarning("Tuner at {Url} did not report a DeviceAuth key.", baseUrl);
                    continue;
                }

                var deviceId = root.TryGetProperty("DeviceID", out var idElement) ? idElement.GetString() ?? baseUrl : baseUrl;
                result[deviceId] = deviceAuth;
                _logger.LogDebug("Retrieved DeviceAuth for tuner {DeviceId}.", deviceId);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                _logger.LogWarning(ex, "Failed to query tuner at {Url}.", baseUrl);
            }
        }

        return result;
    }

    private void UpdateListingsProvider(string deviceAuth)
    {
        var liveTvConfig = _configManager.GetConfiguration<LiveTvOptions>(LiveTvConfigKey);
        var newXmlTvUrl = $"{XmlTvBaseUrl}?DeviceAuth={deviceAuth}";

        var existingProviders = liveTvConfig.ListingProviders
            .Where(p => string.Equals(p.Type, "xmltv", StringComparison.OrdinalIgnoreCase)
                        && p.Path.Contains("api.hdhomerun.com", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var changed = false;

        if (existingProviders.Count == 0)
        {
            liveTvConfig.ListingProviders = liveTvConfig.ListingProviders
                .Append(new ListingsProviderInfo
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Type = "xmltv",
                    Path = newXmlTvUrl,
                    EnableAllTuners = true
                })
                .ToArray();
            changed = true;
            _logger.LogInformation("Added a new HDHomeRun XMLTV listings provider.");
        }
        else
        {
            foreach (var provider in existingProviders)
            {
                if (!string.Equals(provider.Path, newXmlTvUrl, StringComparison.Ordinal))
                {
                    provider.Path = newXmlTvUrl;
                    changed = true;
                }
            }

            if (changed)
            {
                _logger.LogInformation("Updated HDHomeRun XMLTV listings provider guide auth key.");
            }
        }

        if (changed)
        {
            _configManager.SaveConfiguration(LiveTvConfigKey, liveTvConfig);
            QueueGuideRefresh();
        }
        else
        {
            _logger.LogDebug("HDHomeRun guide auth key is already current.");
        }
    }

    private void QueueGuideRefresh()
    {
        var worker = _taskManager.ScheduledTasks
            .FirstOrDefault(w => string.Equals(w.ScheduledTask.Key, RefreshGuideTaskKey, StringComparison.Ordinal));

        if (worker is null)
        {
            _logger.LogWarning("Could not find the Refresh Guide task to queue after updating the guide auth key.");
            return;
        }

        _taskManager.QueueScheduledTask(worker.ScheduledTask, new TaskOptions());
        _logger.LogInformation("Queued the Refresh Guide task to pick up the new guide auth key.");
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(4).Ticks
            }
        };
    }
}
