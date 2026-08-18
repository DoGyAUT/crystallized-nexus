using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace CNLauncher.ViewModels;

public abstract class ViewModelBase : INotifyPropertyChanged
{
	public event PropertyChangedEventHandler? PropertyChanged;

	protected void RaisePropertyChanged([CallerMemberName] string? propertyName = null)
		=> PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

	protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
	{
		if (EqualityComparer<T>.Default.Equals(field, value))
			return false;

		field = value;
		RaisePropertyChanged(propertyName);
		return true;
	}
}

/// <summary>
/// Minimal ICommand so the project can stay free of an MVVM framework - fewer dependencies
/// means a smaller single-file binary and no trimming surprises.
/// </summary>
public sealed class RelayCommand : ICommand
{
	readonly Func<Task> execute;
	readonly Func<bool>? canExecute;
	bool running;

	public RelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
	{
		this.execute = execute;
		this.canExecute = canExecute;
	}

	public RelayCommand(Action execute, Func<bool>? canExecute = null)
		: this(() => { execute(); return Task.CompletedTask; }, canExecute)
	{
	}

	public event EventHandler? CanExecuteChanged;

	public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

	public bool CanExecute(object? parameter) => !running && (canExecute?.Invoke() ?? true);

	public async void Execute(object? parameter)
	{
		// Re-entrancy guard: the long-running actions here (download, install) must not be
		// startable twice by an impatient double click.
		running = true;
		RaiseCanExecuteChanged();

		try
		{
			await execute();
		}
		finally
		{
			running = false;
			RaiseCanExecuteChanged();
		}
	}
}
