using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using YgoMaster.Net;
using YgoMaster.Net.Message;

namespace YgoMaster
{
    partial class GameServer
    {
        sealed class MatchmakingEntry
        {
            public Player Player;
            public int DeckId;
            public int RegulationId;
            public string ClientVersion;
            public DateTime Joined;
            public DateTime Heartbeat;
        }

        // List order is the admission order, unaffected by repeat joins or heartbeats.
        readonly object matchmakingLock = new object();
        readonly List<MatchmakingEntry> matchmakingQueue = new List<MatchmakingEntry>();
        static readonly TimeSpan MatchmakingTimeout = TimeSpan.FromSeconds(45);

        string MatchmakingValidation(Player player, int regulationId)
        {
            if (!MultiplayerEnabled) return "Matchmaking requires multiplayer mode.";
            if (player.NetClient == null || !player.NetClient.IsConnected) return "Connect to the session server before searching.";
            if (regulationId <= 0 || Regulation == null || Utils.GetDictionary(Regulation, regulationId.ToString()) == null)
                return "The standard regulation is unavailable.";
            DeckInfo deck = player.Duel.GetDeck(GameMode.Rank);
            if (deck == null) return "Select a standard deck before searching.";
            // Public matchmaking always enforces legality, even on sandbox servers.
            if (!deck.IsValid(player, regulationId, Regulation))
                return "Invalid deck: use 40-60 main cards, at most 15 extra and 15 side cards, owned copies, and legal card limits.";
            if (!File.Exists(Pvp.DllName) || !File.Exists(Path.Combine(dataDirectory, "CardData", "#", "CARD_IntID.bytes")))
                return "The server is missing required PvP files.";
            return null;
        }

        void UpdateMatchmaking()
        {
            lock (matchmakingLock)
            {
                DateTime now = DateTime.UtcNow;
                matchmakingQueue.RemoveAll(x => now - x.Heartbeat >= MatchmakingTimeout ||
                    x.Player.DuelRoom != null || x.Player.Duel.GetDeckId(GameMode.Rank) != x.DeckId ||
                    MatchmakingValidation(x.Player, x.RegulationId) != null);

                // Reclaim abandoned startup rooms, but never interrupt an active duel.
                foreach (DuelRoom room in GetDuelRoomsByRoomId().Values)
                {
                    if (!room.IsMatchmaking) continue;
                    DuelRoomTable table = room.Tables[0];
                    bool completed = table.Rewards.Player1Rewards != null && table.Rewards.Player2Rewards != null;
                    bool abandonedStartup = (table.State != DuelRoomTableState.Dueling || !table.HasBeginDuel) &&
                        now - room.TimeCreated >= TimeSpan.FromSeconds(Math.Max(60, DuelRoomTableMatchingTimeoutInSeconds));
                    if (completed || abandonedStartup) DisbandRoom(room);
                }
            }
        }

        void Act_Matchmaking(GameServerWebRequest request)
        {
            lock (matchmakingLock)
            {
                UpdateMatchmaking();
                Player player = request.Player;
                Dictionary<string, object> data = request.GetOrCreateDictionary("Matchmaking");
                data["state"] = "idle";
                data["position"] = 0;
                data["wait_seconds"] = 0;
                data["estimated_wait_seconds"] = null; // No fabricated estimate without population history.
                data["error"] = "";
                data["valid"] = false;
                DuelRoom room = player.DuelRoom;
                if (request.ActName == "Matchmaking.cancel")
                {
                    matchmakingQueue.RemoveAll(x => x.Player == player);
                    if (room != null && room.IsMatchmaking && room.Tables[0].State != DuelRoomTableState.Dueling)
                    {
                        DisbandRoom(room);
                        request.Remove("Room.room_info", "Duel");
                    }
                    return;
                }
                if (room != null)
                {
                    if (room.IsMatchmaking)
                    {
                        data["state"] = "matched";
                        WriteMatchmakingStart(request);
                    }
                    else data["error"] = "Leave your current duel room before searching.";
                    return;
                }
                int regulationId = DeckInfo.DefaultRegulationId;
                string error = MatchmakingValidation(player, regulationId);
                data["valid"] = error == null;
                if (error != null)
                {
                    matchmakingQueue.RemoveAll(x => x.Player == player);
                    data["error"] = error;
                    return;
                }
                if (request.ActName == "Matchmaking.check") return;

                MatchmakingEntry entry = matchmakingQueue.Find(x => x.Player == player);
                if (entry == null && request.ActName == "Matchmaking.join")
                {
                    ClearSpectatingDuel(player);
                    entry = new MatchmakingEntry { Player = player, DeckId = player.Duel.GetDeckId(GameMode.Rank),
                        RegulationId = regulationId, ClientVersion = request.ClientVersion,
                        Joined = DateTime.UtcNow, Heartbeat = DateTime.UtcNow };
                    matchmakingQueue.Add(entry);
                }
                if (entry == null)
                {
                    data["error"] = "Search expired or your deck changed. Please search again.";
                    return;
                }
                entry.Heartbeat = DateTime.UtcNow;
                for (int i = 0; i < matchmakingQueue.Count; i++)
                {
                    MatchmakingEntry first = matchmakingQueue[i];
                    MatchmakingEntry second = matchmakingQueue.Skip(i + 1).FirstOrDefault(x =>
                        x.RegulationId == first.RegulationId && x.ClientVersion == first.ClientVersion);
                    if (second == null) continue;
                    try
                    {
                        StartMatchmakingRoom(first, second);
                    }
                    catch (Exception ex)
                    {
                        Utils.LogWarning("[Matchmaking] " + ex.Message);
                        matchmakingQueue.Remove(first);
                        matchmakingQueue.Remove(second);
                        data["error"] = "Unable to start the match. Please search again.";
                        return;
                    }
                    matchmakingQueue.Remove(second);
                    matchmakingQueue.Remove(first);
                    i--;
                }
                if (player.DuelRoom != null)
                {
                    data["state"] = "matched";
                    WriteMatchmakingStart(request);
                }
                else
                {
                    data["state"] = "searching";
                    data["position"] = matchmakingQueue.TakeWhile(x => x != entry).Count(x =>
                        x.RegulationId == entry.RegulationId && x.ClientVersion == entry.ClientVersion) + 1;
                    data["wait_seconds"] = (int)(DateTime.UtcNow - entry.Joined).TotalSeconds;
                }
            }
        }

        void WriteMatchmakingStart(GameServerWebRequest request)
        {
            // Supply the loading screen's player/accessory data, without opening a lobby.
            Act_DuelMatching(request);
            if (request.ResultCode != 0) return;
            Dictionary<string, object> duel = request.GetOrCreateDictionary("Duel");
            foreach (KeyValuePair<string, object> item in new DuelSettings().ToDictionaryForSoloStart())
                if (!duel.ContainsKey(item.Key)) duel[item.Key] = item.Value;
            duel["GameMode"] = (int)GameMode.Room;
            request.Remove("Room.room_info");
        }

        bool SelectMatchmakingTurn(DuelRoom room, DuelRoomTable table, Player player, int select)
        {
            lock (table.Entries)
            {
                if (!room.IsMatchmaking || room.Disbanded || !table.IsMatched) return false;
                if (select < 0 || select > 1 || table.Entries[table.CoinFlipPlayerIndex].Player != player) return false;
                int firstPlayer = player == table.Player1 ? select : 1 - select;
                // Retries may acknowledge the same choice, never reverse it.
                if (table.State == DuelRoomTableState.Dueling) return table.FirstPlayer == firstPlayer;
                if (table.State != DuelRoomTableState.Matched) return false;
                return table.InitDuel(firstPlayer);
            }
        }

        void StartMatchmakingRoom(MatchmakingEntry first, MatchmakingEntry second)
        {
            GameServerWebRequest setup = new GameServerWebRequest { Player = first.Player, ClientVersion = first.ClientVersion,
                Response = new Dictionary<string, object>(), ActParams = new Dictionary<string, object> {
                    { "room_settings", new Dictionary<string, object> {
                        { "member_max", 2 }, { "battle_rule", first.RegulationId }, { "battle_lp", 1 }, { "battle_time", 1 }
                    } }
                } };
            Act_RoomCreate(setup);
            DuelRoom room = first.Player.DuelRoom;
            if (room == null || setup.ResultCode != 0) throw new InvalidOperationException("Unable to create matchmaking room");
            room.IsMatchmaking = true;
            try
            {
                lock (room.MembersLocker)
                {
                    room.Members.Add(second.Player, new DuelRoomRecord());
                    second.Player.DuelRoom = room;
                }
                foreach (MatchmakingEntry entry in new[] { first, second })
                {
                    room.PreviousRoomDeckIds[entry.Player] = entry.Player.Duel.GetDeckId(GameMode.Room);
                    entry.Player.Duel.SetDeckId(GameMode.Room, entry.DeckId);
                    room.Tables[0].AddPlayer(entry.Player);
                }
                foreach (MatchmakingEntry entry in new[] { first, second })
                {
                    setup.Player = entry.Player;
                    setup.ActParams = new Dictionary<string, object> { { "isBattleReady", true } };
                    Act_RoomBattleReady(setup);
                    if (setup.ResultCode != 0) throw new InvalidOperationException("Unable to ready matchmaking players");
                }
                room.Tables[0].MatchedTime = DateTime.UtcNow;
                room.Tables[0].State = DuelRoomTableState.Matched;
            }
            catch
            {
                DisbandRoom(room);
                throw;
            }
        }

        void Act_RoomGetList(GameServerWebRequest request)
        {
            if (!MultiplayerEnabled)
            {
                return;
            }

            ClearSpectatingDuel(request.Player);

            Dictionary<string, object> searchOptions = Utils.GetDictionary(request.ActParams, "search_options");
            if (searchOptions == null)
            {
                return;
            }

            bool isSpectator = Utils.GetValue<bool>(searchOptions, "is_spectator");

            Dictionary<string, object> allRoomData = request.GetOrCreateDictionary("Room");
            List<object> roomList = new List<object>();
            allRoomData["room_list"] = roomList;

            Dictionary<uint, DuelRoom> duelRooms = GetDuelRoomsByRoomId();
            foreach (DuelRoom duelRoom in duelRooms.Values)
            {
                if (duelRoom.Disbanded)
                {
                    continue;
                }
                if (isSpectator && (!duelRoom.AllowSpectators || !duelRoom.ShowInSpectatorList))
                {
                    continue;
                }
                if (!isSpectator && !duelRoom.ShowInRoomList)
                {
                    continue;
                }

                Dictionary<string, object> roomData = new Dictionary<string, object>();
                Dictionary<string, object> roomMasterData = Utils.GetOrCreateDictionary(roomData, "room_master");
                roomMasterData["name"] = duelRoom.Owner.Name;
                roomMasterData["icon_id"] = duelRoom.Owner.IconId;
                roomMasterData["icon_frame_id"] = duelRoom.Owner.IconFrameId;
                roomData["room_id"] = duelRoom.Id;
                lock (duelRoom.Members)
                {
                    roomData["room_member"] = duelRoom.Members.Count;
                }
                roomData["test"] = "get";//?
                roomData["hash_key"] = duelRoom.HashKey;
                roomData["create_time"] = Utils.FormatDateTime(duelRoom.TimeCreated);
                roomData["limit_time"] = Utils.FormatDateTime(duelRoom.TimeExpire);
                roomData["room_comment"] = duelRoom.Comment;
                roomData["member_max"] = duelRoom.MemberLimit;
                roomData["is_public"] = duelRoom.ShowInRoomList;
                roomData["is_specter"] = duelRoom.ShowInSpectatorList;
                roomData["is_spectral"] = duelRoom.AllowSpectators;
                roomData["specter_id"] = duelRoom.SpectatorRoomId;
                roomData["is_replay"] = duelRoom.ViewReplays;
                roomData["rule"] = duelRoom.Rule;
                roomData["lp"] = duelRoom.LifePoints;
                roomData["time"] = duelRoom.DuelTime;
                roomData["xPlatformLimit"] = 0;
                roomData["client_version"] = duelRoom.ClientVersion;
                roomData["update_time"] = Utils.GetEpochTime(duelRoom.TimeUpdated);

                roomList.Add(roomData);
                if (roomList.Count >= DuelRoomMaxSearchResults)
                {
                    break;
                }
            }
        }

        void Act_RoomEntry(GameServerWebRequest request)
        {
            if (!MultiplayerEnabled)
            {
                return;
            }

            ClearSpectatingDuel(request.Player);

            request.Remove("Room.room_info");

            uint roomId = Utils.GetValue<uint>(request.ActParams, "id");
            bool isSpectator = Utils.GetValue<bool>(request.ActParams, "is_specter");
            
            if (roomId == 0)
            {
                request.ResultCode = (int)ResultCodes.RoomCode.ERR_INVALID_ROOM;
                return;
            }

            DuelRoom duelRoom;

            lock (duelRoomsLocker)
            {
                Dictionary<uint, DuelRoom> duelRoomsDict = isSpectator ? duelRoomsBySpectatorRoomId : duelRoomsByRoomId;

                if (!duelRoomsDict.TryGetValue(roomId, out duelRoom) || duelRoom.Disbanded)
                {
                    request.ResultCode = (int)ResultCodes.RoomCode.ERR_ENTRY_FAILED;
                    return;
                }

                DuelRoom currentDuelRoom = request.Player.DuelRoom;
                if (currentDuelRoom != null && currentDuelRoom.Disbanded)
                {
                    request.Player.DuelRoom = null;
                }
                else if (currentDuelRoom != null && currentDuelRoom != duelRoom)
                {
                    request.ResultCode = (int)ResultCodes.RoomCode.ERR_ALREADY_ENTRY_ROOM;
                    return;
                }
            }

            lock (duelRoom.MembersLocker)
            {
                if (duelRoom.IsMatchmaking)
                {
                    // Reopening the native room screen must not unseat a matched player.
                    if (isSpectator || !duelRoom.Members.ContainsKey(request.Player))
                        request.ResultCode = (int)ResultCodes.RoomCode.ERR_ENTRY_FAILED;
                    else
                        WriteRoomInfo(request, duelRoom);
                    return;
                }
                if ((isSpectator && duelRoom.Members.ContainsKey(request.Player)) ||
                    (!isSpectator && duelRoom.Spectators.Contains(request.Player)))
                {
                    request.ResultCode = (int)ResultCodes.RoomCode.ERR_ALREADY_ENTRY_ROOM;
                    return;
                }

                if ((!isSpectator && !duelRoom.Members.ContainsKey(request.Player)) ||
                    (isSpectator && !duelRoom.Spectators.Contains(request.Player)))
                {
                    if (isSpectator)
                    {
                        if (duelRoom.Spectators.Count >= DuelRoomMaxSpectators)
                        {
                            request.ResultCode = (int)ResultCodes.RoomCode.ERR_ROOM_MEMBER_MAX;
                            return;
                        }
                        duelRoom.Spectators.Add(request.Player);
                        duelRoom.TimeUpdated = DateTime.UtcNow;
                        request.Player.DuelRoom = duelRoom;
                    }
                    else
                    {
                        if (duelRoom.Members.Count >= duelRoom.MemberLimit)
                        {
                            request.ResultCode = (int)ResultCodes.RoomCode.ERR_ROOM_MEMBER_MAX;
                            return;
                        }
                        duelRoom.Members.Add(request.Player, new DuelRoomRecord());
                        duelRoom.TimeUpdated = DateTime.UtcNow;
                        request.Player.DuelRoom = duelRoom;
                    }
                }
            }

            if (!isSpectator)
            {
                lock (request.Player.DuelRoomInvitesByFriendId)
                {
                    foreach (KeyValuePair<uint, uint> invite in GetDuelRoomInvites(request.Player))
                    {
                        if (invite.Value == duelRoom.Id)
                        {
                            request.Player.DuelRoomInvitesByFriendId.Remove(invite.Key);
                        }
                    }
                }
            }

            DuelRoomTable table = duelRoom.GetTable(request.Player);
            if (table != null)
            {
                table.RemovePlayer(request.Player);
            }

            WriteRoomInfo(request, duelRoom);
        }

        void Act_RoomExit(GameServerWebRequest request)
        {
            if (!MultiplayerEnabled)
            {
                return;
            }

            ClearSpectatingDuel(request.Player);

            DuelRoom currentDuelRoom = request.Player.DuelRoom;
            if (currentDuelRoom != null)
            {
                if (currentDuelRoom.Owner == request.Player || currentDuelRoom.IsMatchmaking)
                {
                    DisbandRoom(currentDuelRoom);
                }
                else
                {
                    currentDuelRoom.RemovePlayer(request.Player);
                }
                currentDuelRoom.TimeUpdated = DateTime.UtcNow;

                foreach (KeyValuePair<Player, FriendState> friend in GetFriendsObj(request.Player))
                {
                    lock (friend.Key.DuelRoomInvitesByFriendId)
                    {
                        friend.Key.DuelRoomInvitesByFriendId.Remove(request.Player.Code);
                    }
                }

                request.Remove("Room.room_info");
            }
        }

        void Act_RoomCreate(GameServerWebRequest request)
        {
            if (!MultiplayerEnabled)
            {
                return;
            }

            ClearSpectatingDuel(request.Player);

            Dictionary<string, object> roomSettings = Utils.GetDictionary(request.ActParams, "room_settings");

            if (roomSettings == null)
            {
                request.ResultCode = (int)ResultCodes.RoomCode.ERR_ROOM_CREATE_FAILED;
                return;
            }

            if (request.Player.DuelRoom != null)
            {
                Utils.LogWarning("Player " + request.Player + " is trying to create a duel room but is already in a duel room");
                request.ResultCode = (int)ResultCodes.RoomCode.ERR_ALREADY_ENTRY_ROOM;
                return;
            }

            DuelRoom duelRoom = new DuelRoom();
            duelRoom.Id = GetNextDuelRoomId(duelRoomsByRoomId, duelRoomIdRng);
            duelRoom.SpectatorRoomId = GetNextDuelRoomId(duelRoomsBySpectatorRoomId, duelRoomSpectatorRoomIdRng);
            duelRoom.Owner = request.Player;
            duelRoom.Comment = Utils.GetValue<int>(roomSettings, "room_comment");
            duelRoom.MemberLimit = Utils.GetValue<int>(roomSettings, "member_max");
            duelRoom.Rule = Utils.GetValue<int>(roomSettings, "battle_rule");
            duelRoom.DuelTime = Utils.GetValue<int>(roomSettings, "battle_time");
            duelRoom.LifePoints = Utils.GetValue<int>(roomSettings, "battle_lp");
            duelRoom.ShowInRoomList = Utils.GetValue<bool>(roomSettings, "is_public");
            duelRoom.ShowInSpectatorList = Utils.GetValue<bool>(roomSettings, "is_specter");
            duelRoom.AllowSpectators = Utils.GetValue<bool>(roomSettings, "is_spectral");
            duelRoom.ViewReplays = Utils.GetValue<bool>(roomSettings, "is_replay");
            duelRoom.TimeCreated = DateTime.UtcNow;
            duelRoom.TimeUpdated = DateTime.UtcNow;
            duelRoom.TimeExpire = DateTime.UtcNow + TimeSpan.FromDays(1);
            duelRoom.ClientVersion = request.ClientVersion;
            using (MD5 md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(duelRoom.Id + " - " + duelRoom.TimeCreated));
                duelRoom.HashKey = BitConverter.ToString(hash).Replace("-", string.Empty);
            }

            lock (duelRoom.MembersLocker)
            {
                duelRoom.Members.Add(request.Player, new DuelRoomRecord());
                request.Player.DuelRoom = duelRoom;
            }

            lock (duelRoomsLocker)
            {
                duelRoomsByRoomId[duelRoom.Id] = duelRoom;
                duelRoomsBySpectatorRoomId[duelRoom.SpectatorRoomId] = duelRoom;
            }

            duelRoom.InitTables();

            WriteRoomInfo(request, duelRoom);
        }

        void Act_RoomTablePolling(GameServerWebRequest request)
        {
            if (!MultiplayerEnabled)
            {
                return;
            }

            // Removed thie call as polling requests for when spectating can sometimes overlap with the duel begin?
            //request.Player.ClearSpectatingDuel();

            DuelRoom duelRoom = request.Player.DuelRoom;
            if (duelRoom == null)
            {
                request.ResultCode = (int)ResultCodes.RoomCode.ERR_INVALID_ROOM;
            }
            else
            {
                DuelRoomTable table = duelRoom.GetTable(request.Player);
                if (table != null && table.State == DuelRoomTableState.Dueling && table.HasBeginDuel)
                {
                    duelRoom.ResetTableStateIfMatchingOrDueling(request.Player);
                }

                WriteRoomInfo(request, duelRoom);
            }
            request.Remove("Room.room_info");
        }

        void Act_RoomTableArrive(GameServerWebRequest request)
        {
            if (!MultiplayerEnabled)
            {
                return;
            }

            ClearSpectatingDuel(request.Player);

            DuelRoom duelRoom = request.Player.DuelRoom;
            if (duelRoom != null && duelRoom.IsMatchmaking && duelRoom.GetTable(request.Player) != null)
            {
                // The native screen may repeat arrival while opening the allocated table.
                WriteRoomInfo(request, duelRoom);
                return;
            }
            if (duelRoom == null)
            {
                request.ResultCode = (int)ResultCodes.RoomCode.ERR_INVALID_ROOM;
            }
            else
            {
                duelRoom.ResetTableStateIfMatchingOrDueling(request.Player);

                int tableIndex = Utils.GetValue<int>(request.ActParams, "table_no");
                if (tableIndex >= 0 && tableIndex < duelRoom.Tables.Length)
                {
                    DeckInfo deck = request.Player.Duel.GetDeck(GameMode.Room);
                    if (deck == null || (!DisableDeckValidation && !deck.IsValid(request.Player, duelRoom.Rule, Regulation)))
                    {
                        request.ResultCode = (int)ResultCodes.RoomCode.ERR_DECK_REG;
                    }
                    else if (duelRoom.Tables[tableIndex].AddPlayer(request.Player))
                    {
                        duelRoom.TimeUpdated = DateTime.UtcNow;
                        WriteRoomInfo(request, duelRoom);
                        request.Remove("Room.room_info");
                    }
                    else
                    {
                        request.ResultCode = (int)ResultCodes.RoomCode.ERR_NO_VACANT_TABLES;
                    }
                }
                else
                {
                    request.ResultCode = (int)ResultCodes.RoomCode.ERR_NO_VACANT_TABLES;
                }
            }
        }

        void Act_RoomTableLeave(GameServerWebRequest request)
        {
            if (!MultiplayerEnabled)
            {
                return;
            }

            ClearSpectatingDuel(request.Player);

            DuelRoom duelRoom = request.Player.DuelRoom;
            if (duelRoom == null)
            {
                request.ResultCode = (int)ResultCodes.RoomCode.ERR_INVALID_ROOM;
            }
            else
            {
                duelRoom.ResetTableStateIfMatchingOrDueling(request.Player);

                DuelRoomTable table = duelRoom.GetTable(request.Player);
                if (table != null)
                {
                    table.RemovePlayer(request.Player);
                    duelRoom.TimeUpdated = DateTime.UtcNow;
                }
                WriteRoomInfo(request, duelRoom);
            }
            request.Remove("Room.room_info");
        }

        void Act_RoomSetUserComment(GameServerWebRequest request)
        {
            if (!MultiplayerEnabled)
            {
                return;
            }

            ClearSpectatingDuel(request.Player);

            int comment = Utils.GetValue<int>(request.ActParams, "comment_id");

            DuelRoom duelRoom = request.Player.DuelRoom;
            if (duelRoom == null)
            {
                request.ResultCode = (int)ResultCodes.RoomCode.ERR_INVALID_ROOM;
            }
            else
            {
                DuelRoomTable table = duelRoom.GetTable(request.Player);
                if (table != null)
                {
                    duelRoom.TimeUpdated = DateTime.UtcNow;
                    table.SetPlayerComment(request.Player, comment);
                }
                WriteRoomInfo(request, duelRoom);
            }
            request.Remove("Room.room_info");
        }

        void Act_RoomFriendInvite(GameServerWebRequest request)
        {
            if (!MultiplayerEnabled)
            {
                return;
            }

            ClearSpectatingDuel(request.Player);

            DuelRoom duelRoom = request.Player.DuelRoom;

            List<uint> inviteRequests = Utils.GetValueTypeList<uint>(request.ActParams, "invite_list");
            if (inviteRequests == null || duelRoom == null)
            {
                return;
            }

            List<string> invitedList = new List<string>();

            Dictionary<string, object> roomData = request.GetOrCreateDictionary("Room");
            Dictionary<string, object> roomInfo = Utils.GetOrCreateDictionary(roomData, "room_info");
            Dictionary<string, object> invitedListInfo = Utils.GetOrCreateDictionary(roomInfo, "invited_list");
            invitedListInfo["user_list"] = invitedList;

            Dictionary<uint, FriendState> friends = GetFriends(request.Player);
            foreach (uint playerId in inviteRequests)
            {
                FriendState friendState;
                if (friends.TryGetValue(playerId, out friendState) && friendState.HasFlag(FriendState.Following) && friendState.HasFlag(FriendState.Follower))
                {
                    Player friend;
                    lock (playersLock)
                    {
                        if (!playersById.TryGetValue(playerId, out friend))
                        {
                            continue;
                        }
                    }
                    invitedList.Add(playerId.ToString());
                    lock (friend.DuelRoomInvitesByFriendId)
                    {
                        friend.DuelRoomInvitesByFriendId[request.Player.Code] = duelRoom.Id;
                    }

                    NetClient friendClient = friend.NetClient;
                    if (friendClient != null)
                    {
                        friendClient.Send(new FriendDuelInviteMessage()
                        {
                            PlayerCode = request.Player.Code,
                            Name = request.Player.Name
                        });
                    }
                }
            }

            invitedListInfo["num"] = inviteRequests.Count;
            invitedListInfo["success"] = invitedList.Count;
        }

        void Act_RoomBattleReady(GameServerWebRequest request)
        {
            if (!MultiplayerEnabled)
            {
                return;
            }

            ClearSpectatingDuel(request.Player);

            if (!File.Exists(Pvp.DllName))
            {
                Utils.LogWarning("[Act_RoomBattleReady] Failed to find '" + Pvp.DllName + "'");
                request.ResultCode = (int)ResultCodes.RoomCode.ERR_INVALID_DATA;
                return;
            }

            string cardDataFile = Path.Combine(dataDirectory, "CardData", "#", "CARD_IntID.bytes");
            if (!File.Exists(cardDataFile))
            {
                Utils.LogWarning("[Act_RoomBattleReady] Failed to find '" + cardDataFile + "'");
                request.ResultCode = (int)ResultCodes.RoomCode.ERR_INVALID_DATA;
                return;
            }

            bool isBattleReady = Utils.GetValue<bool>(request.ActParams, "isBattleReady");
            //int opponentCode = Utils.GetValue<int>(request.ActParams, "opp_code");

            if (isBattleReady)
            {
                if (request.Player.NetClient == null)
                {
                    Utils.LogWarning("[Act_RoomBattleReady] Player '" + request.Player.Name + "' can't enter ready state as they aren't connected to the session server");
                    request.ResultCode = (int)ResultCodes.RoomCode.ERR_INVALID_DATA;
                    return;
                }
            }

            DuelRoom duelRoom = request.Player.DuelRoom;
            if (duelRoom == null)
            {
                Utils.LogWarning("[Act_RoomBattleReady] duelRoom");
                request.ResultCode = (int)ResultCodes.RoomCode.ERR_INVALID_ROOM;
            }
            else
            {
                DuelRoomTable table = duelRoom.GetTable(request.Player);
                if (table == null || !table.IsFull)
                {
                    Utils.LogWarning("[Act_RoomBattleReady] table == null || !table.IsFull");
                    request.ResultCode = (int)ResultCodes.RoomCode.ERR_RIVAL_LEAVE_TABLE;
                }
                else
                {
                    DuelRoomTableEntry playerTableEntry = table.GetEntry(request.Player);
                    if (playerTableEntry != null)
                    {
                        bool anyBattleReady = false;
                        foreach (DuelRoomTableEntry entry in table.Entries)
                        {
                            if (isBattleReady || entry.IsMatchingOrInDuel)
                            {
                                anyBattleReady = true;
                                if (table.State == DuelRoomTableState.Joinable)
                                {
                                    table.State = table.Player1 == request.Player ? DuelRoomTableState.P1StandingBy : DuelRoomTableState.P2StandingBy;
                                    table.Seed = MultiplayerSeed != -1 ? (uint)MultiplayerSeed : (uint)rand.Next();
                                    table.Bgm = DuelSettings.GetRandomBgmValue();
                                    table.FirstPlayer = -1;
                                    table.CoinFlipPlayerIndex = MultiplayerCoinFlipPlayerIndex != -1 ? MultiplayerCoinFlipPlayerIndex : rand.Next(2);
                                    table.CoinFlipCounter = MultiplayerCoinFlipCounter;
                                    table.ClearPvpClient();
                                    using (SHA1 sha1 = SHA1.Create())
                                    {
                                        table.TableHash = BitConverter.ToString(sha1.ComputeHash(Guid.NewGuid().ToByteArray())).Replace("-", string.Empty);
                                    }
                                    using (MD5 md5 = MD5.Create())
                                    {
                                        table.TableTicket = BitConverter.ToString(md5.ComputeHash(Guid.NewGuid().ToByteArray())).Replace("-", string.Empty);
                                    }
                                    using (SHA256 sha = SHA256.Create())
                                    {
                                        byte[] secretKey = sha.ComputeHash(Encoding.UTF8.GetBytes(duelRoom.Id + " - " + duelRoom.TimeCreated + rand.Next()));
                                        table.SecretKeyForPvpServer = BitConverter.ToString(secretKey).Replace("-", string.Empty);
                                    }
                                }
                                break;
                            }
                        }
                        if (!anyBattleReady)
                        {
                            table.State = DuelRoomTableState.Joinable;
                        }

                        playerTableEntry.IsMatchingOrInDuel = isBattleReady;
                    }

                    WriteRoomInfo(request, duelRoom);
                }
            }

            request.Remove("Duel");
        }

        void Act_DuelMenuDeckCheck(GameServerWebRequest request)
        {
            if (!MultiplayerEnabled)
            {
                return;
            }

            ClearSpectatingDuel(request.Player);

            PlayMode playMode = Utils.GetValue<PlayMode>(request.ActParams, "kind");
            //int tid = Utils.GetValue<int>(request.ActParams, "tid");//?
            //int regulation = Utils.GetValue<int>(request.ActParams, "regulation_id");

            if (playMode != PlayMode.ROOM)
            {
                return;
            }

            DuelRoom duelRoom = request.Player.DuelRoom;
            if (duelRoom == null || !Regulation.ContainsKey(duelRoom.Rule.ToString()))
            {
                return;
            }

            DeckInfo deck = request.Player.Duel.GetDeck(GameMode.Room);

            Dictionary<string, object> roomData = request.GetOrCreateDictionary("Room");
            Dictionary<string, object> deckInfoData = Utils.GetOrCreateDictionary(roomData, "deck_info");
            if (deck != null)
            {
                deckInfoData["deck_id"] = deck.Id;
                deckInfoData["deck"] = deck.ToDictionary();
                deckInfoData["valid"] = DisableDeckValidation || deck.IsValid(request.Player, duelRoom.Rule, Regulation);
                deckInfoData["possession"] = true;
                deckInfoData["regulation"] = deck.RegulationId.ToString();
            }
            else
            {
                deckInfoData["valid"] = false;
                deckInfoData["possession"] = false;
            }

            request.Remove("Duel");
        }

        void WriteRoomInfo(GameServerWebRequest request, DuelRoom duelRoom)
        {
            if (duelRoom == null)
            {
                return;
            }

            Dictionary<string, object> roomData = request.GetOrCreateDictionary("Room");
            Dictionary<string, object> roomInfo = Utils.GetOrCreateDictionary(roomData, "room_info");
            roomInfo["room_id"] = duelRoom.Id;
            roomInfo["room_master"] = duelRoom.Owner.Code;
            lock (duelRoom.MembersLocker)
            {
                roomInfo["is_join_player"] = duelRoom.Members.ContainsKey(request.Player);
            }
            roomInfo["member_num"] = duelRoom.MemberCount;
            roomInfo["xPlatformLimit"] = 0;
            roomInfo["room_specter_id"] = duelRoom.SpectatorRoomId;
            roomInfo["specter_num"] = duelRoom.SpectatorCount;
            roomInfo["limit_time"] = Utils.FormatDateTime(duelRoom.TimeExpire);
            Dictionary<string, object> membersData = Utils.GetOrCreateDictionary(roomInfo, "room_member");
            lock (duelRoom.MembersLocker)
            {
                foreach (KeyValuePair<Player, DuelRoomRecord> player in duelRoom.Members)
                {
                    Dictionary<string, object> memberData = new Dictionary<string, object>();
                    memberData["record"] = new Dictionary<string, object>()
                    {
                        { "win", player.Value.Win },
                        { "lose", player.Value.Loss },
                        { "draw", player.Value.Draw },
                    };
                    if (player.Key != request.Player)
                    {
                        memberData["same_platform"] = true;
                    }
                    memberData["platform_name"] = "";
                    memberData["platform"] = 0;
                    //memberData["comment"] = 0;// Doesn't seem to do anything? Only new_comment works
                    memberData["name"] = player.Key.Name;
                    memberData["icon_id"] = player.Key.IconId;
                    memberData["icon_frame_id"] = player.Key.IconFrameId;
                    membersData[player.Key.Code.ToString()] = memberData;
                }
            }
            roomInfo["is_public"] = duelRoom.ShowInRoomList;
            roomInfo["member_max"] = duelRoom.MemberLimit;
            roomInfo["room_comment"] = duelRoom.Comment;
            roomInfo["is_specter"] = duelRoom.ShowInSpectatorList;
            roomInfo["is_spectral"] = duelRoom.AllowSpectators;
            roomInfo["is_replay"] = duelRoom.ViewReplays;
            roomInfo["battle_setting"] = new Dictionary<string, object>()
            {
                { "rule", duelRoom.Rule },
                { "lp", duelRoom.LifePoints },
                { "time", duelRoom.DuelTime }
            };
            List<object> tableInfos = new List<object>();
            foreach (DuelRoomTable table in duelRoom.Tables)
            {
                Player player1 = table.Player1;
                Player player2 = table.Player2;

                DuelRoomTableState state = table.State;
                if ((request.Player == player1 || request.Player == player2) && state == DuelRoomTableState.Dueling)
                {
                    state = DuelRoomTableState.Matched;
                }
                if (table.IsDuelComplete && request.Player != player1 && request.Player != player2)
                {
                    state = DuelRoomTableState.Joinable;
                }

                Dictionary<string, object> tableInfo = new Dictionary<string, object>();
                tableInfo["player1"] = player1 != null ? player1.Code : 0;
                tableInfo["player2"] = player2 != null ? player2.Code : 0;
                tableInfo["stats"] = (int)state;
                tableInfos.Add(tableInfo);
            }
            roomInfo["table_info"] = tableInfos;

            Dictionary<string, object> playerComments = new Dictionary<string, object>();

            foreach (DuelRoomTable table in duelRoom.Tables)
            {
                foreach (DuelRoomTableEntry entry in table.Entries)
                {
                    Player player = entry.Player;
                    int comment = entry.Comment;
                    if (player != null && comment > 0 && entry.CommentTime > DateTime.UtcNow - TimeSpan.FromSeconds(DuelRoomCommentTimeoutInSeconds))
                    {
                        playerComments[player.Code.ToString()] = entry.Comment;
                    }
                }
            }

            if (playerComments.Count > 0)
            {
                roomInfo["new_comment"] = playerComments;
            }
            else
            {
                roomInfo["new_comment"] = new int[0];
            }
        }

        void Act_DuelMatching(GameServerWebRequest request)
        {
            if (!MultiplayerEnabled)
            {
                return;
            }

            ClearSpectatingDuel(request.Player);

            //Dictionary<string, object> rule = Utils.GetDictionary(request.ActParams, "rule");
            //Utils.GetValue<int>(rule, "mode");// 5 for room duel?

            DuelRoom duelRoom = request.Player.DuelRoom;
            if (duelRoom == null)
            {
                Utils.LogWarning("[Act_DuelMatching] duelRoom == null");
                request.ResultCode = (int)ResultCodes.PvPCode.NOT_EXIST_ROOM;
                return;
            }

            DuelRoomTable table = duelRoom.GetTable(request.Player);
            if (table == null)
            {
                Utils.LogWarning("[Act_DuelMatching] table == null");
                request.ResultCode = (int)ResultCodes.PvPCode.NOT_EXIST_ROOM;
                return;
            }

            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();

            while (stopwatch.Elapsed < TimeSpan.FromSeconds(2))
            {
                Player p1 = table.Player1;
                Player p2 = table.Player2;
                if (p1 == null || p2 == null)
                {
                    Utils.LogWarning("[Act_DuelMatching] p1 == null || p2 == null");
                    request.ResultCode = (int)ResultCodes.PvPCode.NOT_FIND_OPPONENT;
                    return;
                }
                DeckInfo d1 = p1.Duel.GetDeck(GameMode.Room);
                DeckInfo d2 = p2.Duel.GetDeck(GameMode.Room);
                if (d1 == null || d2 == null || (duelRoom.IsMatchmaking &&
                    (!d1.IsValid(p1, duelRoom.Rule, Regulation) || !d2.IsValid(p2, duelRoom.Rule, Regulation))))
                {
                    Utils.LogWarning("[Act_DuelMatching] d1 == null || d2 == null");
                    request.ResultCode = (int)ResultCodes.PvPCode.INVALID_DECK;
                    return;
                }

                DuelRoomTableEntry tableEntry = table.GetEntry(request.Player);
                if (tableEntry == null || !tableEntry.IsMatchingOrInDuel)
                {
                    //Utils.LogWarning("[Act_DuelMatching] tableEntry == null || !tableEntry.IsMatchingOrInDuel");
                    //request.ResultCode = (int)ResultCodes.PvPCode.NOT_FIND_OPPONENT;
                    return;
                }

                DuelRoomTableState state = table.State;
                if (state != DuelRoomTableState.P1StandingBy && state != DuelRoomTableState.P2StandingBy && state != DuelRoomTableState.Matched &&
                    !(duelRoom.IsMatchmaking && state == DuelRoomTableState.Dueling))
                {
                    Utils.LogWarning("[Act_DuelMatching] state != DuelRoomTableState.P1StandingBy && state != DuelRoomTableState.P2StandingBy && state != DuelRoomTableState.Matched");
                    request.ResultCode = (int)ResultCodes.PvPCode.NOT_FIND_OPPONENT;
                    return;
                }

                if (table.IsMatched)
                {
                    if (state == DuelRoomTableState.P1StandingBy || state == DuelRoomTableState.P2StandingBy)
                    {
                        table.MatchedTime = DateTime.UtcNow;
                        table.State = DuelRoomTableState.Matched;
                    }

                    Dictionary<string, object> duelData = request.GetOrCreateDictionary("Duel");
                    /*duelData["match"] = new Dictionary<string, object>()
                    {
                        { "url", serverPollUrl + "/pvp00/" + table.TableHash },// Setting the url results in polling (even in solo)
                        { "ticket", table.TableTicket },
                        { "stat", true }
                    };*/
                    List<object> playersData = new List<object>();
                    Tuple<Player, DeckInfo>[] players =
                    {
                        new Tuple<Player, DeckInfo>(p1, d1),
                        new Tuple<Player, DeckInfo>(p2, d2),
                    };
                    for (int i = 0; i < players.Length; i++)
                    {
                        Player player = players[i].Item1;
                        DeckInfo deck = players[i].Item2;
                        Dictionary<uint, FriendState> friends = GetFriends(player);
                        Dictionary<string, object> playerData = new Dictionary<string, object>();
                        playerData["pcode"] = player.Code;
                        playerData["name"] = player.Name;
                        playerData["mat"] = deck.Accessory.Field;
                        playerData["sleeve"] = deck.Accessory.Sleeve;
                        playerData["rank"] = new Dictionary<string, object>()
                        {
                            { "rank", player.Rank },
                            { "rate", player.Rate }
                        };
                        playerData["icon"] = player.IconId;
                        playerData["icon_frame"] = player.IconFrameId;
                        playerData["avatar"] = deck.Accessory.AvatarId > 0 ? deck.Accessory.AvatarId : player.AvatarId;
                        playerData["avatar_home"] = deck.Accessory.AvBase;
                        playerData["duel_object"] = deck.Accessory.FieldObj;
                        playerData["deck_case"] = deck.Accessory.Box;
                        playerData["coin"] = deck.Accessory.Coin < (int)ItemID.Value.DefaultCoin ? (int)ItemID.Value.DefaultCoin : deck.Accessory.Coin;
                        playerData["level"] = player.Level;
                        playerData["wallpaper"] = player.Wallpaper;
                        playerData["profile_tag"] = player.TitleTags.ToArray();
                        playerData["follow_num"] = friends.Count(x => x.Value.HasFlag(FriendState.Following));
                        playerData["follower_num"] = friends.Count(x => x.Value.HasFlag(FriendState.Follower));
                        playerData["one_more_effect"] = 0;//?
                        playerData["official"] = 0;//?
                        playerData["os"] = (int)PlatformID.Steam;
                        playerData["is_same_os"] = null;
                        playerData["online_id"] = null;
                        playerData["dlv_effect"] = null;
                        playersData.Add(playerData);
                    }
                    duelData["player"] = playersData;
                    duelData["myid"] = request.Player == p1 ? 0 : 1;
                    duelData["MyID"] = request.Player == p1 ? 0 : 1;
                    duelData["choice"] = table.CoinFlipPlayerIndex;
                    duelData["mat"] = new int[2] { d1.Accessory.Field, d2.Accessory.Field };
                    duelData["sleeve"] = new int[2] { d1.Accessory.Sleeve, d2.Accessory.Sleeve };
                    duelData["icon"] = new int[2] { p1.IconId, p2.IconId };
                    duelData["icon_frame"] = new int[2] { p1.IconFrameId, p2.IconFrameId };
                    duelData["avatar"] = new int[2] { d1.Accessory.AvatarId > 0 ? d1.Accessory.AvatarId : p1.AvatarId, d2.Accessory.AvatarId > 0 ? d2.Accessory.AvatarId : p2.AvatarId };
                    duelData["avatar_home"] = new int[2] { d1.Accessory.AvBase, d2.Accessory.AvBase };
                    duelData["duel_object"] = new int[2] { d1.Accessory.FieldObj, d2.Accessory.FieldObj };
                    duelData["deck_case"] = new int[2] { d1.Accessory.Box, d2.Accessory.Box };
                    duelData["coin"] = new int[2] { d1.Accessory.Coin < (int)ItemID.Value.DefaultCoin ? (int)ItemID.Value.DefaultCoin : d1.Accessory.Coin,
                                                    d2.Accessory.Coin < (int)ItemID.Value.DefaultCoin ? (int)ItemID.Value.DefaultCoin : d2.Accessory.Coin };
                    duelData["os"] = new int[2] { (int)PlatformID.Steam, (int)PlatformID.Steam };
                    duelData["pcode"] = new uint[2] { p1.Code, p2.Code };
                    duelData["official"] = new int[2] { 0, 0 };
                    request.Remove("Duel");
                    return;
                }
                Thread.Sleep(250);
            }

            request.ResultCode = (int)ResultCodes.PvPCode.TIMEOUT;
        }

        void Act_DuelMatchingCancel(GameServerWebRequest request)
        {
            lock (matchmakingLock)
            {
                matchmakingQueue.RemoveAll(x => x.Player == request.Player);
                DuelRoom matchRoom = request.Player.DuelRoom;
                if (matchRoom != null && matchRoom.IsMatchmaking && matchRoom.Tables[0].State != DuelRoomTableState.Dueling)
                {
                    DisbandRoom(matchRoom);
                    request.Remove("Room.room_info", "Duel");
                    return;
                }
            }
            if (!MultiplayerEnabled)
            {
                return;
            }

            ClearSpectatingDuel(request.Player);

            DuelRoom duelRoom = request.Player.DuelRoom;
            if (duelRoom == null)
            {
                Utils.LogWarning("[Act_DuelMatchingCancel] duelRoom == null");
                request.ResultCode = (int)ResultCodes.PvPCode.NOT_EXIST_ROOM;
                return;
            }

            duelRoom.ResetTableStateIfMatchingOrDueling(request.Player);
        }

        void Act_DuelStartWating(GameServerWebRequest request)
        {
            if (!MultiplayerEnabled)
            {
                return;
            }

            ClearSpectatingDuel(request.Player);

            DuelRoom duelRoom = request.Player.DuelRoom;
            if (duelRoom == null)
            {
                Utils.LogWarning("[Act_DuelStartWating] duelRoom == null");
                request.ResultCode = (int)ResultCodes.PvPCode.NOT_EXIST_ROOM;
                return;
            }

            DuelRoomTable table = duelRoom.GetTable(request.Player);
            if (table == null)
            {
                Utils.LogWarning("[Act_DuelStartWating] table == null");
                request.ResultCode = (int)ResultCodes.PvPCode.NOT_EXIST_ROOM;
                return;
            }

            DuelRoomTableEntry tableEntry = table.GetEntry(request.Player);
            if (tableEntry == null || !tableEntry.IsMatchingOrInDuel)
            {
                Utils.LogWarning("[Act_DuelStartWating] tableEntry == null || !tableEntry.IsMatchingOrInDuel");
                request.ResultCode = (int)ResultCodes.PvPCode.NOT_FIND_OPPONENT;
                return;
            }

            Player p1 = table.Player1;
            Player p2 = table.Player2;
            if (p1 == null || p2 == null)
            {
                Utils.LogWarning("[Act_DuelStartWating] p1 == null || p2 == null");
                request.ResultCode = (int)ResultCodes.PvPCode.NOT_FIND_OPPONENT;
                return;
            }

            DuelRoomTableState state = table.State;
            if (state != DuelRoomTableState.Matched && state != DuelRoomTableState.Dueling)
            {
                Utils.LogWarning("[Act_DuelStartWating] state != DuelRoomTableState.Matched && state != DuelRoomTableState.Dueling");
                request.ResultCode = (int)ResultCodes.PvPCode.NOT_FIND_OPPONENT;
                return;
            }

            if (table.MatchedTime < DateTime.UtcNow - TimeSpan.FromSeconds(DuelRoomTableMatchingTimeoutInSeconds))
            {
                table.ClearMatching();
                Utils.LogWarning("[Act_DuelStartWating] table.MatchedTime < DateTime.UtcNow - TimeSpan.FromSeconds(DuelRoomTableMatchingTimeoutInSeconds)");
                request.ResultCode = (int)ResultCodes.PvPCode.NOT_FIND_OPPONENT;
                return;
            }

            Thread.Sleep(1000);

            Dictionary<string, object> duelData = request.GetOrCreateDictionary("Duel");
            duelData["pvp_choice"] = new Dictionary<string, object>()
            {
                { "choice", table.CoinFlipPlayerIndex },
                { "sha", table.TableHash },
                { "cnt", Math.Max(0, table.CoinFlipCounter) }
            };
            duelData["myid"] = request.Player == p1 ? 0 : 1;
            int firstPlayer = table.FirstPlayer;
            if (firstPlayer >= 0 || table.CoinFlipCounter <= -1)
            {
                if (firstPlayer == -1 && table.Entries[table.CoinFlipPlayerIndex].Player == request.Player)
                {
                    state = table.State;
                    if (state != DuelRoomTableState.Matched && state != DuelRoomTableState.Dueling)
                    {
                        Utils.LogWarning("[Act_DuelStartWating] state != DuelRoomTableState.Matched && state != DuelRoomTableState.Dueling");
                        request.ResultCode = (int)ResultCodes.PvPCode.NOT_FIND_OPPONENT;
                        return;
                    }
                    lock (table.Entries)
                    {
                        // A choice can arrive while this polling request sleeps.
                        // Never overwrite a choice that won that race.
                        firstPlayer = table.FirstPlayer;
                        if (firstPlayer < 0)
                        {
                            firstPlayer = rand.Next(2);
                            if (!table.InitDuel(firstPlayer))
                            {
                                request.ResultCode = (int)ResultCodes.PvPCode.NOT_FIND_OPPONENT;
                                return;
                            }
                        }
                    }
                }
                if (firstPlayer >= 0)
                {
                    duelData["first"] = firstPlayer;
                    return;
                }
            }
            if (table.Entries[table.CoinFlipPlayerIndex].Player == request.Player)
            {
                if (table.CoinFlipCounter >= 0)
                {
                    table.CoinFlipCounter--;
                }
            }

            request.ResultCode = (int)ResultCodes.PvPCode.TIMEOUT;
        }

        void Act_DuelStartSelecting(GameServerWebRequest request)
        {
            if (!MultiplayerEnabled)
            {
                return;
            }

            ClearSpectatingDuel(request.Player);

            DuelRoom duelRoom = request.Player.DuelRoom;
            if (duelRoom == null)
            {
                Utils.LogWarning("[Act_DuelStartSelecting] duelRoom == null");
                request.ResultCode = (int)ResultCodes.PvPCode.NOT_EXIST_ROOM;
                return;
            }

            DuelRoomTable table = duelRoom.GetTable(request.Player);
            if (table == null)
            {
                Utils.LogWarning("[Act_DuelStartSelecting] table == null");
                request.ResultCode = (int)ResultCodes.PvPCode.NOT_EXIST_ROOM;
                return;
            }

            DuelRoomTableEntry tableEntry = table.GetEntry(request.Player);
            if (tableEntry == null || !tableEntry.IsMatchingOrInDuel)
            {
                Utils.LogWarning("[Act_DuelStartSelecting] tableEntry == null || !tableEntry.IsMatchingOrInDuel");
                request.ResultCode = (int)ResultCodes.PvPCode.NOT_FIND_OPPONENT;
                return;
            }

            DuelRoomTableState state = table.State;
            if (state != DuelRoomTableState.Matched && state != DuelRoomTableState.Dueling)
            {
                Utils.LogWarning("[Act_DuelStartSelecting] state != DuelRoomTableState.Matched && state != DuelRoomTableState.Dueling");
                request.ResultCode = (int)ResultCodes.PvPCode.NOT_FIND_OPPONENT;
                return;
            }

            if (table.MatchedTime < DateTime.UtcNow - TimeSpan.FromSeconds(DuelRoomTableMatchingTimeoutInSeconds))
            {
                table.ClearMatching();
                Utils.LogWarning("[Act_DuelStartSelecting] table.MatchedTime < DateTime.UtcNow - TimeSpan.FromSeconds(DuelRoomTableMatchingTimeoutInSeconds)");
                request.ResultCode = (int)ResultCodes.PvPCode.NOT_FIND_OPPONENT;
                return;
            }

            Player p1 = table.Player1;
            Player p2 = table.Player2;
            if (p1 == null || p2 == null)
            {
                Utils.LogWarning("[Act_DuelStartSelecting] p1 == null || p2 == null");
                request.ResultCode = (int)ResultCodes.PvPCode.NOT_FIND_OPPONENT;
                return;
            }

            int select = Utils.GetValue<int>(request.ActParams, "select");
            if (duelRoom.IsMatchmaking)
            {
                if (!Utils.TryGetValue(request.ActParams, "select", out select) ||
                    !SelectMatchmakingTurn(duelRoom, table, request.Player, select))
                    request.ResultCode = (int)ResultCodes.PvPCode.INVALID_PARAM;
                return;
            }
            if (select >= 0 && select <= 1)
            {
                if (request.Player != p1)
                {
                    select = 1 - select;
                }
                if (!table.InitDuel(select))
                {
                    Utils.LogWarning("[Act_DuelStartSelecting] !table.InitDuel(select)");
                    request.ResultCode = (int)ResultCodes.PvPCode.NOT_FIND_OPPONENT;
                }
            }
            else
            {
                Utils.LogWarning("[Act_DuelStartSelecting] select >= 0 && select <= 1");
                request.ResultCode = (int)ResultCodes.PvPCode.INVALID_PARAM;
            }
        }

        void Act_DuelBeginPvp(GameServerWebRequest request)
        {
            if (!MultiplayerEnabled)
            {
                return;
            }

            ClearSpectatingDuel(request.Player);

            DuelRoom duelRoom = request.Player.DuelRoom;
            if (duelRoom == null)
            {
                Utils.LogWarning("[Act_DuelBeginPvp] duelRoom == null");
                request.ResultCode = (int)ResultCodes.PvPCode.NOT_EXIST_ROOM;
                return;
            }

            DuelRoomTable table = duelRoom.GetTable(request.Player);
            if (table == null)
            {
                Utils.LogWarning("[Act_DuelBeginPvp] table == null");
                request.ResultCode = (int)ResultCodes.PvPCode.NOT_EXIST_ROOM;
                return;
            }

            DuelRoomTableEntry tableEntry = table.GetEntry(request.Player);
            if (tableEntry == null || !tableEntry.IsMatchingOrInDuel)
            {
                Utils.LogWarning("[Act_DuelBeginPvp] tableEntry == null || !tableEntry.IsMatchingOrInDuel");
                request.ResultCode = (int)ResultCodes.PvPCode.NOT_FIND_OPPONENT;
                return;
            }

            if (table.MatchedTime < DateTime.UtcNow - TimeSpan.FromSeconds(DuelRoomTableMatchingTimeoutInSeconds))
            {
                table.ClearMatching();
                Utils.LogWarning("[Act_DuelBeginPvp] table.MatchedTime < DateTime.UtcNow - TimeSpan.FromSeconds(DuelRoomTableMatchingTimeoutInSeconds)");
                request.ResultCode = (int)ResultCodes.PvPCode.NOT_FIND_OPPONENT;
                return;
            }

            Player p1 = table.Player1;
            Player p2 = table.Player2;
            if (p1 == null || p2 == null)
            {
                Utils.LogWarning("[Act_DuelBeginPvp] p1 == null || p2 == null");
                request.ResultCode = (int)ResultCodes.PvPCode.NOT_FIND_OPPONENT;
                return;
            }

            DeckInfo d1 = p1.Duel.GetDeck(GameMode.Room);
            DeckInfo d2 = p2.Duel.GetDeck(GameMode.Room);
            if (d1 == null || d2 == null || (duelRoom.IsMatchmaking &&
                (!d1.IsValid(p1, duelRoom.Rule, Regulation) || !d2.IsValid(p2, duelRoom.Rule, Regulation))))
            {
                Utils.LogWarning("[Act_DuelBeginPvp] d1 == null || d2 == null");
                request.ResultCode = (int)ResultCodes.PvPCode.INVALID_DECK;
                return;
            }

            if (table.State != DuelRoomTableState.Dueling)
            {
                Utils.LogWarning("[Act_DuelBeginPvp] table.State != DuelRoomTableState.Dueling");
                request.ResultCode = (int)ResultCodes.PvPCode.NOT_FIND_OPPONENT;
                return;
            }

            if (request.Player.NetClient == null)
            {
                Utils.LogWarning("[Act_DuelBeginPvp] Player '" + request.Player.Name + "' can't enter duel as they aren't connected to the session server");
                request.ResultCode = (int)ResultCodes.RoomCode.ERR_INVALID_DATA;
                duelRoom.ResetTableStateIfMatchingOrDueling(request.Player);
                return;
            }

            tableEntry.HasBeginDuel = true;
            table.Rewards.Clear();
            lock (duelRoom.Replays)
            {
                table.Replay = null;
            }

            Dictionary<uint, FriendState> p1Friends = GetFriends(p1);
            Dictionary<uint, FriendState> p2Friends = GetFriends(p2);

            Player[] players = { p1, p2 };
            DeckInfo[] decks = { d1, d2 };
            Dictionary<uint, FriendState>[] friends = { p1Friends, p2Friends };

            request.Player.Duel.Mode = GameMode.Room;

            DuelTimerInfo duelTimer = null;
            if (duelRoom.DuelTime <= DuelRoomTimes.Count && duelRoom.DuelTime > 0)
            {
                duelTimer = DuelRoomTimes[duelRoom.DuelTime - 1];
            }

            DuelSettings duelSettings = new DuelSettings();
            duelSettings.RandSeed = table.Seed;
            duelSettings.FirstPlayer = table.FirstPlayer;
            duelSettings.noshuffle = MultiplayerNoShuffle;
            duelSettings.tag = false;
            duelSettings.dlginfo = false;
            duelSettings.MyID = request.Player == p1 ? 0 : 1;
            duelSettings.MyType = 0;
            duelSettings.Type = 0;
            duelSettings.MyPartnerType = 0;
            duelSettings.PlayableTagPartner = 0;
            duelSettings.regulation_id = 0;
            duelSettings.duel_start_timestamp = 0;
            duelSettings.surrender = true;
            duelSettings.Limit = 16;//0 (16 = NoRandom)
            duelSettings.GameMode = (int)GameMode.Room;//GameMode.Normal
            duelSettings.cpu = 100;
            duelSettings.cpuflag = null;
            if (duelTimer != null && duelTimer.Time > 0)
            {
                duelSettings.LeftTimeMax = duelTimer.Time;
                duelSettings.TurnTimeMax = duelTimer.TurnTimeIndicator;
                duelSettings.TotalTimeMax = 99999;// Unused by the client? This is a total time over all turns including increments
            }
            duelSettings.Auto = -1;
            duelSettings.rec = false;
            duelSettings.recf = false;
            duelSettings.did = 0;
            duelSettings.duel = null;
            duelSettings.is_pvp = true;//false
            duelSettings.chapter = 0;
            for (int i = 0; i < players.Length; i++)
            {
                duelSettings.Deck[i] = decks[i];
                duelSettings.life[i] = duelRoom.LifePoints == 1 ? 8000 : 4000;
                duelSettings.level[i] = players[i].Level;
                duelSettings.follow_num[i] = friends[i].Count(x => x.Value.HasFlag(FriendState.Following));
                duelSettings.follower_num[i] = friends[i].Count(x => x.Value.HasFlag(FriendState.Follower));
                duelSettings.pcode[i] = (int)players[i].Code;
                duelSettings.rank[i] = players[i].Rank;
                duelSettings.DuelistLv[i] = players[i].Rate;
                duelSettings.name[i] = players[i].Name;
                duelSettings.avatar[i] = decks[i].Accessory.AvatarId > 0 ? decks[i].Accessory.AvatarId : players[i].AvatarId;
                duelSettings.avatar_home[i] = decks[i].Accessory.AvBase;
                duelSettings.icon[i] = players[i].IconId;
                duelSettings.icon_frame[i] = players[i].IconFrameId;
                duelSettings.sleeve[i] = decks[i].Accessory.Sleeve;
                duelSettings.mat[i] = decks[i].Accessory.Field;
                duelSettings.coin[i] = decks[i].Accessory.Coin < (int)ItemID.Value.DefaultCoin ? (int)ItemID.Value.DefaultCoin : decks[i].Accessory.Coin;
                duelSettings.duel_object[i] = decks[i].Accessory.FieldObj;
                duelSettings.wallpaper[i] = players[i].Wallpaper;
                duelSettings.profile_tag[i] = players[i].TitleTags.ToList();
            }
            duelSettings.SetRequiredDefaults();
            //duelSettings.BgmsFromValue(table.Bgm);// table.Bgm is a random BGM used to be shared between both players
            duelSettings.SetBgm(request.Player.DuelBgmMode);
            request.Player.ActiveDuelSettings.CopyFrom(duelSettings);
            request.Player.ActiveDuelSettings.PvpChoice = table.CoinFlipPlayerIndex;
            request.Player.ActiveDuelSettings.HasSavedReplay = false;
            request.Player.ActiveDuelSettings.DuelBeginTime = Utils.GetEpochTime();

            if (request.Player == p1)
            {
                using (Process process = new Process())
                {
                    Dictionary<string, object> data = new Dictionary<string, object>();
                    data["Duel"] = duelSettings.ToDictionary();
                    data["SessionServerIP"] = multiplayerPvpClientConnectIP;
                    data["SessionServerPort"] = multiplayerPvpClientConnectPort;
                    data["Key"] = table.SecretKeyForPvpServer;
                    data["DuelRoomId"] = duelRoom.Id;
                    data["PlayerId1"] = p1.Code;
                    data["PlayerId2"] = p2.Code;
                    data["Sleep"] = MultiplayerPvpClientSysActSleepInMilliseconds;
                    data["CallsPerSleep"] = MultiplayerPvpClientSysActCallsPerSleep;
                    data["NoDelay"] = MultiplayerNoDelay;
                    data["KeepConsoleAlive"] = MultiplayerPvpClientShowConsole && MultiplayerPvpClientKeepConsoleAlive;
                    data["DoCommandUserOffset"] = MultiplayerPvpClientDoCommandUserOffset;
                    data["RunDialogUserOffset"] = MultiplayerPvpClientRunDialogUserOffset;

                    process.StartInfo.Arguments = "--pvp \"" + Convert.ToBase64String(Encoding.UTF8.GetBytes(MiniJSON.Json.Serialize(data))) + "\"";
                    if (Program.IsMonoRun)
                    {
                        process.StartInfo.WorkingDirectory = Path.GetDirectoryName(ygoMasterExePath);
                        process.StartInfo.FileName = "MonoRun.exe";
                        process.StartInfo.Arguments = "YgoMaster.exe " + process.StartInfo.Arguments;
                    }
                    else
                    {
                        process.StartInfo.FileName = Path.GetFileName(ygoMasterExePath);
                    }
                    if (!MultiplayerPvpClientShowConsole)
                    {
                        if (Program.IsMonoRun)
                        {
                            // This is the only thing that seems to work under Linux
                            // TODO: Validate this is actually running under Linux
                            process.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                        }
                        else
                        {
                            process.StartInfo.CreateNoWindow = true;
                            process.StartInfo.UseShellExecute = false;
                        }
                    }
                    process.Start();
                }
            }

            Stopwatch sw = new Stopwatch();
            sw.Start();
            while (table.PvpClientState != PvpClientState.Ready && table.State == DuelRoomTableState.Dueling &&
                sw.Elapsed < TimeSpan.FromSeconds(DuelRoomPvpClientSetupTimeoutInSeconds))
            {
                Thread.Sleep(200);
            }

            if (table.PvpClientState == PvpClientState.Ready && table.State == DuelRoomTableState.Dueling)
            {
                //Console.WriteLine("Ready");
            }
            else
            {
                Utils.LogWarning("[Act_DuelBeginPvp] Player '" + request.Player.Name + "' can't enter duel as the pvp client wasn't set up in time");
                request.ResultCode = (int)ResultCodes.RoomCode.ERR_INVALID_DATA;
                duelRoom.ResetTableStateIfMatchingOrDueling(request.Player);
                return;
            }

            Dictionary<string, object> duelData = duelSettings.ToDictionary();
            duelData["SendLiveRecordData"] = duelRoom.AllowSpectators && request.Player == p1;
            if (duelTimer != null)
            {
                duelData["AddTimeAtStartOfTurn"] = duelTimer.AddTimeAtStartOfTurn;
                duelData["AddTimeAtEndOfTurn"] = duelTimer.AddTimeAtEndOfTurn;
            }
            request.Response["Duel"] = duelData;

            request.Remove("Duel", "DuelResult", "Result");
        }

        void Act_RoomGetResultList(GameServerWebRequest request)
        {
            if (!MultiplayerEnabled)
            {
                return;
            }

            ClearSpectatingDuel(request.Player);

            DuelRoom duelRoom = request.Player.DuelRoom;
            if (duelRoom == null)
            {
                Utils.LogWarning("[Act_RoomGetResultList] duelRoom == null");
                request.ResultCode = (int)ResultCodes.PvPCode.NOT_EXIST_ROOM;
                return;
            }

            Dictionary<string, object> roomData = request.GetOrCreateDictionary("Room");
            Dictionary<string, object> roomInfoData = Utils.GetOrCreateDictionary(roomData, "room_info");
            List<object> battleResultList = new List<object>();
            roomInfoData["battle_result_list"] = battleResultList;

            List<DuelRoomReplay> replays;
            lock (duelRoom.Replays)
            {
                replays = new List<DuelRoomReplay>(duelRoom.Replays);
            }

            foreach (DuelRoomReplay replay in replays)
            {
                Dictionary<string, object> battleResultData = new Dictionary<string, object>();
                battleResultList.Add(battleResultData);

                battleResultData["player1"] = replay.Player1.pcode == null || replay.Player1.pcode.Length <= replay.Player1.MyID ?
                    0 : replay.Player1.pcode[replay.Player1.MyID];

                battleResultData["player2"] = replay.Player2.pcode == null || replay.Player2.pcode.Length <= replay.Player2.MyID ?
                    0 : replay.Player2.pcode[replay.Player2.MyID];

                int win = 0;
                switch ((DuelResultType)replay.Player1.res)
                {
                    case DuelResultType.Win:
                        win = 1;
                        break;
                    case DuelResultType.Lose:
                        win = 2;
                        break;
                    case DuelResultType.Draw:
                        win = 0;
                        break;
                }
                battleResultData["win"] = win;

                battleResultData["did"] = replay.Did;
            }
        }

        void Act_RoomReplayDuel(GameServerWebRequest request)
        {
            if (!MultiplayerEnabled)
            {
                return;
            }

            ClearSpectatingDuel(request.Player);

            uint pcode = Utils.GetValue<uint>(request.ActParams, "pcode");
            long did = Utils.GetValue<long>(request.ActParams, "did");

            DuelRoom duelRoom = request.Player.DuelRoom;
            if (duelRoom == null)
            {
                Utils.LogWarning("[Act_RoomGetResultList] duelRoom == null");
                request.ResultCode = (int)ResultCodes.PvPCode.NO_REPLAY_DATA;
                return;
            }

            DuelRoomReplay replay;
            lock (duelRoom.Replays)
            {
                if (!duelRoom.ReplaysByDid.TryGetValue(did, out replay))
                {
                    request.ResultCode = (int)ResultCodes.PvPCode.NO_REPLAY_DATA;
                    return;
                }
            }

            DuelSettings duelSettings = null;
            if (pcode == replay.Player1Code)
            {
                duelSettings = replay.Player1;
            }
            else if (pcode == replay.Player2Code)
            {
                duelSettings = replay.Player2;
            }

            if (duelSettings == null)
            {
                request.ResultCode = (int)ResultCodes.PvPCode.NO_REPLAY_DATA;
                return;
            }

            Dictionary<string, object> responseData = request.GetOrCreateDictionary("Response");
            responseData["UrlScheme"] = "duel:push?GameMode=" + (int)GameMode.Replay + "&did=" + replay.Did + "&roomReplayDid=" + duelSettings.did;
        }

        void Act_RoomSaveReplay(GameServerWebRequest request)
        {
            if (!MultiplayerEnabled)
            {
                return;
            }

            // Not handling this as we already auto save replays no matter what

            //GameMode gameMode = Utils.GetValue<GameMode>(request.ActParams, "mode");
            //long did = Utils.GetValue<long>(request.ActParams, "mode");
            //int eid = Utils.GetValue<int>(request.ActParams, "eid");//?

            // Response: {"code":0,"res":[[166,[],0,0]]}
        }

        void Act_RoomWatchDuel(GameServerWebRequest request, bool beginDuel)
        {
            if (!MultiplayerEnabled)
            {
                return;
            }

            Dictionary<string, object> rule = beginDuel ? Utils.GetDictionary(request.ActParams, "rule") : request.ActParams;
            if (rule == null)
            {
                return;
            }

            uint pcode = Utils.GetValue<uint>(rule, "pcode");
            bool rapid = Utils.GetValue<bool>(rule, "rapid");

            Player targetPlayer;
            lock (playersLock)
            {
                playersById.TryGetValue(pcode, out targetPlayer);
            }
            if (targetPlayer == null || targetPlayer == request.Player)
            {
                Utils.LogWarning("[Act_RoomWatchDuel:" + beginDuel +"] targetPlayer == null || targetPlayer == request.Player");
                request.ResultCode = (int)ResultCodes.PvPCode.NOT_EXIST_ROOM_OPP;
                return;
            }

            DuelRoom duelRoom = targetPlayer.DuelRoom;
            if (duelRoom == null)
            {
                Utils.LogWarning("[Act_RoomWatchDuel:" + beginDuel + "] duelRoom == null");
                request.ResultCode = (int)ResultCodes.PvPCode.NOT_EXIST_ROOM_OPP;
                return;
            }

            DuelRoomTable table = duelRoom.GetTable(targetPlayer);
            if (table == null)
            {
                Utils.LogWarning("[Act_RoomWatchDuel:" + beginDuel +"] table == null");
                request.ResultCode = (int)ResultCodes.PvPCode.NOT_EXIST_ROOM_OPP;
                return;
            }

            if (table.State != DuelRoomTableState.Dueling)
            {
                Utils.LogWarning("[Act_RoomWatchDuel:" + beginDuel +"] table.State != DuelRoomTableState.Dueling");
                request.ResultCode = (int)ResultCodes.PvPCode.NOT_EXIST_ROOM_OPP;
                return;
            }

            if (table.Player1 != targetPlayer)
            {
                Utils.LogWarning("[Act_RoomWatchDuel:" + beginDuel +"] table.Player1 != targetPlayer");
                request.ResultCode = (int)ResultCodes.PvPCode.NOT_EXIST_ROOM_OPP;
                return;
            }

            if (beginDuel)
            {
                request.Player.SpectatingPlayerCode = pcode;

                DuelSettings duelSettings = new DuelSettings();
                duelSettings.CopyFrom(targetPlayer.ActiveDuelSettings);
                duelSettings.GameMode = (int)GameMode.Audience;
                duelSettings.MyType = (int)DuelPlayerType.Replay;

                Dictionary<string, object> duelData = duelSettings.ToDictionary();
                DuelSettings.FixupReplayRequirements(duelSettings, duelData);
                duelData["rapid"] = rapid;
                duelData["publicLevel"] = (int)DuelRoomSpectatorCardVisibility;

                request.Response["Duel"] = duelData;

                request.Remove("Duel", "DuelResult", "Result");
            }
            else
            {
                ClearSpectatingDuel(request.Player);
                Dictionary<string, object> responseData = request.GetOrCreateDictionary("Response");
                responseData["UrlScheme"] = "duel:push?GameMode=" + (int)GameMode.Audience + "&pcode=" + pcode + "&rapid=" + rapid;
            }
        }

        public DuelRoom GetDuelRoom(uint duelRoomId)
        {
            lock (duelRoomsLocker)
            {
                DuelRoom result;
                duelRoomsByRoomId.TryGetValue(duelRoomId, out result);
                return result;
            }
        }

        public Player GetDuelingOpponent(Player player)
        {
            DuelRoomTableState tableState;
            return GetDuelingOpponent(player, out tableState);
        }

        public Player GetDuelingOpponent(Player player, out DuelRoomTableState tableState)
        {
            tableState = DuelRoomTableState.None;

            if (player == null)
            {
                return null;
            }

            DuelRoom duelRoom = player.DuelRoom;
            if (duelRoom == null)
            {
                return null;
            }

            DuelRoomTable table = duelRoom.GetTable(player);
            if (table == null)
            {
                return null;
            }

            tableState = table.State;

            if (table.State != DuelRoomTableState.Dueling)
            {
                return null;
            }

            DuelRoomTableEntry p1Entry = table.Entries[0];
            DuelRoomTableEntry p2Entry = table.Entries[1];

            if (!p1Entry.IsMatchingOrInDuel || !p2Entry.IsMatchingOrInDuel ||
                !p1Entry.HasBeginDuel || !p2Entry.HasBeginDuel)
            {
                return null;
            }

            Player p1 = p1Entry.Player;
            Player p2 = p2Entry.Player;
            if (p1 == null || p2 == null)
            {
                return null;
            }

            if (p1 != player && p2 != player)
            {
                return null;
            }

            return p1 == player ? p2 : p1;
        }

        public void ClearSpectatingDuel(Player player)
        {
            DuelRoom duelRoom = player.DuelRoom;
            if (duelRoom != null)
            {
                DuelRoomTable table = duelRoom.GetTableAsSpectator(player);
                if (table != null)
                {
                    lock (table.Spectators)
                    {
                        if (table.Spectators.Remove(player))
                        {
                            sessionServer.BroadcastMessageToTable(table, new DuelSpectatorCountMessage()
                            {
                                Count = table.Spectators.Count
                            });
                        }
                    }
                }
            }
            player.SpectatingPlayerCode = 0;
        }
    }
}
