using System.Threading;
using System.Threading.Tasks;

namespace QoffeAutomatService.Services
{
    public interface IServiceModule
    {
        Task HandleAsync(string entity, string state, CancellationToken ct);
    }
}
