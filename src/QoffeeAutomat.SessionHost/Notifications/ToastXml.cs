using System.Text;
using System.Xml.Linq;

namespace QoffeeAutomat.SessionHost.Notifications;

/// <summary>
/// The text that a button carries and that comes back when it is pressed: <c>qa:a=&lt;action&gt;&amp;t=&lt;tag&gt;</c>, the values
/// percent-encoded. The prefix tells a button of this program from anything else that activates the notification.
/// </summary>
public static class ToastArguments
{
    private const string Prefix = "qa:";

    /// <summary>Writes the arguments of a button.</summary>
    /// <param name="action">The id of the button.</param>
    /// <param name="tag">The tag of the notification, or null.</param>
    /// <returns>The text.</returns>
    public static string Encode(string action, string? tag)
    {
        ArgumentNullException.ThrowIfNull(action);

        string text = Prefix + "a=" + Uri.EscapeDataString(action);
        return string.IsNullOrEmpty(tag) ? text : text + "&t=" + Uri.EscapeDataString(tag);
    }

    /// <summary>Reads the arguments of a button.</summary>
    /// <param name="arguments">What Windows hands back.</param>
    /// <param name="action">The id of the button.</param>
    /// <param name="tag">The tag, or null.</param>
    /// <returns>False when the text is not the arguments of a button of this program.</returns>
    public static bool TryParse(string? arguments, out string action, out string? tag)
    {
        action = string.Empty;
        tag = null;
        if (arguments is null || !arguments.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string? parsedAction = null;
        foreach (string part in arguments[Prefix.Length..].Split('&'))
        {
            int equals = part.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                continue;
            }

            string name = part[..equals];
            string value = Uri.UnescapeDataString(part[(equals + 1)..]);
            if (name == "a")
            {
                parsedAction = value;
            }
            else if (name == "t" && value.Length > 0)
            {
                tag = value;
            }
        }

        if (string.IsNullOrEmpty(parsedAction))
        {
            tag = null;
            return false;
        }

        action = parsedAction;
        return true;
    }
}

/// <summary>Builds the XML of a toast notification (the <c>ToastGeneric</c> template, PLAN.md T702). Pure, so it is tested without Windows.</summary>
public static class ToastXml
{
    /// <summary>Builds the XML.</summary>
    /// <param name="request">The notification.</param>
    /// <returns>The XML text for <c>XmlDocument.LoadXml</c>; every value is escaped by the XML writer.</returns>
    public static string Build(ToastRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var binding = new XElement("binding", new XAttribute("template", "ToastGeneric"));
        if (!string.IsNullOrWhiteSpace(request.Title))
        {
            binding.Add(new XElement("text", Clean(request.Title)));
        }

        binding.Add(new XElement("text", Clean(request.Message)));
        if (request.ImagePath is { Length: > 0 } imagePath && Path.IsPathFullyQualified(imagePath))
        {
            binding.Add(new XElement("image", new XAttribute("src", new Uri(imagePath).AbsoluteUri)));
        }

        var toast = new XElement("toast", new XElement("visual", binding));
        if (request.Buttons.Count > 0)
        {
            var actions = new XElement("actions");
            foreach (ToastButton button in request.Buttons)
            {
                actions.Add(new XElement(
                    "action",
                    new XAttribute("content", Clean(button.Label)),
                    new XAttribute("arguments", ToastArguments.Encode(button.Id, request.Tag)),
                    new XAttribute("activationType", "foreground")));
            }

            toast.Add(actions);
        }

        return toast.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>
    /// Replaces the characters that XML 1.0 cannot carry (control characters other than tab and line breaks, lone surrogates)
    /// with a space: the text comes from a Home Assistant template, and one stray character must not cost the notification.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The text that the XML writer accepts.</returns>
    public static string Clean(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var builder = new StringBuilder(text.Length);
        for (int index = 0; index < text.Length; index++)
        {
            char character = text[index];
            if (char.IsHighSurrogate(character) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                builder.Append(character).Append(text[index + 1]);
                index++;
            }
            else if (character is '\t' or '\n' or '\r' || (character >= ' ' && !char.IsSurrogate(character) && character is not ('￾' or '￿')))
            {
                builder.Append(character);
            }
            else
            {
                builder.Append(' ');
            }
        }

        return builder.ToString();
    }
}
