namespace Meterist.Core.Http;

/// <summary>
/// Marks an outgoing HTTP request whose URL carries something that must not reach logs or traces. The member list
/// APIs paginate with a cursor taken from the last member's id, so every page after the first has a member id in its
/// query string. Code that writes the URL out (the HttpClient factory's logging, OpenTelemetry's HTTP instrumentation,
/// whatever is added later) can ask <see cref="IsMarked"/> and leave the request out.
/// <para>
/// A marker on the request, rather than a list of paths kept somewhere else, so the code that builds the URL is the
/// code that says it is sensitive, and a new endpoint cannot be forgotten by editing the wrong file.
/// </para>
/// </summary>
public static class SensitiveRequest
{
    public static readonly HttpRequestOptionsKey<bool> Key = new("Meterist.SensitiveUrl");

    public static void MarkSensitive(this HttpRequestMessage request) => request.Options.Set(Key, true);

    public static bool IsMarked(HttpRequestMessage request) => request.Options.TryGetValue(Key, out var marked) && marked;
}
