// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Piston.TestProcessHost;

/// <summary>
/// A minimal Microsoft.Testing.Platform server: answers <c>initialize</c>, reports one passed test per
/// request (its uid carries this process's id and the request count), and exits on <c>exit</c>.
/// </summary>
internal static class FakeMtpServer
{
    public static async Task<int> RunAsync(int port)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        var requests = 0;
        while (await ReadAsync(stream) is { } message)
        {
            var method = message["method"]?.GetValue<string>();
            var id = message["id"]?.DeepClone();
            switch (method)
            {
                case "initialize":
                    await WriteAsync(stream, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = new JsonObject() });
                    break;
                case "testing/discoverTests":
                case "testing/runTests":
                    requests++;
                    var node = new JsonObject
                    {
                        ["uid"] = string.Create(CultureInfo.InvariantCulture, $"{Environment.ProcessId}:{requests}"),
                        ["display-name"] = "Fake",
                        ["node-type"] = "action",
                        ["execution-state"] = method == "testing/runTests" ? "passed" : "discovered",
                    };
                    await WriteAsync(stream, new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["method"] = "testing/testUpdates/tests",
                        ["params"] = new JsonObject { ["changes"] = new JsonArray(new JsonObject { ["node"] = node }) },
                    });
                    await WriteAsync(stream, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = new JsonObject() });
                    break;
                case "exit":
                    return 0;
            }
        }

        return 0;
    }

    private static async Task<JsonNode?> ReadAsync(NetworkStream stream)
    {
        var header = new StringBuilder();
        var one = new byte[1];
        while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(one) == 0)
            {
                return null;
            }

            _ = header.Append((char)one[0]);
        }

        var length = int.Parse(header.ToString().Split(':')[1].Trim(), CultureInfo.InvariantCulture);
        var body = new byte[length];
        await stream.ReadExactlyAsync(body);
        return JsonNode.Parse(body);
    }

    private static async Task WriteAsync(NetworkStream stream, JsonObject message)
    {
        var body = Encoding.UTF8.GetBytes(message.ToJsonString());
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {body.Length.ToString(CultureInfo.InvariantCulture)}\r\n\r\n"));
        await stream.WriteAsync(body);
    }
}
