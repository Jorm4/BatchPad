using System.Text.Json;
using System.Text.Json.Serialization;

namespace BatchPad.Core.Model;

public abstract class ExtensibleObject
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
