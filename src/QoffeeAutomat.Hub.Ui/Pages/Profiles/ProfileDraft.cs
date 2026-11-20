using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using QoffeeAutomat.Hub.Config;
using QoffeeAutomat.Hub.Ui.Pages.Library;

namespace QoffeeAutomat.Hub.Ui.Pages.Profiles;

/// <summary>One variable of a profile as it is edited: a name and a value.</summary>
public sealed class VariableRow
{
    /// <summary>The name; becomes <c>QA_HUBVAR_&lt;NAME&gt;</c> on the agent.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The value; never spliced into script text (D-012).</summary>
    public string Value { get; set; } = string.Empty;
}

/// <summary>One document of a profile as it is edited: a library document, pinned at a revision or following the newest.</summary>
/// <param name="Kind">One of the document kinds.</param>
/// <param name="Id">The library id.</param>
/// <param name="Revision">The pinned revision; <c>null</c> pins the current revision when the draft is saved.</param>
public sealed record ProfileItemRow(string Kind, string Id, int? Revision);

/// <summary>
/// The draft of a profile on the editing page (PLAN.md S20, T916): the mutable form of <see cref="ProfileContent"/>. It turns into the request that
/// replaces the draft at the hub, reports what the page can see is wrong before anything is sent (the hub validates again), and says whether it
/// differs from what was loaded, so the page can ask the user to save before a publish.
/// </summary>
public sealed partial class ProfileDraft
{
    /// <summary>The value of "do not set": the policy of the profile does not say anything and the policy of other profiles, or the default, applies.</summary>
    public const string Inherit = "inherit";

    private static readonly Regex VariableNamePattern = VariableName();

    /// <summary>The os filters: none, <c>windows</c> and <c>linux</c>.</summary>
    public static IReadOnlyList<string> OsFilters { get; } = ["", "windows", "linux"];

    /// <summary>The values of the policy for local configuration.</summary>
    public static IReadOnlyList<string> LocalConfigChoices { get; } = [Inherit, "allow", "locked"];

    /// <summary>The values of the policy for the direct link to Home Assistant.</summary>
    public static IReadOnlyList<string> DirectLinkChoices { get; } = [Inherit, "allow", "deny"];

    /// <summary>The id of the profile.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The priority; the higher number wins between equally specific targets.</summary>
    public int Priority { get; set; }

    /// <summary>The OS filter: empty for every agent, else <c>windows</c> or <c>linux</c>.</summary>
    public string OsFilter { get; set; } = string.Empty;

    /// <summary>The policy for local configuration: <see cref="Inherit"/>, <c>allow</c> or <c>locked</c>.</summary>
    public string LocalConfig { get; set; } = Inherit;

    /// <summary>The policy for the direct link: <see cref="Inherit"/>, <c>allow</c> or <c>deny</c>.</summary>
    public string DirectLink { get; set; } = Inherit;

    /// <summary>The documents.</summary>
    public List<ProfileItemRow> Items { get; } = [];

    /// <summary>The variables.</summary>
    public List<VariableRow> Variables { get; } = [];

    /// <summary>Makes the draft from the content that the hub returned.</summary>
    /// <param name="id">The id of the profile.</param>
    /// <param name="content">The draft or a published revision.</param>
    /// <returns>The draft.</returns>
    public static ProfileDraft From(string id, ProfileContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var draft = new ProfileDraft
        {
            Id = id,
            Name = content.Name,
            Priority = content.Priority,
            OsFilter = content.OsFilter ?? string.Empty,
            LocalConfig = content.Policy.LocalConfig,
            DirectLink = content.Policy.DirectLink,
        };
        draft.Items.AddRange(content.Items.Select(item => new ProfileItemRow(item.Kind, item.Id, item.Revision)));
        draft.Variables.AddRange(content.Variables
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new VariableRow { Name = pair.Key, Value = pair.Value }));
        return draft;
    }

    /// <summary>Tells whether a document is in the profile already.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="id">The library id.</param>
    /// <returns>True when the profile contains it.</returns>
    public bool Contains(string kind, string id) => Items.Any(item => item.Kind == kind && item.Id == id);

    /// <summary>The problems that the page can see itself.</summary>
    /// <returns>A key of the hub texts (<c>Profile.Problem.*</c>) per problem; empty when the draft can be sent.</returns>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(Name))
        {
            problems.Add("Profile.Problem.Name");
        }

        if (Priority is < -10_000 or > 10_000)
        {
            problems.Add("Profile.Problem.Priority");
        }

        if (Variables.Any(row => !VariableNamePattern.IsMatch(row.Name)))
        {
            problems.Add("Profile.Problem.VariableName");
        }

        if (Variables.GroupBy(row => row.Name, StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            problems.Add("Profile.Problem.VariableDuplicate");
        }

        if (Items.Count(item => item.Kind == QoffeeAutomat.Contracts.Hub.DocumentKinds.MetricOverrides) > 1)
        {
            problems.Add("Profile.Problem.MetricOverrides");
        }

        return problems;
    }

    /// <summary>The request that replaces the draft at the hub.</summary>
    /// <returns>The request, without an id (the id is in the address).</returns>
    public ProfileDraftRequest ToRequest()
    {
        return new ProfileDraftRequest(
            null,
            Name.Trim(),
            Priority,
            OsFilter.Length == 0 ? null : OsFilter,
            [.. Items.Select(item => new ProfileItemRef(item.Kind, item.Id, item.Revision))],
            Variables.ToDictionary(row => row.Name, row => row.Value, StringComparer.Ordinal),
            new ProfilePolicyRequest(LocalConfig, DirectLink));
    }

    /// <summary>The request that creates a profile with only a name; everything else starts at its default.</summary>
    /// <param name="id">The id of the new profile.</param>
    /// <param name="name">The display name.</param>
    /// <returns>The request.</returns>
    public static ProfileDraftRequest ToCreateRequest(string id, string name)
    {
        return new ProfileDraftRequest(id, name.Trim(), 0, null, [], new Dictionary<string, string>(), new ProfilePolicyRequest(Inherit, Inherit));
    }

    /// <summary>A text that is equal for equal drafts; compared with the text of the loaded draft it tells whether there are unsaved changes.</summary>
    /// <returns>The text.</returns>
    public string Snapshot()
    {
        var builder = new StringBuilder();
        builder.Append(Name).Append('\u001f')
            .Append(Priority.ToString(CultureInfo.InvariantCulture)).Append('\u001f')
            .Append(OsFilter).Append('\u001f')
            .Append(LocalConfig).Append('\u001f')
            .Append(DirectLink).Append('\u001e');
        foreach (ProfileItemRow item in Items)
        {
            builder.Append(item.Kind).Append(':').Append(item.Id).Append('@').Append(item.Revision?.ToString(CultureInfo.InvariantCulture)).Append('\u001f');
        }

        builder.Append('\u001e');
        foreach (VariableRow row in Variables)
        {
            builder.Append(row.Name).Append('=').Append(row.Value).Append('\u001f');
        }

        return builder.ToString();
    }

    /// <summary>Describes a policy value for a person: its text key in the hub resources.</summary>
    /// <param name="value">A value of <see cref="LocalConfigChoices"/> or <see cref="DirectLinkChoices"/>.</param>
    /// <returns>The key, <c>Policy.Value.&lt;value&gt;</c>.</returns>
    public static string PolicyKey(string value) => "Policy.Value." + value;

    /// <summary>The kinds in the order of <see cref="LibraryKinds"/>, used to sort the documents of a profile.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The position.</returns>
    public static int KindOrder(string kind)
    {
        for (int index = 0; index < LibraryKinds.All.Count; index++)
        {
            if (LibraryKinds.All[index] == kind)
            {
                return index;
            }
        }

        return LibraryKinds.All.Count;
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex VariableName();
}
