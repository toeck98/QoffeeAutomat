using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using QoffeeAutomat.Ui.Components.Pages.Dashboard;

namespace QoffeeAutomat.Ui.Tests.Dashboard;

/// <summary>Shows the dashboard while <see cref="Show"/> is true and something else once it is false: the page is left.</summary>
public sealed class DashboardHost : ComponentBase
{
    /// <summary>Whether the dashboard is on the page.</summary>
    [Parameter]
    public bool Show { get; set; } = true;

    /// <summary>Leaves the page: the dashboard is removed from the tree and disposed. Call it inside the renderer's dispatcher.</summary>
    public void Leave()
    {
        Show = false;
        StateHasChanged();
    }

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (Show)
        {
            builder.OpenComponent<DashboardPage>(0);
            builder.CloseComponent();
        }
        else
        {
            builder.AddContent(1, "left");
        }
    }
}
