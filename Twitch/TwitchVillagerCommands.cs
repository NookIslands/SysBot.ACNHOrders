using SysBot.ACNHOrders; // for Globals
using SysBot.Base;        // for GameInfo
using NHSE.Core;
using NHSE.Villagers;     // for VillagerResources
using System;
using System.Linq;


public static class TwitchVillagerCommands
{
    public static string InjectVillager(string username, int startIndex, string[] villagerNames)
    {
        var bot = Globals.Bot;

        if (!bot.Config.DodoModeConfig.LimitedDodoRestoreOnlyMode)
            return $"@{username} - Villagers cannot be injected in order mode.";

        if (!bot.Config.AllowVillagerInjection)
            return $"@{username} - Villager injection is currently disabled.";

        if (villagerNames.Length < 1)
            return $"@{username} - No villager names provided.";

        int index = startIndex;
        foreach (var nameLookup in villagerNames)
        {
            var internalName = nameLookup;
            var originalName = internalName;

            if (!VillagerResources.IsVillagerDataKnown(internalName))
                internalName = GameInfo.Strings.VillagerMap
                    .FirstOrDefault(z => string.Equals(z.Value, internalName, StringComparison.InvariantCultureIgnoreCase)).Key;

            if (internalName == default)
                return $"@{username} - {originalName} is not a valid internal villager name.";

            if (index < 0 || index > byte.MaxValue)
                return $"@{username} - {index} is not a valid villager index.";

            var villagerData = VillagerResources.GetVillager(internalName);
            var extraMsg = VillagerOrderParser.IsUnadoptable(internalName)
                ? " Please note that you will not be able to adopt this villager."
                : string.Empty;

            var request = new VillagerRequest(username, villagerData, (byte)index, GameInfo.Strings.GetVillager(internalName))
            {
                OnFinish = _ => { /* TwitchCrossBot handles reply */ }
            };

            bot.VillagerInjections.Enqueue(request);
            index = (index + 1) % 10;
        }

        var count = villagerNames.Length;
        var addMsg = count > 1 ? $"Villager inject request for {count} villagers has" : "Villager inject request has";
        return $":{addMsg} been added to the queue and will be injected momentarily.";
    }
}
