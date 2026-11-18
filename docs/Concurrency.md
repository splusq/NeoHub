# Concurrency and Timing

NeoHub runs a security system, so a race that drops one zone update or leaves the panel
"not connected" is a real failure, not a cosmetic one. This document lists the timing and
race conditions found in a review in November 2026, what each one could do in practice, how
it was fixed, and which test pins the fix. It ends with the rules the code now follows and the
risks that remain.

## Threads that touch shared state

| Thread | Runs | Touches |
|---|---|---|
| Receive pump (one per connection) | `ITv2Session.ReceivePumpAsync` | reads the socket, acks every packet, matches replies to waiting requests, queues notifications |
| Senders (any thread) | `ITv2Session.SendAsync` from MediatR handlers, the web UI, WebSocket clients | sequence numbers, encryption, the socket, the list of waiting requests |
| Heartbeat (one per connection) | `ITv2Session.HeartbeatLoopAsync` | sends a poll every 100 s |
| Notification loop (one per connection) | `ITv2ConnectionHandler` → MediatR handlers | `PanelStateService`, which raises the WebSocket and UI events |
| Configuration pull (one per connect) | `PanelConfigurationHandler`, fire-and-forget from the session manager | `PanelStateService`, programming mode on the panel |
| WebSocket clients (one per client) | `PanelWebSocketHandler` | commands to the panel, replies to the client |
| Settings | handshake (`ConnectionSettingsProvider`), web UI, file watcher (`reloadOnChange`) | `userSettings.json`, `IOptionsMonitor.CurrentValue` |

## Findings

Severity: **Critical** can leave the alarm unreachable or its state wrong without recovery;
**High** loses or corrupts updates under ordinary load; **Medium** needs an unusual but
realistic sequence; **Low** is hardening.

### Panel session (TLink)

| # | Severity | Problem | What it could do | Fix | Test |
|---|---|---|---|---|---|
| T1 | Critical | Reconnect with the same panel ID was refused ("Session already registered"), and the old connection's cleanup then removed the ID. *(Fixed earlier in #3, listed for completeness.)* | Panel connected but unreachable for about 2 hours on 2026-11-17. | Newest connection replaces the stale one; unregister only removes its own entry. | `ITv2SessionManagerTests` |
| T2 | High | The receive pump wrote acks without the send lock, so an ack and a command could be written at the same time. `PipeWriter` and the shared AES instance allow only one writer. | Interleaved or corrupted frames; the panel drops the session or ignores a command. | One `_writeLock` covers every write (commands, notifications, acks, END_SESSION), including sequence numbers and encryption. | `ConcurrentSendsAndInboundTraffic_NeverOverlapWrites` |
| T3 | High | The list of requests waiting for replies was a plain `List`, changed by senders while the pump enumerated it. | "Collection was modified" inside the pump: logged as a fatal error, and the session ends. | Every access holds `_receiversLock`. | same as T2 |
| T4 | High | Replies are matched partly by message type, and any number of commands could be in flight. Two requests of the same type, or one plus an unsolicited panel message of that type, could swap replies. | One partition's status applied to another; a config read gets the wrong section. | `_transactionGate`: one command transaction in flight per session. Notifications (the heartbeat poll) are matched by sequence number and skip it. | `SameTypeRequests_EachGetsItsOwnReply` |
| T5 | High | No timeout on any command. | An arm or disarm the panel never answers hangs the caller (and the WebSocket client that asked) until the session dies. | Every send has a deadline (`ITv2SessionTimings.CommandTimeout`, 15 s) and returns `TLinkErrorCode.Timeout`; a closed session returns `Disconnected`. Only the caller's own token throws. | `UnansweredCommand_FailsWithTimeout_InsteadOfHanging`, `PanelHangsUp_PendingCommandFailsWithDisconnected` |
| T6 | High | A request that timed out or was cancelled stayed registered, and a completed receiver still claimed matching packets. | The reply to the *next* request of that type was swallowed, so it timed out too. | Receivers are removed in `finally`; completed receivers are skipped. | `TimedOutRequest_DoesNotSwallowTheNextReply` |
| T7 | Medium | Readiness waited for 2 s of silence after connect, with no upper bound. | A panel that never goes quiet blocks every send forever. | Ready after the quiet period or `MaxReadyWait` (10 s), whichever comes first. | `PanelThatNeverGoesQuiet_StillBecomesReady` |
| T8 | Medium | The readiness timer disposed itself in its callback while the pump could call `Change` on it. | `ObjectDisposedException` in the pump: the session ends. | Callback only completes the task; the timer is disposed once, in `DisposeAsync`. | covered by every session test |
| T9 | Medium | The heartbeat waited for a reply forever, so it never noticed a half-open connection. *(Fixed in #3.)* | A panel that lost power stays "connected" indefinitely. | Each poll waits `HeartbeatAckTimeout`; after `MaxMissedHeartbeats` silent polls the session closes. | `UnresponsivePanel_HeartbeatClosesSession`, `ResponsivePanel_HeartbeatKeepsSessionOpen` |
| T10 | Medium | One malformed frame ended `ReadAllAsync`, which ended the session. | A single line glitch disconnects the alarm. | Framing and encoding errors are reported and skipped; only a closed or cancelled connection ends the stream. | `MalformedFrame_IsReported_ThenReadingContinues`, `RecoverableTransportError_DoesNotEndSession` |
| T11 | Low | Bytes without a frame delimiter were buffered without limit. | Memory grows until the process is killed. | Over 16 KB without a delimiter is dropped and reported; the stream resynchronizes on the next frame. | `GarbageWithNoDelimiter_IsDropped_AndStreamResynchronizes` |
| T12 | Medium | Dispose completed the pipe while the pump could still be reading it, and disposed the send lock and cancellation source while other threads used them. | `ObjectDisposedException` / invalid pipe state on disconnect, or a send that throws instead of failing cleanly. | Dispose cancels, waits (bounded) for the pump, heartbeat and any writer, then closes the transport. The lock and cancellation source are never disposed. | `DisposeWhileSending_EverySendCompletes_WithoutThrowing`, `SendAfterDispose_ReturnsDisconnected`, `ITv2SessionDisposeTests` |
| T13 | Low | A peer that connected and never finished the handshake was kept forever. | Leaked connections. | `HandshakeTimeout` (30 s). | — |
| T14 | Low | When the panel hung up, notifications already queued were dropped. | The last zone change before a disconnect is lost. | The notification stream ends when the queue drains, not when the session is cancelled. | `NotificationsQueuedBeforeHangUp_AreStillDelivered` |

### Application (NeoHub)

| # | Severity | Problem | What it could do | Fix | Test |
|---|---|---|---|---|---|
| N1 | Critical | Each state change started its own fire-and-forget WebSocket broadcast. A WebSocket allows only one send at a time. | Two quick changes: the second send throws and that update is **dropped**, or they arrive out of order (a door that closed shows open in HomeKit until its next change). | `WebSocketClient`: one bounded queue and one send loop per client. Events are serialized synchronously, so each client sees them in the order they were applied. | `BurstOfZoneChanges_ArrivesComplete_AndInOrder`, `ManyProducers_SendsNeverOverlap_AndEverythingIsDelivered`, `SingleProducer_OrderIsPreserved` |
| N2 | Critical | On disconnect, `ConcurrentBag.TryTake` removed an **arbitrary** client, not the one leaving. | With two clients (Homebridge plus a browser, or a Homebridge restart), the client that stayed could silently stop getting updates. | Clients are tracked by ID in a `ConcurrentDictionary` and remove themselves. | `OneClientDisconnecting_DoesNotStopUpdatesToAnother` |
| N3 | High | `SessionState.Partitions` and `Zones` were plain `Dictionary` objects, written by the notification loop and the configuration pull and read by the UI and WebSocket at the same time. | Exceptions while building `full_state`; concurrent `Dictionary` writes can corrupt it (lost entries, infinite loops). | `ConcurrentDictionary`. Zone partition lists are replaced, never edited in place. | `ConcurrentWritersAndReaders_DoNotCorruptState` |
| N4 | High | The configuration pull wrote status from its reply, on its own thread, after live notifications may already have applied a newer change. | A door that closed while the pull was running shows open again. | Pulled status is applied only if the entity hasn't changed since the request was sent (`ShouldApplyPulledStatus`). | `PulledStatusGuardTests` |
| N5 | High | The configuration pull ran with no cancellation. After a reconnect the old connection's pull kept sending commands, which now went to the new connection, alongside the new pull. | Two pulls entering and leaving programming mode at once; state written for the wrong connection. | The connected notification carries the connection's `Lifetime`; the pull stops when its connection ends. | `Reconnect_*` (manager), runtime log |
| N6 | High | The programming-mode lock lived in `SessionState`, which is removed on disconnect and recreated on reconnect. The pull also released `session.ConfigLock` after reassigning `session`. | Operations for the old and new connection held different locks; a release on the wrong semaphore (`SemaphoreFullException`, or two holders). | One lock per panel ID for the life of the process (`PanelStateService.GetConfigLock`); the pull releases the lock it acquired. | `ConfigLock_IsTheSameAcrossDisconnectAndReconnect` |
| N7 | High | Zone bypass entered and left programming mode without the programming lock. | Bypass during the connect pull or a user-code change: the panel rejects one of them, or one leaves the panel in programming mode for the other. | Bypass takes the lock (30 s bounded wait, then a clear "panel busy" error). | — |
| N8 | High | Settings were written in place (`File.WriteAllText`), and `userSettings.json` is watched with `reloadOnChange`. | A reload during the write, or a power cut, reads an empty or partial file. Settings vanish, including the panel's encryption key, and the panel can't reconnect. | `AtomicFile`: write a temp file in the same directory, flush to disk, rename over the target. Also used for the user list and panel config exports and the startup migration. | `ConcurrentReader_NeverSeesAPartialFile` |
| N9 | Medium | `ConnectionSettingsProvider` changed the shared settings list without a lock and found a new placeholder with `connections.Last()`. | Two unknown panels at once, or a reload in between, returned another panel's entry. | A lock around resolve and confirm; the placeholder method returns the entry it created; saves serialize a snapshot. | — |
| N10 | Low | WebSocket requests larger than 4 KB arrived in several frames and were parsed one frame at a time. | Larger requests rejected as invalid JSON. | Frames are assembled up to 64 KB; larger messages disconnect the client. | `OversizedMessage_DisconnectsTheClient` |

## Rules the code follows now

1. **One writer per socket.** Everything written to the panel goes through `ITv2Session._writeLock`.
   Everything written to a WebSocket goes through that client's `WebSocketClient` queue.
2. **One command transaction per panel at a time** (`_transactionGate`). Multi-step programming
   sequences (enter → read/write → exit) additionally hold the per-panel programming lock.
3. **Every wait is bounded**: handshake 30 s, command 15 s, readiness 10 s, heartbeat
   3 × (100 s + 30 s), dispose 2 s per step, programming lock 30 s, WebSocket send 10 s.
4. **Work tied to a connection carries that connection's `Lifetime` token** and stops when it fires.
5. **Shared collections are concurrent or copy-on-write.** Lists inside shared state objects are
   replaced, not edited in place.
6. **Events are raised and serialized synchronously; delivery is asynchronous.** That keeps per-client
   order equal to apply order without letting a slow client block the panel.
7. **Files that something else reads are replaced atomically.**

## Remaining risks

- **Late replies.** If the panel answers a request after its 15 s timeout, the late reply can still be
  matched to the next request of the same type. The gate and the long timeout make this unlikely; the
  protocol has no request ID to rule it out completely.
- **Field-level updates.** Notification handlers change `ZoneState` and `PartitionState` objects in place.
  Different handlers touch different fields, so updates aren't lost, but a reader can see a mix of an
  old and a new field for an instant. The next event corrects it.
- **Synchronous event subscribers.** `PanelStateService` events run on the notification loop. The
  WebSocket handler only queues, but any new subscriber must not block.
- **Corrupt settings file.** If `userSettings.json` is ever unreadable, `SettingsPersistenceService` starts
  from an empty document and the next save keeps only the section being saved. Atomic writes make a
  corrupt file far less likely; they don't repair one.
- **Cancelled programming sequences.** `ExecuteInConfigModeAsync` exits programming mode with the
  caller's token. That is right for the connection-tied pull (an ended connection's exit must not reach
  the next connection, and the next pull exits first if needed), but a user-initiated read cancelled
  mid-way also skips the exit and leaves the panel in programming mode until its own timeout.
- **Sequence wraparound.** ITv2 sequence numbers are one byte and wrap; behavior at the wrap point is
  untested against a real panel.

## Testing

`NeoHub/TLink.Tests` drives a real `ITv2Session` over `ScriptedTransport`, an in-memory panel that
records writes, detects overlapping ones and answers requests. `NeoHub/NeoHub.Tests` drives
`PanelWebSocketHandler` and `WebSocketClient` over `FakeWebSocket`, which detects overlapping sends.
Timings are passed per session (`ITv2SessionTimings`), so tests run in milliseconds and in parallel.

The key regression tests were checked against deliberately broken builds: removing the transaction gate,
letting acks bypass the write lock, or leaving timed-out requests registered each makes its test fail.
CI runs both projects with `--blame-hang-timeout`, so a deadlock fails the build with a dump instead of
hanging it.
