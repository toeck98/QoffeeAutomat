namespace QoffeAutomatService.Services
{
    // Minimal parts of Home Assistant state entity used by the service
    public class HomeAssistantState
    {
        public string? Entity_Id { get; set; }
        public string? State { get; set; }
        public object? Attributes { get; set; }
        public long? Last_Changed { get; set; }
    }
}
