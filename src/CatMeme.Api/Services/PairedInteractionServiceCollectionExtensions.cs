using Microsoft.Extensions.DependencyInjection;

namespace CatMeme.Api.Services;

public static class PairedInteractionServiceCollectionExtensions
{
    /// <summary>Registers the whole-scene interaction selector for payoff-beat casting.</summary>
    public static IServiceCollection AddPairedInteractionAssets(this IServiceCollection services)
    {
        services.AddScoped<IPairedInteractionAssetSelector, PairedInteractionAssetService>();
        return services;
    }
}
