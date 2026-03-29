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
                foreach (var dm in maps) {
                    CancelPendingFinish(dm.Key);
                    var pausObjs = _safetyStateManager.OnDetected(dm);
                    foreach (var segment in pausObjs.SegmentIds) await _connectionService.ModifySegment(segment, true, ct);
                    foreach (var port in pausObjs.PortIds) await _connectionService.ModifyPort(port, true, ct);
                    await _connectionService.EstopVehicle(pausObjs.VehicleIds);
                    foreach (var robot in pausObjs.RobotIds) await _connectionService.UpdateEquipment(robot, true);
                }
                break;
            case Status.IN_PROGRESS:
                break;
            case Status.FINISHED:
                foreach (var dmObj in GetDetectionMaps(status)) {
                    if (_options.DwellTime <= 0) {
                        await ExecuteFinished(dmObj, ct);
                    }
                    else {
                        ScheduleFinished(dmObj, dmObj.Key, ct);
                    }
                }
                break;
        }
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
            // 기존 타이머가 있으면 교체 (연속 FINISHED 방어)
            if (_pendingFinish.TryGetValue(dmKey, out var existing)) {
                existing.Cancel();
                existing.Dispose();
            }
            _pendingFinish[dmKey] = cts;
        }

        _logger.LogInformation("Dwell: FINISHED received — scheduling execution in {DwellTime}s for [{EventType}] Channel={ChannelId} Roi={RoI}",
            _options.DwellTime, dmKey.EventType, dmKey.ChannelId, dmKey.RoI);

        _ = Task.Run(async () => {
            try {
                await Task.Delay(TimeSpan.FromSeconds(_options.DwellTime), cts.Token);
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
