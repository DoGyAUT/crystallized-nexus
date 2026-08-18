using Avalonia.Controls;
using CNLauncher.ViewModels;

namespace CNLauncher.Views;

public partial class MainWindow : Window
{
	readonly MainViewModel model = new();

	public MainWindow()
	{
		InitializeComponent();

		model.ConfirmAsync = (heading, message) => MessageDialog.ConfirmAsync(this, heading, message);
		model.NotifyAsync = (heading, message) => MessageDialog.NotifyAsync(this, heading, message);
		model.PickFolderAsync = suggested => InstallPathDialog.ShowAsync(this, suggested);

		DataContext = model;

		// The update check needs a shown window to parent its dialogs on, so it starts
		// after the first frame rather than in the constructor.
		Opened += async (_, _) => await model.InitialiseAsync();
	}
}
