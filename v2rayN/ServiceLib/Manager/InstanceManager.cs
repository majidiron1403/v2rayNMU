namespace ServiceLib.Manager;

/// <summary>
///     One isolated v2rayN "profile": its own database (server list, subscriptions, routing),
///     its own guiNConfig.json (inbound port, settings) and its own running core process(es).
/// </summary>
public class InstanceItem
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>Local socks port assigned when the instance was created.</summary>
    public int Port { get; set; }

    /// <summary>true until the first time the instance was opened (used to apply the port once).</summary>
    public bool Fresh { get; set; }

    public override string ToString() => Name;
}

/// <summary>
///     Runs several independent instances inside ONE process and ONE UI.
///     The UI always shows the active instance; the core processes of the other instances keep running.
///     The default instance uses the legacy data layout, so existing installations keep working unchanged.
/// </summary>
public sealed class InstanceManager
{
    private static readonly Lazy<InstanceManager> _instance = new(() => new());
    public static InstanceManager Instance => _instance.Value;

    public const string DefaultId = "default";
    private const string _fileName = "instances.json";
    private const string _dirName = "instances";
    private const int _portStep = 100;
    private const string _tag = "InstanceManager";

    private readonly object _lock = new();
    private readonly SemaphoreSlim _switchLock = new(1, 1);
    private readonly Dictionary<string, CoreManager.RunState> _background = new();
    private List<InstanceItem> _items = [];

    public string CurrentId { get; private set; } = DefaultId;

    public bool IsSecondary => CurrentId != DefaultId;

    public IReadOnlyList<InstanceItem> Items
    {
        get
        {
            lock (_lock)
            {
                return _items.ToList();
            }
        }
    }

    public InstanceItem Current => Items.FirstOrDefault(t => t.Id == CurrentId) ?? Items.First();

    private InstanceManager()
    {
        Load();
    }

    #region Persistence

    private static string FilePath => Path.Combine(Utils.StartupPath(), _fileName);

    private void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                _items = JsonUtils.Deserialize<List<InstanceItem>>(File.ReadAllText(FilePath)) ?? [];
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }

        if (_items.All(t => t.Id != DefaultId))
        {
            _items.Insert(0, new InstanceItem { Id = DefaultId, Name = "Default" });
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonUtils.Serialize(_items, true) ?? "[]");
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    private static string DirOf(string id)
    {
        return id == DefaultId ? Utils.StartupPath() : Path.Combine(Utils.StartupPath(), _dirName, id);
    }

    #endregion Persistence

    #region Items

    public InstanceItem Add(string? name)
    {
        lock (_lock)
        {
            var id = Utils.GetGuid(false)[..8];
            var port = Math.Max(10808, _items.Select(t => t.Port).DefaultIfEmpty(10808).Max()) + _portStep;
            var item = new InstanceItem
            {
                Id = id,
                Name = name.IsNullOrEmpty() ? $"Instance {_items.Count}" : name.Trim(),
                Port = port,
                Fresh = true,
            };
            Directory.CreateDirectory(DirOf(id));
            _items.Add(item);
            Save();
            return item;
        }
    }

    public void Rename(string id, string name)
    {
        lock (_lock)
        {
            var item = _items.FirstOrDefault(t => t.Id == id);
            if (item == null || name.IsNullOrEmpty())
            {
                return;
            }
            item.Name = name.Trim();
            Save();
        }
    }

    public bool IsRunning(string id)
    {
        if (id == CurrentId)
        {
            return CoreManager.Instance.IsCoreRunning;
        }
        return _background.TryGetValue(id, out var state) && state.IsRunning;
    }

    #endregion Items

    #region Switch

    /// <summary>
    ///     Save the active instance, keep its cores running, and load another instance into the UI.
    ///     Subscribers of <see cref="AppEvents.InstanceChanged" /> refresh their lists afterwards.
    /// </summary>
    public async Task<bool> SwitchTo(string id)
    {
        if (id == CurrentId || Items.All(t => t.Id != id))
        {
            return false;
        }

        await _switchLock.WaitAsync();
        try
        {
            var app = AppManager.Instance;

            // 1. persist the instance that is leaving
            await ConfigHandler.SaveConfig(app.Config);
            await ProfileExManager.Instance.SaveTo();
            await StatisticsManager.Instance.SaveTo();
            StatisticsManager.Instance.Close();

            // 2. keep its core processes alive in the background
            var state = CoreManager.Instance.Detach();
            if (state != null)
            {
                _background[CurrentId] = state;
            }

            // 3. point every path / database / config to the new instance
            Utils.InstanceDataDir = id == DefaultId ? null : DirOf(id);
            Directory.CreateDirectory(Utils.DataPath());
            SQLiteHelper.Instance.Reopen();
            app.EnsureTables();
            if (!app.ReloadConfigInPlace())
            {
                return false;
            }
            CurrentId = id;

            // 4. first time: give the instance its own port and make sure it never touches the system proxy
            var item = Current;
            if (item.Fresh)
            {
                if (app.Config.Inbound.Count > 0 && item.Port > 0)
                {
                    app.Config.Inbound[0].LocalPort = item.Port;
                }
                app.Config.SystemProxyItem.SysProxyType = ESysProxyType.ForcedClear;
                item.Fresh = false;
                lock (_lock)
                {
                    Save();
                }
                await ConfigHandler.SaveConfig(app.Config);
            }

            // 5. re-init the data bound managers for the new instance
            await ConfigHandler.InitBuiltinDNS(app.Config);
            await ConfigHandler.InitBuiltinFullConfigTemplate(app.Config);
            await ProfileExManager.Instance.Init();
            if (_background.Remove(id, out var restored))
            {
                CoreManager.Instance.Attach(restored);
            }
            if (app.Config.GuiItem.EnableStatistics || app.Config.GuiItem.DisplayRealTimeSpeed)
            {
                await StatisticsManager.Instance.Init(app.Config, _statisticsUpdate ?? (_ => Task.CompletedTask));
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return false;
        }
        finally
        {
            _switchLock.Release();
        }

        AppEvents.InstanceChanged.Publish(id);
        return true;
    }

    private Func<ServerSpeedItem, Task>? _statisticsUpdate;

    public void SetStatisticsHandler(Func<ServerSpeedItem, Task> handler)
    {
        _statisticsUpdate = handler;
    }

    /// <summary>Stop the cores of one instance (active or background).</summary>
    public async Task StopInstance(string id)
    {
        if (id == CurrentId)
        {
            await CoreManager.Instance.CoreStop();
            return;
        }

        if (_background.Remove(id, out var state))
        {
            await state.StopAsync();
        }
    }

    public async Task StopAllBackground()
    {
        foreach (var id in _background.Keys.ToList())
        {
            await StopInstance(id);
        }
    }

    /// <summary>Delete an instance and its data. The active and the default instance cannot be removed.</summary>
    public async Task<bool> Remove(string id)
    {
        if (id == DefaultId || id == CurrentId)
        {
            return false;
        }

        await StopInstance(id);
        lock (_lock)
        {
            _items.RemoveAll(t => t.Id == id);
            Save();
        }
        try
        {
            var dir = DirOf(id);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
        return true;
    }

    #endregion Switch
}
