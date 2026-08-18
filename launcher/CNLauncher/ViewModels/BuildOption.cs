namespace CNLauncher.ViewModels;

/// <summary>
/// An entry in the build picker. A null tag means "follow whatever is newest in the
/// channel"; any other value pins that exact release.
/// </summary>
public sealed record BuildOption(string Label, string? Tag);
