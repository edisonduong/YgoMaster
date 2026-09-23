using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace YgoMaster
{
    static class MatchmakingTests
    {
        static int Main()
        {
            string original = Environment.CurrentDirectory;
            string temp = Path.Combine(Path.GetTempPath(), "YgoMatchmakingTests-" + Guid.NewGuid());
            Directory.CreateDirectory(Path.Combine(temp, "server"));
            try
            {
                Environment.CurrentDirectory = Path.Combine(temp, "server");
                // Only existence is tested: this suite never loads the duel engine.
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(Pvp.DllName)));
                File.WriteAllBytes(Pvp.DllName, new byte[0]);
                new GameServer().RunMatchmakingTests();
                Console.WriteLine("All matchmaking regression tests passed.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
            finally
            {
                Environment.CurrentDirectory = original;
                Directory.Delete(temp, true);
            }
        }
    }

    partial class GameServer
    {
        static void AssertMatch(bool condition, string message)
        {
            if (!condition) throw new Exception("FAIL: " + message);
            Console.WriteLine("PASS: " + message);
        }

        public void RunMatchmakingTests()
        {
            MultiplayerEnabled = true;
            DisableDeckValidation = true; // Queue must still enforce legality.
            MultiplayerSeed = -1;
            MultiplayerCoinFlipPlayerIndex = -1;
            DuelRoomTableMatchingTimeoutInSeconds = 30;
            DuelRoomMaxId = 999999;
            duelRoomIdRng = new URNG.LinearCongruentialGenerator(1, 0, DuelRoomMaxId);
            duelRoomSpectatorRoomIdRng = new URNG.LinearCongruentialGenerator(2, 0, DuelRoomMaxId);
            DeckInfo.DefaultRegulationId = 1;
            Regulation = new Dictionary<string, object> { { "1", new Dictionary<string, object> {
                { "available", new Dictionary<string, object> {
                    { "a0", new List<object> { 999 } }, { "a1", new List<object> { 1 } },
                    { "a2", new List<object> { 2 } }, { "a3", new List<object>() }
                } }
            } } };
            dataDirectory = Path.Combine(Environment.CurrentDirectory, "data");
            Directory.CreateDirectory(Path.Combine(dataDirectory, "CardData", "#"));
            File.WriteAllBytes(Path.Combine(dataDirectory, "CardData", "#", "CARD_IntID.bytes"), new byte[0]);
            var sockets = new List<Socket>();
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            uint nextCode = 1;
            Func<Player> newPlayer = () =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socket.Connect(listener.LocalEndpoint);
                sockets.Add(socket);
                Socket peer = listener.AcceptSocket();
                sockets.Add(peer);
                var player = new Player(nextCode++);
                player.Name = "Test " + player.Code;
                player.NetClient = new Net.NetClient(socket, null);
                var deck = new DeckInfo { Id = 1, RegulationId = 1 };
                for (int id = 1; id <= 40; id++)
                {
                    deck.MainDeckCards.Add(id);
                    player.Cards.Add(id, 6, PlayerCardKind.Dismantle, CardStyleRarity.Normal);
                }
                player.Decks.Add(1, deck);
                player.Duel.SetDeckId(GameMode.Rank, 1);
                return player;
            };
            Func<Player, string, string, GameServerWebRequest> call = (p, action, version) =>
            {
                var req = new GameServerWebRequest { Player = p, ClientVersion = version, ActName = "Matchmaking." + action,
                    ActParams = new Dictionary<string, object>(), Response = new Dictionary<string, object>() };
                Act_Matchmaking(req);
                return req;
            };
            Func<GameServerWebRequest, string> state = r => Utils.GetValue<string>(Utils.GetDictionary(r.Response, "Matchmaking"), "state");
            try
            {
                Player a = newPlayer(), b = newPlayer(), c = newPlayer();
                DeckInfo deck = a.Duel.GetDeck(GameMode.Rank);
                AssertMatch(deck.IsValid(a, 1, Regulation), "legal owned deck accepted");
                deck.MainDeckCards.RemoveAll(40);
                AssertMatch(!deck.IsValid(a, 1, Regulation), "39 main cards rejected");
                deck.MainDeckCards.Add(40);
                for (int id = 41; id <= 61; id++)
                {
                    a.Cards.Add(id, 1, PlayerCardKind.Dismantle, CardStyleRarity.Normal);
                    deck.MainDeckCards.Add(id);
                }
                AssertMatch(!deck.IsValid(a, 1, Regulation), "61 main cards rejected");
                deck.MainDeckCards.RemoveAll(61);
                AssertMatch(deck.IsValid(a, 1, Regulation), "60 main cards accepted");
                for (int id = 41; id <= 60; id++) deck.MainDeckCards.RemoveAll(id);
                for (int id = 41; id <= 56; id++) deck.ExtraDeckCards.Add(id);
                AssertMatch(!deck.IsValid(a, 1, Regulation), "16 extra cards rejected");
                deck.ExtraDeckCards.Clear();
                for (int id = 41; id <= 56; id++) deck.SideDeckCards.Add(id);
                AssertMatch(!deck.IsValid(a, 1, Regulation), "16 side cards rejected");
                deck.SideDeckCards.Clear();
                deck.SideDeckCards.Add(3, CardStyleRarity.Royal);
                AssertMatch(!deck.IsValid(a, 1, Regulation), "ownership checked for exact card finish");
                deck.SideDeckCards.Clear();
                deck.SideDeckCards.Add(999); a.Cards.Add(999, 1, PlayerCardKind.Dismantle, CardStyleRarity.Normal);
                AssertMatch(!deck.IsValid(a, 1, Regulation), "banned side-deck card rejected");
                deck.SideDeckCards.Clear(); deck.SideDeckCards.Add(1);
                AssertMatch(!deck.IsValid(a, 1, Regulation), "limited copies counted across main and side");
                deck.SideDeckCards.Clear();
                for (int i = 0; i < 3; i++) deck.SideDeckCards.Add(3);
                AssertMatch(!deck.IsValid(a, 1, Regulation), "unlisted card still limited to three copies");
                deck.SideDeckCards.Clear();
                a.Duel.SetDeckId(GameMode.Rank, 0);
                AssertMatch(state(call(a, "join", "v1")) == "idle" && matchmakingQueue.Count == 0, "missing selected standard deck cannot queue");
                a.Duel.SetDeckId(GameMode.Rank, 1);
                a.Cards.SetCount(1, 0, PlayerCardKind.Dismantle, CardStyleRarity.Normal);
                AssertMatch(state(call(a, "join", "v1")) == "idle", "ownership enforced despite DisableDeckValidation");
                a.Cards.SetCount(1, 6, PlayerCardKind.Dismantle, CardStyleRarity.Normal);
                AssertMatch(state(call(a, "join", "v1")) == "searching", "valid player enters queue");
                DateTime joined = matchmakingQueue[0].Joined;
                Parallel.For(0, 12, i => call(a, "join", "v1"));
                AssertMatch(matchmakingQueue.Count == 1 && matchmakingQueue[0].Joined == joined, "concurrent repeat joins are idempotent and preserve FIFO");
                call(b, "join", "v2");
                AssertMatch(matchmakingQueue.Count == 2, "incompatible client versions do not pair");
                call(c, "join", "v1");
                DuelRoom room = a.DuelRoom;
                AssertMatch(room != null && room == c.DuelRoom && b.DuelRoom == null, "earliest compatible players pair");
                AssertMatch(room.Tables[0].IsMatched && room.Tables[0].State == DuelRoomTableState.Matched &&
                    room.LifePoints == 1 && room.DuelTime == 1 && !string.IsNullOrEmpty(room.Tables[0].TableTicket) &&
                    !string.IsNullOrEmpty(room.Tables[0].SecretKeyForPvpServer), "room startup initializes tickets, secret, ready state and encoded rules");
                GameServerWebRequest start = call(a, "poll", "v1");
                AssertMatch(state(start) == "matched", "first waiter learns match through polling");
                Dictionary<string, object> loading = Utils.GetDictionary(start.Response, "Duel");
                AssertMatch(loading != null && Utils.GetValue<int>(loading, "GameMode") == (int)GameMode.Room &&
                    loading.ContainsKey("dialog_intro") && loading.ContainsKey("ex_sleeve") &&
                    loading.ContainsKey("player") && !start.Response.ContainsKey("Room"),
                    "match response supplies duel loading data without sending a lobby");
                AssertMatch(state(call(c, "join", "v1")) == "matched" && duelRoomsByRoomId.Count == 1, "retry after pairing cannot create another room");
                var reentry = new GameServerWebRequest { Player = a, Response = new Dictionary<string, object>(),
                    ActParams = new Dictionary<string, object> { { "id", room.Id }, { "is_specter", false } } };
                Act_RoomEntry(reentry);
                AssertMatch(reentry.ResultCode == 0 && room.Tables[0].IsMatched, "native room re-entry preserves matched seats");
                Act_RoomTableArrive(reentry);
                AssertMatch(reentry.ResultCode == 0 && room.Tables[0].IsMatched, "repeated table arrival preserves ready state");
                reentry.Player = b;
                Act_RoomEntry(reentry);
                AssertMatch(reentry.ResultCode != 0 && b.DuelRoom == null, "private match refuses outsiders");
                var matching = new GameServerWebRequest { Player = a, Response = new Dictionary<string, object>() };
                Act_DuelMatching(matching);
                AssertMatch(matching.ResultCode == 0 && matching.Response.ContainsKey("Duel"), "existing Duel.matching emits duel transition payload");
                a.Cards.SetCount(1, 0, PlayerCardKind.Dismantle, CardStyleRarity.Normal);
                matching.Response.Clear(); matching.ResultCode = 0;
                Act_DuelMatching(matching);
                AssertMatch(matching.ResultCode == (int)ResultCodes.PvPCode.INVALID_DECK, "deck legality rechecked at duel transition");
                a.Cards.SetCount(1, 6, PlayerCardKind.Dismantle, CardStyleRarity.Normal);
                call(a, "cancel", "v1");
                AssertMatch(a.DuelRoom == null && c.DuelRoom == null && duelRoomsByRoomId.Count == 0 && a.Duel.GetDeckId(GameMode.Room) == 0,
                    "cancel after pairing releases both players and restores room deck selection");
                AssertMatch(state(call(c, "poll", "v1")) == "idle", "opponent cancellation does not silently requeue");
                call(b, "cancel", "v2");
                call(a, "join", "v1");
                matchmakingQueue[0].Heartbeat = DateTime.UtcNow.AddSeconds(-46);
                UpdateMatchmaking();
                AssertMatch(matchmakingQueue.Count == 0 && state(call(a, "poll", "v1")) == "idle", "expired session removed without new joins or implicit rejoin");
                call(a, "join", "v1");
                matchmakingQueue[0].Joined = DateTime.UtcNow.AddMinutes(-10);
                call(a, "poll", "v1");
                AssertMatch(matchmakingQueue.Count == 1, "heartbeat keeps long searches alive");
                a.Duel.SetDeckId(GameMode.Rank, 0);
                UpdateMatchmaking();
                AssertMatch(matchmakingQueue.Count == 0, "changed deck selection removed before matching");
                a.Duel.SetDeckId(GameMode.Rank, 1);
                call(a, "join", "v1");
                a.NetClient.Close();
                UpdateMatchmaking();
                AssertMatch(matchmakingQueue.Count == 0, "disconnect removes queued player");
                call(b, "join", "v1"); call(c, "join", "v1");
                room = b.DuelRoom; room.TimeCreated = DateTime.UtcNow.AddMinutes(-2);
                UpdateMatchmaking();
                AssertMatch(b.DuelRoom == null && c.DuelRoom == null, "abandoned matched room is reclaimed");
                call(b, "join", "v1");
                b.Cards.SetCount(1, 0, PlayerCardKind.Dismantle, CardStyleRarity.Normal);
                call(c, "join", "v1");
                AssertMatch(matchmakingQueue.Count == 1 && matchmakingQueue[0].Player == c, "invalid FIFO head removed without blocking valid waiters");
                call(c, "cancel", "v1");
                for (int i = 0; i < 10; i++)
                {
                    Player left = newPlayer(), right = newPlayer();
                    call(left, "join", "v1");
                    Parallel.Invoke(() => call(right, "join", "v1"), () => call(left, "cancel", "v1"));
                    AssertMatch(left.DuelRoom == null && right.DuelRoom == null && !matchmakingQueue.Any(x => x.Player == left),
                        "cancel/pair race releases cancelling player " + i);
                    call(right, "cancel", "v1");
                }
                Player oldRule = newPlayer(), newRule = newPlayer(), sameRule = newPlayer();
                call(oldRule, "join", "v1");
                Regulation["2"] = Regulation["1"];
                DeckInfo.DefaultRegulationId = 2;
                call(newRule, "join", "v1");
                AssertMatch(matchmakingQueue.Count == 2, "different admitted regulations do not pair");
                call(sameRule, "join", "v1");
                room = newRule.DuelRoom;
                AssertMatch(room != null && room.Rule == 2 && sameRule.DuelRoom == room && oldRule.DuelRoom == null,
                    "matching uses the admitted regulation for the new room");
                DuelRoomTable choiceTable = room.Tables[0];
                AssertMatch(choiceTable.FirstPlayer == -1 && choiceTable.State == DuelRoomTableState.Matched,
                    "match waits for the native coin-flip choice instead of auto-starting");
                for (int winnerIndex = 0; winnerIndex < 2; winnerIndex++)
                for (int choice = 0; choice < 2; choice++)
                {
                    choiceTable.State = DuelRoomTableState.Matched;
                    choiceTable.FirstPlayer = -1;
                    choiceTable.CoinFlipPlayerIndex = winnerIndex;
                    Player winner = choiceTable.Entries[winnerIndex].Player;
                    Player loser = choiceTable.Entries[1 - winnerIndex].Player;
                    var selection = new GameServerWebRequest { Player = loser, Response = new Dictionary<string, object>(),
                        ActParams = new Dictionary<string, object> { { "select", choice } } };
                    Act_DuelStartSelecting(selection);
                    AssertMatch(selection.ResultCode == (int)ResultCodes.PvPCode.INVALID_PARAM && choiceTable.FirstPlayer == -1,
                        "coin-flip loser cannot choose turn order");
                    selection.Player = winner; selection.ResultCode = 0;
                    Act_DuelStartSelecting(selection);
                    int expectedFirst = choice == 0 ? winnerIndex : 1 - winnerIndex;
                    AssertMatch(selection.ResultCode == 0 && choiceTable.FirstPlayer == expectedFirst,
                        "winner " + winnerIndex + " can choose " + (choice == 0 ? "first" : "second"));
                    Act_DuelStartSelecting(selection);
                    AssertMatch(selection.ResultCode == 0 && choiceTable.FirstPlayer == expectedFirst,
                        "repeated choice is idempotent");
                    selection.ActParams["select"] = 1 - choice;
                    Act_DuelStartSelecting(selection);
                    AssertMatch(selection.ResultCode != 0 && choiceTable.FirstPlayer == expectedFirst,
                        "committed turn choice cannot be reversed");
                }
                DateTime matchedAt = room.Tables[0].MatchedTime;
                GameServerWebRequest latePoll = call(sameRule, "poll", "v1");
                AssertMatch(latePoll.ResultCode == 0 && latePoll.Response.ContainsKey("Duel") &&
                    room.Tables[0].State == DuelRoomTableState.Dueling && room.Tables[0].MatchedTime == matchedAt,
                    "late waiter gets loading data even after the first client starts, without resetting duel state");
                room.Tables[0].Entries[0].HasBeginDuel = true;
                room.Tables[0].Entries[1].HasBeginDuel = true;
                room.TimeCreated = DateTime.UtcNow.AddHours(-1);
                UpdateMatchmaking();
                call(newRule, "cancel", "v1");
                AssertMatch(newRule.DuelRoom == room && sameRule.DuelRoom == room,
                    "queue timeout and search cancellation never disband an active duel");
                DisbandRoom(room);
                call(oldRule, "cancel", "v1");
            }
            finally { listener.Stop(); foreach (Socket socket in sockets) socket.Dispose(); }
        }
    }
}
