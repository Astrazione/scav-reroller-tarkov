using System.Text.Json;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Generators.Bot;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Servers;

namespace ScavReroller.Server;

/// <summary>SPT 4.1 mod manifest discovered by the server mod loader.</summary>
public sealed record ScavRerollerMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "local.scav.loadout.reroller";
    public string Name { get; init; } = "Scav Loadout Reroller";
    public string Author { get; init; } = "Community";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("0.1.3");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.0");
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; }
    public string License { get; init; } = "MIT";
    public bool HasPrepatcher { get; init; }
}

/// <summary>
/// Use the normal SPT request pipeline. The session ID comes from the PHPSESSID
/// cookie (not from the request body) so Fika clients affect only their own profile.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Routers)]
public sealed class ScavRerollerRouter : StaticRouter
{
    public ScavRerollerRouter(JsonUtil jsonUtil, ScavRerollerHandler handler)
        : base(jsonUtil,
        [
            new RouteAction<EmptyRequestData>(
                "/scavreroller/reroll",
                async (url, info, sessionId, output, cancellationToken) =>
                    await handler.GenerateAsync(sessionId, cancellationToken))
        ])
    {
    }
}

/// <summary>Server-side reroll with a single serialization gate for generator caches.</summary>
[Injectable]
public sealed class ScavRerollerHandler(PlayerScavGenerator generator, SaveServer saveServer)
{
    private static readonly SemaphoreSlim GenerateGate = new(1, 1);

    public async ValueTask<string> GenerateAsync(MongoId sessionId, CancellationToken cancellationToken)
    {
        // Do not accept a profile identifier from the request. We only trust
        // the session ID that SPT extracted from the normal request cookie.
        if (string.IsNullOrEmpty(sessionId.ToString()) ||
            sessionId.ToString() == "000000000000000000000000")
        {
            return Reply(false, "Missing SPT session cookie (PHPSESSID).");
        }

        await GenerateGate.WaitAsync(cancellationToken);
        try
        {
            var profile = saveServer.GetProfile(sessionId);
            if (profile?.CharacterData?.PmcData is null ||
                profile.CharacterData.ScavData is null)
            {
                return Reply(false, "Player profile or player scav does not exist.");
            }

            // PlayerScavGenerator.Generate() deliberately preserves scav XP,
            // skills, quests and statistics, but resets the scav cooldown.
            // For a preview reroll, we restore the previous cooldown.
            var priorCooldown = profile.CharacterData.ScavData.Info?.SavageLockTime;

            var newScav = generator.Generate(sessionId);
            if (newScav.Info is not null)
            {
                newScav.Info.SavageLockTime = priorCooldown;
            }

            // Generate() assigns the new ScavData to the server's profile.
            // No file is edited directly; SPT owns profile persistence.
            return Reply(true, "New scav generated. Reload the client profile to see and use it.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            // Do not return internal server exception details to clients.
            Console.Error.WriteLine($"[ScavReroller] Reroll error: {e}");
            return Reply(false, "Generation failed. See the SPT server log.");
        }
        finally
        {
            GenerateGate.Release();
        }
    }

    private static string Reply(bool ok, string message) =>
        JsonSerializer.Serialize(new { ok, message });
}
