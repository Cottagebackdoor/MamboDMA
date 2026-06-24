#nullable enable

using System.Collections.Generic;

namespace MamboDMA.Games.Common
{
    public interface IWebRadarFrameSource
    {
        string BuildFrameJson();
        IReadOnlyList<WebRadarMapInfo> ListMaps();
        string? GetMapSidecarJson(string key);
        string? GetMapImagePath(string filename);
        bool TrySaveMapSidecar(string key, string json);
        string GameKey { get; }
    }

    public sealed record WebRadarMapInfo(string Name, string File, string Url);
}
