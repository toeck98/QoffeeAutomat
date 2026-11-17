namespace QoffeeAutomat.Ui.Components.Pages.Modes;

/// <summary>Tuning of the mode pages (T717). The defaults are right for users; tests shorten the interval.</summary>
public sealed class ModeOptions
{
    /// <summary>How often the state of a group is read while a switch of it is running.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
}
