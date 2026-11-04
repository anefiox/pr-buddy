using System.Net.Http.Headers;
using System.Text.Json;

namespace PrBuddy.Core.Packages;

/// <param name="Exists">False when the registry has no listed package with this id.</param>
public sealed record PackageInfo(string Id, bool Exists, long TotalDownloads = 0, bool Verified = false);

/// <summary>Where PRB202 looks packages up. Only used with --online.</summary>
public interface IPackageRegistry
{
    /// <summary>The package's details, or null if the registry couldn't be reached.</summary>
    PackageInfo? Lookup(string id);
}

/// <summary>
/// nuget.org's search API, found through its service index. Synchronous on purpose: rules are
/// synchronous, and HttpClient.Send avoids blocking on async code.
/// </summary>
public sealed class NuGetOrgRegistry(HttpClient http) : IPackageRegistry
{
    const string ServiceIndex = "https://api.nuget.org/v3/index.json";
    readonly Dictionary<string, PackageInfo?> _cache = new(StringComparer.OrdinalIgnoreCase);
    string? _searchUrl;
    bool _unreachable;

    public PackageInfo? Lookup(string id)
    {
        if (_cache.TryGetValue(id, out var cached)) return cached;
        var info = _unreachable ? null : Query(id);
        _cache[id] = info;
        return info;
    }

    PackageInfo? Query(string id)
    {
        try
        {
            _searchUrl ??= FindSearchService();
            var url = $"{_searchUrl}?q=packageid:{Uri.EscapeDataString(id)}&prerelease=true&semVerLevel=2.0.0&take=1";
            using var json = Get(url);
            var data = json.RootElement.GetProperty("data");
            if (data.GetArrayLength() == 0) return new PackageInfo(id, Exists: false);

            var package = data[0];
            return new PackageInfo(
                package.GetProperty("id").GetString() ?? id,
                Exists: true,
                package.TryGetProperty("totalDownloads", out var downloads) ? downloads.GetInt64() : 0,
                package.TryGetProperty("verified", out var verified) && verified.GetBoolean());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            _unreachable = true; // one failure is enough; don't wait on a timeout for every package
            return null;
        }
    }

    string FindSearchService()
    {
        using var index = Get(ServiceIndex);
        foreach (var resource in index.RootElement.GetProperty("resources").EnumerateArray())
            if (resource.GetProperty("@type").GetString()?.StartsWith("SearchQueryService", StringComparison.Ordinal) == true)
                return resource.GetProperty("@id").GetString()!;
        throw new InvalidOperationException("nuget.org's service index lists no search service.");
    }

    JsonDocument Get(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("PrBuddy", "1.0"));
        using var response = http.Send(request);
        response.EnsureSuccessStatusCode();
        using var stream = response.Content.ReadAsStream();
        return JsonDocument.Parse(stream);
    }
}
