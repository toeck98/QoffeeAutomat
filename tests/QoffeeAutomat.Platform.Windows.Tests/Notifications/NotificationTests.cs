using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Xml.Linq;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Contracts.Session;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Platform.Windows.Actions;
using QoffeeAutomat.Platform.Windows.Notifications;
using QoffeeAutomat.Platform.Windows.Sessions;
using QoffeeAutomat.Platform.Windows.Tests.Actions;
using QoffeeAutomat.SessionHost.Actions;
using QoffeeAutomat.SessionHost.Notifications;

namespace QoffeeAutomat.Platform.Windows.Tests.Notifications;

/// <summary>A presenter that records what it was asked to show and lets the test press buttons.</summary>
internal sealed class FakeToastPresenter : IToastPresenter
{
    public List<ToastRequest> Shown { get; } = [];

    /// <summary>When set, <see cref="Show"/> refuses with this text.</summary>
    public string? Problem { get; set; }

    public event EventHandler<ToastActionEventArgs>? ActionInvoked;

    public string? Show(ToastRequest request)
    {
        if (Problem is not null)
        {
            return Problem;
        }

        Shown.Add(request);
        return null;
    }

    public void Press(string action, string? tag = null) => ActionInvoked?.Invoke(this, new ToastActionEventArgs(action, tag));
}

/// <summary>An HTTP handler that answers from a function and never touches the network.</summary>
internal sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _respond = respond;
    }

    public List<Uri> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        return Task.FromResult(_respond(request));
    }
}

/// <summary>
/// Notifications from Home Assistant on the desktop (PLAN.md S11, S16, T702): the XML of a toast, the way a button press travels,
/// the picture that the agent fetches and the SessionHost checks again, and what <c>notify.toast</c> does with its variables.
/// Windows itself (<see cref="WindowsToastPresenter"/>) is a manual check.
/// </summary>
[Trait("Category", "Notifications")]
public sealed class NotificationTests : IDisposable
{
    // The first bytes of a PNG, a JPEG, a GIF and a BMP; the rest is filler. Only the beginning is judged.
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0];
    private static readonly byte[] Gif = [(byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 0, 0];
    private static readonly byte[] Bmp = [(byte)'B', (byte)'M', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static UserBuiltinActions NewProvider(FakeToastPresenter? toasts, ToastImageStore? images = null)
    {
        return new UserBuiltinActions(new FakeDesktopNativeApi(), new FakeAudioEndpoint(), new FakeProcessControl(), toasts, images);
    }

    private static ValueTask<ExecutionResult> Notify(UserBuiltinActions provider, params (string Name, object Value)[] values)
    {
        return provider.ExecuteAsync(UserBuiltinActions.NotifyToast, ActionTestData.Vars(values), CancellationToken.None);
    }

    // ------------------------------------------------------------------
    // The XML of a toast
    // ------------------------------------------------------------------

    [Fact]
    public void A_toast_has_the_texts_and_one_action_per_button()
    {
        var request = new ToastRequest(
            "Doorbell",
            "Somebody is at the door",
            [new ToastButton("open", "Open the door"), new ToastButton("ignore", "Ignore")],
            Tag: "door");

        XElement toast = XDocument.Parse(ToastXml.Build(request)).Root!;

        string[] texts = [.. toast.Descendants("text").Select(text => text.Value)];
        Assert.Equal(["Doorbell", "Somebody is at the door"], texts);
        Assert.Equal("ToastGeneric", toast.Descendants("binding").Single().Attribute("template")!.Value);
        XElement[] actions = [.. toast.Descendants("action")];
        Assert.Equal(["Open the door", "Ignore"], actions.Select(action => action.Attribute("content")!.Value));
        Assert.Equal(["qa:a=open&t=door", "qa:a=ignore&t=door"], actions.Select(action => action.Attribute("arguments")!.Value));
        Assert.All(actions, action => Assert.Equal("foreground", action.Attribute("activationType")!.Value));
    }

    [Fact]
    public void A_toast_without_title_buttons_and_picture_is_just_the_text()
    {
        XElement toast = XDocument.Parse(ToastXml.Build(new ToastRequest(null, "Hello", []))).Root!;

        Assert.Equal(["Hello"], toast.Descendants("text").Select(text => text.Value));
        Assert.Empty(toast.Descendants("actions"));
        Assert.Empty(toast.Descendants("image"));
    }

    [Fact]
    public void A_picture_is_shown_from_its_file()
    {
        string file = Path.Combine(Path.GetTempPath(), "qa-door.png");

        XElement toast = XDocument.Parse(ToastXml.Build(new ToastRequest(null, "Hello", [], ImagePath: file))).Root!;

        string source = toast.Descendants("image").Single().Attribute("src")!.Value;
        Assert.StartsWith("file:///", source, StringComparison.Ordinal);
        Assert.EndsWith("qa-door.png", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Markup_and_control_characters_in_the_texts_cannot_break_the_xml()
    {
        var request = new ToastRequest("<b>Bold</b> & \"quoted\"", "line one\nline two\u0001end", [new ToastButton("go", "Go <now> & \"later\"")]);

        XElement toast = XDocument.Parse(ToastXml.Build(request)).Root!;

        string[] texts = [.. toast.Descendants("text").Select(text => text.Value)];
        Assert.Equal("<b>Bold</b> & \"quoted\"", texts[0]);
        Assert.Equal("line one\nline two end", texts[1]);
        Assert.Equal("Go <now> & \"later\"", toast.Descendants("action").Single().Attribute("content")!.Value);
    }

    [Theory]
    [InlineData("open", null)]
    [InlineData("open", "door")]
    [InlineData("a:b-c.d_e", "tag:1")]
    public void The_arguments_of_a_button_come_back_unchanged(string action, string? tag)
    {
        bool parsed = ToastArguments.TryParse(ToastArguments.Encode(action, tag), out string actualAction, out string? actualTag);

        Assert.True(parsed);
        Assert.Equal(action, actualAction);
        Assert.Equal(tag, actualTag);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("open")]
    [InlineData("qa:")]
    [InlineData("qa:t=door")]
    [InlineData("qa:a=")]
    [InlineData("other:a=open")]
    public void Anything_that_is_not_the_arguments_of_a_button_is_ignored(string? arguments)
    {
        Assert.False(ToastArguments.TryParse(arguments, out string action, out string? tag));
        Assert.Equal(string.Empty, action);
        Assert.Null(tag);
    }

    // ------------------------------------------------------------------
    // The way back: from the SessionHost to the event
    // ------------------------------------------------------------------

    [Fact]
    public void A_report_of_a_session_becomes_the_data_of_the_event()
    {
        SessionEventMessage message = EventMessage("""{"action":"open","tag":"door"}""");

        Assert.True(NotificationActionRelay.TryRead(message, out string action, out string? tag));
        Assert.Equal("open", action);
        Assert.Equal("door", tag);
    }

    [Fact]
    public void The_tag_is_optional()
    {
        Assert.True(NotificationActionRelay.TryRead(EventMessage("""{"action":"open"}"""), out string action, out string? tag));
        Assert.Equal("open", action);
        Assert.Null(tag);

        Assert.True(NotificationActionRelay.TryRead(EventMessage("""{"action":"open","tag":null}"""), out _, out string? nullTag));
        Assert.Null(nullTag);
    }

    [Theory]
    [InlineData("""{"tag":"door"}""")]
    [InlineData("""{"action":""}""")]
    [InlineData("""{"action":5}""")]
    [InlineData("""{"action":"has space"}""")]
    [InlineData("""{"action":"semi;colon"}""")]
    [InlineData("""{"action":"open","tag":"bad tag"}""")]
    [InlineData("""{"action":"open","tag":7}""")]
    [InlineData("[]")]
    public void A_report_with_free_text_or_without_an_action_never_becomes_an_event(string data)
    {
        Assert.False(NotificationActionRelay.TryRead(EventMessage(data), out string action, out _));
        Assert.Equal(string.Empty, action);
    }

    [Fact]
    public void Other_events_of_the_session_are_not_notification_actions()
    {
        using JsonDocument data = JsonDocument.Parse("""{"name":"mute"}""");
        var hotkey = new SessionEventMessage(SessionHotkeyEvents.Pressed, 1, data.RootElement.Clone());

        Assert.False(NotificationActionRelay.TryRead(hotkey, out _, out _));
        Assert.False(NotificationActionRelay.TryRead(new SessionEventMessage(SessionNotificationEvents.Action, 1), out _, out _));
    }

    private static SessionEventMessage EventMessage(string data)
    {
        using JsonDocument document = JsonDocument.Parse(data);
        return new SessionEventMessage(SessionNotificationEvents.Action, 1, document.RootElement.Clone());
    }

    // ------------------------------------------------------------------
    // The picture in the SessionHost
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(".png")]
    [InlineData(".jpg")]
    [InlineData(".gif")]
    [InlineData(".bmp")]
    public void A_picture_is_saved_under_a_name_of_the_store_with_the_extension_of_its_kind(string extension)
    {
        byte[] data = extension switch
        {
            ".png" => Png,
            ".jpg" => Jpeg,
            ".gif" => Gif,
            _ => Bmp,
        };
        var store = new ToastImageStore(_temp.Path, new ManualTime());

        string? problem = store.TrySave(Convert.ToBase64String(data), out string? path);

        Assert.Null(problem);
        Assert.NotNull(path);
        Assert.Equal(extension, Path.GetExtension(path));
        Assert.Equal(_temp.Path, Path.GetDirectoryName(path));
        Assert.Equal(data, File.ReadAllBytes(path));
    }

    [Fact]
    public void Data_that_is_no_picture_is_refused_whatever_it_claims_to_be()
    {
        var store = new ToastImageStore(_temp.Path, new ManualTime());

        string? problem = store.TrySave(Convert.ToBase64String("<html>not a picture</html>"u8.ToArray()), out string? path);

        Assert.NotNull(problem);
        Assert.Null(path);
        Assert.False(Directory.Exists(_temp.Path) && Directory.EnumerateFileSystemEntries(_temp.Path).Any());
    }

    [Theory]
    [InlineData("%%% not base64 %%%")]
    [InlineData("")]
    public void Text_that_is_no_base64_is_refused(string text)
    {
        var store = new ToastImageStore(_temp.Path, new ManualTime());

        Assert.NotNull(store.TrySave(text, out string? path));
        Assert.Null(path);
    }

    [Fact]
    public void A_picture_over_the_limit_is_refused()
    {
        var store = new ToastImageStore(_temp.Path, new ManualTime());
        byte[] big = new byte[NotifyToastSpec.MaxImageBytes + 1];
        Png.CopyTo(big, 0);

        string? problem = store.TrySave(Convert.ToBase64String(big), out string? path);

        Assert.NotNull(problem);
        Assert.Contains("1 MiB", problem, StringComparison.Ordinal);
        Assert.Null(path);
    }

    [Fact]
    public void A_picture_of_exactly_the_limit_is_accepted()
    {
        var store = new ToastImageStore(_temp.Path, new ManualTime());
        byte[] big = new byte[NotifyToastSpec.MaxImageBytes];
        Png.CopyTo(big, 0);

        Assert.Null(store.TrySave(Convert.ToBase64String(big), out string? path));
        Assert.NotNull(path);
    }

    [Fact]
    public void Old_pictures_are_removed_when_a_new_one_is_saved_and_other_files_are_left_alone()
    {
        var time = new ManualTime();
        var store = new ToastImageStore(_temp.Path, time);
        Directory.CreateDirectory(_temp.Path);
        string old = Path.Combine(_temp.Path, "old.png");
        string recent = Path.Combine(_temp.Path, "recent.png");
        string foreign = Path.Combine(_temp.Path, "notes.txt");
        File.WriteAllBytes(old, Png);
        File.WriteAllBytes(recent, Png);
        File.WriteAllText(foreign, "keep me");
        File.SetLastWriteTimeUtc(old, time.Now.UtcDateTime - ToastImageStore.Retention - TimeSpan.FromHours(1));
        File.SetLastWriteTimeUtc(recent, time.Now.UtcDateTime - TimeSpan.FromHours(1));
        File.SetLastWriteTimeUtc(foreign, time.Now.UtcDateTime - TimeSpan.FromDays(30));

        Assert.Null(store.TrySave(Convert.ToBase64String(Png), out string? path));

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(foreign));
        Assert.True(File.Exists(path));
    }

    // ------------------------------------------------------------------
    // The picture in the agent
    // ------------------------------------------------------------------

    private static HttpResponseMessage Picture(byte[] data, string contentType = "image/png", HttpStatusCode status = HttpStatusCode.OK)
    {
        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new HttpResponseMessage(status) { Content = content };
    }

    [Fact]
    public async Task A_picture_that_passes_every_limit_is_fetched()
    {
        var handler = new StubHttpHandler(_ => Picture(Png));
        using var fetcher = new NotificationImageFetcher(handler);

        NotificationImage image = await fetcher.FetchAsync("http://homeassistant.local:8123/local/door.png", CancellationToken.None);

        Assert.Equal(Png, image.Data);
        Assert.Null(image.Failure);
        Assert.Equal(new Uri("http://homeassistant.local:8123/local/door.png"), Assert.Single(handler.Requests));
    }

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("IMAGE/PNG")]
    public async Task The_accepted_content_types_are_the_picture_types_in_any_case(string contentType)
    {
        using var fetcher = new NotificationImageFetcher(new StubHttpHandler(_ => Picture(contentType == "image/jpeg" ? Jpeg : Png, contentType)));

        NotificationImage image = await fetcher.FetchAsync("https://example.com/x", CancellationToken.None);

        Assert.NotNull(image.Data);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("application/octet-stream")]
    [InlineData("image/svg+xml")]
    [InlineData("image/webp")]
    public async Task Another_content_type_is_refused(string contentType)
    {
        using var fetcher = new NotificationImageFetcher(new StubHttpHandler(_ => Picture(Png, contentType)));

        NotificationImage image = await fetcher.FetchAsync("https://example.com/x", CancellationToken.None);

        Assert.Null(image.Data);
        Assert.Contains("PNG, JPEG, GIF or BMP", image.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Data_that_is_no_picture_is_refused_although_the_server_calls_it_one()
    {
        using var fetcher = new NotificationImageFetcher(new StubHttpHandler(_ => Picture("<html></html>"u8.ToArray())));

        NotificationImage image = await fetcher.FetchAsync("https://example.com/x.png", CancellationToken.None);

        Assert.Null(image.Data);
        Assert.NotNull(image.Failure);
    }

    [Fact]
    public async Task A_picture_that_is_too_large_is_refused_while_it_is_read()
    {
        byte[] big = new byte[NotifyToastSpec.MaxImageBytes + 1];
        Png.CopyTo(big, 0);
        using var fetcher = new NotificationImageFetcher(new StubHttpHandler(_ => Picture(big)));

        NotificationImage image = await fetcher.FetchAsync("https://example.com/big.png", CancellationToken.None);

        Assert.Null(image.Data);
        Assert.Contains("1 MiB", image.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_picture_of_exactly_the_limit_is_fetched()
    {
        byte[] big = new byte[NotifyToastSpec.MaxImageBytes];
        Png.CopyTo(big, 0);
        using var fetcher = new NotificationImageFetcher(new StubHttpHandler(_ => Picture(big)));

        NotificationImage image = await fetcher.FetchAsync("https://example.com/big.png", CancellationToken.None);

        Assert.Equal(NotifyToastSpec.MaxImageBytes, image.Data?.Length);
    }

    [Fact]
    public async Task An_error_status_is_a_failure_with_the_status()
    {
        using var fetcher = new NotificationImageFetcher(new StubHttpHandler(_ => Picture(Png, status: HttpStatusCode.NotFound)));

        NotificationImage image = await fetcher.FetchAsync("https://example.com/missing.png", CancellationToken.None);

        Assert.Null(image.Data);
        Assert.Contains("404", image.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_network_problem_is_a_failure_and_never_an_exception()
    {
        using var fetcher = new NotificationImageFetcher(new StubHttpHandler(_ => throw new HttpRequestException("No route to host.")));

        NotificationImage image = await fetcher.FetchAsync("https://example.com/x.png", CancellationToken.None);

        Assert.Null(image.Data);
        Assert.Contains("No route to host", image.Failure, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ftp://example.com/x.png")]
    [InlineData("file:///C:/Windows/Web/Wallpaper/img0.jpg")]
    [InlineData("\\\\server\\share\\x.png")]
    [InlineData("/local/door.png")]
    [InlineData("door.png")]
    [InlineData("http://user:secret@example.com/x.png")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://[fe80::1]/x.png")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_address_that_may_not_be_fetched_is_refused_without_a_request(string url)
    {
        var handler = new StubHttpHandler(_ => Picture(Png));
        using var fetcher = new NotificationImageFetcher(handler);

        NotificationImage image = await fetcher.FetchAsync(url, CancellationToken.None);

        Assert.Null(image.Data);
        Assert.NotNull(image.Failure);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void An_address_that_is_too_long_is_refused()
    {
        string url = "https://example.com/" + new string('a', NotifyToastSpec.MaxUrlLength);

        Assert.NotNull(NotificationImageFetcher.CheckUrl(url, out Uri? uri));
        Assert.Null(uri);
    }

    [Theory]
    [InlineData("http://homeassistant.local:8123/api/camera_proxy/camera.door?token=abc")]
    [InlineData("https://example.com/x.png")]
    [InlineData("http://192.168.1.10:8123/local/x.png")]
    [InlineData("http://localhost:8123/local/x.png")]
    public void The_addresses_of_the_local_network_are_fine(string url)
    {
        Assert.Null(NotificationImageFetcher.CheckUrl(url, out Uri? uri));
        Assert.NotNull(uri);
    }

    [Fact]
    public async Task The_agent_hands_the_picture_to_the_session_and_not_the_address()
    {
        var handler = new StubHttpHandler(_ => Picture(Png));
        using var fetcher = new NotificationImageFetcher(handler);

        (IReadOnlyDictionary<string, object> prepared, string? skipped) = await NotifyToastVariables.PrepareAsync(
            ActionTestData.Vars(
                (NotifyToastSpec.Message, "Hello"),
                (NotifyToastSpec.ImageUrl, "https://example.com/door.png")),
            fetcher,
            CancellationToken.None);

        Assert.Null(skipped);
        Assert.False(prepared.ContainsKey(NotifyToastSpec.ImageUrl));
        Assert.Equal(Convert.ToBase64String(Png), prepared[NotifyToastSpec.ImageData]);
        Assert.Equal("Hello", prepared[NotifyToastSpec.Message]);
    }

    [Fact]
    public async Task A_picture_that_cannot_be_fetched_is_left_out_and_the_notification_goes_on()
    {
        using var fetcher = new NotificationImageFetcher(new StubHttpHandler(_ => Picture(Png, status: HttpStatusCode.Forbidden)));

        (IReadOnlyDictionary<string, object> prepared, string? skipped) = await NotifyToastVariables.PrepareAsync(
            ActionTestData.Vars(
                (NotifyToastSpec.Message, "Hello"),
                (NotifyToastSpec.ImageUrl, "https://example.com/door.png")),
            fetcher,
            CancellationToken.None);

        Assert.NotNull(skipped);
        Assert.False(prepared.ContainsKey(NotifyToastSpec.ImageData));
        Assert.False(prepared.ContainsKey(NotifyToastSpec.ImageUrl));
        Assert.Equal("Hello", prepared[NotifyToastSpec.Message]);
    }

    [Fact]
    public async Task A_caller_cannot_hand_the_session_a_picture_of_its_own()
    {
        var handler = new StubHttpHandler(_ => Picture(Png));
        using var fetcher = new NotificationImageFetcher(handler);

        (IReadOnlyDictionary<string, object> prepared, _) = await NotifyToastVariables.PrepareAsync(
            ActionTestData.Vars(
                (NotifyToastSpec.Message, "Hello"),
                (NotifyToastSpec.ImageData, Convert.ToBase64String(Jpeg))),
            fetcher,
            CancellationToken.None);

        Assert.False(prepared.ContainsKey(NotifyToastSpec.ImageData));
        Assert.Empty(handler.Requests);
    }

    // ------------------------------------------------------------------
    // notify.toast in the SessionHost
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_notification_with_a_message_only_is_shown()
    {
        var toasts = new FakeToastPresenter();

        ExecutionResult result = await Notify(NewProvider(toasts), (NotifyToastSpec.Message, "Hello"));

        Assert.True(result.Succeeded);
        ToastRequest shown = Assert.Single(toasts.Shown);
        Assert.Equal("Hello", shown.Message);
        Assert.Null(shown.Title);
        Assert.Null(shown.Tag);
        Assert.Null(shown.ImagePath);
        Assert.Empty(shown.Buttons);
    }

    [Fact]
    public async Task Title_tag_and_up_to_three_buttons_are_passed_on()
    {
        var toasts = new FakeToastPresenter();

        ExecutionResult result = await Notify(
            NewProvider(toasts),
            (NotifyToastSpec.Title, "Doorbell"),
            (NotifyToastSpec.Message, "Somebody is at the door"),
            (NotifyToastSpec.Tag, "door"),
            ("Action1Id", "open"),
            ("Action1Title", "Open"),
            ("Action3Id", "ignore"),
            ("Action3Title", "Ignore"));

        Assert.True(result.Succeeded);
        ToastRequest shown = Assert.Single(toasts.Shown);
        Assert.Equal("Doorbell", shown.Title);
        Assert.Equal("door", shown.Tag);
        Assert.Equal([new ToastButton("open", "Open"), new ToastButton("ignore", "Ignore")], shown.Buttons);
    }

    [Fact]
    public async Task All_three_buttons_fit()
    {
        var toasts = new FakeToastPresenter();

        await Notify(
            NewProvider(toasts),
            (NotifyToastSpec.Message, "Pick one"),
            ("Action1Id", "a"),
            ("Action1Title", "A"),
            ("Action2Id", "b"),
            ("Action2Title", "B"),
            ("Action3Id", "c"),
            ("Action3Title", "C"));

        Assert.Equal(["a", "b", "c"], Assert.Single(toasts.Shown).Buttons.Select(button => button.Id));
    }

    public static TheoryData<string, string, string> InvalidInputs()
    {
        // The variable to set, its value, and what the message of the refusal talks about.
        return new TheoryData<string, string, string>
        {
            { NotifyToastSpec.Message, "", "Message" },
            { NotifyToastSpec.Message, "   ", "Message" },
            { NotifyToastSpec.Message, new string('m', NotifyToastSpec.MaxMessageLength + 1), "Message" },
            { NotifyToastSpec.Title, new string('t', NotifyToastSpec.MaxTitleLength + 1), "Title" },
            { NotifyToastSpec.Tag, "has space", "Tag" },
            { NotifyToastSpec.Tag, new string('x', NotifyToastSpec.MaxIdLength + 1), "Tag" },
            { "Action1Id", "bad id", "Button 1" },
            { "Action1Id", "x;y", "Button 1" },
            { "Action1Title", new string('l', NotifyToastSpec.MaxButtonTitleLength + 1), "Button 1" },
        };
    }

    [Theory]
    [MemberData(nameof(InvalidInputs))]
    public async Task A_value_that_breaks_a_limit_is_refused_and_nothing_is_shown(string name, string value, string talksAbout)
    {
        var toasts = new FakeToastPresenter();
        var values = new List<(string Name, object Value)>
        {
            (NotifyToastSpec.Message, "Hello"),
            ("Action1Id", "open"),
            ("Action1Title", "Open"),
        };
        values.RemoveAll(entry => entry.Name == name);
        values.Add((name, value));

        ExecutionResult result = await Notify(NewProvider(toasts), [.. values]);

        Assert.Equal(ErrorCodes.InvalidVars, result.ErrorCode);
        Assert.Contains(talksAbout, result.StandardError, StringComparison.Ordinal);
        Assert.Empty(toasts.Shown);
    }

    [Fact]
    public async Task A_message_is_required()
    {
        var toasts = new FakeToastPresenter();

        ExecutionResult result = await Notify(NewProvider(toasts));

        Assert.Equal(ErrorCodes.InvalidVars, result.ErrorCode);
        Assert.Empty(toasts.Shown);
    }

    [Fact]
    public async Task A_button_needs_an_id_and_a_label()
    {
        var toasts = new FakeToastPresenter();

        ExecutionResult idOnly = await Notify(NewProvider(toasts), (NotifyToastSpec.Message, "Hello"), ("Action2Id", "open"));
        ExecutionResult labelOnly = await Notify(NewProvider(toasts), (NotifyToastSpec.Message, "Hello"), ("Action2Title", "Open"));

        Assert.Equal(ErrorCodes.InvalidVars, idOnly.ErrorCode);
        Assert.Contains("Button 2", idOnly.StandardError, StringComparison.Ordinal);
        Assert.Equal(ErrorCodes.InvalidVars, labelOnly.ErrorCode);
        Assert.Empty(toasts.Shown);
    }

    [Fact]
    public async Task The_ids_of_the_buttons_must_differ()
    {
        var toasts = new FakeToastPresenter();

        ExecutionResult result = await Notify(
            NewProvider(toasts),
            (NotifyToastSpec.Message, "Hello"),
            ("Action1Id", "same"),
            ("Action1Title", "One"),
            ("Action2Id", "same"),
            ("Action2Title", "Two"));

        Assert.Equal(ErrorCodes.InvalidVars, result.ErrorCode);
        Assert.Empty(toasts.Shown);
    }

    [Fact]
    public async Task A_refusal_of_windows_is_reported_and_is_not_an_invalid_input()
    {
        var toasts = new FakeToastPresenter { Problem = "Windows does not show notifications from QoffeeAutomat (DisabledForUser)." };

        ExecutionResult result = await Notify(NewProvider(toasts), (NotifyToastSpec.Message, "Hello"));

        Assert.False(result.Succeeded);
        Assert.Null(result.ErrorCode);
        Assert.Contains("DisabledForUser", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_presenter_the_action_says_so()
    {
        ExecutionResult result = await Notify(NewProvider(null), (NotifyToastSpec.Message, "Hello"));

        Assert.False(result.Succeeded);
        Assert.Contains("not available", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_picture_from_the_agent_is_saved_and_shown_with_the_notification()
    {
        var toasts = new FakeToastPresenter();
        var store = new ToastImageStore(_temp.Path, new ManualTime());

        ExecutionResult result = await Notify(
            NewProvider(toasts, store),
            (NotifyToastSpec.Message, "Hello"),
            (NotifyToastSpec.ImageData, Convert.ToBase64String(Png)));

        Assert.True(result.Succeeded);
        string path = Assert.Single(toasts.Shown).ImagePath!;
        Assert.Equal(Png, File.ReadAllBytes(path));
        Assert.StartsWith(_temp.Path, path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_picture_that_is_not_one_is_left_out_and_the_notification_is_still_shown()
    {
        var toasts = new FakeToastPresenter();
        var store = new ToastImageStore(_temp.Path, new ManualTime());

        ExecutionResult result = await Notify(
            NewProvider(toasts, store),
            (NotifyToastSpec.Message, "Hello"),
            (NotifyToastSpec.ImageData, Convert.ToBase64String("not a picture"u8.ToArray())));

        Assert.True(result.Succeeded);
        Assert.Null(Assert.Single(toasts.Shown).ImagePath);
        Assert.Contains("left out", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_address_that_arrives_in_the_session_is_ignored_because_only_the_agent_fetches()
    {
        var toasts = new FakeToastPresenter();

        ExecutionResult result = await Notify(
            NewProvider(toasts, new ToastImageStore(_temp.Path, new ManualTime())),
            (NotifyToastSpec.Message, "Hello"),
            (NotifyToastSpec.ImageUrl, "http://example.com/x.png"));

        Assert.True(result.Succeeded);
        Assert.Null(Assert.Single(toasts.Shown).ImagePath);
    }

    // ------------------------------------------------------------------
    // The definitions
    // ------------------------------------------------------------------

    [Fact]
    public void The_parameters_follow_the_plan_up_to_three_buttons_with_an_id_and_a_label_each()
    {
        string[] names = [.. NotifyToastSpec.Parameters.Select(parameter => parameter.Name)];

        Assert.Equal(
            ["Title", "Message", "ImageUrl", "Tag", "Action1Id", "Action1Title", "Action2Id", "Action2Title", "Action3Id", "Action3Title"],
            names);
        Assert.Equal(["Message"], NotifyToastSpec.Parameters.Where(parameter => parameter.Required).Select(parameter => parameter.Name));
        Assert.DoesNotContain(NotifyToastSpec.ImageData, names);
        Assert.Equal(NotifyToastSpec.MaxButtons, names.Count(name => name.EndsWith("Id", StringComparison.Ordinal) && name.StartsWith("Action", StringComparison.Ordinal)));
    }

    [Fact]
    public void The_names_of_the_actions_of_the_catalog_are_those_of_the_specifications()
    {
        Assert.Equal(NotifyToastSpec.ActionName, WindowsActionNames.NotifyToast);
        Assert.Equal(MediaControlSpec.ActionName, WindowsActionNames.MediaControl);
        Assert.Equal(NotifyToastSpec.ActionName, UserBuiltinActions.NotifyToast);
        Assert.Equal(MediaControlSpec.ActionName, UserBuiltinActions.MediaControl);
    }

    [Theory]
    [InlineData("open", true)]
    [InlineData("a:b-c.d_e", true)]
    [InlineData("ABC123", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("has space", false)]
    [InlineData("semi;colon", false)]
    [InlineData("ümlaut", false)]
    [InlineData("a&b", false)]
    public void An_id_is_made_of_letters_digits_and_a_few_signs(string? id, bool valid)
    {
        Assert.Equal(valid, NotifyToastSpec.IsValidId(id));
    }

    [Fact]
    public void An_id_may_have_64_characters_and_not_more()
    {
        Assert.True(NotifyToastSpec.IsValidId(new string('a', 64)));
        Assert.False(NotifyToastSpec.IsValidId(new string('a', 65)));
    }
}
