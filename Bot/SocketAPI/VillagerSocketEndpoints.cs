using System;
using System.Text.Json;
using System.Linq;
using NHSE.Villagers;
using NHSE.Core;

namespace SysBot.ACNHOrders
{
    [SocketAPIController]
    public static class VillagerSocketEndpoints
    {
        [SocketAPIEndpoint]
        public static object InjectVillager(string args)
        {
            // Runtime check for Raymond resource
            bool raymondKnown = NHSE.Villagers.VillagerResources.IsVillagerDataKnown("Raymond");
            Console.WriteLine($"[SocketAPI][DEBUG] Raymond resource found: {raymondKnown}");

            try
            {
                // Parse the JSON arguments
                using var doc = JsonDocument.Parse(args);
                var root = doc.RootElement;

                if (!root.TryGetProperty("house", out var houseProp) || !root.TryGetProperty("villager", out var villagerProp))
                    return new { error = "Missing 'house' or 'villager' in arguments." };

                byte house = houseProp.GetByte();
                string villagerName = villagerProp.GetString();

                if (string.IsNullOrWhiteSpace(villagerName))
                    return new { error = "Villager name cannot be empty." };

                // Use the same mapping logic as Discord
                string internalName = villagerName;
                if (!VillagerResources.IsVillagerDataKnown(internalName))
                    internalName = GameInfo.Strings.VillagerMap
                        .FirstOrDefault(z => string.Equals(z.Value, internalName, StringComparison.InvariantCultureIgnoreCase)).Key;

                if (string.IsNullOrEmpty(internalName))
                    return new { error = $"Villager '{villagerName}' is not a valid internal villager name." };

                // Log the parsed arguments and mapping
                Console.WriteLine($"[SocketAPI] InjectVillager: house={house}, villager={villagerName}, internalName={internalName}");

                // Get the villager object using the internal name
                var villager = VillagerResources.GetVillager(internalName);
                if (villager == null)
                    return new { error = $"Villager '{villagerName}' (internal: '{internalName}') not found." };
                if (villager.Villager == null)
                    return new { error = $"Villager data for '{villagerName}' (internal: '{internalName}') is incomplete (Villager property is null)." };

                // Enqueue the request
                Globals.Bot.VillagerInjections.Enqueue(
                    new VillagerRequest(
                        "INJECT",
                        villager,
                        house,
                        villagerName
                    )
                );

                return new { status = "okay", message = $"Enqueued villager '{villagerName}' (internal: '{internalName}') to house {house}." };
            }
            catch (Exception ex)
            {
                return new { error = ex.Message };
            }
        }
    }
}
