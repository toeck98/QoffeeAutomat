using System.Text.Json;
using QoffeeAutomat.Contracts.Link;

namespace QoffeeAutomat.Contracts.Tests;

public class CanonicalJsonTests
{
    private static string Canonical(string json, string? omittedRootMember = null)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return CanonicalJson.Serialize(document.RootElement, omittedRootMember);
    }

    [Fact]
    public void Members_are_sorted_whitespace_is_removed_and_null_members_are_omitted()
    {
        string canonical = Canonical("""{ "b": 1, "a": [ true, null, "x" ], "c": null }""");

        Assert.Equal("""{"a":[true,null,"x"],"b":1}""", canonical);
    }

    [Fact]
    public void Members_are_sorted_in_ordinal_order()
    {
        string canonical = Canonical("""{"b":1,"a":2,"B":3,"_":4,"aa":5}""");

        Assert.Equal("""{"B":3,"_":4,"a":2,"aa":5,"b":1}""", canonical);
    }

    [Fact]
    public void Only_the_root_member_is_omitted()
    {
        string canonical = Canonical("""{"rev":"x","n":{"rev":"y"}}""", "rev");

        Assert.Equal("""{"n":{"rev":"y"}}""", canonical);
    }

    [Fact]
    public void Strings_are_escaped_only_where_json_requires_it()
    {
        // The JSON escapes \" \\ \n \t \u0001 and \u007f ä and a slash.
        string canonical = Canonical("""{"k":"q\" b\\ n\n t\t c\u0001 d\u007f uä s/"}""");

        string expected = "{\"k\":\"q\\\" b\\\\ n\\n t\\t c\\u0001 d" + "\u007f" + " u" + "ä" + " s/\"}";
        Assert.Equal(expected, canonical);
    }

    [Fact]
    public void Numbers_keep_their_written_form()
    {
        string canonical = Canonical("""{"i":12,"d":35.5,"z":0}""");

        Assert.Equal("""{"d":35.5,"i":12,"z":0}""", canonical);
    }

    [Fact]
    public void Utf8_output_is_the_utf8_encoding_of_the_text()
    {
        using JsonDocument document = JsonDocument.Parse("""{"k":"ä"}""");

        byte[] bytes = CanonicalJson.SerializeToUtf8(document.RootElement);

        Assert.Equal(System.Text.Encoding.UTF8.GetBytes("{\"k\":\"ä\"}"), bytes);
    }
}
