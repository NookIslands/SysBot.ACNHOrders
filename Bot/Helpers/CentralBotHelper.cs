using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;

namespace SysBot.ACNHOrders.Helpers
{
    public static class CentralBotHelper
    {
        // Map islands (1–22) to bot TCP ports
        private static readonly Dictionary<int, int> IslandToPort = new()
        {
            { 1, 5201 }, { 2, 5202 }, { 3, 5203 }, { 4, 5204 },
            { 5, 5205 }, { 6, 5206 }, { 7, 5207 }, { 8, 5208 },
            { 9, 5209 }, { 10, 5210 }, { 11, 5211 }, { 12, 5212 },
            { 13, 5213 }, { 14, 5214 }, { 15, 5215 }, { 16, 5216 },
            { 17, 5217 }, { 18, 5218 }, { 19, 5219 }, { 20, 5220 },
            { 21, 5221 }, { 22, 5222 }
        };

        public static void SendVillager(int island, int house, string villagerName, Dictionary<string,string>? flags = null)
        {
            if (!IslandToPort.TryGetValue(island, out int port))
            {
                Console.WriteLine($"Island {island} not mapped to any bot port.");
                return;
            }

            if (house < 0 || house > 9)
            {
                Console.WriteLine("House must be between 0 and 9.");
                return;
            }

            flags ??= new Dictionary<string,string>();
            string flagString = string.Join(",", flags.Select(f => $"{f.Key}={f.Value}"));
            string payload = $"inject_villager|{house}|{villagerName}|{flagString}";

            try
            {
                using TcpClient client = new TcpClient("127.0.0.1", port);
                NetworkStream stream = client.GetStream();
                byte[] data = Encoding.UTF8.GetBytes(payload + "\n");
                stream.Write(data, 0, data.Length);

                Console.WriteLine($"Sent to island {island} (port {port}): {payload}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to send to bot on port {port}: {ex.Message}");
            }
        }
    }
}
