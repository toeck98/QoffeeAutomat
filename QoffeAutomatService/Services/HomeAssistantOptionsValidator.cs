using System;
using Microsoft.Extensions.Options;

namespace QoffeAutomatService.Services
{
    public class HomeAssistantOptionsValidator : IValidateOptions<HomeAssistantOptions>
    {
        public ValidateOptionsResult Validate(string? name, HomeAssistantOptions options)
        {
            if (options == null)
                return ValidateOptionsResult.Fail("HomeAssistantOptions is null.");

            if (string.IsNullOrWhiteSpace(options.EntityToWatch))
                return ValidateOptionsResult.Fail("EntityToWatch must be configured in homeassistant.conf under [homeassistant].");

            if (string.IsNullOrWhiteSpace(options.Url))
                return ValidateOptionsResult.Fail("Url must be configured in homeassistant.conf.");

            if (string.IsNullOrWhiteSpace(options.Token))
                return ValidateOptionsResult.Fail("Token must be configured in homeassistant.conf.");

            return ValidateOptionsResult.Success;
        }
    }
}
