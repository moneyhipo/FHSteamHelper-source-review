// FHSteamHelper: Steam lobby + P2P relay for the FH2 co-op mod.
//
// The game (NW.js 0.29) cannot use Steam itself. It starts this helper and
// talks to it over a local control socket (JSON lines, default port 27610):
//
//   game -> helper   {"cmd":"hello"}                 send our Steam name
//                    {"cmd":"host","port":27600}     create a friends-only lobby,
//                                                    relay friends to that port
//                    {"cmd":"friends"}               online friends -> {"evt":"friends"}
//                    {"cmd":"inviteUser","id":"<id>"} lobby invite -> {"evt":"invited"}
//                    {"cmd":"invite"}                Steam overlay dialog (unusable in NW.js)
//                    {"cmd":"join","lobby":"<id>"}   join a friend's lobby
//   helper -> game   {"evt":"name","name":"...","ver":1}  ver: ProtocolVersion
//                    {"evt":"lobby","lobby":"<id>"}
//                    {"evt":"joinRequest","lobby":"<id>"}  invite accepted
//                    {"evt":"ready","port":27601}    connect the game here
//                    {"evt":"error","text":"..."}
//
// Traffic: the host's helper opens one local TCP connection to the game's
// co-op port per friend; the guest's helper listens on a local port for its
// own game. Bytes go through SteamNetworkingMessages (reliable, channel 0).
// Every Steam message starts with one byte: 0 = data, 1 = the guest's game
// connected (start a fresh stream), 2 = that side's connection closed.
// The App ID comes from steam_appid.txt next to the helper (Fear & Hunger 2:
// Termina = 2171440 by default; 480 = Spacewar for tests).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Steamworks;

internal static class Program
{
    private const int Channel = 0;
    // Sent with our Steam name ("ver"): the game asks for a newer helper when
    // this is below the version it needs (the helper is a separate download).
    private const int ProtocolVersion = 1;
    private const int ChunkSize = 256 * 1024;
    private const int SendReliable = 8; // k_nSteamNetworkingSend_Reliable
    private const byte KindData = 0;
    private const byte KindOpen = 1;
    private const byte KindClose = 2;

    private static int _controlPort = 27610;
    private static int _guestPort = 27601;
    private static TcpListener? _control;
    private static TcpClient? _game;              // control connection
    private static readonly object GameLock = new object();
    private static readonly ConcurrentQueue<string> Commands = new ConcurrentQueue<string>();

    private static CSteamID _lobby = CSteamID.Nil;
    private static bool _isHost;
    private static int _hostPort = 27600;
    private static CSteamID _hostId = CSteamID.Nil;

    // host: one local connection per friend; guest: the game's connection
    private static readonly Dictionary<ulong, TcpClient> Peers = new Dictionary<ulong, TcpClient>();
    private static readonly object PeersLock = new object();
    private static TcpListener? _guestListener;
    private static TcpClient? _guestGame;
    // filled by socket threads, sent on the main thread (Steam is not thread safe)
    private static readonly ConcurrentQueue<(ulong peer, byte kind, byte[] data)> Outgoing =
        new ConcurrentQueue<(ulong, byte, byte[])>();
    // framed messages Steam did not take yet (send buffer full): retried in order
    private static readonly Dictionary<ulong, Queue<byte[]>> Pending = new Dictionary<ulong, Queue<byte[]>>();

    private static Callback<GameLobbyJoinRequested_t>? _joinRequested;
    private static Callback<GameRichPresenceJoinRequested_t>? _presenceJoin;
    private static Callback<SteamNetworkingMessagesSessionRequest_t>? _sessionRequest;
    private static Callback<SteamNetworkingMessagesSessionFailed_t>? _sessionFailed;
    private static CallResult<LobbyCreated_t>? _lobbyCreated;
    private static CallResult<LobbyEnter_t>? _lobbyEntered;

    // Native AOT: Steamworks.NET reads its callback structs with
    // Marshal.PtrToStructure(IntPtr, Type); the compiler only makes the
    // marshalling code for types it sees used. Name them here (never run).
    private static void KeepCallbackTypes()
    {
        if (Environment.GetEnvironmentVariable("FH_AOT_KEEP_NEVER") != "run") return;
        var p = IntPtr.Zero;
        Marshal.PtrToStructure<GameLobbyJoinRequested_t>(p);
        Marshal.PtrToStructure<GameRichPresenceJoinRequested_t>(p);
        Marshal.PtrToStructure<SteamNetworkingMessagesSessionRequest_t>(p);
        Marshal.PtrToStructure<SteamNetworkingMessagesSessionFailed_t>(p);
        Marshal.PtrToStructure<LobbyCreated_t>(p);
        Marshal.PtrToStructure<LobbyEnter_t>(p);
        Marshal.PtrToStructure<SteamAPICallCompleted_t>(p);
        Marshal.PtrToStructure<SteamNetworkingIdentity>(p);
        Marshal.PtrToStructure<SteamNetworkingMessage_t>(p);
    }

    private static int Main(string[] args)
    {
        KeepCallbackTypes();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--port") int.TryParse(args[i + 1], out _controlPort);
            if (args[i] == "--guest-port") int.TryParse(args[i + 1], out _guestPort);
        }
        string? connectLobby = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "+connect_lobby") connectLobby = args[i + 1];
        }

        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "steam_appid.txt")))
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "steam_appid.txt"), "2171440");
        }
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);

        // Another helper already owns the port (second game window): leave it.
        if (!StartControl()) return 2;

        if (!SteamAPI.Init())
        {
            Console.Error.WriteLine("SteamAPI.Init failed (is Steam running?)");
            // the game connects a moment after starting us
            for (int i = 0; i < 100 && !GameConnected(); i++) Thread.Sleep(50);
            SendEvent("{\"evt\":\"error\",\"text\":\"Steam is not running\"}");
            Thread.Sleep(1000);
            return 1;
        }

        // Bigger send buffer: a whole world snapshot fits at once.
        var bufferSize = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(bufferSize, 8 * 1024 * 1024);
            SteamNetworkingUtils.SetConfigValue(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize,
                ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero,
                ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, bufferSize);
        }
        finally
        {
            Marshal.FreeHGlobal(bufferSize);
        }

        _joinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnJoinRequested);
        _presenceJoin = Callback<GameRichPresenceJoinRequested_t>.Create(OnPresenceJoin);
        _sessionRequest = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(OnSessionRequest);
        _sessionFailed = Callback<SteamNetworkingMessagesSessionFailed_t>.Create(OnSessionFailed);
        _lobbyCreated = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
        _lobbyEntered = CallResult<LobbyEnter_t>.Create(OnLobbyEntered);

        if (connectLobby != null) Commands.Enqueue("{\"cmd\":\"join\",\"lobby\":\"" + connectLobby + "\"}");

        var lastGame = DateTime.UtcNow;
        while (true)
        {
            SteamAPI.RunCallbacks();
            while (Commands.TryDequeue(out var line)) HandleCommand(line);
            ReceiveSteam();
            while (Outgoing.TryDequeue(out var item)) QueueSteam(item.peer, item.kind, item.data);
            FlushSteam();
            if (GameConnected()) lastGame = DateTime.UtcNow;
            // The game is gone for a while: quit (it starts a new helper).
            if ((DateTime.UtcNow - lastGame).TotalSeconds > 30) break;
            Thread.Sleep(5);
        }
        if (_lobby != CSteamID.Nil) SteamMatchmaking.LeaveLobby(_lobby);
        SteamAPI.Shutdown();
        return 0;
    }

    private static bool GameConnected()
    {
        lock (GameLock) return _game != null;
    }

    // ------------------------------------------------------------------ control

    private static bool StartControl()
    {
        try
        {
            _control = new TcpListener(IPAddress.Loopback, _controlPort);
            _control.Start();
        }
        catch (SocketException)
        {
            return false;
        }
        new Thread(() =>
        {
            while (true)
            {
                TcpClient client;
                try { client = _control.AcceptTcpClient(); } catch { return; }
                lock (GameLock) { _game = client; }
                new Thread(() => ReadControl(client)) { IsBackground = true }.Start();
            }
        }) { IsBackground = true }.Start();
        return true;
    }

    private static void ReadControl(TcpClient client)
    {
        try
        {
            using var reader = new StreamReader(client.GetStream(), new UTF8Encoding(false));
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length > 0) Commands.Enqueue(line);
            }
        }
        catch { }
        // end of stream: the game closed or crashed
        lock (GameLock)
        {
            if (_game == client) _game = null;
        }
        try { client.Dispose(); } catch { }
    }

    private static void SendEvent(string json)
    {
        lock (GameLock)
        {
            if (_game == null) return;
            try
            {
                var bytes = Encoding.UTF8.GetBytes(json + "\n");
                _game.GetStream().Write(bytes, 0, bytes.Length);
            }
            catch { }
        }
    }

    private static string Esc(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch == '\\' || ch == '"') sb.Append('\\').Append(ch);
            else if (ch < ' ') sb.Append(' '); // keeps the reply on one line
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    // Online friends for the game's own invite window:
    // {"evt":"friends","list":[{"id":"7656...","name":"...","game":1,"st":1}]}
    // game: 1 = playing this game; st: EPersonaState (1 online, 3 away...).
    private static void SendFriends()
    {
        var sb = new StringBuilder("{\"evt\":\"friends\",\"list\":[");
        int count = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
        var appId = SteamUtils.GetAppID();
        bool first = true;
        for (int i = 0; i < count; i++)
        {
            var id = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
            var state = SteamFriends.GetFriendPersonaState(id);
            if (state == EPersonaState.k_EPersonaStateOffline) continue;
            bool inGame = SteamFriends.GetFriendGamePlayed(id, out var played) &&
                played.m_gameID.AppID() == appId;
            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"id\":\"").Append(id.m_SteamID).Append("\",\"name\":\"")
                .Append(Esc(SteamFriends.GetFriendPersonaName(id))).Append("\",\"game\":")
                .Append(inGame ? 1 : 0).Append(",\"st\":").Append((int)state).Append('}');
        }
        sb.Append("]}");
        SendEvent(sb.ToString());
    }

    private static string? Field(string json, string name)
    {
        // tiny reader for {"cmd":"x","port":1,"lobby":"123"}
        var key = "\"" + name + "\":";
        int i = json.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return null;
        i += key.Length;
        if (i < json.Length && json[i] == '"')
        {
            int end = json.IndexOf('"', i + 1);
            return end < 0 ? null : json.Substring(i + 1, end - i - 1);
        }
        int stop = i;
        while (stop < json.Length && (char.IsDigit(json[stop]) || json[stop] == '-')) stop++;
        return json.Substring(i, stop - i);
    }

    private static void SendName()
    {
        SendEvent("{\"evt\":\"name\",\"name\":\"" + Esc(SteamFriends.GetPersonaName()) + "\",\"ver\":" + ProtocolVersion + "}");
    }

    private static void HandleCommand(string line)
    {
        var cmd = Field(line, "cmd");
        switch (cmd)
        {
            case "hello":
                SendName();
                break;
            case "host":
                if (int.TryParse(Field(line, "port"), out var port)) _hostPort = port;
                _isHost = true;
                SendName();
                if (_lobby == CSteamID.Nil)
                {
                    _lobbyCreated!.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, 4));
                }
                break;
            case "invite":
                // Kept for old game scripts. The overlay draws in NW.js's GPU
                // process but never gets the mouse/keyboard there, so the
                // dialog cannot be used or closed: the game uses "friends" and
                // "inviteUser" with its own window instead.
                if (_lobby != CSteamID.Nil) SteamFriends.ActivateGameOverlayInviteDialog(_lobby);
                else SendEvent("{\"evt\":\"error\",\"text\":\"no lobby yet\"}");
                break;
            case "friends":
                SendFriends();
                break;
            case "inviteUser":
                if (_lobby == CSteamID.Nil)
                {
                    SendEvent("{\"evt\":\"error\",\"text\":\"no lobby yet\"}");
                }
                else if (ulong.TryParse(Field(line, "id"), out var friendId))
                {
                    var friend = new CSteamID(friendId);
                    bool ok = SteamMatchmaking.InviteUserToLobby(_lobby, friend);
                    SendEvent("{\"evt\":\"invited\",\"id\":\"" + friendId + "\",\"ok\":" + (ok ? "true" : "false") +
                        ",\"name\":\"" + Esc(SteamFriends.GetFriendPersonaName(friend)) + "\"}");
                }
                break;
            case "join":
                if (ulong.TryParse(Field(line, "lobby"), out var id))
                {
                    _isHost = false;
                    _lobbyEntered!.Set(SteamMatchmaking.JoinLobby(new CSteamID(id)));
                }
                break;
        }
    }

    // ------------------------------------------------------------------ lobby

    private static void OnLobbyCreated(LobbyCreated_t data, bool failure)
    {
        if (failure || data.m_eResult != EResult.k_EResultOK)
        {
            SendEvent("{\"evt\":\"error\",\"text\":\"could not create a Steam lobby\"}");
            return;
        }
        _lobby = new CSteamID(data.m_ulSteamIDLobby);
        SteamMatchmaking.SetLobbyData(_lobby, "fhcoop", "1");
        SteamFriends.SetRichPresence("connect", "+connect_lobby " + _lobby.m_SteamID);
        SendEvent("{\"evt\":\"lobby\",\"lobby\":\"" + _lobby.m_SteamID + "\"}");
    }

    private static void OnJoinRequested(GameLobbyJoinRequested_t data)
    {
        SendEvent("{\"evt\":\"joinRequest\",\"lobby\":\"" + data.m_steamIDLobby.m_SteamID + "\"}");
    }

    // "Join Game" in the friends list while our game runs: the connect string.
    private static void OnPresenceJoin(GameRichPresenceJoinRequested_t data)
    {
        var text = data.m_rgchConnect ?? "";
        var parts = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (parts[i] == "+connect_lobby" && ulong.TryParse(parts[i + 1], out var id))
            {
                SendEvent("{\"evt\":\"joinRequest\",\"lobby\":\"" + id + "\"}");
                return;
            }
        }
    }

    private static void OnLobbyEntered(LobbyEnter_t data, bool failure)
    {
        if (failure || data.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
        {
            SendEvent("{\"evt\":\"error\",\"text\":\"could not join the Steam lobby\"}");
            return;
        }
        _lobby = new CSteamID(data.m_ulSteamIDLobby);
        _hostId = SteamMatchmaking.GetLobbyOwner(_lobby);
        StartGuestListener();
        SendName();
        SendEvent("{\"evt\":\"ready\",\"port\":" + _guestPort + "}");
    }

    private static void OnSessionRequest(SteamNetworkingMessagesSessionRequest_t data)
    {
        var who = data.m_identityRemote.GetSteamID();
        bool inLobby = false;
        if (_lobby != CSteamID.Nil)
        {
            int n = SteamMatchmaking.GetNumLobbyMembers(_lobby);
            for (int i = 0; i < n; i++)
            {
                if (SteamMatchmaking.GetLobbyMemberByIndex(_lobby, i) == who) inLobby = true;
            }
        }
        if (inLobby)
        {
            var identity = data.m_identityRemote;
            SteamNetworkingMessages.AcceptSessionWithUser(ref identity);
        }
    }

    // The Steam link to a peer broke: same as that peer closing.
    private static void OnSessionFailed(SteamNetworkingMessagesSessionFailed_t data)
    {
        var who = data.m_info.m_identityRemote.GetSteamID().m_SteamID;
        Pending.Remove(who);
        if (_isHost) ClosePeer(who);
        else if (who == _hostId.m_SteamID) CloseGuestGame();
    }

    // ------------------------------------------------------------------ relay

    private static void StartGuestListener()
    {
        if (_guestListener != null) return;
        _guestListener = new TcpListener(IPAddress.Loopback, _guestPort);
        _guestListener.Start();
        new Thread(() =>
        {
            while (true)
            {
                TcpClient client;
                try { client = _guestListener.AcceptTcpClient(); } catch { return; }
                client.NoDelay = true;
                _guestGame = client;
                ulong host = _hostId.m_SteamID;
                Outgoing.Enqueue((host, KindOpen, Array.Empty<byte>())); // the host starts a fresh stream (and drops our old backlog)
                PumpLocal(client, host);
                if (_guestGame == client)
                {
                    _guestGame = null;
                    Outgoing.Enqueue((host, KindClose, Array.Empty<byte>()));
                }
                try { client.Dispose(); } catch { }
            }
        }) { IsBackground = true }.Start();
    }

    // Read a local TCP connection and queue its bytes for a Steam peer.
    private static void PumpLocal(TcpClient client, ulong peer)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            var stream = client.GetStream();
            int n;
            while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                var copy = new byte[n];
                Buffer.BlockCopy(buffer, 0, copy, 0, n);
                Outgoing.Enqueue((peer, KindData, copy));
            }
        }
        catch { }
    }

    // Host: the local connection that stands for this friend in the game.
    private static TcpClient? HostConnectionFor(ulong peer, bool fresh)
    {
        lock (PeersLock)
        {
            if (Peers.TryGetValue(peer, out var existing))
            {
                if (!fresh && existing.Connected) return existing;
                Peers.Remove(peer);
                try { existing.Dispose(); } catch { }
            }
        }
        try
        {
            var client = new TcpClient();
            client.NoDelay = true;
            client.Connect(IPAddress.Loopback, _hostPort);
            lock (PeersLock) Peers[peer] = client;
            new Thread(() =>
            {
                PumpLocal(client, peer);
                // the game dropped this friend: tell its helper
                bool current;
                lock (PeersLock)
                {
                    current = Peers.TryGetValue(peer, out var now) && now == client;
                    if (current) Peers.Remove(peer);
                }
                if (current) Outgoing.Enqueue((peer, KindClose, Array.Empty<byte>()));
                try { client.Dispose(); } catch { }
            }) { IsBackground = true }.Start();
            return client;
        }
        catch
        {
            return null;
        }
    }

    private static TcpClient? ExistingPeer(ulong peer)
    {
        lock (PeersLock)
        {
            return Peers.TryGetValue(peer, out var client) ? client : null;
        }
    }

    private static void ClosePeer(ulong peer)
    {
        TcpClient? client = null;
        lock (PeersLock)
        {
            if (Peers.TryGetValue(peer, out client)) Peers.Remove(peer);
        }
        try { client?.Dispose(); } catch { }
    }

    private static void CloseGuestGame()
    {
        var client = _guestGame;
        _guestGame = null;
        try { client?.Dispose(); } catch { }
    }

    // Frame and queue; chunks keep the order of one reliable channel.
    private static void QueueSteam(ulong peer, byte kind, byte[] data)
    {
        if (!Pending.TryGetValue(peer, out var queue))
        {
            queue = new Queue<byte[]>();
            Pending[peer] = queue;
        }
        if (kind == KindOpen) queue.Clear(); // a new stream: old unsent data belongs to the closed one
        int offset = 0;
        do
        {
            int size = Math.Min(ChunkSize, data.Length - offset);
            var frame = new byte[size + 1];
            frame[0] = kind;
            Buffer.BlockCopy(data, offset, frame, 1, size);
            queue.Enqueue(frame);
            offset += size;
        } while (offset < data.Length);
    }

    private static void FlushSteam()
    {
        foreach (var pair in Pending)
        {
            var queue = pair.Value;
            if (queue.Count == 0) continue;
            var identity = new SteamNetworkingIdentity();
            identity.SetSteamID(new CSteamID(pair.Key));
            while (queue.Count > 0)
            {
                var frame = queue.Peek();
                var handle = GCHandle.Alloc(frame, GCHandleType.Pinned);
                EResult result;
                try
                {
                    result = SteamNetworkingMessages.SendMessageToUser(ref identity,
                        handle.AddrOfPinnedObject(), (uint)frame.Length, SendReliable, Channel);
                }
                finally
                {
                    handle.Free();
                }
                if (result == EResult.k_EResultOK) queue.Dequeue();
                else if (result == EResult.k_EResultLimitExceeded) break; // buffer full: next tick
                else
                {
                    // no route to the peer: drop what is waiting
                    queue.Clear();
                    break;
                }
            }
        }
    }

    private static readonly IntPtr[] Inbox = new IntPtr[64];

    private static void ReceiveSteam()
    {
        int count = SteamNetworkingMessages.ReceiveMessagesOnChannel(Channel, Inbox, Inbox.Length);
        for (int i = 0; i < count; i++)
        {
            var msg = Marshal.PtrToStructure<SteamNetworkingMessage_t>(Inbox[i]);
            var bytes = new byte[msg.m_cbSize];
            Marshal.Copy(msg.m_pData, bytes, 0, msg.m_cbSize);
            ulong from = msg.m_identityPeer.GetSteamID().m_SteamID;
            SteamNetworkingMessage_t.Release(Inbox[i]);
            if (bytes.Length == 0) continue;
            byte kind = bytes[0];
            if (_isHost)
            {
                if (kind == KindClose)
                {
                    ClosePeer(from);
                    Pending.Remove(from); // the old stream's backlog must not reach a new one
                    continue;
                }
                if (kind == KindOpen)
                {
                    Pending.Remove(from);
                    HostConnectionFor(from, true);
                    continue;
                }
                // data only for a stream that was opened (late frames of a closed one are dropped)
                var target = ExistingPeer(from);
                if (target != null) Write(target, bytes);
            }
            else
            {
                if (from != _hostId.m_SteamID) continue;
                if (kind == KindClose)
                {
                    CloseGuestGame(); // the game sees the host leave
                    Pending.Remove(from);
                    continue;
                }
                var game = _guestGame;
                if (kind == KindData && game != null) Write(game, bytes);
            }
        }
    }

    private static void Write(TcpClient client, byte[] frame)
    {
        try { client.GetStream().Write(frame, 1, frame.Length - 1); } catch { }
    }
}
