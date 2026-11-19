using System.Text.Json.Serialization;

namespace VpnControl.Models;

/// Uniform HTTP envelope every endpoint returns (success or error), same
/// shape as MinimalTemplate's ApiResponse.
public class ApiResponse<T>
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("statusCode")]
    public int StatusCode { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("data")]
    public T? Data { get; set; }

    [JsonPropertyName("errors")]
    public IEnumerable<string>? Errors { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; set; }
}
