using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace CNLauncher.Services;

public sealed record DownloadProgress(long BytesRead, long TotalBytes);

/// <summary>
/// Resumable, retrying file download with checksum verification. Testers pull ~200 MB over
/// connections that drop, so a broken transfer resumes instead of starting over.
/// </summary>
public static class Downloader
{
	const int MaxAttempts = 4;

	public static async Task DownloadAsync(
		HttpClient http,
		ReleaseAsset asset,
		string destinationPath,
		IProgress<DownloadProgress> progress,
		CancellationToken ct)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

		Exception? lastError = null;
		for (var attempt = 1; attempt <= MaxAttempts; attempt++)
		{
			ct.ThrowIfCancellationRequested();

			try
			{
				await DownloadAttemptAsync(http, asset, destinationPath, progress, ct);

				if (asset.Sha256 != null)
				{
					var actual = await ComputeSha256Async(destinationPath, ct);
					if (!string.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase))
					{
						// A corrupt file can never be repaired by resuming, so the partial
						// download is discarded before the next attempt.
						File.Delete(destinationPath);
						throw new IOException("The downloaded file failed its checksum check.");
					}
				}

				return;
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception e) when (attempt < MaxAttempts)
			{
				lastError = e;
				await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
			}
		}

		throw new IOException($"Download failed after {MaxAttempts} attempts.", lastError);
	}

	static async Task DownloadAttemptAsync(
		HttpClient http,
		ReleaseAsset asset,
		string destinationPath,
		IProgress<DownloadProgress> progress,
		CancellationToken ct)
	{
		var existingBytes = File.Exists(destinationPath) ? new FileInfo(destinationPath).Length : 0;

		// More bytes on disk than the asset has means the partial file belongs to some
		// other download; start clean rather than producing a corrupt mix.
		if (existingBytes > 0 && existingBytes >= asset.Size)
		{
			File.Delete(destinationPath);
			existingBytes = 0;
		}

		using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl);
		request.Headers.Accept.Clear();
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
		if (existingBytes > 0)
			request.Headers.Range = new RangeHeaderValue(existingBytes, null);

		using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

		// A server that ignores the range header answers 200 with the whole file, in which
		// case the bytes already on disk have to be thrown away.
		var resuming = response.StatusCode == HttpStatusCode.PartialContent;
		if (existingBytes > 0 && !resuming)
			existingBytes = 0;

		response.EnsureSuccessStatusCode();

		var totalBytes = asset.Size > 0
			? asset.Size
			: existingBytes + (response.Content.Headers.ContentLength ?? 0);

		await using var destination = new FileStream(
			destinationPath,
			resuming ? FileMode.Append : FileMode.Create,
			FileAccess.Write,
			FileShare.None);

		await using var source = await response.Content.ReadAsStreamAsync(ct);

		var buffer = new byte[128 * 1024];
		var bytesRead = existingBytes;
		var lastReport = DateTime.MinValue;
		int read;
		while ((read = await source.ReadAsync(buffer, ct)) > 0)
		{
			await destination.WriteAsync(buffer.AsMemory(0, read), ct);
			bytesRead += read;

			// Throttled so the UI thread is not flooded with marshalled updates.
			var now = DateTime.UtcNow;
			if (now - lastReport > TimeSpan.FromMilliseconds(100))
			{
				lastReport = now;
				progress.Report(new DownloadProgress(bytesRead, totalBytes));
			}
		}

		progress.Report(new DownloadProgress(bytesRead, totalBytes));
	}

	static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
	{
		await using var stream = File.OpenRead(path);
		var hash = await SHA256.HashDataAsync(stream, ct);
		return Convert.ToHexString(hash);
	}
}
