using IcpaaS.Core.Telephony;
using Microsoft.Extensions.DependencyInjection;

namespace IcpaaS.Telephony;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddIcpaaSTelephony(this IServiceCollection services)
    {
        services.AddSingleton<SimulatorEngine>();
        services.AddSingleton<FreeSwitchEslConnection>();
        services.AddHostedService(sp => sp.GetRequiredService<FreeSwitchEslConnection>());
        services.AddSingleton<FreeSwitchEngine>();
        services.AddSingleton<AsteriskEngine>();
        services.AddSingleton<GenericSipEngine>();
        services.AddSingleton<ITelephonyEngine>(sp => sp.GetRequiredService<SimulatorEngine>());
        services.AddSingleton<ITelephonyEngine>(sp => sp.GetRequiredService<FreeSwitchEngine>());
        services.AddSingleton<ITelephonyEngine>(sp => sp.GetRequiredService<AsteriskEngine>());
        services.AddSingleton<ITelephonyEngine>(sp => sp.GetRequiredService<GenericSipEngine>());
        services.AddSingleton<TelephonyRouter>();
        return services;
    }
}
