using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace HDHomeRunAuthPlugin;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "HDHomeRun Guide Auth Sync";

    public override Guid Id => Guid.Parse("2e08612f-3c3f-449b-a672-9ccb56495f12");

    public override string Description =>
        "Keeps the HDHomeRun XMLTV guide DeviceAuth key in sync with Jellyfin's Live TV listings provider.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = "HDHomeRunAuthPlugin",
            EmbeddedResourcePath = string.Format("{0}.configPage.html", GetType().Namespace)
        };
    }
}
