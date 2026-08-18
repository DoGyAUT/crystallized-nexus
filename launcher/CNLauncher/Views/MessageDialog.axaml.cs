using Avalonia.Controls;
using Avalonia.Interactivity;

namespace CNLauncher.Views;

public partial class MessageDialog : Window
{
	bool result;

	public MessageDialog()
	{
		InitializeComponent();
	}

	/// <summary>Yes/no prompt. Returns true when the user confirms.</summary>
	public static Task<bool> ConfirmAsync(Window owner, string heading, string message,
		string confirmText = "Continue")
	{
		var dialog = Create(heading, message);
		dialog.ConfirmButton.Content = confirmText;
		return dialog.ShowDialog<bool>(owner);
	}

	/// <summary>Acknowledgement-only prompt.</summary>
	public static async Task NotifyAsync(Window owner, string heading, string message)
	{
		var dialog = Create(heading, message);
		dialog.CancelButton.IsVisible = false;
		dialog.ConfirmButton.Content = "OK";
		await dialog.ShowDialog<bool>(owner);
	}

	static MessageDialog Create(string heading, string message)
	{
		var dialog = new MessageDialog { Title = heading };
		dialog.HeadingText.Text = heading;
		dialog.MessageText.Text = message;
		return dialog;
	}

	void OnConfirm(object? sender, RoutedEventArgs e)
	{
		result = true;
		Close(result);
	}

	void OnCancel(object? sender, RoutedEventArgs e) => Close(result);
}
