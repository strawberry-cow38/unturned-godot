using System;
using System.Collections.Generic;
using SDG.NetTransport;
using SDG.NetTransport.Mem;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // The deterministic multi-peer sim from MP_PLAN §6: one server session + N client sessions over
    // MemTransport, stepped one 50 Hz tick at a time. No sockets, no Thread.Sleep, no wall clock --
    // a 1000-tick two-client sim runs in milliseconds and identically every run. Adverse networks are
    // just FaultyLinkConfig values; every failure message carries the seed for exact reproduction.
    sealed class NetSimHarness
    {
        public readonly MemNetwork Net;
        public readonly NetServerSession Server;
        public readonly List<NetClientSession> Clients = new List<NetClientSession>();

        public struct Failure
        {
            public ITransportConnection Connection;
            public string Reason;
            public bool IsError;
        }
        public readonly List<Failure> Failures = new List<Failure>();

        public string SeedInfo => "seed=" + Net.Seed;

        public NetSimHarness(int seed,
                             FaultyLinkConfig clientToServer = null,
                             FaultyLinkConfig serverToClient = null,
                             byte serverVersion = NetProtocol.Version,
                             int maxPeers = 32)
        {
            Net = new MemNetwork(seed);
            if (clientToServer != null) Net.ClientToServer = clientToServer;
            if (serverToClient != null) Net.ServerToClient = serverToClient;
            Server = new NetServerSession(
                new MemServerTransport(Net),
                (conn, reason, isError) => Failures.Add(new Failure { Connection = conn, Reason = reason, IsError = isError }),
                serverVersion,
                maxPeers);
        }

        public NetClientSession AddClient(string name = "player", byte version = NetProtocol.Version)
        {
            var client = new NetClientSession(new MemClientTransport(Net), name, version);
            Clients.Add(client);
            return client;
        }

        public void Step()
        {
            Net.Tick();
            foreach (var client in Clients) client.Tick();
            Server.Tick();
        }

        public void Step(int ticks)
        {
            for (int i = 0; i < ticks; i++) Step();
        }

        public bool StepUntil(Func<bool> condition, int maxTicks)
        {
            for (int i = 0; i < maxTicks; i++)
            {
                if (condition()) return true;
                Step();
            }
            return condition();
        }

        /// <summary>Connect one client and pump until accepted; asserts inline so tests can one-line it.</summary>
        public NetClientSession ConnectClient(string name = "player", int maxTicks = 400)
        {
            var client = AddClient(name);
            client.Connect();
            if (!StepUntil(() => client.State == NetSessionState.Connected, maxTicks))
                throw new InvalidOperationException($"client failed to connect within {maxTicks} ticks ({SeedInfo})");
            return client;
        }
    }
}
