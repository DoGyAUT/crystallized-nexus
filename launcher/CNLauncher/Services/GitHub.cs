using System.Net.Http.Headers;
using System.Text.Json;

namespace CNLauncher.Services;

public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size, string? Sha256);

public sealed record Release(
	string TagName,
	string Title,
	string Notes,
	DateTimeOffset PublishedAt,
	IReadOnlyList<ReleaseAsset> Assets);

/// <summary>Read-only access to the mod repository's GitHub releases.</summary>
public static class GitHub
{
	public const string RepoOwner = "DoGyAUT";
	public const string RepoName = "crystallized-nexus";

	public static string ReleasesPage => $"https://github.com/{RepoOwner}/{RepoName}/releases";

	public static HttpClient CreateClient()
	{
		// The repository is public, so no authentication is needed. A descriptive user
		// agent is still required - GitHub rejects API requests that omit one.
		var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
		http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CNLauncher", AppVersion.Current));
		http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
		return http;
	}

	public static async Task<IReadOnlyList<Release>> FetchReleasesAsync(HttpClient http, CancellationToken ct)
	{
		// One page covers far more history than the launcher ever needs to walk back
		// through when looking for a release that has a package for this platform.
		using var response = await http.GetAsync(
			$"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases?per_page=50", ct);
		response.EnsureSuccessStatusCode();

		using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));

		var releases = new List<Release>();
		foreach (var element in doc.RootElement.EnumerateArray())
		{
			if (element.TryGetProperty("draft", out var draft) && draft.GetBoolean())
				continue;

			var assets = new List<ReleaseAsset>();
			foreach (var asset in element.GetProperty("assets").EnumerateArray())
			{
				// "digest" is a newer API field of the form "sha256:<hex>"; releases cut
				// before it existed simply omit it and skip checksum verification.
				string? sha256 = null;
				if (asset.TryGetProperty("digest", out var digest) && digest.ValueKind == JsonValueKind.String)
				{
					var value = digest.GetString();
					if (value != null && value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
						sha256 = value["sha256:".Length..];
				}

				assets.Add(new ReleaseAsset(
					asset.GetProperty("name").GetString()!,
					asset.GetProperty("browser_download_url").GetString()!,
					asset.GetProperty("size").GetInt64(),
					sha256));
			}

			var tag = element.GetProperty("tag_name").GetString()!;
			releases.Add(new Release(
				tag,
				element.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
					? name.GetString()! : tag,
				element.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String
					? body.GetString()! : "",
				element.TryGetProperty("published_at", out var published) && published.ValueKind == JsonValueKind.String
					? published.GetDateTimeOffset() : DateTimeOffset.MinValue,
				assets));
		}

		return releases;
	}
}
