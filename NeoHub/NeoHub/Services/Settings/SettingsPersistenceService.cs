using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace NeoHub.Services.Settings
{
    /// <summary>Reads and persists settings sections in persist/userSettings.json.</summary>
    public interface ISettingsPersistenceService
    {
        object GetSettings(Type settingsType);

        /// <summary>Saves <paramref name="settings"/> as its section. The value is captured when this is called.</summary>
        Task SaveSettingsAsync(Type settingsType, object settings);

        /// <summary>
        /// Changes the live settings of type <typeparamref name="T"/> and saves them, one change at a
        /// time: <paramref name="change"/> runs on the store's loop (never concurrently with
        /// another change or save) and returns whether it changed anything.
        /// </summary>
        Task<bool> UpdateAsync<T>(Func<T, bool> change) where T : class;

        string PersistPath { get; }
    }

    /// <summary>
    /// The only writer of userSettings.json. Saves and changes are messages in one channel,
    /// handled in order by one loop; each batch is written once, atomically (temp file, then
    /// rename), so neither a crash nor the configuration reloader ever sees a half-written file.
    /// </summary>
    public sealed class SettingsPersistenceService : ISettingsPersistenceService, IAsyncDisposable
    {
        private const string PersistFolder = "persist";
        private const string SettingsFileName = "userSettings.json";
        private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

        private abstract record Message(TaskCompletionSource<bool> Done);
        private sealed record Save(string Section, JsonNode? Value, TaskCompletionSource<bool> Done) : Message(Done);
        private sealed record Change(Type SettingsType, Func<object, bool> Apply, TaskCompletionSource<bool> Done) : Message(Done);

        private readonly Channel<Message> _inbox = Channel.CreateUnbounded<Message>(new UnboundedChannelOptions { SingleReader = true });
        private readonly Func<Type, object> _current;
        private readonly ILogger<SettingsPersistenceService> _log;
        private readonly Task _run;

        /// <summary>
        /// Returns the persist folder path. Checks NEOHUB_PERSIST_PATH env var first,
        /// falls back to {contentRootPath}/persist.
        /// </summary>
        public static string GetPersistPath(string contentRootPath)
        {
            var envPath = Environment.GetEnvironmentVariable("NEOHUB_PERSIST_PATH");
            return !string.IsNullOrEmpty(envPath) ? envPath : Path.Combine(contentRootPath, PersistFolder);
        }

        /// <summary>
        /// Returns the absolute path to userSettings.json.
        /// Used by Program.cs (AddJsonFile), SettingsMigration, and this service.
        /// </summary>
        public static string GetSettingsFilePath(string contentRootPath)
            => Path.Combine(GetPersistPath(contentRootPath), SettingsFileName);

        public string PersistPath { get; }
        internal string SettingsFilePath { get; }

        public SettingsPersistenceService(IServiceProvider serviceProvider, IWebHostEnvironment env, ILogger<SettingsPersistenceService> log)
            : this(GetPersistPath(env.ContentRootPath), type => CurrentValue(serviceProvider, type), log) { }

        internal SettingsPersistenceService(string persistPath, Func<Type, object> current, ILogger<SettingsPersistenceService> log)
        {
            _current = current;
            _log = log;
            PersistPath = persistPath;
            SettingsFilePath = Path.Combine(PersistPath, SettingsFileName);
            Directory.CreateDirectory(PersistPath);
            _log.LogInformation("Settings file: {Path}", SettingsFilePath);
            _run = RunAsync();
        }

        public object GetSettings(Type settingsType) => _current(settingsType);

        public Task SaveSettingsAsync(Type settingsType, object settings)
        {
            // Captured now, on the caller: later edits to the object don't leak into this save.
            var value = JsonSerializer.SerializeToNode(settings, settingsType);
            return Post(new Save(SectionName(settingsType), value, NewDone())).Task;
        }

        public Task<bool> UpdateAsync<T>(Func<T, bool> change) where T : class
            => Post(new Change(typeof(T), o => change((T)o), NewDone())).Task;

        public async ValueTask DisposeAsync()
        {
            _inbox.Writer.TryComplete();
            await _run;
        }

        private static TaskCompletionSource<bool> NewDone() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private TaskCompletionSource<bool> Post(Message message)
        {
            if (!_inbox.Writer.TryWrite(message))
                message.Done.TrySetException(new ObjectDisposedException(nameof(SettingsPersistenceService)));
            return message.Done;
        }

        private async Task RunAsync()
        {
            await Task.Yield();
            var batch = new List<Message>();
            while (await _inbox.Reader.WaitToReadAsync())
            {
                // Everything queued so far goes into one write.
                batch.Clear();
                while (_inbox.Reader.TryRead(out var m))
                    batch.Add(m);

                var root = ReadRoot();
                var changed = new List<(Message Message, bool Changed)>();
                foreach (var message in batch)
                {
                    try
                    {
                        switch (message)
                        {
                            case Save save:
                                root[save.Section] = save.Value;
                                _log.LogInformation("Saving settings for section {Section}", save.Section);
                                changed.Add((save, true));
                                break;
                            case Change change:
                                var current = _current(change.SettingsType);
                                var did = change.Apply(current);
                                if (did)
                                    root[SectionName(change.SettingsType)] = JsonSerializer.SerializeToNode(current, change.SettingsType);
                                changed.Add((change, did));
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        message.Done.TrySetException(ex);
                    }
                }

                if (changed.Count == 0)
                    continue;
                try
                {
                    if (changed.Any(c => c.Changed))
                        await AtomicFile.WriteAllTextAsync(SettingsFilePath, root.ToJsonString(WriteOptions), _log);
                    foreach (var (message, did) in changed)
                        message.Done.TrySetResult(did);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Failed to write {File}", SettingsFilePath);
                    foreach (var (message, _) in changed)
                        message.Done.TrySetException(ex);
                }
            }
        }

        private JsonObject ReadRoot()
        {
            if (!File.Exists(SettingsFilePath))
                return new JsonObject();
            try
            {
                var json = File.ReadAllText(SettingsFilePath);
                return string.IsNullOrWhiteSpace(json) ? new JsonObject() : JsonNode.Parse(json) as JsonObject ?? new JsonObject();
            }
            catch (JsonException ex)
            {
                _log.LogWarning(ex, "Invalid JSON in {File}, starting fresh", SettingsFilePath);
                return new JsonObject();
            }
        }

        private static string SectionName(Type settingsType)
            => settingsType.GetField("SectionName", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                   ?.GetValue(null)?.ToString() ?? settingsType.Name;

        private static object CurrentValue(IServiceProvider services, Type settingsType)
        {
            var monitorType = typeof(IOptionsMonitor<>).MakeGenericType(settingsType);
            var monitor = services.GetRequiredService(monitorType);
            return monitorType.GetProperty("CurrentValue")!.GetValue(monitor)!;
        }
    }

    /// <summary>Replaces a file's contents so readers see either the old file or the new one, never a mix.</summary>
    public static class AtomicFile
    {
        public static async Task WriteAllTextAsync(string path, string contents, ILogger? log = null)
        {
            var temp = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await using (var writer = new StreamWriter(stream))
                {
                    await writer.WriteAsync(contents);
                    await writer.FlushAsync();
                    stream.Flush(flushToDisk: true);
                }
                try
                {
                    File.Move(temp, path, overwrite: true);
                }
                catch (IOException ex)
                {
                    // E.g. the file itself is a bind mount, which can't be replaced by rename.
                    log?.LogWarning("Could not replace {Path} atomically ({Error}); writing it in place", path, ex.Message);
                    await File.WriteAllTextAsync(path, contents);
                }
            }
            finally
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
        }
    }
}
