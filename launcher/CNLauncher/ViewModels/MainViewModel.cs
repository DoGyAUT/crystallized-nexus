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

	// Set while the build list is being rebuilt, so writing SelectedBuildIndex back to the
	// combo box does not look like the user picking a different build.
	bool rebuildingBuildList;

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

	IReadOnlyList<BuildOption> builds = [];
	public IReadOnlyList<BuildOption> Builds { get => builds; private set => SetField(ref builds, value); }

	int selectedBuildIndex;
	public int SelectedBuildIndex
	{
		get => selectedBuildIndex;
		set
		{
			if (!SetField(ref selectedBuildIndex, value) || rebuildingBuildList)
				return;

			// Index 0 is "Latest", which means follow the channel rather than pin anything.
			config.PinnedRelease = value > 0 && value < Builds.Count ? Builds[value].Tag : null;
			config.Save();
			ResolveCandidate();
			UpdateState();
		}
	}

	string installedVersion = "";
	public string InstalledVersion { get => installedVersion; private set => SetField(ref installedVersion, value); }

	bool updateAvailable;
	public bool UpdateAvailable { get => updateAvailable; private set => SetField(ref updateAvailable, value); }

	string status = "Checking for updates...";
	public string Status { get => status; private set => SetField(ref status, value); }

	string primaryText = "PLEASE WAIT";
	public string PrimaryText { get => primaryText; private set => SetField(ref primaryText, value); }

	IReadOnlyList<ReleaseNote> releaseNotes = [];
	public IReadOnlyList<ReleaseNote> ReleaseNotes { get => releaseNotes; private set => SetField(ref releaseNotes, value); }

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

		RebuildBuildList();
		ResolveCandidate();
		UpdateState(reachedGitHub);
		IsBusy = false;

		if (launcherUpdate != null)
			await OfferLauncherUpdateAsync();
	}

	/// <summary>
	/// Rebuilds the pickable builds. Only releases with a package for this platform are
	/// listed - offering a build that cannot be installed here would be a dead end.
	/// </summary>
	void RebuildBuildList()
	{
		var installable = GameUpdater.SelectAll(releases, SelectedChannel);
		var newest = installable.FirstOrDefault();

		var options = new List<BuildOption>
		{
			new(newest == null ? "Latest" : $"Latest  ({newest.Release.TagName})", null),
		};

		options.AddRange(installable.Select(c => new BuildOption(c.Release.TagName, c.Release.TagName)));

		rebuildingBuildList = true;
		try
		{
			Builds = options;

			var pinned = config.PinnedRelease;
			var index = pinned == null ? 0 : options.FindIndex(o => o.Tag == pinned);

			// A pinned build that has disappeared from the list silently reverts to Latest
			// rather than leaving the launcher pointing at nothing.
			if (index < 0)
			{
				config.PinnedRelease = null;
				config.Save();
				index = 0;
			}

			SelectedBuildIndex = index;
		}
		finally
		{
			rebuildingBuildList = false;
		}
	}

	void ResolveCandidate()
	{
		candidate = config.PinnedRelease != null
			? GameUpdater.SelectSpecific(releases, config.PinnedRelease)
			: GameUpdater.SelectCandidate(releases, SelectedChannel);
	}

	void UpdateState(bool reachedGitHub = true)
	{
		var installed = GameInstall.ReadInstalledVersion(InstallDir);
		InstalledVersion = installed ?? "not installed";
		InstallPathText = InstallDir;

		ReleaseNotes = ReleaseNoteBuilder.Build(releases, SelectedChannel, installed);

		var hasContent = GameInstall.HasTiberianSunContent(InstallDir);
		ContentMissing = !hasContent;
		ContentStatus = hasContent
			? "Tiberian Sun assets found."
			: "Tiberian Sun assets not found - the game downloads them on first start.";

		// GitHub returns releases newest-first, so a higher index is an older build.
		var installedIndex = IndexOf(installed);
		var targetIndex = IndexOf(candidate?.Release.TagName);
		var targetIsOlder = installedIndex >= 0 && targetIndex > installedIndex;

		UpdateAvailable = candidate != null && installed != candidate.Release.TagName && !targetIsOlder;

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
		else if (installed == candidate.Release.TagName)
		{
			PrimaryText = "PLAY";
			Status = config.PinnedRelease == null
				? "Up to date."
				: $"Pinned to {candidate.Release.TagName}. Newer builds will not be installed.";
		}
		else if (targetIsOlder)
		{
			PrimaryText = "SWITCH";
			Status = $"Going back to {candidate.Release.TagName} ({Format(candidate.Asset.Size)}).";
		}
		else
		{
			PrimaryText = "UPDATE";
			Status = $"Update available: {installed} -> {candidate.Release.TagName}.";
		}

		PrimaryCommand.RaiseCanExecuteChanged();
		RepairCommand.RaiseCanExecuteChanged();
		UninstallCommand.RaiseCanExecuteChanged();
	}

	int IndexOf(string? tagName)
	{
		if (tagName == null)
			return -1;

		for (var i = 0; i < releases.Count; i++)
			if (releases[i].TagName == tagName)
				return i;

		return -1;
	}

	async Task PrimaryActionAsync()
	{
		var installed = GameInstall.ReadInstalledVersion(InstallDir);
		if (candidate != null && installed != candidate.Release.TagName)
		{
			if (!await InstallAsync(candidate))
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
