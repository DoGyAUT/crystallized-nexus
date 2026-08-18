using Avalonia.Threading;
using CNLauncher.Services;

namespace CNLauncher.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
	readonly HttpClient http = GitHub.CreateClient();
	readonly LauncherConfig config = LauncherConfig.Load();

	IReadOnlyList<Release> releases = [];
	InstallCandidate? candidate;
	LauncherUpdate? launcherUpdate;

	public MainViewModel()
	{
		PrimaryCommand = new RelayCommand(PrimaryActionAsync, () => !IsBusy && (candidate != null || IsInstalled));
		RepairCommand = new RelayCommand(RepairAsync, () => !IsBusy && candidate != null);
		UninstallCommand = new RelayCommand(UninstallAsync, () => !IsBusy && IsInstalled);
		OpenLogsCommand = new RelayCommand(() => Shell.OpenFolder(GameInstall.LogsDir(InstallDir)));
		CollectLogsCommand = new RelayCommand(CollectLogsAsync, () => !IsBusy);
		OpenInstallDirCommand = new RelayCommand(() => Shell.OpenFolder(InstallDir));
		ChangeInstallDirCommand = new RelayCommand(ChangeInstallDirAsync, () => !IsBusy);
		DiscordCommand = new RelayCommand(() => Shell.OpenUrl(Shell.DiscordInvite));
		ReleasesPageCommand = new RelayCommand(() => Shell.OpenUrl(GitHub.ReleasesPage));
		RefreshCommand = new RelayCommand(RefreshAsync, () => !IsBusy);
	}

	/// <summary>Set by the view so the view model can ask questions without knowing about windows.</summary>
	public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }
	public Func<string, string, Task>? NotifyAsync { get; set; }
	public Func<string, Task<string?>>? PickFolderAsync { get; set; }

	public RelayCommand PrimaryCommand { get; }
	public RelayCommand RepairCommand { get; }
	public RelayCommand UninstallCommand { get; }
	public RelayCommand OpenLogsCommand { get; }
	public RelayCommand CollectLogsCommand { get; }
	public RelayCommand OpenInstallDirCommand { get; }
	public RelayCommand ChangeInstallDirCommand { get; }
	public RelayCommand DiscordCommand { get; }
	public RelayCommand ReleasesPageCommand { get; }
	public RelayCommand RefreshCommand { get; }

	public string LauncherVersionText => $"Launcher {AppVersion.Current}";

	string InstallDir => config.InstallDir ?? LauncherConfig.DefaultInstallDir;
	bool IsInstalled => GameInstall.IsInstalled(InstallDir);

	ReleaseChannel SelectedChannel => config.Channel;

	/// <summary>
	/// Bound to the channel combo box. An index keeps the view free of value converters;
	/// the order matches the ComboBoxItems declared in MainWindow.axaml.
	/// </summary>
	public int SelectedChannelIndex
	{
		get => config.Channel == ReleaseChannel.Stable ? 0 : 1;
		set
		{
			var channel = value == 0 ? ReleaseChannel.Stable : ReleaseChannel.Playtest;
			if (config.Channel == channel)
				return;

			config.Channel = channel;
			config.Save();
			RaisePropertyChanged();
			_ = RefreshAsync();
		}
	}

	string installedVersion = "";
	public string InstalledVersion { get => installedVersion; private set => SetField(ref installedVersion, value); }

	string availableVersion = "";
	public string AvailableVersion { get => availableVersion; private set => SetField(ref availableVersion, value); }

	bool updateAvailable;
	public bool UpdateAvailable { get => updateAvailable; private set => SetField(ref updateAvailable, value); }

	string status = "Checking for updates...";
	public string Status { get => status; private set => SetField(ref status, value); }

	string primaryText = "PLEASE WAIT";
	public string PrimaryText { get => primaryText; private set => SetField(ref primaryText, value); }

	string releaseNotes = "";
	public string ReleaseNotes { get => releaseNotes; private set => SetField(ref releaseNotes, value); }

	string contentStatus = "";
	public string ContentStatus { get => contentStatus; private set => SetField(ref contentStatus, value); }

	bool contentMissing;
	public bool ContentMissing { get => contentMissing; private set => SetField(ref contentMissing, value); }

	string installPathText = "";
	public string InstallPathText { get => installPathText; private set => SetField(ref installPathText, value); }

	bool isBusy;
	public bool IsBusy
	{
		get => isBusy;
		private set
		{
			if (!SetField(ref isBusy, value))
				return;

			foreach (var command in new[]
			{
				PrimaryCommand, RepairCommand, UninstallCommand, CollectLogsCommand,
				ChangeInstallDirCommand, RefreshCommand,
			})
				command.RaiseCanExecuteChanged();
		}
	}

	bool showProgress;
	public bool ShowProgress { get => showProgress; private set => SetField(ref showProgress, value); }

	double progressValue;
	public double ProgressValue { get => progressValue; private set => SetField(ref progressValue, value); }

	bool progressIndeterminate;
	public bool ProgressIndeterminate { get => progressIndeterminate; private set => SetField(ref progressIndeterminate, value); }

	string progressText = "";
	public string ProgressText { get => progressText; private set => SetField(ref progressText, value); }

	/// <summary>Runs once at startup: settles on an install directory, then does the update check.</summary>
	public async Task InitialiseAsync()
	{
		SelfUpdater.CleanUpAfterUpdate();

		if (config.IsFirstRun)
		{
			var chosen = PickFolderAsync == null ? null : await PickFolderAsync(LauncherConfig.DefaultInstallDir);
			config.InstallDir = string.IsNullOrWhiteSpace(chosen) ? LauncherConfig.DefaultInstallDir : chosen;
			config.Save();
		}

		await RefreshAsync();
	}

	public async Task RefreshAsync()
	{
		IsBusy = true;
		Status = "Checking for updates...";

		var reachedGitHub = true;
		try
		{
			releases = await GitHub.FetchReleasesAsync(http, CancellationToken.None);
			launcherUpdate = SelfUpdater.FindUpdate(releases);
		}
		catch (Exception e)
		{
			releases = [];
			launcherUpdate = null;
			reachedGitHub = false;
			Status = $"Could not reach GitHub ({e.Message}).";
		}

		candidate = GameUpdater.SelectCandidate(releases, SelectedChannel);
		UpdateState(reachedGitHub);
		IsBusy = false;

		if (launcherUpdate != null)
			await OfferLauncherUpdateAsync();
	}

	void UpdateState(bool reachedGitHub = true)
	{
		var installed = GameInstall.ReadInstalledVersion(InstallDir);
		InstalledVersion = installed ?? "not installed";
		InstallPathText = InstallDir;

		AvailableVersion = candidate?.Release.TagName ?? "unavailable";
		ReleaseNotes = candidate?.Notes() ?? "";

		var hasContent = GameInstall.HasTiberianSunContent(InstallDir);
		ContentMissing = !hasContent;
		ContentStatus = hasContent
			? "Tiberian Sun assets found."
			: "Tiberian Sun assets not found - the game downloads them on first start.";

		UpdateAvailable = candidate != null && installed != candidate.Release.TagName;

		if (candidate == null)
		{
			PrimaryText = installed != null ? "PLAY" : "UNAVAILABLE";

			// Leave the connection error in place rather than overwriting it with a
			// channel message that would be misleading when nothing was fetched at all.
			if (reachedGitHub)
				Status = $"No {SelectedChannel.DisplayName()} build has a package for this platform yet.";
		}
		else if (installed == null)
		{
			PrimaryText = "INSTALL";
			Status = $"Ready to install {candidate.Release.TagName} ({Format(candidate.Asset.Size)}).";
		}
		else if (UpdateAvailable)
		{
			PrimaryText = "UPDATE";
			Status = $"Update available: {installed} -> {candidate.Release.TagName}.";
		}
		else
		{
			PrimaryText = "PLAY";
			Status = "Up to date.";
		}

		PrimaryCommand.RaiseCanExecuteChanged();
		RepairCommand.RaiseCanExecuteChanged();
		UninstallCommand.RaiseCanExecuteChanged();
	}

	async Task PrimaryActionAsync()
	{
		if (UpdateAvailable || !IsInstalled)
		{
			if (candidate == null || !await InstallAsync(candidate))
				return;
		}

		LaunchGame();
	}

	async Task RepairAsync()
	{
		if (candidate == null)
			return;

		var confirmed = ConfirmAsync == null || await ConfirmAsync(
			"Reinstall",
			$"This deletes {InstallDir} and downloads {candidate.Release.TagName} again " +
			$"({Format(candidate.Asset.Size)}).\n\n" +
			"Settings, replays and maps are stored elsewhere and are not affected.");

		if (confirmed)
			await InstallAsync(candidate);
	}

	async Task UninstallAsync()
	{
		var confirmed = ConfirmAsync == null || await ConfirmAsync(
			"Uninstall",
			$"This deletes {InstallDir}.\n\n" +
			"Settings, replays and maps are stored elsewhere and are not affected.");

		if (!confirmed)
			return;

		try
		{
			GameInstall.Uninstall(InstallDir);
			Status = "Uninstalled.";
			UpdateState();
		}
		catch (Exception e)
		{
			UpdateState();
			Status = $"Uninstall failed: {e.Message}";
		}
	}

	async Task<bool> InstallAsync(InstallCandidate target)
	{
		if (GameInstall.IsGameRunning())
		{
			if (NotifyAsync != null)
				await NotifyAsync("Game is running", "Close Crystallized Nexus before updating, then try again.");

			return false;
		}

		IsBusy = true;
		ShowProgress = true;
		ProgressIndeterminate = false;
		ProgressValue = 0;

		try
		{
			var progress = new Progress<DownloadProgress>(p =>
			{
				ProgressValue = p.TotalBytes > 0 ? 100.0 * p.BytesRead / p.TotalBytes : 0;
				ProgressText = $"{Format(p.BytesRead)} / {Format(p.TotalBytes)}";
			});

			await GameUpdater.InstallAsync(http, target, InstallDir, progress,
				text => Dispatcher.UIThread.Post(() =>
				{
					Status = text;

					// Extraction gives no byte-level feedback, so the bar switches to a
					// marquee instead of sitting frozen at 100%.
					ProgressIndeterminate = text.StartsWith("Installing", StringComparison.Ordinal);
				}),
				CancellationToken.None);

			ShowProgress = false;
			UpdateState();
			Status = $"Installed {target.Release.TagName}.";
			return true;
		}
		catch (Exception e)
		{
			ShowProgress = false;
			UpdateState();
			Status = $"Installation failed: {e.Message}";
			if (NotifyAsync != null)
				await NotifyAsync("Installation failed", e.Message);

			return false;
		}
		finally
		{
			ShowProgress = false;
			ProgressIndeterminate = false;
			ProgressText = "";
			IsBusy = false;
		}
	}

	void LaunchGame()
	{
		try
		{
			GameInstall.Launch(InstallDir);
			Status = "Game started.";
		}
		catch (Exception e)
		{
			Status = $"Could not start the game: {e.Message}";
		}
	}

	async Task CollectLogsAsync()
	{
		try
		{
			var zipPath = DiagnosticsBundle.Create(InstallDir);
			Status = $"Log bundle written to {zipPath}.";
			Shell.OpenFolder(Path.GetDirectoryName(zipPath)!);
		}
		catch (Exception e)
		{
			if (NotifyAsync != null)
				await NotifyAsync("No logs to collect", e.Message);
		}
	}

	async Task ChangeInstallDirAsync()
	{
		if (PickFolderAsync == null)
			return;

		var chosen = await PickFolderAsync(InstallDir);
		if (string.IsNullOrWhiteSpace(chosen))
			return;

		config.InstallDir = chosen;
		config.Save();
		UpdateState();
	}

	async Task OfferLauncherUpdateAsync()
	{
		if (launcherUpdate == null)
			return;

		var confirmed = ConfirmAsync == null || await ConfirmAsync(
			"Launcher update",
			$"A newer launcher is available ({AppVersion.Current} -> {launcherUpdate.Version}).\n\n" +
			"Update and restart now?");

		if (!confirmed)
			return;

		IsBusy = true;
		ShowProgress = true;
		ProgressIndeterminate = false;
		Status = "Updating the launcher...";

		try
		{
			var progress = new Progress<DownloadProgress>(p =>
			{
				ProgressValue = p.TotalBytes > 0 ? 100.0 * p.BytesRead / p.TotalBytes : 0;
				ProgressText = $"{Format(p.BytesRead)} / {Format(p.TotalBytes)}";
			});

			if (await SelfUpdater.ApplyAsync(http, launcherUpdate, progress, CancellationToken.None))
			{
				(Avalonia.Application.Current?.ApplicationLifetime
					as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();
				return;
			}

			Status = "The launcher could not be replaced; continuing with this version.";
		}
		catch (Exception e)
		{
			Status = $"Launcher update failed: {e.Message}";
		}
		finally
		{
			// Cleared regardless, so a declined or failed self-update does not leave the
			// window stuck behind a progress bar.
			ShowProgress = false;
			ProgressText = "";
			IsBusy = false;
			launcherUpdate = null;
		}
	}

	static string Format(long bytes)
		=> bytes >= 1024L * 1024 * 1024
			? $"{bytes / 1024.0 / 1024 / 1024:0.00} GB"
			: $"{bytes / 1024.0 / 1024:0.0} MB";
}

static class ReleaseNotesExtensions
{
	/// <summary>
	/// Release bodies are markdown; the launcher shows them as plain text, so the heading
	/// markers that would otherwise appear as literal hashes are stripped.
	/// </summary>
	public static string Notes(this InstallCandidate candidate)
	{
		var body = candidate.Release.Notes;
		if (string.IsNullOrWhiteSpace(body))
			return "No release notes for this build.";

		var lines = body.Replace("\r\n", "\n").Split('\n')
			.Select(l => l.TrimEnd())
			.Select(l => l.StartsWith('#') ? l.TrimStart('#', ' ') : l);

		return string.Join('\n', lines).Trim();
	}
}
