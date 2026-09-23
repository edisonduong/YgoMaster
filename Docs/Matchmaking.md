# Find Match implementation

## Implementation

- `GameServer.cs` retains the original server lifecycle and dispatch, adds four matchmaking requests, and sweeps stale entries every ten seconds.
- `Acts/Act_Room.cs` maintains an instance-owned FIFO queue under one lock. Repeated joins retain their original place. Matches require the same admitted regulation and client version.
- The selected standard deck is `GameMode.Rank`. Both deck checks and admission use `DeckInfo.DefaultRegulationId`. The queue always calls `DeckInfo.IsValid`, even when `DisableDeckValidation` is enabled.
- The validator now counts side-deck cards toward ban/limit checks and enforces the default three-copy cap for unlisted cards. Optional missing regulation sections no longer throw.
- Each player must have a connected session client; the server must have the normal PvP engine and card-data files.
- `Matchmaking.poll` refreshes a 45-second heartbeat. Total waiting time is not a timeout. Expired, disconnected, invalid, and deck-selection-changed entries are removed. Polling never implicitly rejoins.
- Pairing creates a private two-member room and calls the existing room battle-ready code for both players. This initializes seeds, coin flip, table hash, ticket, secret, and ready state. Life points and timer use room setting IDs, not literal values.
- Both clients receive duel loading data via their normal request responses. The client opens the native DuelStartViewController directly, using the shipped Matching/DuelStart prefab and native argument constants (the controller's PrefabPath references an unavailable development asset). The game displays VS, animates the coin toss, and lets the winner choose first or second before sending Duel.begin. The server accepts only the winner's choice, preserves it across retries, and retains the existing selection-timeout fallback. The room exists only as server-side PvP infrastructure. Deck legality is checked again in `Duel.matching` and before PvP begin.
- Cancellation is serialized with admission/pairing. A cancellation after pairing but before the table enters dueling releases both players. It never cancels an active duel. Expired startup rooms and completed rooms are reclaimed. The prior room-deck selection is restored when the temporary room is disbanded.
- The Colosseum UI reuses its original tiles: Ranked becomes Find Match / Cancel Search, Room opens PvP rooms, Casual opens the PvE duel starter, and Team selects the standard deck. Other tiles are disabled while searching. The event panel displays validity errors, queue position, and elapsed time. It polls every two seconds through `Request.Entry`. Leaving the screen cancels search. Estimated wait is explicitly unknown rather than fabricated.

## Request contract

All four requests use the existing authenticated game request envelope with empty params:

| Act | Behavior |
| --- | --- |
| `Matchmaking.check` | Validate without enrolling |
| `Matchmaking.join` | Validate and enroll idempotently |
| `Matchmaking.poll` | Read a match or heartbeat an existing entry |
| `Matchmaking.cancel` | Remove entry or release a match that has not entered dueling |

`Matchmaking` response fields: `state` (`idle`, `searching`, `matched`), `valid`, `error`, `position`, `wait_seconds`, and `estimated_wait_seconds` (null). A matched response includes `Duel` loading data and removes stale `Room.room_info`; it does not open a lobby. Validation failures return an idle state and a readable error; they do not create queue entries.

## Verification

Build the solution with Visual Studio MSBuild, Release/x64. Build and run the independent regression executable:

```powershell
msbuild Tests/MatchmakingTests.csproj /t:Build
./Tests/bin/MatchmakingTests.exe
```

The tests compile the actual server sources and exercise legal/illegal deck sizes, finish-specific ownership, banned side cards, aggregate card limits, concurrent duplicate joins, compatible FIFO pairing, private room startup, native room re-entry, the `Duel.matching` payload, revalidation, cancellation/pair races, heartbeat expiration, disconnects, and abandoned-room cleanup. They create loopback session sockets and isolated temporary file-existence fixtures. They do not load the native duel engine.

## Required live-client smoke test

The native game is not included in this checkout. UI layout, IL2CPP hooks, and a complete two-client duel have **not** been verified at runtime. Server tests verify the direct-start state transition and shared starting player, but native loading and a complete PvP duel still require runtime verification.

1. Run the multiplayer server with its normal PvP dependencies and two compatible clients with distinct tokens.
2. Click Duel on the home screen, then Find Match in the three-option menu (the other options remain Duel Room / PvP and Duel Starter / PvE). This opens Colosseum. Select Standard Deck on each client. Verify missing/illegal decks show errors and disable Find Match.
3. Join on the first client. Verify position and elapsed time update, and waiting more than 45 seconds stays queued while polling.
4. Join on the second client. Verify both go directly through duel loading into the same playable duel, after the native coin flip and winner's first/second selection, without a Room Match lobby, Ready click, or table selection.
5. Cancel before and during pairing. Verify both players can search again and no hidden room remains attached.
6. Disconnect a searching client; verify it cannot be paired after the next sweep. Complete a duel and verify searching again works.
7. Recheck ordinary manually-created rooms and solo duels.
