// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.DataCollection;

namespace Piston.LiveCollector;

/// <summary>
/// Streams test start/end events to the Piston process that launched <c>dotnet test</c>, so it can show
/// which tests are running. Piston passes a loopback port in <c>PISTON_LIVE_PORT</c>; each event is one
/// tab-separated line: <c>S\t&lt;display name&gt;</c> or <c>E\t&lt;display name&gt;\t&lt;outcome&gt;</c>.
/// Does nothing when the variable is missing or the connection fails.
/// </summary>
[DataCollectorFriendlyName("PistonLive")]
[DataCollectorTypeUri("datacollector://piston/live/1.0")]
public sealed class LiveCollector : DataCollector
{
    public const string PortVariable = "PISTON_LIVE_PORT";

    private readonly object _gate = new();
    private TcpClient? _client;
    private StreamWriter? _writer;

    public override void Initialize(
        XmlElement? configurationElement,
        DataCollectionEvents events,
        DataCollectionSink dataSink,
        DataCollectionLogger logger,
        DataCollectionEnvironmentContext? environmentContext)
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable(PortVariable), out var port))
        {
            return;
        }

        try
        {
            _client = new TcpClient { NoDelay = true };
            _client.Connect("127.0.0.1", port);
            _writer = new StreamWriter(_client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            _client?.Dispose();
            _client = null;
            return;
        }

        events.TestCaseStart += (_, e) => Write("S\t" + Clean(e.TestElement?.DisplayName));
        events.TestCaseEnd += (_, e) => Write("E\t" + Clean(e.TestElement?.DisplayName) + "\t" + e.TestOutcome);
    }

    private void Write(string line)
    {
        lock (_gate)
        {
            if (_writer is null)
            {
                return;
            }

            try
            {
                _writer.WriteLine(line);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
            {
                _writer = null;
            }
        }
    }

    private static string Clean(string? value) =>
        (value ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_gate)
            {
                try { _writer?.Dispose(); } catch (IOException) { }
                _writer = null;
                _client?.Dispose();
                _client = null;
            }
        }

        base.Dispose(disposing);
    }
}
