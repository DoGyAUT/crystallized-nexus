using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CNLauncher.Services;

namespace CNLauncher.Views;

public partial class InstallPathDialog : Window
{
	const string FolderName = "CrystallizedNexus";

	public InstallPathDialog()
	{
		InitializeComponent();
		PathBox.TextChanged += (_, _) => Validate();
	}

	/// <summary>Returns the chosen install directory, or null if the user cancelled.</summary>
	public static Task<string?> ShowAsync(Window owner, string suggestedPath)
	{
		var dialog = new InstallPathDialog();
		dialog.PathBox.Text = suggestedPath;
		dialog.Validate();
		return dialog.ShowDialog<string?>(owner);
	}

	async void OnBrowse(object? sender, RoutedEventArgs e)
	{
		var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
		{
			Title = "Select the folder to install into",
			AllowMultiple = false,
		});

		var picked = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
		if (string.IsNullOrEmpty(picked))
			return;

		// Folder pickers select a parent ("Games"), so the game gets its own subfolder
		// rather than scattering hundreds of files into whatever was chosen. The result is
		// written back into the text box so the user sees exactly what will be used.
		PathBox.Text = Path.GetFileName(picked.TrimEnd(Path.DirectorySeparatorChar)) == FolderName
			? picked
			: Path.Combine(picked, FolderName);
	}

	void Validate()
	{
		var path = PathBox.Text?.Trim() ?? "";
		var (warning, canConfirm) = Inspect(path);

		WarningText.Text = warning;
		WarningText.IsVisible = warning.Length > 0;
		ConfirmButton.IsEnabled = canConfirm;
	}

	static (string Warning, bool CanConfirm) Inspect(string path)
	{
		if (path.Length == 0)
			return ("Enter a folder path.", false);

		string full;
		try
		{
			full = Path.GetFullPath(path);
		}
		catch (Exception)
		{
			return ("That is not a valid folder path.", false);
		}

		if (Path.GetPathRoot(full) == full)
			return ("Pick a subfolder rather than the root of a drive - updates delete this folder's contents.", false);

		if (!Directory.Exists(full))
			return ("", true);

		// An existing install is exactly what we want to find; any other non-empty folder
		// is a trap, because installing replaces the directory wholesale.
		if (GameInstall.IsInstalled(full))
			return ("An existing installation was found here and will be used.", true);

		if (Directory.EnumerateFileSystemEntries(full).Any())
			return ("This folder is not empty. Everything in it will be deleted when the game is installed.", true);

		return ("", true);
	}

	void OnConfirm(object? sender, RoutedEventArgs e)
	{
		var path = PathBox.Text?.Trim();
		Close(string.IsNullOrEmpty(path) ? null : Path.GetFullPath(path));
	}

	void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
