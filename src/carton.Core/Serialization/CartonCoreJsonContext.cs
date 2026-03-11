using System.Text.Json.Serialization;
using carton.Core.Models;

namespace carton.Core.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    GenerationMode = JsonSourceGenerationMode.Metadata | JsonSourceGenerationMode.Serialization)]
[JsonSerializable(typeof(AppPreferences))]
[JsonSerializable(typeof(AppTheme))]
[JsonSerializable(typeof(Profile))]
[JsonSerializable(typeof(ProfileRuntimeOptions))]
[JsonSerializable(typeof(SingBoxData))]
[JsonSerializable(typeof(SingBoxConfig))]
[JsonSerializable(typeof(OutboundSelectionRequest))]
[JsonSerializable(typeof(WindowsHelperStartRequest))]
[JsonSerializable(typeof(WindowsHelperActionResponse))]
internal partial class CartonCoreJsonContext : JsonSerializerContext;
