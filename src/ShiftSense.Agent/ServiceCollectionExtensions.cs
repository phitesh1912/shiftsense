using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShiftSense.Agent.Llm;
using ShiftSense.Agent.Persistence;
using ShiftSense.Agent.Tools;
using ShiftSense.Domain;

namespace ShiftSense.Agent;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers everything below the host: database, clock, caller context, store, tools, chat
    /// client and agent. Both the API and the MCP server call this, which is how they share one
    /// set of tools without duplicating a line.
    /// </summary>
    public static IServiceCollection AddShiftSense(this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        var dbPath = config["SHIFTSENSE_DB"] ?? "shiftsense.db";
        services.AddDbContext<ShiftSenseDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));

        // The demo world is frozen at a Wednesday mid-roster so every conversation sees the same
        // "today". Set SHIFTSENSE_FIXED_NOW=false to use the real clock.
        var fixedNow = config["SHIFTSENSE_FIXED_NOW"];
        services.AddSingleton<IClock>(
            string.Equals(fixedNow, "false", StringComparison.OrdinalIgnoreCase) || fixedNow == "0"
                ? new SystemClock()
                : new FixedClock(SeedData.DemoNow));

        services.AddScoped<CallerAccessor>();
        services.AddScoped<ICallerAccessor>(sp => sp.GetRequiredService<CallerAccessor>());
        services.AddScoped<ShiftSenseStore>();
        services.AddScoped<SchedulingTools>();

        services.AddSingleton(LlmOptions.FromConfiguration(config));
        services.AddSingleton<IChatClient>(sp =>
            ChatClientFactory.Create(sp.GetRequiredService<LlmOptions>(), sp.GetService<ILoggerFactory>()));
        services.AddSingleton<ConversationStore>();
        services.AddScoped<ShiftSenseAgent>();
        return services;
    }

    /// <summary>Create the schema and seed the demo data if the database is empty.</summary>
    public static async Task InitializeShiftSenseAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ShiftSenseStore>().SeedIfEmptyAsync(ct);
    }
}
