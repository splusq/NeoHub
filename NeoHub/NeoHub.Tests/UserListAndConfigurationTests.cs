using DSC.TLink.ITv2.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using NeoHub.Domain;
using NeoHub.Services;
using NeoHub.Services.Models;
using NeoHub.Services.PanelConfiguration;
using NeoHub.Services.Settings;

namespace NeoHub.Tests;

public class UserListAndConfigurationTests
{
    private const string Panel = "200000000001";
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset T = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static PanelStoreState Run(params PanelEvent[] events)
    {
        var state = PanelStoreState.Empty;
        foreach (var e in events)
            state = PanelReducer.Apply(state, e).State;
        return state;
    }

    private static PanelUserState User(int index, string code) => new() { UserIndex = index, CodeValue = code, CodeLength = code.Length };

    [Fact]
    public void UserListUpdated_AppliesTheUpdate_InOrder()
    {
        var state = Run(
            new SessionStarted(Panel, A, T),
            new UserListUpdated(Panel, l => l with { IsReading = true, ReadTotal = 72 }),
            new UserListUpdated(Panel, l => l with { Users = l.Users.SetItem(1, User(1, "0000")) }),
            new UserListUpdated(Panel, l => l with { IsReading = false }));

        var list = state.Sessions[Panel].UserList;
        Assert.False(list.IsReading);
        Assert.Equal(72, list.ReadTotal);
        Assert.Equal("0000", list.Users[1].CodeValue);
    }

    [Fact]
    public void UserLabels_FromThePanel_ApplyOnlyToUsersAlreadyRead()
    {
        var state = Run(
            new SessionStarted(Panel, A, T),
            new UserListUpdated(Panel, l => l with { Users = l.Users.SetItem(2, User(2, "0000")) }),
            new MessageReceived(Panel, A, new NotificationLabelText
            {
                Collection = NotificationLabelText.LabelCollection.User,
                Start = 2,
                End = 3,
                Labels = ["Guest        ", "Nobody"],
            }, T));

        var users = state.Sessions[Panel].UserList.Users;
        Assert.Equal("Guest", users[2].UserLabel);
        Assert.False(users.ContainsKey(3));
    }

    [Fact]
    public void AReader_KeepsItsSnapshot_WhileTheListChanges()
    {
        var before = Run(new SessionStarted(Panel, A, T)).Sessions[Panel].UserList;
        var after = PanelReducer.Apply(Run(new SessionStarted(Panel, A, T)),
            new UserListUpdated(Panel, l => l with { Users = l.Users.SetItem(1, User(1, "0000")) })).State.Sessions[Panel].UserList;

        Assert.Empty(before.Users);
        Assert.Single(after.Users);
    }

    [Fact]
    public void FileImport_IsAPureMerge()
    {
        var files = new PanelUserListFileService(new StubPersistence(), NullLogger<PanelUserListFileService>.Instance);
        var current = new PanelUserListState { Users = [KeyValuePair.Create(1, User(1, "0000")), KeyValuePair.Create(5, User(5, "0005"))] };
        var file = new PanelUserListFile
        {
            ExportedAtUtc = T.UtcDateTime,
            Users = [new PanelUserRecord { UserIndex = 5, Code = "1111", CodeLength = 4, Partitions = [1, 2] }],
        };

        var merged = files.MergeInto(current, file);

        Assert.Equal("0005", current.Users[5].CodeValue);       // input untouched
        Assert.Equal("1111", merged.Users[5].CodeValue);
        Assert.Equal("0000", merged.Users[1].CodeValue);        // slots not in the file are kept
        Assert.Equal(new byte[] { 1, 2 }, merged.Users[5].Partitions.ToArray());
        Assert.Equal(T.UtcDateTime, merged.LastReadAt);
    }

    [Fact]
    public void ConfigurationClone_IsIndependent_AndStartsIdentical()
    {
        var original = new PanelConfigurationState(new PanelCapabilities
        {
            MaxZones = 8, MaxPartitions = 2, MaxUsers = 8, MaxFOBs = 0, MaxProxTags = 0, MaxOutputs = 0,
        });
        // Zone labels arrive from the panel as UTF-16BE, 28 characters each.
        static byte[] Label(string text) => System.Text.Encoding.BigEndianUnicode.GetBytes(text.PadRight(28));
        original.ZoneLabels.ImportItem(1, Label("FRONT DOOR"));

        var copy = original.Clone();
        Assert.Equal(original.AllSections.Select(s => Convert.ToHexString(s.Export())),
                     copy.AllSections.Select(s => Convert.ToHexString(s.Export())));

        copy.ZoneLabels.ImportItem(1, Label("BACK DOOR"));
        Assert.StartsWith("FRONT DOOR", original.ZoneLabels[1]);
        Assert.StartsWith("BACK DOOR", copy.ZoneLabels[1]);
    }

    /// <summary>A section that failed to read must not break copying (or file export).</summary>
    [Fact]
    public void ConfigurationWithUnreadSections_ClonesWithoutCrashing()
    {
        var unread = new PanelConfigurationState(new PanelCapabilities
        {
            MaxZones = 8, MaxPartitions = 2, MaxUsers = 8, MaxFOBs = 0, MaxProxTags = 0, MaxOutputs = 0,
        });

        var copy = unread.Clone();

        Assert.All(copy.AllSections, s => Assert.Empty(s.Export()));
    }

    private sealed class StubPersistence : ISettingsPersistenceService
    {
        public string PersistPath { get; } = Directory.CreateTempSubdirectory("neohub-users-").FullName;
        public object GetSettings(Type settingsType) => throw new NotSupportedException();
        public Task SaveSettingsAsync(Type settingsType, object settings) => throw new NotSupportedException();
        public Task<bool> UpdateAsync<T>(Func<T, bool> change) where T : class => throw new NotSupportedException();
    }
}
