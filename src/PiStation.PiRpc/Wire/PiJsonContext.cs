using System.Text.Json.Serialization;
using PiStation.PiRpc.Wire.Responses;

namespace PiStation.PiRpc.Wire;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(PiRpcResponse))]
internal sealed partial class PiJsonContext : JsonSerializerContext;
