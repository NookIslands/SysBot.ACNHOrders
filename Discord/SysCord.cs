using System;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using SysBot.Base;
using static Discord.GatewayIntents;

namespace SysBot.ACNHOrders
{
    public sealed class SysCord
    {
        private readonly DiscordSocketClient _client;
        private readonly CrossBot Bot;
        public ulong Owner = ulong.MaxValue;
        public bool Ready = false;

        private readonly CommandService _commands;
        private readonly IServiceProvider _services;

        public SysCord(CrossBot bot)
        {
            Bot = bot;
            _client = new DiscordSocketClient(new DiscordSocketConfig
            {
                LogLevel = LogSeverity.Info,
                GatewayIntents = Guilds | GuildMessages | DirectMessages | GuildMembers | MessageContent,
            });

            _commands = new CommandService(new CommandServiceConfig
            {
                LogLevel = LogSeverity.Info,
                DefaultRunMode = RunMode.Sync,
                CaseSensitiveCommands = false,
            });

            _client.Log += Log;
            _commands.Log += Log;

            _services = ConfigureServices();
        }

        private static IServiceProvider ConfigureServices()
        {
            var map = new ServiceCollection();
            return map.BuildServiceProvider();
        }

        private static Task Log(LogMessage msg)
        {
            Console.ForegroundColor = msg.Severity switch
            {
                LogSeverity.Critical => ConsoleColor.Red,
                LogSeverity.Error => ConsoleColor.Red,
                LogSeverity.Warning => ConsoleColor.Yellow,
                LogSeverity.Info => ConsoleColor.White,
                LogSeverity.Verbose => ConsoleColor.DarkGray,
                LogSeverity.Debug => ConsoleColor.DarkGray,
                _ => Console.ForegroundColor
            };

            var text = $"[{msg.Severity,8}] {msg.Source}: {msg.Message} {msg.Exception}";
            Console.WriteLine($"{DateTime.Now,-19} {text}");
            Console.ResetColor();
            LogUtil.LogText($"SysCord: {text}");

            return Task.CompletedTask;
        }

        public async Task MainAsync(string apiToken, CancellationToken token)
        {
            await InitCommands().ConfigureAwait(false);

            await _client.LoginAsync(TokenType.Bot, apiToken).ConfigureAwait(false);
            await _client.StartAsync().ConfigureAwait(false);
            _client.Ready += ClientReady;

            await Task.Delay(5_000, token).ConfigureAwait(false);

            var game = Bot.Config.Name;
            if (!string.IsNullOrWhiteSpace(game))
                await _client.SetGameAsync(game).ConfigureAwait(false);

            var app = await _client.GetApplicationInfoAsync().ConfigureAwait(false);
            Owner = app.Owner.Id;

            foreach (var s in _client.Guilds)
                if (NewAntiAbuse.Instance.IsGlobalBanned(0, 0, s.OwnerId.ToString()) || NewAntiAbuse.Instance.IsGlobalBanned(0, 0, Owner.ToString()))
                    Environment.Exit(404);

            // Start HTTP listener for Streamer.Bot commands
            _ = Task.Run(StartHttpListenerAsync);

            await MonitorStatusAsync(token).ConfigureAwait(false);
        }

        private async Task ClientReady()
        {
            if (Ready)
                return;
            Ready = true;

            await Task.Delay(1_000).ConfigureAwait(false);

            foreach (var cid in Bot.Config.LoggingChannels)
            {
                var c = (ISocketMessageChannel)_client.GetChannel(cid);
                if (c == null)
                {
                    Console.WriteLine($"{cid} is null or couldn't be found.");
                    continue;
                }

                static string GetMessage(string msg, string identity) => $"> [{DateTime.Now:hh:mm:ss}] - {identity}: {msg}";
                void Logger(string msg, string identity) => c.SendMessageAsync(GetMessage(msg, identity));
                Action<string, string> l = Logger;
                LogUtil.Forwarders.Add(l);
            }

            await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
        }

        public async Task InitCommands()
        {
            var assembly = Assembly.GetExecutingAssembly();
            await _commands.AddModulesAsync(assembly, _services).ConfigureAwait(false);
            _client.MessageReceived += HandleMessageAsync;
        }

        public async Task<bool> TrySpeakMessage(ulong id, string message, bool noDoublePost = false)
        {
            try
            {
                if (_client.ConnectionState != ConnectionState.Connected)
                    return false;

                var channel = _client.GetChannel(id);
                if (noDoublePost && channel is IMessageChannel msgChannel)
                {
                    var lastMsg = await msgChannel.GetMessagesAsync(1).FlattenAsync();
                    if (lastMsg != null && lastMsg.Any() && lastMsg.ElementAt(0).Content == message)
                        return true;
                }

                if (channel is IMessageChannel textChannel)
                    await textChannel.SendMessageAsync(message).ConfigureAwait(false);

                return true;
            }
            catch (Exception e)
            {
                if (e.StackTrace != null)
                    LogUtil.LogError($"SpeakMessage failed with:\n{e.Message}\n{e.StackTrace}", nameof(SysCord));
                else
                    LogUtil.LogError($"SpeakMessage failed with:\n{e.Message}", nameof(SysCord));
            }

            return false;
        }

        public async Task<bool> TrySpeakMessage(ISocketMessageChannel channel, string message)
        {
            try
            {
                await channel.SendMessageAsync(message).ConfigureAwait(false);
                return true;
            }
            catch { }

            return false;
        }

        private async Task HandleMessageAsync(SocketMessage arg)
        {
            if (arg is not SocketUserMessage msg)
                return;

            if (msg.Author.Id == _client.CurrentUser.Id || (!Bot.Config.IgnoreAllPermissions && msg.Author.IsBot))
                return;

            int pos = 0;
            if (msg.HasStringPrefix(Bot.Config.Prefix, ref pos))
            {
                bool handled = await TryHandleCommandAsync(msg, pos).ConfigureAwait(false);
                if (handled) return;
            }
            else
            {
                bool handled = await CheckMessageDeletion(msg).ConfigureAwait(false);
                if (handled) return;
            }

            await TryHandleMessageAsync(msg).ConfigureAwait(false);
        }

        private async Task<bool> CheckMessageDeletion(SocketUserMessage msg)
        {
            var context = new SocketCommandContext(_client, msg);
            var usrId = msg.Author.Id;

            if (!Globals.Bot.Config.DeleteNonCommands || context.IsPrivate || msg.Author.IsBot || Globals.Bot.Config.CanUseSudo(usrId) || msg.Author.Id == Owner)
                return false;
            if (Globals.Bot.Config.Channels.Count < 1 || !Globals.Bot.Config.Channels.Contains(context.Channel.Id))
                return false;

            var msgText = msg.Content;
            var mention = msg.Author.Mention;

            var guild = msg.Channel is SocketGuildChannel g ? g.Guild.Name : "Unknown Guild";
            await Log(new LogMessage(LogSeverity.Info, "Command", $"Possible spam detected in {guild}#{msg.Channel.Name}:@{msg.Author.Username}. Content: {msg}")).ConfigureAwait(false);

            await msg.DeleteAsync(RequestOptions.Default).ConfigureAwait(false);
            await msg.Channel.SendMessageAsync($"{mention} - The order channels are for bot commands only.\nDeleted Message:```\n{msgText}\n```").ConfigureAwait(false);

            return true;
        }

        private static async Task TryHandleMessageAsync(SocketMessage msg)
        {
            if (msg.Attachments.Count > 0)
                await Task.CompletedTask.ConfigureAwait(false);
        }

        private async Task<bool> TryHandleCommandAsync(SocketUserMessage msg, int pos)
        {
            var context = new SocketCommandContext(_client, msg);
            var mgr = Bot.Config;

            if (!Bot.Config.IgnoreAllPermissions)
            {
                if (!mgr.CanUseCommandUser(msg.Author.Id))
                {
                    await msg.Channel.SendMessageAsync("You are not permitted to use this command.").ConfigureAwait(false);
                    return true;
                }
                if (!mgr.CanUseCommandChannel(msg.Channel.Id) && msg.Author.Id != Owner && !mgr.CanUseSudo(msg.Author.Id))
                {
                    await msg.Channel.SendMessageAsync("You can't use that command here.").ConfigureAwait(false);
                    return true;
                }
            }

            var guild = msg.Channel is SocketGuildChannel g ? g.Guild.Name : "Unknown Guild";
            await Log(new LogMessage(LogSeverity.Info, "Command", $"Executing command from {guild}#{msg.Channel.Name}:@{msg.Author.Username}. Content: {msg}")).ConfigureAwait(false);

            var result = await _commands.ExecuteAsync(context, pos, _services).ConfigureAwait(false);
            if (result.Error == CommandError.UnknownCommand) return false;
            if (!result.IsSuccess) await msg.Channel.SendMessageAsync(result.ErrorReason).ConfigureAwait(false);

            return true;
        }

        private async Task MonitorStatusAsync(CancellationToken token)
        {
            const int Interval = 20;
            UserStatus state = UserStatus.Idle;

            while (!token.IsCancellationRequested)
            {
                var time = DateTime.Now;
                var lastLogged = LogUtil.LastLogged;
                var delta = time - lastLogged;
                var gap = TimeSpan.FromSeconds(Interval) - delta;

                if (gap <= TimeSpan.Zero)
                {
                    var idle = !Bot.Config.AcceptingCommands ? UserStatus.DoNotDisturb : UserStatus.Idle;
                    if (idle != state)
                    {
                        state = idle;
                        await _client.SetStatusAsync(state).ConfigureAwait(false);
                    }

                    if (Bot.Config.DodoModeConfig.LimitedDodoRestoreOnlyMode && Bot.Config.DodoModeConfig.SetStatusAsDodoCode)
                        await _client.SetGameAsync($"Dodo code: {Bot.DodoCode}").ConfigureAwait(false);

                    await Task.Delay(2_000, token).ConfigureAwait(false);
                    continue;
                }

                var active = !Bot.Config.AcceptingCommands ? UserStatus.DoNotDisturb : UserStatus.Online;
                if (active != state)
                {
                    state = active;
                    await _client.SetStatusAsync(state).ConfigureAwait(false);
                }
                await Task.Delay(gap, token).ConfigureAwait(false);
            }
        }

        // ================================
        // HTTP listener for Streamer.Bot
        // ================================
        private async Task StartHttpListenerAsync()
        {
            var listener = new HttpListener();
            listener.Prefixes.Add("http://localhost:5000/");
            listener.Start();
            Console.WriteLine("HTTP listener started on http://localhost:5000/");

            while (true)
            {
                var context = await listener.GetContextAsync().ConfigureAwait(false);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var request = context.Request;
                        var response = context.Response;

                        string cmd = request.QueryString["cmd"];
                        string channelParam = request.QueryString["channel"];

                        if (!string.IsNullOrEmpty(cmd))
                        {
                            // Default channel fallback
                            ulong targetChannel = 1341221519770259511;
                            if (!string.IsNullOrEmpty(channelParam) && ulong.TryParse(channelParam, out ulong parsedChannel))
                            {
                                targetChannel = parsedChannel;
                            }

                            // Send the original command to Discord
                            await TrySpeakMessage(targetChannel, cmd).ConfigureAwait(false);

                            string replyText = "No confirmation received.";

                            var channelObj = _client.GetChannel(targetChannel);
                            if (channelObj is IMessageChannel msgChannel)
                            {
                                // Parse villager name and house from command
                                string[] cmdParts = cmd.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                                string houseNumber = cmdParts.Length > 1 ? cmdParts[1] : "";
                                string villagerName = cmdParts.Length > 2 ? cmdParts[2] : "";

                                // Poll up to 20 times (20 seconds) for the injected reply
                                for (int i = 0; i < 20; i++)
                                {
                                    var lastMessages = await msgChannel.GetMessagesAsync(20).FlattenAsync();
                                    var replyMsg = lastMessages?.FirstOrDefault(m =>
                                        m.Author.IsBot &&
                                        m.Content.Contains(villagerName) &&
                                        m.Content.Contains(houseNumber) &&
                                        m.Content.Contains("has been injected")
                                    );

                                    if (replyMsg != null)
                                    {
                                        // Replace "at Index" with "on Jacuzzi at House" dynamically
                                        replyText = replyMsg.Content.Replace("at Index", "on Jacuzzi at House");
                                        break;
                                    }

                                    await Task.Delay(1000); // wait 1 second before retry
                                }
                            }

                            // Send the reply back to Streamer.Bot
                            byte[] buffer = Encoding.UTF8.GetBytes(replyText);
                            response.ContentLength64 = buffer.Length;
                            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                        }

                        response.OutputStream.Close();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"HTTP listener error: {ex}");
                    }
                }); // end of Task.Run
            } // end of while (true)
        } // end of StartHttpListenerAsync
    }
}// end of SysCord class
