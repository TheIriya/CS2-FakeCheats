using CounterStrikeSharp.API.Core;

namespace FakeCheats;

public sealed class FakeCheatsConfig : BasePluginConfig
{
    public string Language { get; set; } = "en";
    public float ReplicateIntervalSeconds { get; set; } = 3;
    public List<string> AdditionalAllowedServerCommands { get; set; } = [];
}
