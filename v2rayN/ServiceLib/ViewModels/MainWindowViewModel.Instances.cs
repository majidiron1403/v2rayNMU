namespace ServiceLib.ViewModels;

public class InstanceOption
{
    public string Id { get; init; } = string.Empty;
    public string Display { get; init; } = string.Empty;

    public override string ToString() => Display;
}

/// <summary>
///     Multi-instance support: several independent profiles (own server list, own port, own core)
///     in one process and one UI. See <see cref="InstanceManager" />.
/// </summary>
public partial class MainWindowViewModel
{
    private bool _instanceUiSync;

    public ReactiveCommand<RxVoid, RxVoid> AddInstanceCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> RemoveInstanceCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> StopInstanceCmd { get; private set; } = null!;

    [Reactive]
    public partial List<InstanceOption> Instances { get; set; }

    [Reactive]
    public partial InstanceOption? SelectedInstance { get; set; }

    [Reactive]
    public partial string NewInstanceName { get; set; }

    private void InitInstances()
    {
        Instances = [];
        NewInstanceName = string.Empty;

        AddInstanceCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            var item = InstanceManager.Instance.Add(NewInstanceName);
            NewInstanceName = string.Empty;
            RefreshInstances();
            await InstanceManager.Instance.SwitchTo(item.Id);
        });

        RemoveInstanceCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            // The active instance cannot be removed: move to the default one first, then delete the old one.
            var id = InstanceManager.Instance.CurrentId;
            if (id == InstanceManager.DefaultId)
            {
                return;
            }
            if (await InstanceManager.Instance.SwitchTo(InstanceManager.DefaultId))
            {
                await InstanceManager.Instance.Remove(id);
                RefreshInstances();
            }
        });

        StopInstanceCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await InstanceManager.Instance.StopInstance(InstanceManager.Instance.CurrentId);
            RefreshInstances();
        });

        this.WhenAnyValue(x => x.SelectedInstance)
            .Where(y => y != null)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .SubscribeAsync(async option =>
            {
                if (option == null)
                {
                    return;
                }
                if (_instanceUiSync || option.Id == InstanceManager.Instance.CurrentId)
                {
                    return;
                }
                await InstanceManager.Instance.SwitchTo(option.Id);
            });

        AppEvents.InstanceChanged
            .AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .SubscribeAsync(async _ =>
            {
                RefreshInstances();
                await RefreshServersDispatcherAsync();
                if (_config.UiItem.EnableAutoAdjustMainLvColWidth)
                {
                    await ProfilesViewModel.AdjustMainLvColWidth();
                }
            });

        InstanceManager.Instance.SetStatisticsHandler(UpdateStatisticsHandler);
        RefreshInstances();
    }

    private void RefreshInstances()
    {
        RxSchedulers.MainThreadScheduler.Schedule(() =>
        {
            _instanceUiSync = true;
            try
            {
                var manager = InstanceManager.Instance;
                var list = manager.Items.Select(t => new InstanceOption
                {
                    Id = t.Id,
                    Display = $"{(manager.IsRunning(t.Id) ? "●" : "○")} {t.Name}" + (t.Id == InstanceManager.DefaultId ? $" (:{_config.Inbound.FirstOrDefault()?.LocalPort ?? 10808})" : $" (:{t.Port})"),
                }).ToList();
                Instances = list;
                SelectedInstance = list.FirstOrDefault(t => t.Id == manager.CurrentId);
            }
            finally
            {
                _instanceUiSync = false;
            }
        });
    }
}
