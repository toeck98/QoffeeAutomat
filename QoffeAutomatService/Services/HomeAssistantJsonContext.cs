using System.Text.Json.Serialization;

namespace QoffeAutomatService.Services
{
    [JsonSerializable(typeof(HomeAssistantState))]
    public partial class HomeAssistantJsonContext : System.Text.Json.Serialization.JsonSerializerContext
    {
    }
}
