using MediaBrowser.Model.Plugins;

namespace HDHomeRunAuthPlugin;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a comma-separated list of tuner base URLs (e.g. "http://192.168.1.137")
    /// to check in addition to (or instead of, if UseConfiguredTunerHosts is false) the
    /// HDHomeRun tuner hosts already configured in Jellyfin's Live TV settings.
    /// </summary>
    public string ManualTunerUrls { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the task should also check tuner hosts
    /// already configured under Live TV &gt; Tuner Devices (type "hdhomerun").
    /// </summary>
    public bool UseConfiguredTunerHosts { get; set; } = true;
}
