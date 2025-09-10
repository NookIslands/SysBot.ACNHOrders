using NHSE.Core;
using SysBot.Base;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TwitchLib.Client;
using TwitchLib.Client.Events;
using TwitchLib.Client.Models;
using TwitchLib.Communication.Clients;
using TwitchLib.Communication.Events;
using TwitchLib.Communication.Models;

namespace SysBot.ACNHOrders.Twitch
{
    public class TwitchCrossBot
    {
        internal static CrossBot Bot = default!;
        internal static string BotName = default!;
        internal static readonly List<TwitchQueue> QueuePool = new();
        private readonly TwitchClient client;
        private readonly string Channel;
        private readonly TwitchConfig Settings;

        public TwitchCrossBot(TwitchConfig settings, CrossBot bot)
        {
            Settings = settings;
            Bot = bot;
            BotName = settings.Username;

            var credentials = new ConnectionCredentials(settings.Username.ToLower(), settings.Token);
            var clientOptions = new ClientOptions
            {
                MessagesAllowedInPeriod = settings.ThrottleMessages,
                ThrottlingPeriod = TimeSpan.FromSeconds(settings.ThrottleSeconds),
                WhispersAllowedInPeriod = settings.ThrottleWhispers,
                WhisperThrottlingPeriod = TimeSpan.FromSeconds(settings.ThrottleWhispersSeconds)
            };

            // Normalize user-defined command keys to lowercase
            Settings.UserDefinitedCommands = Settings.UserDefinitedCommands
                .ToDictionary(k => k.Key.ToLower(), v => v.Value);
            Settings.UserDefinedSubOnlyCommands = Settings.UserDefinedSubOnlyCommands
                .ToDictionary(k => k.Key.ToLower(), v => v.Value);

            Channel = settings.Channel;
            client = new TwitchClient(new WebSocketClient(clientOptions));
            client.Initialize(credentials, Channel, settings.CommandPrefix, settings.CommandPrefix);

            AttachEventHandlers();
            client.Connect();

            // Forward logs
            EchoUtil.Forwarders.Add(msg => client.SendMessage(Channel, msg));
        }

        private void AttachEventHandlers()
        {
            client.OnLog += (_, e) => LogUtil.LogText($"[{client.TwitchUsername}] -[{e.BotUsername}] {e.Data}");
            client.OnConnected += (_, e) => LogUtil.LogText($"[{client.TwitchUsername}] Connected {e.AutoJoinChannel} as {e.BotUsername}");
            client.OnDisconnected += Client_OnDisconnected;
            client.OnJoinedChannel += (_, e) =>
            {
                LogUtil.LogInfo($"Joined {e.Channel}", e.BotUsername);
                client.SendMessage(e.Channel, "Connected!");
            };
            client.OnLeftChannel += (_, e) => client.JoinChannel(e.Channel);
            client.OnMessageReceived += (_, e) => { /* optional logging */ };
            client.OnWhisperReceived += Client_OnWhisperReceived;
            client.OnChatCommandReceived += Client_OnChatCommandReceived;
            client.OnWhisperCommandReceived += Client_OnWhisperCommandReceived;
        }

        private async void Client_OnDisconnected(object? sender, OnDisconnectedEventArgs e)
        {
            LogUtil.LogText($"[{client.TwitchUsername}] - Disconnected.");
            const int maxRetries = 10, delayMs = 5000;
            int retry = 0;
            while (!client.IsConnected && retry < maxRetries)
            {
                try { client.Reconnect(); await Task.Delay(delayMs); retry++; }
                catch { await Task.Delay(delayMs); retry++; }
            }
            if (!client.IsConnected)
                LogUtil.LogText($"[{client.TwitchUsername}] - Failed to reconnect after {maxRetries} attempts.");
        }

        private void Client_OnChatCommandReceived(object? sender, OnChatCommandReceivedArgs e)
        {
            if (!Settings.AllowCommandsViaChannel || Settings.UserBlacklist.Contains(e.Command.ChatMessage.Username))
                return;

            var msg = e.Command.ChatMessage;
            var cmd = e.Command.CommandText.ToLower();
            var args = e.Command.ArgumentsAsString;
            var response = HandleCommand(msg, cmd, args, false, out var dest);
            if (string.IsNullOrEmpty(response)) return;

            if (dest == TwitchMessageDestination.Whisper)
                client.SendWhisper(msg.Username, response);
            else
                client.SendMessage(msg.Channel, response);
        }

        private void Client_OnWhisperCommandReceived(object? sender, OnWhisperCommandReceivedArgs e)
        {
            if (!Settings.AllowCommandsViaWhisper || Settings.UserBlacklist.Contains(e.Command.WhisperMessage.Username))
                return;

            var msg = e.Command.WhisperMessage;
            var cmd = e.Command.CommandText.ToLower();
            var args = e.Command.ArgumentsAsString;
            var response = HandleCommand(msg, cmd, args, true, out _);
            if (!string.IsNullOrEmpty(response))
                client.SendWhisper(msg.Username, response);
        }

        private void Client_OnWhisperReceived(object? sender, OnWhisperReceivedArgs e)
        {
            if (QueuePool.Count > 100)
            {
                var removed = QueuePool[0];
                QueuePool.RemoveAt(0);
                client.SendMessage(Channel, $"Removed @{removed.DisplayName} from the waiting list: stale request.");
            }

            var queueItem = QueuePool.FindLast(q => q.ID == ulong.Parse(e.WhisperMessage.UserId));
            if (queueItem == null) return;
            QueuePool.Remove(queueItem);

            try
            {
                var _ = AddToTradeQueue(queueItem, e.WhisperMessage.Message, out string msg);
                client.SendMessage(Channel, msg);
            }
            catch (Exception ex)
            {
                LogUtil.LogError(ex.Message, nameof(TwitchCrossBot));
            }
        }

        private string HandleCommand(TwitchLibMessage m, string c, string args, bool whisper, out TwitchMessageDestination dest)
        {
            dest = TwitchMessageDestination.Disabled;
            bool sudo() => m is ChatMessage ch && (ch.IsBroadcaster || Settings.IsSudo(m.Username));
            bool subscriber() => m is ChatMessage { IsSubscriber: true };

            // User-defined commands
            if (Settings.UserDefinedSubOnlyCommands.ContainsKey(c))
                return subscriber()
                    ? ReplacePredefined(Settings.UserDefinedSubOnlyCommands[c], m.Username)
                    : $"@{m.Username} - You must be a subscriber to use this command.";

            if (Settings.UserDefinitedCommands.ContainsKey(c))
                return ReplacePredefined(Settings.UserDefinitedCommands[c], m.Username);

            switch (c)
            {
                case "injectvillager":
                case "iv":
                    // parse index + villager(s)
                    var parts = args.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                    int index = 0;
                    string[] names;

                    if (parts.Length == 0)
                        return $"@{m.Username} - You must specify at least a villager.";

                    if (!int.TryParse(parts[0], out index))
                    {
                        index = 0;
                        names = args.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    }
                    else if (parts.Length == 1)
                        return $"@{m.Username} - You must specify the villager name after the index.";
                    else
                        names = parts[1].Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);

                    dest = TwitchMessageDestination.Channel;
                    return TwitchVillagerCommands.InjectVillager(m.Username, index, names);

                case "order":
                    TwitchHelper.AddToWaitingList(args, m.DisplayName, m.Username, ulong.Parse(m.UserId), subscriber(), false, out string msg);
                    return msg;
                case "ordercat":
                    TwitchHelper.AddToWaitingList(args, m.DisplayName, m.Username, ulong.Parse(m.UserId), subscriber(), true, out string msg1);
                    return msg1;
                case "preset":
                    TwitchHelper.AddToWaitingListPreset(args, m.DisplayName, m.Username, ulong.Parse(m.UserId), subscriber(), out string msg2);
                    return msg2;
                case "presets":
                    return TwitchHelper.GetPresets(Settings.CommandPrefix);
                case "drop":
                    return $"@{m.Username}: {TwitchHelper.Drop(args, ulong.Parse(m.UserId), m.Username, Settings)}";
                case "clean":
                    return $"@{m.Username}: {TwitchHelper.Clean(ulong.Parse(m.UserId), m.Username, Settings)}";
                case "ts":
                case "pos":
                case "position":
                case "time":
                case "eta":
                    return $"@{m.Username}: {TwitchHelper.GetPosition(ulong.Parse(m.UserId))}";
                case "tc":
                case "remove":
                case "delete":
                case "qc":
                    return $"@{m.Username}: {TwitchHelper.ClearTrade(ulong.Parse(m.UserId))}";
                case "ping":
                    return $"@{m.Username}: pong!";
                case "tcu" when !sudo():
                case "toggledrop" when !sudo():
                    return "This command is locked for sudo users only!";
                case "tcu":
                    return TwitchHelper.ClearTrade(args);
                case "toggledrop":
                    Settings.AllowDropViaTwitchChat = !Settings.AllowDropViaTwitchChat;
                    return Settings.AllowDropViaTwitchChat ? "I am now accepting drop commands!" : "I am no longer accepting drop commands!";
                default: return string.Empty;
            }
        }

        private bool AddToTradeQueue(TwitchQueue queueItem, string pass, out string msg)
        {
            if (int.TryParse(pass, out var ps))
            {
                var twitchRequest = new TwitchOrderRequest<Item>(
                    queueItem.ItemReq.ToArray(), queueItem.ID, QueueExtensions.GetNextID(),
                    queueItem.DisplayName, queueItem.DisplayName, client, Channel, Settings, ps,
                    queueItem.VillagerReq);

                var result = QueueExtensions.AddToQueueSync(twitchRequest, queueItem.DisplayName, queueItem.DisplayName, out var msge);
                msg = TwitchOrderRequest<Item>.SanitizeForTwitch(msge);
                return result;
            }

            msg = $"@{queueItem.DisplayName} - Your 3-digit number was invalid. Order has been removed, please start over.";
            return false;
        }

        private static string ReplacePredefined(string message, string caller)
        {
            return message.Replace("{islandname}", Bot.TownName)
                .Replace("{dodo}", Bot.DodoCode)
                .Replace("{vcount}", Math.Min(0, Bot.VisitorList.VisitorCount - 1).ToString())
                .Replace("{visitorlist}", Bot.VisitorList.VisitorFormattedString)
                .Replace("{villagerlist}", Bot.Villagers.LastVillagers)
                .Replace("{user}", caller);
        }
    }
}
