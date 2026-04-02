using Daim.Xms.Common;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Threading.Channels;

namespace sas;

public interface ISafetyMonitoringService {
    Task WriteChannelAsync(object request, CancellationToken ct);
}

public class SafetyMonitoringService : BackgroundService, ISafetyMonitoringService {
    private readonly ILogger<SafetyMonitoringService> _logger;
    private readonly IConnectionService _connectionService;
    private readonly IDetectionMapService _detectionMapService;
    private readonly ISafetyMonitoringManager _safetyStateManager;
    private readonly IVehicleService _vehicleService;
    private readonly IHostApplicationLifetime _appLifetime;
    private readonly Channel<object> _eventChannel = Channel.CreateUnbounded<object>(new UnboundedChannelOptions {
        AllowSynchronousContinuations = false,
        SingleReader = true,
        SingleWriter = false,
    });
    private readonly GeneralOptions _options;

    private readonly Dictionary<DetectionMapKey, CancellationTokenSource> _pendingFinish = [];
    private readonly Lock _pendingLock = new();
    private record DwellFinishedEvent(DetectionMap DetectionMap, DetectionMapKey Key, CancellationTokenSource Cts);

    // ChannelId별 preset 대기 상태 (채널당 최대 1개)
    private record PresetWaiting(EventType ArrivedType, List<DetectionMap> Maps, CancellationTokenSource Cts);
    private readonly Dictionary<string, PresetWaiting> _pendingPreset = []; // key: ChannelId
    private readonly Lock _presetLock = new();

    // OnDetected가 호출된 키 추적 (이벤트 채널 단일 reader이므로 lock 불필요)
    private readonly HashSet<DetectionMapKey> _activatedKeys = [];

    public SafetyMonitoringService(
        IConnectionService connectionService,
        IDetectionMapService detectionMapService,
        ISafetyMonitoringManager safetyStateManager,
        IVehicleService vehicleService,
        IHostApplicationLifetime appLifetime,
        IOptions<GeneralOptions> generalOptions,
        ILogger<SafetyMonitoringService> logger) {
        _connectionService = connectionService;
        _logger = logger;
        _connectionService.OnStatusReceived += async (e) => await WriteChannelAsync(e);
        _detectionMapService = detectionMapService;
        _safetyStateManager = safetyStateManager;
        _vehicleService = vehicleService;
        _vehicleService.VehicleChanged += VehicleChangedEventHandler;
        _appLifetime = appLifetime;
        _options = generalOptions.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        _logger.LogInformation("SafetyMonitoringService ExecuteAsync started");
        while (!stoppingToken.IsCancellationRequested) {
            var eventBuffer = new Queue<object>();
            await foreach (var eventData in _eventChannel.Reader.ReadAllAsync(stoppingToken)) {
                eventBuffer.Enqueue(eventData);
                if (_eventChannel.Reader.TryPeek(out _)) continue;
                await HandleEvents(eventBuffer, stoppingToken);
            }
        }
    }

    private async void VehicleChangedEventHandler(object? sender, ChangedEvent<Vehicle> e) {
        try { await WriteChannelAsync(e); }
        catch (Exception ex) { _logger.LogError(ex, "Failed to enqueue vehicle changed event"); }
    }

    public async Task WriteChannelAsync(object request, CancellationToken ct = default) {
        await _eventChannel.Writer.WriteAsync(request, ct);
    }

    private async Task HandleEvents(Queue<object> events, CancellationToken ct) {
        while (events.Count > 0) {
            var bufferedEvent = events.Dequeue();
            switch (bufferedEvent) {
                case ScpStatus e: await HandleScpStatus(e, ct); break;
                case DwellFinishedEvent e: await HandleDwellFinished(e, ct); break;
                case ChangedEvent<Vehicle> e: HandleVehicleChangedEvent(e); break;
                default:
                    break;
            }
        }
    }

    private async Task HandleScpStatus(ScpStatus status, CancellationToken ct) {
        if (_options.ReceivableRoi) {
            _logger.LogInformation("Received SCP Status: [{EventType}] RoiId = {RoiId}, Status={Status}, EventId={EventId}, Timestamp={Timestamp}",
                status.EventType, status.RoiId, status.Status, status.EventId, status.Timestamp);
        }
        else {
            _logger.LogInformation("Received SCP Status: [{EventType}] Status={Status}, EventId={EventId}, Timestamp={Timestamp}",
                status.EventType, status.Status, status.EventId, status.Timestamp);
        }
        switch (status.Status) {
            case Status.NEW:
                var maps = GetDetectionMaps(status);
                var mapsToActivate = ResolveMapsToActivate(status, [.. maps]);

                foreach (var dm in mapsToActivate) {
                    CancelPendingFinish(dm.Key);
                    await ActivateMap(dm, ct);
                }
                break;

                List<DetectionMap> ResolveMapsToActivate(ScpStatus status, List<DetectionMap> maps) {
                    if (status.EventType == EventType.HandDetected || _options.PresetDwellTime <= 0)
                        return maps;

                    lock (_presetLock) {
                        if (_pendingPreset.TryGetValue(status.ChannelId, out var pending)) {
                            pending.Cts.Cancel();
                            if (pending.ArrivedType != status.EventType) {
                                _pendingPreset.Remove(status.ChannelId);
                                return [.. pending.Maps, .. maps];
                            }
                        }

                        // 페어링 미성립 → 대기 등록
                        StartPreset(status.ChannelId, status.EventType, [.. maps], ct);
                        return [];
                    }
                }
            case Status.IN_PROGRESS:
                break;
            case Status.FINISHED:
                foreach (var dmObj in GetDetectionMaps(status)) {
                    // 대기 중 Finished 수신 -> 페어링 취소 및 skip
                    if (TryCancelPreset(dmObj.Key.EventType, status.ChannelId)) {
                        _logger.LogInformation(
                            "Preset: FINISHED received while waiting for pair — cancelling preset for [{EventType}] Channel={ChannelId}",
                            dmObj.Key.EventType, status.ChannelId);
                        continue;
                    }

                    if (!_activatedKeys.Contains(dmObj.Key)) continue;

                    if (_options.RecoveryDwellTime <= 0)
                        await ExecuteFinished(dmObj, ct);
                    else
                        ScheduleFinished(dmObj, dmObj.Key, ct);
                }
                break;

                bool TryCancelPreset(EventType eventType, string channelId) {
                    if (!IsPresetType(eventType) || _options.PresetDwellTime <= 0)
                        return false;

                    lock (_presetLock) {
                        if (_pendingPreset.TryGetValue(channelId, out var pending)
                            && pending.ArrivedType == eventType) {
                            _pendingPreset.Remove(channelId);
                            pending.Cts.Cancel();
                            return true;
                        }
                    }
                    return false;
                }
        }
    }

    private static bool IsPresetType(EventType t) => t is EventType.HelmetMissing or EventType.PersonIntrusion;

    private async Task ActivateMap(DetectionMap dm, CancellationToken ct) {
        _activatedKeys.Add(dm.Key);
        var pausObjs = _safetyStateManager.OnDetected(dm);
        foreach (var segment in pausObjs.SegmentIds) await _connectionService.ModifySegment(segment, true, ct);
        foreach (var port in pausObjs.PortIds) await _connectionService.ModifyPort(port, true, ct);
        await _connectionService.EstopVehicle(pausObjs.VehicleIds);
        foreach (var robot in pausObjs.RobotIds) await _connectionService.UpdateEquipment(robot, true);
    }

    private void StartPreset(string channelId, EventType eventType, List<DetectionMap> maps, CancellationToken serviceCt) {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(serviceCt);
        _pendingPreset[channelId] = new PresetWaiting(eventType, maps, cts);

        _logger.LogInformation("Preset: [{EventType}] NEW received — waiting {PresetDwellTime}s for pair on Channel={ChannelId}",
            eventType, _options.PresetDwellTime, channelId);

        _ = Task.Run(async () => {
            try {
                await Task.Delay(TimeSpan.FromSeconds(_options.PresetDwellTime), cts.Token);
                lock (_presetLock) {
                    if (_pendingPreset.TryGetValue(channelId, out var current) && ReferenceEquals(current.Cts, cts)) {
                        _pendingPreset.Remove(channelId);
                    }
                    else return;
                }
                _logger.LogInformation("Preset: [{EventType}] pair window expired on Channel={ChannelId} — no action taken",
                    eventType, channelId);
            }
            catch (OperationCanceledException) {
                // 페어 성립 또는 FINISHED로 취소됨
            }
        }, CancellationToken.None);
    }

    private DetectionMap[] GetDetectionMaps(ScpStatus status) {
        if (_options.ReceivableRoi) {
            return _detectionMapService.GetDetectionMap(
                new DetectionMapKey(status.ChannelId, status.RoiId!, status.EventType), out var dm) && dm is { }
                ? [dm] : [];
        }
        return _detectionMapService.GetDetectionMaps(status.ChannelId, status.EventType);
    }

    private void CancelPendingFinish(DetectionMapKey key) {
        lock (_pendingLock) {
            if (!_pendingFinish.TryGetValue(key, out var cts)) return;
            _logger.LogInformation("Dwell: NEW received within dwell window — cancelling pending FINISHED for [{EventType}] Channel={ChannelId} Roi={RoI}",
                key.EventType, key.ChannelId, key.RoI);
            cts.Cancel();
            cts.Dispose();
            _pendingFinish.Remove(key);
        }
    }

    private void ScheduleFinished(DetectionMap dmObj, DetectionMapKey dmKey, CancellationToken serviceCt) {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(serviceCt);
        lock (_pendingLock) {
            // 이미 타이머가 있으면 첫 번째 기준 유지 (연속 FINISHED 무시)
            if (_pendingFinish.ContainsKey(dmKey)) {
                cts.Dispose();
                return;
            }
            _pendingFinish[dmKey] = cts;
        }

        _logger.LogInformation("Dwell: FINISHED received — scheduling execution in {RecoveryDwellTime}s for [{EventType}] Channel={ChannelId} Roi={RoI}",
            _options.RecoveryDwellTime, dmKey.EventType, dmKey.ChannelId, dmKey.RoI);

        _ = Task.Run(async () => {
            try {
                await Task.Delay(TimeSpan.FromSeconds(_options.RecoveryDwellTime), cts.Token);
                await WriteChannelAsync(new DwellFinishedEvent(dmObj, dmKey, cts));
            }
            catch (OperationCanceledException) {
                // NEW 이벤트로 취소됨
            }
        }, CancellationToken.None);
    }

    private async Task HandleDwellFinished(DwellFinishedEvent e, CancellationToken ct) {
        // 본인 CTS가 맞는지 확인 (중복 방지)
        lock (_pendingLock) {
            if (!_pendingFinish.TryGetValue(e.Key, out var current) || !ReferenceEquals(current, e.Cts)) {
                e.Cts.Dispose();
                return;
            }
            _pendingFinish.Remove(e.Key);
        }
        e.Cts.Dispose();

        _logger.LogInformation("Dwell: Executing FINISHED after dwell for [{EventType}] Channel={ChannelId} Roi={RoI}",
            e.Key.EventType, e.Key.ChannelId, e.Key.RoI);
        await ExecuteFinished(e.DetectionMap, ct);
    }

    private async Task ExecuteFinished(DetectionMap dmObj, CancellationToken ct) {
        _activatedKeys.Remove(dmObj.Key);
        var resumeObjs = _safetyStateManager.OnCleared(dmObj);
        foreach (var segment in resumeObjs.SegmentIds) await _connectionService.ModifySegment(segment, false, ct);
        foreach (var port in resumeObjs.PortIds) await _connectionService.ModifyPort(port, false, ct);
        foreach (var robot in resumeObjs.RobotIds) await _connectionService.UpdateEquipment(robot, false);
        await _connectionService.ResetVehicle(resumeObjs.VehicleIds);
        await Task.Delay(1000, ct); // 차량 상태가 ESTOP에서 RESET으로 변경되는 것을 보장하기 위한 지연);
        await _connectionService.AutoVehicle(resumeObjs.VehicleIds);

        if (_options.TimeToKillProgramAfterRecovery > 0) {
            _logger.LogInformation("Recovery complete. Program will exit in {Seconds}s.", _options.TimeToKillProgramAfterRecovery);
            _ = Task.Run(async () => {
                await Task.Delay(TimeSpan.FromSeconds(_options.TimeToKillProgramAfterRecovery));
                _logger.LogInformation("Stopping program after recovery.");
                _appLifetime.StopApplication();
            });
        }
    }

    private void HandleVehicleChangedEvent(ChangedEvent<Vehicle> e) {
        var now = e.New;
        var old = e.Old;
        var vehId = now?.Id ?? old?.Id;
        if (vehId is null) return;

        if (now is null) {
            _detectionMapService.UnRegisterVehicle(vehId);
            return;
        }

        if (_detectionMapService.IsVehicleRegistered(vehId) && now.NextNodeId == old?.NextNodeId) return;
        _detectionMapService.UpdateVehicleRegistration(vehId, now.NextNodeId, now.Path);
    }
}
