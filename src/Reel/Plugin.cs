using System;
using System.Collections.Generic;
using System.IO;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Reel.Configuration;

namespace Reel;

/// <summary>
/// Reel plugin identity, dashboard pages and thumbnail.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage
{
    public static Plugin Instance { get; private set; }

    public override string Name => "Reel";

    public override string Description =>
        "Finds and downloads music videos for songs in your music library, so Emby can show them on artist pages and play them natively.";

    public override Guid Id => new Guid("B910B3A1-A0EE-4C87-9695-AFD021DE678E");

    public ImageFormat ThumbImageFormat => ImageFormat.Jpg;

    public Plugin(IApplicationPaths appPaths, IXmlSerializer xmlSerializer)
        : base(appPaths, xmlSerializer)
    {
        Instance = this;
    }

    public Stream GetThumbImage()
    {
        var type = GetType();
        return type.Assembly.GetManifestResourceStream(type.Namespace + ".thumb.jpg");
    }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = "Reel",
            EmbeddedResourcePath = GetType().Namespace + ".Web.configPage.html",
            EnableInMainMenu = false
        };
        yield return new PluginPageInfo
        {
            Name = "reeljs",
            EmbeddedResourcePath = GetType().Namespace + ".Web.configPage.js"
        };
    }
}
