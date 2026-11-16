namespace QoffeeAutomat.Ui.Tests.Api;

[Trait("Category", "Ui")]
public sealed class ServerSentEventReaderTests
{
    [Fact]
    public async Task Events_have_a_name_and_data_and_end_with_a_blank_line()
    {
        List<ServerSentEvent> events = await ReadAsync("event: snapshot\ndata: {\"a\":1}\n\nevent: update\ndata: {\"a\":2}\n\n");

        Assert.Equal([new ServerSentEvent("snapshot", "{\"a\":1}"), new ServerSentEvent("update", "{\"a\":2}")], events);
    }

    [Fact]
    public async Task An_event_without_a_name_is_a_message_and_data_lines_are_joined_with_line_feeds()
    {
        List<ServerSentEvent> events = await ReadAsync("data: one\ndata: two\n\n");

        Assert.Equal([new ServerSentEvent("message", "one\ntwo")], events);
    }

    [Fact]
    public async Task Comments_unknown_fields_and_empty_events_are_ignored()
    {
        List<ServerSentEvent> events = await ReadAsync(": keep-alive\n\nid: 7\nretry: 100\n\nevent: only-a-name\n\ndata: x\n\n");

        Assert.Equal([new ServerSentEvent("message", "x")], events);
    }

    [Fact]
    public async Task Windows_line_ends_work_and_one_space_after_the_colon_is_removed()
    {
        List<ServerSentEvent> events = await ReadAsync("event:update\r\ndata:  two spaces\r\n\r\n");

        Assert.Equal([new ServerSentEvent("update", " two spaces")], events);
    }

    [Fact]
    public async Task An_event_that_the_stream_did_not_finish_is_dropped()
    {
        List<ServerSentEvent> events = await ReadAsync("event: update\ndata: done\n\nevent: update\ndata: cut off");

        Assert.Equal([new ServerSentEvent("update", "done")], events);
    }

    private static async Task<List<ServerSentEvent>> ReadAsync(string text)
    {
        using var reader = new StringReader(text);
        var events = new List<ServerSentEvent>();
        await foreach (ServerSentEvent item in ServerSentEventReader.ReadAsync(reader))
        {
            events.Add(item);
        }

        return events;
    }
}
