using System.Text.Json.Serialization;

namespace Proxyz.Core.Models;

public sealed record Profile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public string Note { get; init; } = "";
    public ProxySettings Settings { get; init; } = new();
    [JsonIgnore] public string Display => Name + " — " + Settings.Summary;
}
