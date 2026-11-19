using System.Collections.Immutable;
using DSC.TLink.ITv2.Enumerations;
using DSC.TLink.ITv2.Messages;
using NeoHub.Services.Models;

namespace NeoHub.Domain
{
    /// <summary>All panels' state, and the version of the last event applied.</summary>
    public sealed record PanelStoreState(ImmutableDictionary<string, SessionState> Sessions, long Version)
    {
        public static PanelStoreState Empty { get; } = new(ImmutableDictionary<string, SessionState>.Empty, 0);
    }

    /// <summary>
    /// Panel state as a pure function: <c>Apply(state, event) → (state, changes)</c>. No clock (time
    /// comes in the events), no I/O, no mutation; the same events always give the same state.
    ///
    /// Ported from the eight notification handlers and the configuration pull's writes, keeping
    /// their behavior, with one structural difference: an event scoped to a connection that is no
    /// longer the panel's current one changes nothing.
    /// </summary>
    public static class PanelReducer
    {
        public static (PanelStoreState State, ImmutableArray<PanelChange> Changes) Apply(PanelStoreState state, PanelEvent e)
        {
            var version = state.Version + 1;
            var changes = ImmutableArray.CreateBuilder<PanelChange>();
            var sessions = state.Sessions;

            switch (e)
            {
                case SessionStarted s:
                {
                    var fresh = new SessionState
                    {
                        SessionId = s.SessionId,
                        ConnectionId = s.Connection,
                        ConnectionPhase = ConnectionPhase.Handshake,
                        ConnectedAt = s.At.UtcDateTime,
                        LastMessageAt = s.At.UtcDateTime,
                        LastUpdated = s.At.UtcDateTime,
                    };
                    sessions = sessions.SetItem(s.SessionId, fresh);
                    changes.Add(new SessionChanged(s.SessionId, fresh));
                    break;
                }

                case SessionsListChanged:
                    changes.Add(new SessionsChanged());
                    break;

                case SessionEnded ended:
                {
                    if (sessions.TryGetValue(ended.SessionId, out var current) && current.ConnectionId == ended.Connection)
                    {
                        sessions = sessions.Remove(ended.SessionId);
                        changes.Add(new SessionRemoved(ended.SessionId));
                    }
                    break;
                }

                default:
                {
                    if (!sessions.TryGetValue(e.SessionId, out var session) || session.ConnectionId == Guid.Empty)
                        break;   // no live connection for this panel
                    if (e.ConnectionId is { } connection && connection != session.ConnectionId)
                        break;   // from a connection that has been replaced

                    var updated = ApplyToSession(session, e, version, changes);
                    if (!ReferenceEquals(updated, session))
                        sessions = sessions.SetItem(e.SessionId, updated);
                    break;
                }
            }

            return (new PanelStoreState(sessions, version), changes.ToImmutable());
        }

        private static SessionState ApplyToSession(SessionState s, PanelEvent e, long version, ImmutableArray<PanelChange>.Builder changes)
        {
            switch (e)
            {
                case MessageReceived m:
                    return Message(s, m, version, changes);

                case ConnectionPhaseChanged p:
                    return Changed(s with { ConnectionPhase = p.Phase }, changes);

                case CapabilitiesRead c:
                    return Changed(s with { MaxZones = c.MaxZones, MaxPartitions = c.MaxPartitions, MaxUsers = c.MaxUsers }, changes);

                case ConfigurationReplaced c:
                    return Changed(s with { Configuration = c.Configuration }, changes);

                case UserListUpdated u:
                    return Changed(s with { UserList = u.Update(s.UserList) }, changes);

                case InstallerSetupApplied setup:
                    return Setup(s, setup, changes);

                case PartitionStatusRead read:
                    return PulledPartition(s, read, changes);

                case ZoneStatusRead read:
                    return PulledZones(s, read, changes);

                case InitialPullFinished done:
                {
                    var next = s with
                    {
                        IsInitialized = true,
                        ConnectionPhase = done.Completed ? ConnectionPhase.Connected : s.ConnectionPhase,
                    };
                    Changed(next, changes);
                    if (done.Completed)
                        changes.Add(new ConfigurationCompleted(s.SessionId));
                    return next;
                }

                default:
                    return s;
            }
        }

        // ── Live notifications (were the MediatR notification handlers) ──

        private static SessionState Message(SessionState s, MessageReceived m, long version, ImmutableArray<PanelChange>.Builder changes)
        {
            var at = m.At.UtcDateTime;
            switch (m.Message)
            {
                case NotificationArmDisarm msg:
                {
                    var p = Partition(s, (byte)msg.Partition) with
                    {
                        Status = msg.ArmMode switch
                        {
                            ArmingMode.Disarm => PartitionStatus.Disarmed,
                            ArmingMode.AwayArm or ArmingMode.AwayArmWithNoEntryDelay => PartitionStatus.ArmedAway,
                            ArmingMode.StayArm or ArmingMode.StayArmWithNoEntryDelay => PartitionStatus.ArmedHome,
                            ArmingMode.NightArm or ArmingMode.NightArmWithNoEntryDelay => PartitionStatus.ArmedNight,
                            ArmingMode.ArmWithNoEntryDelay => PartitionStatus.ArmedAway,
                            _ => PartitionStatus.ArmedAway,
                        },
                        LastUpdated = at,
                        Version = version,
                    };
                    if (msg.ArmMode == ArmingMode.Disarm)
                        p = WithoutExitDelay(p);
                    return Put(s, p, at, changes);
                }

                case NotificationExitDelay msg:
                {
                    var p = Partition(s, (byte)msg.Partition);
                    if (msg.IsActive && msg.DurationInSeconds > 0)
                    {
                        var restart = !p.ExitDelayActive || p.ExitDelayDurationSeconds != msg.DurationInSeconds;
                        p = p with
                        {
                            ExitDelayActive = true,
                            ExitDelayStartedAt = restart ? at : p.ExitDelayStartedAt,
                            ExitDelayDurationSeconds = restart ? msg.DurationInSeconds : p.ExitDelayDurationSeconds,
                            ExitDelayAudible = msg.AudibleDelay,
                            ExitDelayUrgent = msg.IsUrgent,
                        };
                    }
                    else
                    {
                        p = WithoutExitDelay(p);
                    }
                    return Put(s, p with { LastUpdated = at, Version = version }, at, changes);
                }

                case NotificationPartitionReadyStatus msg:
                {
                    // Kept from the previous handler: a ready-status notification marks the
                    // partition disarmed. Open question whether the panel ever sends one while armed.
                    var p = WithoutExitDelay(Partition(s, msg.PartitionNumber)) with
                    {
                        IsReady = msg.Status is NotificationPartitionReadyStatus.PartitionReadyStatusEnum.ReadyToArm
                                             or NotificationPartitionReadyStatus.PartitionReadyStatusEnum.ReadyToForceArm,
                        Status = PartitionStatus.Disarmed,
                        LastUpdated = at,
                        Version = version,
                    };
                    return Put(s, p, at, changes);
                }

                case NotificationLifestyleZoneStatus msg:
                {
                    var z = Zone(s, msg.ZoneNumber);
                    z = z with
                    {
                        IsOpen = msg.Status == NotificationLifestyleZoneStatus.LifeStyleZoneStatusCode.Open,
                        // No protocol-level zone→partition mapping yet: default to partition 1.
                        Partitions = z.Partitions.IsEmpty ? [1] : z.Partitions,
                        LastUpdated = at,
                        Version = version,
                    };
                    return Put(s, z, at, changes);
                }

                case SingleZoneBypassStatus msg:
                {
                    var z = Zone(s, msg.ZoneNumber) with
                    {
                        IsBypassed = msg.BypassStatus == BypassStatusEnum.Bypassed,
                        LastUpdated = at,
                        Version = version,
                    };
                    return Put(s, z, at, changes);
                }

                case NotificationDateTimeBroadcast msg:
                    return Changed(s with { PanelDateTime = msg.DateTime, PanelDateTimeSyncedAt = at, LastMessageAt = at, LastUpdated = at }, changes);

                case ProgrammingLeadInOut msg:
                    return Changed(s with { IsInProgrammingMode = msg.Programming == ProgrammingLeadInOut.ProgrammingType.LeadIn }, changes);

                case NotificationLabelText msg:
                    return Labels(s, msg, at, changes);

                default:
                    return s;
            }
        }

        private static SessionState Labels(SessionState s, NotificationLabelText msg, DateTime at, ImmutableArray<PanelChange>.Builder changes)
        {
            switch (msg.Collection)
            {
                case NotificationLabelText.LabelCollection.Zone:
                    for (int i = 0; i < msg.Labels.Length; i++)
                    {
                        var label = msg.Labels[i];
                        var line1 = Line(label, 0);
                        var line2 = Line(label, 14);
                        var z = Zone(s, (byte)(msg.Start + i)) with { DisplayNameLine1 = line1, DisplayNameLine2 = line2 };
                        s = Put(s, z, at, changes);
                    }
                    return s;

                case NotificationLabelText.LabelCollection.Partition:
                    // Only partitions that already exist get names (as before).
                    for (int i = 0; i < msg.Labels.Length; i++)
                    {
                        var number = (byte)(msg.Start + i);
                        if (s.Partitions.TryGetValue(number, out var p))
                            s = Put(s, p with { Name = msg.Labels[i].Trim() }, at, changes);
                    }
                    return s;

                case NotificationLabelText.LabelCollection.User:
                {
                    // Only users already read get labels (as before).
                    var users = s.UserList.Users;
                    for (int i = 0; i < msg.Labels.Length; i++)
                    {
                        var label = msg.Labels[i]?.Trim();
                        if (users.TryGetValue(msg.Start + i, out var user))
                            users = users.SetItem(msg.Start + i, user with { UserLabel = string.IsNullOrEmpty(label) ? null : label });
                    }
                    return ReferenceEquals(users, s.UserList.Users)
                        ? s
                        : Changed(s with { UserList = s.UserList with { Users = users } }, changes);
                }

                default:
                    return s;
            }
        }

        private static string? Line(string label, int start)
        {
            var line = label.Length > start ? label.Substring(start, Math.Min(14, label.Length - start)).Trim() : "";
            return line.Length == 0 ? null : line;
        }

        // ── Configuration pull ──

        private static SessionState Setup(SessionState s, InstallerSetupApplied setup, ImmutableArray<PanelChange>.Builder changes)
        {
            foreach (var p in setup.Partitions)
            {
                var current = Partition(s, p.Number);
                s = PutQuiet(s, p.Name is null ? current : current with { Name = p.Name }, changes);
            }
            foreach (var z in setup.Zones)
            {
                var current = Zone(s, z.Number);
                var next = current with
                {
                    Definition = z.Definition ?? current.Definition,
                    DisplayNameLine1 = z.HasLabel ? z.Line1 : current.DisplayNameLine1,
                    DisplayNameLine2 = z.HasLabel ? z.Line2 : current.DisplayNameLine2,
                    Partitions = z.Partitions,
                };
                s = PutQuiet(s, next, changes);
            }
            return s;
        }

        private static SessionState PulledPartition(SessionState s, PartitionStatusRead read, ImmutableArray<PanelChange>.Builder changes)
        {
            var r = read.Reading;
            var existing = s.Partitions.GetValueOrDefault(r.Partition);
            if (existing is not null && existing.Version > read.RequestedAtVersion)
                return s;   // changed live after the request: the reading is older

            var at = read.At.UtcDateTime;
            var p = (existing ?? new PartitionState { PartitionNumber = r.Partition }) with
            {
                Status = r.Status,
                IsReady = r.IsReady,
                LastUpdated = at,
            };
            p = r.ExitDelayInProgress
                ? p with { ExitDelayActive = true, ExitDelayStartedAt = p.ExitDelayActive ? p.ExitDelayStartedAt : at }
                : p with { ExitDelayActive = false, ExitDelayStartedAt = null };
            return Put(s, p, at, changes, touchSession: false);
        }

        private static SessionState PulledZones(SessionState s, ZoneStatusRead read, ImmutableArray<PanelChange>.Builder changes)
        {
            var at = read.At.UtcDateTime;
            foreach (var r in read.Readings)
            {
                var existing = s.Zones.GetValueOrDefault(r.Zone);
                if (existing is not null && existing.Version > read.RequestedAtVersion)
                    continue;   // changed live after the request
                var z = (existing ?? new ZoneState { ZoneNumber = r.Zone }) with
                {
                    IsOpen = r.Open,
                    IsFaulted = r.Faulted,
                    IsTampered = r.Tampered,
                    IsBypassed = r.Bypassed,
                    Partitions = existing is null || existing.Partitions.IsEmpty ? [1] : existing.Partitions,
                    LastUpdated = at,
                };
                s = Put(s, z, at, changes, touchSession: false);
            }
            return s;
        }

        // ── Helpers ──

        private static PartitionState Partition(SessionState s, byte number)
            => s.Partitions.GetValueOrDefault(number) ?? new PartitionState { PartitionNumber = number };

        private static ZoneState Zone(SessionState s, byte number)
            => s.Zones.GetValueOrDefault(number) ?? new ZoneState { ZoneNumber = number };

        private static PartitionState WithoutExitDelay(PartitionState p) => p with
        {
            ExitDelayActive = false,
            ExitDelayStartedAt = null,
            ExitDelayDurationSeconds = 0,
            ExitDelayAudible = false,
            ExitDelayUrgent = false,
        };

        private static SessionState Put(SessionState s, PartitionState p, DateTime at, ImmutableArray<PanelChange>.Builder changes, bool touchSession = true)
        {
            changes.Add(new PartitionChanged(s.SessionId, p));
            var next = s with { Partitions = s.Partitions.SetItem(p.PartitionNumber, p) };
            return touchSession ? next with { LastUpdated = at, LastMessageAt = at } : next;
        }

        private static SessionState Put(SessionState s, ZoneState z, DateTime at, ImmutableArray<PanelChange>.Builder changes, bool touchSession = true)
        {
            changes.Add(new ZoneChanged(s.SessionId, z));
            var next = s with { Zones = s.Zones.SetItem(z.ZoneNumber, z) };
            return touchSession ? next with { LastUpdated = at, LastMessageAt = at } : next;
        }

        private static SessionState PutQuiet(SessionState s, PartitionState p, ImmutableArray<PanelChange>.Builder changes)
        {
            changes.Add(new PartitionChanged(s.SessionId, p));
            return s with { Partitions = s.Partitions.SetItem(p.PartitionNumber, p) };
        }

        private static SessionState PutQuiet(SessionState s, ZoneState z, ImmutableArray<PanelChange>.Builder changes)
        {
            changes.Add(new ZoneChanged(s.SessionId, z));
            return s with { Zones = s.Zones.SetItem(z.ZoneNumber, z) };
        }

        private static SessionState Changed(SessionState s, ImmutableArray<PanelChange>.Builder changes)
        {
            changes.Add(new SessionChanged(s.SessionId, s));
            return s;
        }
    }
}
